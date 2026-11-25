// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Quack.Adbc.Substrait;
using Quack.Data;
using Quack.Types;
using IngestMode = Quack.QuackConnection.IngestMode;
using IngestTarget = Quack.QuackConnection.IngestTarget;

namespace Quack.Adbc;

internal sealed class QuackAdbcStatement : AdbcStatement
{
    // ADBC's standard bulk ingest options.
    internal const string IngestTargetTableOption = "adbc.ingest.target_table";
    internal const string IngestTargetCatalogOption = "adbc.ingest.target_catalog";
    internal const string IngestTargetDbSchemaOption = "adbc.ingest.target_db_schema";
    internal const string IngestTemporaryOption = "adbc.ingest.temporary";
    internal const string IngestModeOption = "adbc.ingest.mode";

    private readonly QuackAdbcConnection _connection;
    private RecordBatch? _boundBatch;
    private IArrowArrayStream? _boundStream;
    private string? _ingestTable;
    private string? _ingestCatalog;
    private string? _ingestSchema;
    private bool _ingestTemporary;
    // ADBC's default ingest mode is create.
    private IngestMode _ingestMode = IngestMode.Create;
    private TimeSpan? _commandTimeoutOverride;
    private bool _commandTimeoutOverrideSet;
    private bool _disposed;
    // A Substrait plan instead of SQL (SubstraitPlan), and its translation once made.
    private byte[]? _substraitPlan;
    private string? _planSql;

    public QuackAdbcStatement(QuackAdbcConnection connection)
    {
        _connection = connection;
    }

    public override void Bind(RecordBatch batch, Schema schema)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(batch);
        // The schema is provided by callers for parameterized prepared
        // statements; we don't support those, so we just keep the batch.
        // The batch carries its own schema for the ingest path.
        ReleaseBound();
        _boundBatch = batch;
    }

    // Ingest from a stream: its batches are sent as they are read, each one
    // released once it is serialized.
    public override void BindStream(IArrowArrayStream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        ReleaseBound();
        _boundStream = stream;
    }

    // A SQL statement ends ingest mode: the ingest options are cleared (bound
    // data is kept, for the older SqlQuery-names-the-table form).
    public override string? SqlQuery
    {
        get => base.SqlQuery;
        set
        {
            base.SqlQuery = value;
            _substraitPlan = null;
            _planSql = null;
            _ingestTable = null;
            _ingestCatalog = null;
            _ingestSchema = null;
            _ingestTemporary = false;
            _ingestMode = IngestMode.Create;
        }
    }

    // A Substrait plan, run as the DuckDB query it translates to (Substrait/DuckDbSubstrait.cs). It replaces the SQL
    // text, and SQL text replaces it; like SQL text, it ends ingest mode.
    public override byte[]? SubstraitPlan
    {
        get => _substraitPlan;
        set
        {
            SqlQuery = null;
            _substraitPlan = value;
        }
    }

    // ADBC Prepare: for a Substrait plan, the translation, so a plan that cannot be translated exactly is refused
    // here (NotImplemented), before anything runs. SQL text is not prepared (quack has no parameters).
    public override void Prepare()
    {
        ThrowIfDisposed();
        if (_substraitPlan is null)
        {
            base.Prepare();
            return;
        }
        _planSql = TranslatePlan(_substraitPlan);
    }

    // The query text: the SQL, or the plan's translation.
    private string? QueryText => _substraitPlan is null ? SqlQuery : _planSql ??= TranslatePlan(_substraitPlan);

    private string TranslatePlan(byte[] plan)
    {
        DuckDbSettings settings;
        using (QuackOperation operation = BeginOperation())
        {
            try
            {
                QuackQueryResult result = _connection.Underlying
                    .ExecuteAsync(DuckDbSettings.Query, operation.Token)
                    .GetAwaiter()
                    .GetResult();
                using var stream = new QuackArrowArrayStream(result);
                settings = DuckDbSettings.Read(stream);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw operation.Translate("Prepare");
            }
            catch (QuackException e)
            {
                throw QuackAdbcException.From(e);
            }
        }
        try
        {
            return DuckDbSubstrait.ToSql(plan, settings);
        }
        catch (SubstraitTranslationException e)
        {
            throw new AdbcException(e.Message, AdbcStatusCode.NotImplemented);
        }
    }

    public override void SetOption(string key, string value)
    {
        ThrowIfDisposed();
        if (string.Equals(key, QuackAdbcDriver.CommandTimeoutSecondsParameter, StringComparison.Ordinal))
        {
            _commandTimeoutOverride = string.IsNullOrEmpty(value)
                ? null
                : QuackAdbcDatabase.ParseCommandTimeoutSeconds(value);
            _commandTimeoutOverrideSet = true;
            return;
        }
        switch (key)
        {
            case IngestTargetTableOption:
                _ingestTable = string.IsNullOrEmpty(value) ? null : value;
                return;
            case IngestTargetCatalogOption:
                _ingestCatalog = string.IsNullOrEmpty(value) ? null : value;
                return;
            case IngestTargetDbSchemaOption:
                _ingestSchema = string.IsNullOrEmpty(value) ? null : value;
                return;
            case IngestTemporaryOption:
                _ingestTemporary = value switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw new AdbcException($"Invalid value '{value}' for {IngestTemporaryOption}; expected 'true' or 'false'.", AdbcStatusCode.InvalidArgument),
                };
                return;
            case IngestModeOption:
                _ingestMode = value switch
                {
                    "adbc.ingest.mode.create" => IngestMode.Create,
                    "adbc.ingest.mode.append" => IngestMode.Append,
                    "adbc.ingest.mode.replace" => IngestMode.Replace,
                    "adbc.ingest.mode.create_append" => IngestMode.CreateAppend,
                    _ => throw new AdbcException($"Invalid value '{value}' for {IngestModeOption}.", AdbcStatusCode.InvalidArgument),
                };
                return;
        }
        base.SetOption(key, value);
    }

    public override QueryResult ExecuteQuery()
    {
        ThrowIfDisposed();
        if (_boundBatch is not null || _boundStream is not null || _ingestTable is not null)
        {
            throw new InvalidOperationException(
                "ExecuteQuery cannot be used to ingest; use ExecuteUpdate with the bound data.");
        }
        string? query = QueryText;
        if (string.IsNullOrEmpty(query))
        {
            throw new InvalidOperationException("SqlQuery or SubstraitPlan must be set before calling ExecuteQuery.");
        }

        // The operation lives on in the result stream, so a cancel also stops
        // reading; the timeout covers getting the first response only.
        QuackOperation operation = BeginOperation();
        QuackQueryResult result;
        try
        {
            result = _connection.Underlying
                .ExecuteAsync(query, operation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            operation.Dispose();
            throw operation.Translate("ExecuteQuery");
        }
        catch (QuackException e)
        {
            operation.Dispose();
            throw QuackAdbcException.From(e);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
        operation.StopTimeout();
        try
        {
            return new QueryResult(-1, new QuackArrowArrayStream(result, operation, _connection.ArrowLossless));
        }
        catch (NotSupportedException e)
        {
            // A result type without an Arrow mapping: end the query on the server.
            result.CloseAsync().GetAwaiter().GetResult();
            operation.Dispose();
            throw new AdbcException(e.Message, AdbcStatusCode.NotImplemented);
        }
    }

    // ADBC 1.1: the result schema without running the statement. The query is
    // wrapped as SELECT * FROM (query) LIMIT 0, which DuckDB plans as an empty
    // result, and which is a parser error for anything that is not a query, so
    // a statement with side effects is never executed.
    public override Schema ExecuteSchema()
    {
        ThrowIfDisposed();
        if (_boundBatch is not null || _boundStream is not null || _ingestTable is not null)
        {
            throw new InvalidOperationException("ExecuteSchema does not apply to an ingest.");
        }
        string? text = QueryText;
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("SqlQuery or SubstraitPlan must be set before calling ExecuteSchema.");
        }
        string query = text.TrimEnd();
        while (query.EndsWith(';'))
        {
            query = query[..^1].TrimEnd();
        }

        using QuackOperation operation = BeginOperation();
        try
        {
            QuackQueryResult result = _connection.Underlying
                .ExecuteAsync($"SELECT * FROM (\n{query}\n) LIMIT 0", operation.Token)
                .GetAwaiter()
                .GetResult();
            _ = result.ToListAsync(operation.Token).GetAwaiter().GetResult();
            return ArrowConverter.BuildSchema(result.ColumnNames, result.ColumnTypes, _connection.ArrowLossless);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw operation.Translate("ExecuteSchema");
        }
        catch (QuackException e)
        {
            throw QuackAdbcException.From(e);
        }
    }

    // ADBC 1.1 StatementCancel: stops this statement's running query, ingest
    // or result stream; the server cancels the query.
    public override void Cancel()
    {
        ThrowIfDisposed();
        _connection.CancelOperations(this);
    }

    public override string GetOption(string key)
    {
        ThrowIfDisposed();
        if (string.Equals(key, QuackAdbcDriver.CommandTimeoutSecondsParameter, StringComparison.Ordinal))
        {
            TimeSpan? timeout = _commandTimeoutOverrideSet ? _commandTimeoutOverride : _connection.DefaultCommandTimeout;
            return timeout is null ? "" : timeout.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return key switch
        {
            IngestTargetTableOption => _ingestTable ?? throw NotSet(key),
            IngestTargetCatalogOption => _ingestCatalog ?? throw NotSet(key),
            IngestTargetDbSchemaOption => _ingestSchema ?? throw NotSet(key),
            IngestTemporaryOption => _ingestTemporary ? "true" : "false",
            IngestModeOption => _ingestMode switch
            {
                IngestMode.Append => "adbc.ingest.mode.append",
                IngestMode.Replace => "adbc.ingest.mode.replace",
                IngestMode.CreateAppend => "adbc.ingest.mode.create_append",
                _ => "adbc.ingest.mode.create",
            },
            _ => base.GetOption(key),
        };
    }

    private static AdbcException NotSet(string key) => new($"Option '{key}' is not set.", AdbcStatusCode.NotFound);

    public override UpdateResult ExecuteUpdate()
    {
        ThrowIfDisposed();
        if (_boundBatch is not null || _boundStream is not null)
        {
            return Ingest();
        }
        if (_ingestTable is not null)
        {
            throw new AdbcException("Bulk ingest needs bound data (Bind or BindStream).", AdbcStatusCode.InvalidState);
        }

        string? update = QueryText;
        if (string.IsNullOrEmpty(update))
        {
            throw new InvalidOperationException("SqlQuery or SubstraitPlan must be set before calling ExecuteUpdate.");
        }

        // SQL-only path: execute and drain. quack doesn't return an affected-
        // row count, so we report -1 unless rows came back.
        using QuackOperation operation = BeginOperation();
        try
        {
            QuackQueryResult result = _connection.Underlying
                .ExecuteAsync(update, operation.Token)
                .GetAwaiter()
                .GetResult();
            IReadOnlyList<DuckDbChunk> chunks = result.ToListAsync(operation.Token).GetAwaiter().GetResult();
            return new UpdateResult(AffectedRows(result, chunks));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw operation.Translate("ExecuteUpdate");
        }
        catch (QuackException e)
        {
            throw QuackAdbcException.From(e);
        }
    }

    // DuckDB answers INSERT / UPDATE / DELETE (and CREATE TABLE AS) with its own
    // one-row result, a BIGINT column named Count; quack sends it as the result
    // when no statement returned rows. Anything else: unknown (-1).
    private static long AffectedRows(QuackQueryResult result, IReadOnlyList<DuckDbChunk> chunks)
    {
        if (result.ColumnNames is ["Count"] && result.ColumnTypes[0].Id == Quack.Types.LogicalTypeId.BigInt
            && chunks.Sum(c => c.RowCount) == 1)
        {
            DuckDbChunk chunk = chunks.First(c => c.RowCount == 1);
            if (chunk.Columns[0] is FixedSizeColumn count && !count.IsNull(0))
            {
                return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(count.GetBytes(0));
            }
        }
        return -1;
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseBound();
            // A result stream left undisposed no longer keeps its operation registered.
            _connection.ForgetOperations(this);
        }
        base.Dispose();
    }

    // Bulk ingest of the bound batch or stream. With adbc.ingest.target_table
    // the standard options apply (mode, catalog, db_schema, temporary); without
    // it, SqlQuery names an existing table to append to (this driver's older form).
    private UpdateResult Ingest()
    {
        IngestTarget target;
        if (_ingestTable is not null)
        {
            if (_ingestTemporary && (_ingestCatalog is not null || _ingestSchema is not null))
            {
                throw new AdbcException(
                    $"{IngestTemporaryOption} can't be combined with {IngestTargetCatalogOption} or {IngestTargetDbSchemaOption}; a temporary table lives in temp.main.",
                    AdbcStatusCode.InvalidArgument);
            }
            target = new IngestTarget(_ingestTable, _ingestCatalog, _ingestSchema, _ingestTemporary, _ingestMode);
        }
        else if (!string.IsNullOrEmpty(SqlQuery))
        {
            target = new IngestTarget(SqlQuery, null, null, false, IngestMode.Append);
        }
        else
        {
            ReleaseBound();
            throw new AdbcException($"Set {IngestTargetTableOption} (or SqlQuery to an existing table) to ingest bound data.", AdbcStatusCode.InvalidState);
        }

        RecordBatch? batch = _boundBatch;
        IArrowArrayStream? stream = _boundStream;
        _boundBatch = null;
        _boundStream = null;
        try
        {
            Schema schema = batch?.Schema ?? stream!.Schema;
            List<string> names = schema.FieldsList.Select(f => f.Name).ToList();
            List<LogicalType> types = schema.FieldsList.Select(f => RecordBatchConverter.MapField(f)).ToList();
            using QuackOperation operation = BeginOperation();
            try
            {
                long rows = _connection.Underlying
                    .IngestAsync(target, names, types, Chunks(batch, stream, operation.Token), operation.Token)
                    .GetAwaiter()
                    .GetResult();
                return new UpdateResult(rows);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw operation.Translate("ExecuteUpdate (ingest)");
            }
            catch (QuackException e)
            {
                throw target.Mode is IngestMode.Append or IngestMode.CreateAppend
                    ? QuackAdbcException.FromAppend(e)
                    : QuackAdbcException.From(e);
            }
            catch (ArgumentException e)
            {
                // A batch whose columns differ from the bound schema's.
                throw new AdbcException(e.Message, AdbcStatusCode.InvalidArgument, e);
            }
        }
        finally
        {
            batch?.Dispose();
            stream?.Dispose();
        }
    }

    // The bound data as chunks. A chunk can point into its batch's buffers, so a
    // stream's batch is released only when the next one is requested, by which
    // time the sender has serialized it.
    private static async IAsyncEnumerable<DuckDbChunk> Chunks(RecordBatch? batch, IArrowArrayStream? stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (batch is not null)
        {
            yield return RecordBatchConverter.ToDuckDbChunk(batch);
            yield break;
        }
        RecordBatch? previous = null;
        try
        {
            while (await stream!.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false) is RecordBatch next)
            {
                previous?.Dispose();
                previous = next;
                if (next.Length > 0)
                {
                    yield return RecordBatchConverter.ToDuckDbChunk(next);
                }
            }
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private void ReleaseBound()
    {
        _boundBatch?.Dispose();
        _boundBatch = null;
        _boundStream?.Dispose();
        _boundStream = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackAdbcStatement));
        }
    }

    private QuackOperation BeginOperation()
        => _connection.BeginOperation(this, _commandTimeoutOverrideSet ? _commandTimeoutOverride : _connection.DefaultCommandTimeout);
}

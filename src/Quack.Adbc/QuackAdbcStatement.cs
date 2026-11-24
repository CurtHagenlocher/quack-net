// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
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
        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteQuery.");
        }

        using TimeoutScope scope = OpenTimeoutScope();
        QuackQueryResult result;
        try
        {
            result = _connection.Underlying
                .ExecuteAsync(SqlQuery, scope.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (scope.FiredByTimeout)
        {
            throw new TimeoutException(scope.TimeoutMessage("ExecuteQuery", _connection.Underlying.IsUsable));
        }
        return new QueryResult(-1, new QuackArrowArrayStream(result));
    }

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

        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteUpdate.");
        }

        // SQL-only path: execute and drain. quack doesn't return an affected-
        // row count, so we report -1 unless rows came back.
        using TimeoutScope scope = OpenTimeoutScope();
        long total = 0;
        try
        {
            QuackQueryResult result = _connection.Underlying
                .ExecuteAsync(SqlQuery, scope.Token)
                .GetAwaiter()
                .GetResult();
            foreach (DuckDbChunk chunk in result.ToListAsync(scope.Token).GetAwaiter().GetResult())
            {
                total += chunk.RowCount;
            }
        }
        catch (OperationCanceledException) when (scope.FiredByTimeout)
        {
            throw new TimeoutException(scope.TimeoutMessage("ExecuteUpdate", _connection.Underlying.IsUsable));
        }
        return new UpdateResult(total == 0 ? -1 : total);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseBound();
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
            List<LogicalType> types = schema.FieldsList.Select(f => RecordBatchConverter.MapType(f.DataType)).ToList();
            using TimeoutScope scope = OpenTimeoutScope();
            try
            {
                long rows = _connection.Underlying
                    .IngestAsync(target, names, types, Chunks(batch, stream, scope.Token), scope.Token)
                    .GetAwaiter()
                    .GetResult();
                return new UpdateResult(rows);
            }
            catch (OperationCanceledException) when (scope.FiredByTimeout)
            {
                throw new TimeoutException(scope.TimeoutMessage("ExecuteUpdate (ingest)", _connection.Underlying.IsUsable));
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

    private TimeoutScope OpenTimeoutScope()
    {
        TimeSpan? effective = _commandTimeoutOverrideSet
            ? _commandTimeoutOverride
            : _connection.DefaultCommandTimeout;
        return new TimeoutScope(effective);
    }

    // Bundles the CTS lifecycle so callers can `using` it and ask whether
    // a thrown OperationCanceledException originated from the timeout.
    private readonly struct TimeoutScope : IDisposable
    {
        private readonly CancellationTokenSource? _cts;
        public TimeSpan? Timeout { get; }

        public TimeoutScope(TimeSpan? timeout)
        {
            Timeout = timeout;
            _cts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        }

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

        public bool FiredByTimeout => _cts is not null && _cts.IsCancellationRequested;

        public string TimeoutMessage(string operation, bool connectionUsable)
            => $"{operation} exceeded command_timeout_seconds={Timeout!.Value.TotalSeconds:0.###}; "
               + (connectionUsable
                   ? "the query was cancelled on the server and the ADBC connection remains usable."
                   : "the server did not stop the query in time and the ADBC connection is no longer usable.");

        public void Dispose() => _cts?.Dispose();
    }
}

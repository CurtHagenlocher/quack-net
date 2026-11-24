// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Quack.Adbc;

internal sealed class QuackAdbcConnection : AdbcConnection
{
    // ADBC's standard connection options (ADBC 1.1).
    internal const string CurrentCatalogOption = "adbc.connection.catalog";
    internal const string CurrentDbSchemaOption = "adbc.connection.db_schema";

    private readonly QuackConnection _connection;
    private readonly object _operationsLock = new();
    private readonly HashSet<QuackOperation> _operations = [];
    private bool _disposed;

    public QuackAdbcConnection(QuackConnection connection, TimeSpan? defaultCommandTimeout = null)
    {
        _connection = connection;
        DefaultCommandTimeout = defaultCommandTimeout;
    }

    internal QuackConnection Underlying => _connection;

    // Default per-query timeout inherited from the database parameters;
    // statements may override via SetOption. See QuackAdbcDriver for the
    // semantics — terminal for the connection on fire.
    internal TimeSpan? DefaultCommandTimeout { get; }

    internal QuackOperation BeginOperation(object? owner, TimeSpan? timeout)
    {
        QuackOperation operation = new(this, owner, timeout);
        lock (_operationsLock)
        {
            _operations.Add(operation);
        }
        return operation;
    }

    // False if the operation had already ended.
    internal bool EndOperation(QuackOperation operation)
    {
        lock (_operationsLock)
        {
            return _operations.Remove(operation);
        }
    }

    // Cancels the running operations of owner, or all of them for null.
    internal void CancelOperations(object? owner)
    {
        lock (_operationsLock)
        {
            foreach (QuackOperation operation in _operations)
            {
                if (owner is null || ReferenceEquals(operation.Owner, owner))
                {
                    operation.Cancel();
                }
            }
        }
    }

    // ADBC 1.1 ConnectionCancel: stops every running call on this connection,
    // statements and metadata alike.
    public override void Cancel() => CancelOperations(owner: null);

    public override string GetOption(string key) => key switch
    {
        CurrentCatalogOption => CurrentCatalog!,
        CurrentDbSchemaOption => CurrentDbSchema!,
        _ => base.GetOption(key),
    };

    public override void SetOption(string key, string value)
    {
        switch (key)
        {
            case CurrentCatalogOption:
                CurrentCatalog = value;
                return;
            case CurrentDbSchemaOption:
                CurrentDbSchema = value;
                return;
        }
        base.SetOption(key, value);
    }

    // The session's current catalog and schema: current_database() and
    // current_schema(), set with USE (a catalog switch starts in its default schema).
    public override string? CurrentCatalog
    {
        get => QueryText("SELECT current_database()");
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            Run($"USE {QuoteIdentifier(value)}");
        }
    }

    public override string? CurrentDbSchema
    {
        get => QueryText("SELECT current_schema()");
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            Run($"USE {QuoteIdentifier(CurrentCatalog!)}.{QuoteIdentifier(value)}");
        }
    }

    public override IArrowArrayStream GetStatisticsNames()
    {
        ThrowIfDisposed();
        return StatisticsBuilder.BuildNames();
    }

    // Table row counts, from DuckDB's estimate (duckdb_tables().estimated_size),
    // always marked approximate: ADBC lets a driver answer approximately even
    // when exact values are asked for.
    public override IArrowArrayStream GetStatistics(string? catalogPattern, string? dbSchemaPattern, string? tableNamePattern, bool approximate)
    {
        ThrowIfDisposed();
        return StatisticsBuilder.Build(this, catalogPattern, dbSchemaPattern, tableNamePattern);
    }

    // Runs a metadata query as an operation of this connection.
    internal IReadOnlyList<Data.DuckDbChunk> RunQuery(string sql, out QuackQueryResult result, string operationName)
    {
        using QuackOperation operation = BeginOperation(owner: null, DefaultCommandTimeout);
        try
        {
            result = _connection.ExecuteAsync(sql, operation.Token).GetAwaiter().GetResult();
            return result.ToListAsync(operation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw operation.Translate(operationName);
        }
    }

    private string QueryText(string sql)
    {
        ThrowIfDisposed();
        IReadOnlyList<Data.DuckDbChunk> chunks = RunQuery(sql, out _, "GetOption");
        Data.DuckDbChunk chunk = chunks.First(c => c.RowCount > 0);
        ReadOnlyMemory<byte>? value = ((Data.VarBytesColumn)chunk.Columns[0]).Values[0];
        return Encoding.UTF8.GetString((value ?? ReadOnlyMemory<byte>.Empty).Span);
    }

    private void Run(string sql)
    {
        ThrowIfDisposed();
        _ = RunQuery(sql, out _, "SetOption");
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public override AdbcStatement CreateStatement()
    {
        ThrowIfDisposed();
        return new QuackAdbcStatement(this);
    }

    public override IArrowArrayStream GetInfo(IReadOnlyList<AdbcInfoCode> codes)
    {
        ThrowIfDisposed();
        return GetInfoBuilder.Build(this, codes);
    }

    public override IArrowArrayStream GetObjects(
        GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        IReadOnlyList<string>? tableTypes,
        string? columnNamePattern)
    {
        ThrowIfDisposed();
        // tableTypes filter not yet applied — DuckDB only has BASE TABLE / VIEW
        // and they're already unioned, but we don't subset the output by the
        // user-supplied list yet. Reasonable v1 behavior since the set is small.
        return GetObjectsBuilder.Build(this, depth, catalogPattern, dbSchemaPattern, tableNamePattern, columnNamePattern);
    }

    public override Schema GetTableSchema(string? catalog, string? dbSchema, string tableName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        // SELECT ... LIMIT 0 round-trips the table's column types through the
        // wire layer without materialising rows, which is the simplest way to
        // get authoritative LogicalTypes (DuckDB's duckdb_columns view loses
        // some type-info nuance — e.g. DECIMAL width/scale come back as text).
        string qualified = QualifyTableName(catalog, dbSchema, tableName);
        string sql = $"SELECT * FROM {qualified} LIMIT 0";
        // Drains the result, which releases the streaming cursor on the server.
        _ = RunQuery(sql, out QuackQueryResult result, "GetTableSchema");
        return ArrowConverter.BuildSchema(result.ColumnNames, result.ColumnTypes);
    }

    public override IArrowArrayStream GetTableTypes()
    {
        ThrowIfDisposed();
        // DuckDB's table catalog exposes BASE TABLE / VIEW. Don't bother
        // round-tripping the server for this — the set is stable.
        Schema schema = new Schema.Builder()
            .Field(new Field("table_type", StringType.Default, nullable: false))
            .Build();
        StringArray.Builder builder = new();
        builder.Append("BASE TABLE");
        builder.Append("VIEW");
        RecordBatch batch = new(schema, [builder.Build()], length: 2);
        return new SingleBatchStream(schema, batch);
    }

    private static string QualifyTableName(string? catalog, string? dbSchema, string tableName)
    {
        StringBuilder sb = new();
        if (!string.IsNullOrEmpty(catalog))
        {
            sb.Append('"').Append(catalog.Replace("\"", "\"\"")).Append("\".");
        }
        if (!string.IsNullOrEmpty(dbSchema))
        {
            sb.Append('"').Append(dbSchema.Replace("\"", "\"\"")).Append("\".");
        }
        sb.Append('"').Append(tableName.Replace("\"", "\"\"")).Append('"');
        return sb.ToString();
    }

    private sealed class SingleBatchStream : IArrowArrayStream
    {
        private readonly Schema _schema;
        private RecordBatch? _batch;

        public SingleBatchStream(Schema schema, RecordBatch batch)
        {
            _schema = schema;
            _batch = batch;
        }

        public Schema Schema => _schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            RecordBatch? next = _batch;
            _batch = null;
            return ValueTask.FromResult(next);
        }

        public void Dispose()
        {
            _batch?.Dispose();
            _batch = null;
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            CancelOperations(owner: null);
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackAdbcConnection));
        }
    }
}

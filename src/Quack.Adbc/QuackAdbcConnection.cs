using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Quack.Adbc;

internal sealed class QuackAdbcConnection : AdbcConnection
{
    private readonly QuackConnection _connection;
    private bool _disposed;

    public QuackAdbcConnection(QuackConnection connection)
    {
        _connection = connection;
    }

    internal QuackConnection Underlying => _connection;

    public override AdbcStatement CreateStatement()
    {
        ThrowIfDisposed();
        return new QuackAdbcStatement(this);
    }

    public override IArrowArrayStream GetInfo(IReadOnlyList<AdbcInfoCode> codes)
        => throw AdbcException.NotImplemented("GetInfo is not yet implemented for quack-net.");

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
        QuackQueryResult result = _connection.ExecuteAsync(sql).GetAwaiter().GetResult();
        // Drain to release the streaming cursor on the server.
        foreach (var _ in result.ToListAsync().GetAwaiter().GetResult()) { }
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

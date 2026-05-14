using Apache.Arrow;
using Apache.Arrow.Adbc;
using Quack.Data;

namespace Quack.Adbc;

internal sealed class QuackAdbcStatement : AdbcStatement
{
    private readonly QuackAdbcConnection _connection;
    private RecordBatch? _boundBatch;
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
        _boundBatch?.Dispose();
        _boundBatch = batch;
    }

    public override QueryResult ExecuteQuery()
    {
        ThrowIfDisposed();
        if (_boundBatch is not null)
        {
            throw new InvalidOperationException(
                "ExecuteQuery cannot be used with a bound RecordBatch; use ExecuteUpdate to ingest rows.");
        }
        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteQuery.");
        }

        QuackQueryResult result = _connection.Underlying
            .ExecuteAsync(SqlQuery)
            .GetAwaiter()
            .GetResult();
        return new QueryResult(-1, new QuackArrowArrayStream(result));
    }

    public override UpdateResult ExecuteUpdate()
    {
        ThrowIfDisposed();
        // Bound-batch path: append the batch's rows to the table named by
        // SqlQuery. ADBC's contract for ingest via Bind+ExecuteUpdate uses
        // SqlQuery as the target table identifier.
        if (_boundBatch is not null)
        {
            if (string.IsNullOrEmpty(SqlQuery))
            {
                throw new InvalidOperationException(
                    "SqlQuery (target table name) must be set when a RecordBatch is bound.");
            }
            DuckDbChunk chunk = RecordBatchConverter.ToDuckDbChunk(_boundBatch);
            _connection.Underlying
                .AppendAsync(SqlQuery, chunk)
                .GetAwaiter()
                .GetResult();
            long appended = _boundBatch.Length;
            _boundBatch.Dispose();
            _boundBatch = null;
            return new UpdateResult(appended);
        }

        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteUpdate.");
        }

        // SQL-only path: execute and drain. quack doesn't return an affected-
        // row count, so we report -1 unless rows came back.
        QuackQueryResult result = _connection.Underlying
            .ExecuteAsync(SqlQuery)
            .GetAwaiter()
            .GetResult();
        long total = 0;
        foreach (DuckDbChunk chunk in result.ToListAsync().GetAwaiter().GetResult())
        {
            total += chunk.RowCount;
        }
        return new UpdateResult(total == 0 ? -1 : total);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _boundBatch?.Dispose();
            _boundBatch = null;
        }
        base.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackAdbcStatement));
        }
    }
}

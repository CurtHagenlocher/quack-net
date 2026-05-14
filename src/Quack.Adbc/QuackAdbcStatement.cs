using Apache.Arrow.Adbc;
using Quack.Data;

namespace Quack.Adbc;

internal sealed class QuackAdbcStatement : AdbcStatement
{
    private readonly QuackAdbcConnection _connection;
    private bool _disposed;

    public QuackAdbcStatement(QuackAdbcConnection connection)
    {
        _connection = connection;
    }

    public override QueryResult ExecuteQuery()
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteQuery.");
        }

        QuackQueryResult result = _connection.Underlying
            .ExecuteAsync(SqlQuery)
            .GetAwaiter()
            .GetResult();
        // ADBC convention: -1 means "row count unknown" (true for SELECT here).
        return new QueryResult(-1, new QuackArrowArrayStream(result));
    }

    public override UpdateResult ExecuteUpdate()
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteUpdate.");
        }

        // Drain the result (CREATE/INSERT/UPDATE/DELETE produce a no-row
        // result that must still be consumed for the server to advance).
        QuackQueryResult result = _connection.Underlying
            .ExecuteAsync(SqlQuery)
            .GetAwaiter()
            .GetResult();
        long total = 0;
        foreach (DuckDbChunk chunk in result.ToListAsync().GetAwaiter().GetResult())
        {
            total += chunk.RowCount;
        }
        // The quack protocol doesn't surface affected-row counts back from
        // INSERT/UPDATE/DELETE; -1 is ADBC's "unknown" sentinel.
        return new UpdateResult(total == 0 ? -1 : total);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
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

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Quack.Data;

namespace Quack.Adbc;

// Wraps a streaming QuackQueryResult as an Apache Arrow IArrowArrayStream.
// Pulls one DuckDbChunk per ReadNextRecordBatchAsync call and converts it
// to an Arrow RecordBatch. The Arrow Schema is derived once from the
// QuackQueryResult's column metadata so callers can inspect types before
// pulling rows.
internal sealed class QuackArrowArrayStream : IArrowArrayStream
{
    private readonly QuackQueryResult _result;
    private readonly Schema _schema;
    private IAsyncEnumerator<DuckDbChunk>? _enumerator;
    private bool _disposed;

    public QuackArrowArrayStream(QuackQueryResult result)
    {
        _result = result;
        _schema = ArrowConverter.BuildSchema(result.ColumnNames, result.ColumnTypes);
    }

    public Schema Schema => _schema;

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackArrowArrayStream));
        }
        _enumerator ??= _result.GetChunksAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }
        DuckDbChunk chunk = _enumerator.Current;
        return ArrowConverter.ToRecordBatch(_schema, chunk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_enumerator is not null)
        {
            // IAsyncEnumerator.DisposeAsync must be awaited; sync-over-async
            // is fine because the underlying enumerator's Dispose is a no-op
            // beyond releasing the streaming cursor on the server.
            _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _enumerator = null;
        }
    }
}

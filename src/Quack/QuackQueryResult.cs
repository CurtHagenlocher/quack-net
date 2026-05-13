using System.Runtime.CompilerServices;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;

namespace Quack;

// Streaming result for a prepared SQL query. The first batch of chunks arrives
// with the PrepareResponse; subsequent batches are pulled via FetchRequest.
// Iterate via GetChunksAsync until the server returns a FetchResponse with
// no chunks, which is the authoritative end-of-stream signal.
//
// Cancelling the token passed to GetChunksAsync/ToListAsync aborts the local
// HTTP wait for the next batch but does not interrupt server-side execution
// of the query that produced this result — see the cancellation note on
// QuackConnection for the underlying reason (v1.5-variegata).
public sealed class QuackQueryResult
{
    private readonly QuackTransport _transport;
    private readonly string _connectionId;
    private readonly IReadOnlyList<DuckDbChunk> _firstBatch;
    private readonly bool _needsMoreFetch;
    private readonly Int128 _resultUuid;

    public IReadOnlyList<LogicalType> ColumnTypes { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public Int128 ResultUuid => _resultUuid;

    internal QuackQueryResult(
        QuackTransport transport,
        string connectionId,
        IReadOnlyList<LogicalType> columnTypes,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<DuckDbChunk> firstBatch,
        bool needsMoreFetch,
        Int128 resultUuid)
    {
        _transport = transport;
        _connectionId = connectionId;
        ColumnTypes = columnTypes;
        ColumnNames = columnNames;
        _firstBatch = firstBatch;
        _needsMoreFetch = needsMoreFetch;
        _resultUuid = resultUuid;
    }

    public async IAsyncEnumerable<DuckDbChunk> GetChunksAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (DuckDbChunk chunk in _firstBatch)
        {
            yield return chunk;
        }

        if (!_needsMoreFetch)
        {
            yield break;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FetchResponseMessage response = await _transport.SendAsync<FetchResponseMessage>(
                new FetchRequestMessage
                {
                    ConnectionId = _connectionId,
                    Uuid = _resultUuid,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.IsEndOfStream)
            {
                yield break;
            }

            foreach (DuckDbChunk chunk in response.Results)
            {
                yield return chunk;
            }
        }
    }

    public async Task<IReadOnlyList<DuckDbChunk>> ToListAsync(CancellationToken cancellationToken = default)
    {
        List<DuckDbChunk> chunks = [];
        await foreach (DuckDbChunk chunk in GetChunksAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;

namespace Quack;

// Streaming result for a prepared SQL query. The leading batches arrive with
// the PrepareResponse; the rest are pulled one dense batch at a time with
// FetchRequest(batch_index = n, ack_index = n - 1), n counting from 1. A
// FetchResponse with no chunks ends the stream and carries the total batch
// count, which is checked against the batches received. After a complete read
// the client sends an Acknowledgement, so a server that retains results for
// replay can drop this one.
//
// Cancelling the token passed to GetChunksAsync/ToListAsync cancels the query
// on the server and keeps the connection usable (see QuackConnection).
public sealed class QuackQueryResult
{
    private readonly QuackTransport _transport;
    private readonly string _connectionId;
    private readonly IReadOnlyList<DuckDbChunk> _firstBatch;
    private readonly bool _needsMoreFetch;
    private readonly Int128 _queryUuid;

    public IReadOnlyList<LogicalType> ColumnTypes { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public Int128 QueryUuid => _queryUuid;

    internal QuackQueryResult(
        QuackTransport transport,
        string connectionId,
        IReadOnlyList<LogicalType> columnTypes,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<DuckDbChunk> firstBatch,
        bool needsMoreFetch,
        Int128 queryUuid)
    {
        _transport = transport;
        _connectionId = connectionId;
        ColumnTypes = columnTypes;
        ColumnNames = columnNames;
        _firstBatch = firstBatch;
        _needsMoreFetch = needsMoreFetch;
        _queryUuid = queryUuid;
    }

    public async IAsyncEnumerable<DuckDbChunk> GetChunksAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (DuckDbChunk chunk in _firstBatch)
        {
            yield return chunk;
        }

        if (_needsMoreFetch)
        {
            for (ulong batchIndex = 1; ; batchIndex++)
            {
                // Ack everything before this batch: we never ask for an earlier one again.
                FetchResponseMessage response = await QueryCancellation.SendAsync<FetchResponseMessage>(
                    _transport,
                    new FetchRequestMessage
                    {
                        ConnectionId = _connectionId,
                        QueryUuid = _queryUuid,
                        BatchIndex = batchIndex,
                        AckIndex = batchIndex - 1,
                    },
                    _queryUuid,
                    cancellationToken,
                    queryStarted: true).ConfigureAwait(false);

                if (response.IsEndOfStream)
                {
                    ulong received = batchIndex - 1;
                    if (response.TotalBatches is ulong total && total != received)
                    {
                        throw new QuackException(
                            $"Result ended after {received} fetched batches, but the server announced {total}.");
                    }
                    break;
                }
                if (response.BatchIndex is ulong served && served != batchIndex)
                {
                    throw new QuackException($"Asked for batch {batchIndex}, but the server sent batch {served}.");
                }

                foreach (DuckDbChunk chunk in response.Results)
                {
                    yield return chunk;
                }
            }
        }

        await AcknowledgeAsync().ConfigureAwait(false);
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

    // The result is complete, so the failure of this best-effort message must
    // not fail the read.
    private async Task AcknowledgeAsync()
    {
        if (_transport.IsBroken)
        {
            return;
        }
        try
        {
            await _transport.SendAsync<SuccessResponse>(
                new AcknowledgementMessage { ConnectionId = _connectionId, QueryUuid = _queryUuid },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (QuackException)
        {
        }
    }
}

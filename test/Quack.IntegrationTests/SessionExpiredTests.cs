// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;

namespace Quack.IntegrationTests;

public class SessionExpiredTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public SessionExpiredTests(QuackServerFixture server)
    {
        _server = server;
    }

    // Simulates a server restart by asking the server to forget our session
    // out-of-band, while the client object still thinks it owns it. The
    // client's stored ConnectionId becomes invalid; the next request sees
    // ErrorResponse("Invalid connection id").
    private static async Task ForceServerForgetSessionAsync(QuackConnection conn)
    {
        await conn.Transport.SendAsync<SuccessResponse>(
            new DisconnectMessage { ConnectionId = conn.ConnectionId },
            CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_NoReconnect_ThrowsTypedSessionExpired()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string lostId = conn.ConnectionId;
        await ForceServerForgetSessionAsync(conn);

        QuackSessionExpiredException ex = await Assert.ThrowsAsync<QuackSessionExpiredException>(
            () => conn.ExecuteAsync("SELECT 1"));

        Assert.Equal(lostId, ex.ConnectionId);
        Assert.Contains("Invalid connection id", ex.ServerMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, conn.SessionGeneration);
    }

    [Fact]
    public async Task ExecuteAsync_WithReconnect_TransparentlyReissuesQuery()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, autoReconnect: true);
        string lostId = conn.ConnectionId;
        await ForceServerForgetSessionAsync(conn);

        QuackQueryResult result = await conn.ExecuteAsync("SELECT 42 AS x");
        IReadOnlyList<DuckDbChunk> chunks = await result.ToListAsync();

        Assert.Single(chunks);
        Assert.Equal(1, chunks[0].RowCount);
        Assert.Equal(1, conn.SessionGeneration);
        Assert.NotEqual(lostId, conn.ConnectionId);
    }

    [Fact]
    public async Task FetchAsync_AfterSessionLoss_IsNotAutoRecovered()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, autoReconnect: true);

        // A multi-chunk result forces a real Fetch round-trip after the
        // first batch arrives with PrepareResponse. 50k rows comfortably
        // exceeds the server's chunk size and triggers NeedsMoreFetch.
        QuackQueryResult result = await conn.ExecuteAsync(
            "SELECT i::INTEGER AS i FROM range(50000) t(i)");

        await using IAsyncEnumerator<DuckDbChunk> chunks = result
            .GetChunksAsync()
            .GetAsyncEnumerator();
        Assert.True(await chunks.MoveNextAsync(), "expected at least one chunk");

        // Now simulate the server restart in the middle of the result set.
        // The result_uuid is gone server-side and the query could have had
        // side effects, so the contract is hard-fail (no auto-recover).
        await ForceServerForgetSessionAsync(conn);

        await Assert.ThrowsAsync<QuackSessionExpiredException>(async () =>
        {
            while (await chunks.MoveNextAsync()) { }
        });
    }

    [Fact]
    public async Task AppendAsync_WithReconnect_TransparentlySucceeds()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, autoReconnect: true);

        // CREATE TABLE goes against the shared :memory: DB, not the per-
        // session state, so it survives the reconnect we're about to force.
        string table = "t_append_reconnect_" + Guid.NewGuid().ToString("N");
        await (await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER)")).ToListAsync();
        Assert.Equal(0, conn.SessionGeneration);

        await ForceServerForgetSessionAsync(conn);

        // AppendAsync uses the same SendWithReconnectAsync helper but a
        // different message type — exercising it here proves the helper's
        // generic path works for Append, not just Execute.
        await conn.AppendAsync(table, BuildSingleIntChunk(42));

        Assert.Equal(1, conn.SessionGeneration);

        // Verify the row landed by reading it back through the (now post-
        // reconnect) session.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id FROM {table}"))
            .ToListAsync();
        Assert.Equal(1, back.Sum(c => c.RowCount));
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(back[0].Columns[0]);
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
    }

    [Fact]
    public async Task RepeatedSessionLoss_ReconnectsEachTime()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, autoReconnect: true);
        Assert.Equal(0, conn.SessionGeneration);

        // First cycle.
        string idAfterOpen = conn.ConnectionId;
        await ForceServerForgetSessionAsync(conn);
        await (await conn.ExecuteAsync("SELECT 1")).ToListAsync();
        Assert.Equal(1, conn.SessionGeneration);
        string idAfterFirstReconnect = conn.ConnectionId;
        Assert.NotEqual(idAfterOpen, idAfterFirstReconnect);

        // Second cycle — make sure the lock/counter still behave after a
        // prior reconnect and that we re-handshake again rather than (e.g.)
        // sticking with a stale "already recovered" flag.
        await ForceServerForgetSessionAsync(conn);
        await (await conn.ExecuteAsync("SELECT 2")).ToListAsync();
        Assert.Equal(2, conn.SessionGeneration);
        Assert.NotEqual(idAfterFirstReconnect, conn.ConnectionId);
    }

    [Fact]
    public async Task ConcurrentExecuteAsync_ReconnectsOnce()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, autoReconnect: true);
        await ForceServerForgetSessionAsync(conn);

        Task<QuackQueryResult> a = conn.ExecuteAsync("SELECT 1 AS a");
        Task<QuackQueryResult> b = conn.ExecuteAsync("SELECT 2 AS b");
        await Task.WhenAll(a, b);

        // Exactly one re-handshake should have happened. If both callers
        // raced past the SessionGeneration guard we'd see 2.
        Assert.Equal(1, conn.SessionGeneration);
    }

    private static DuckDbChunk BuildSingleIntChunk(int value)
    {
        LogicalType intType = new(LogicalTypeId.Integer);
        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        return new DuckDbChunk
        {
            Types = [intType],
            Columns =
            [
                new FixedSizeColumn
                {
                    Type = intType,
                    Count = 1,
                    ElementSize = 4,
                    Data = data,
                },
            ],
            RowCount = 1,
        };
    }
}

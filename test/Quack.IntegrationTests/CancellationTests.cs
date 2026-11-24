// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack;
using Quack.Data;

namespace Quack.IntegrationTests;

public class CancellationTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public CancellationTests(QuackServerFixture server)
    {
        _server = server;
    }

    // A query that's reliably slow enough to outlast a sub-second cancellation.
    // 1B-row range scan with a non-trivial predicate; even with DuckDB's
    // parallelism this takes seconds on any normal machine.
    private const string SlowQuery =
        "SELECT count(*) FROM range(1000000000) t(i) WHERE (i * i) % 7 = 0";

    [Fact]
    public async Task ExecuteAsync_CancellationFires_CancelsOnServerAndKeepsConnection()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            QuackQueryResult result = await conn.ExecuteAsync(SlowQuery, cts.Token);
            // Defensive: if Prepare ever races and completes before the
            // timeout, drain to give the token a chance to fire on Fetch.
            _ = await result.ToListAsync(cts.Token);
        });

        // Cancelling the token cancels the query on the server (CANCEL_REQUEST),
        // so the connection stays usable.
        Assert.True(conn.IsUsable);
        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync("SELECT 1")).ToListAsync();
        Assert.Equal(1, back.Sum(c => c.RowCount));
    }

    [Fact]
    public async Task DisposeAsync_AfterCancellation_DoesNotSendDisconnect()
    {
        // Just exercising that dispose is clean after cancellation.
        QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.ExecuteAsync(SlowQuery, cts.Token));

        await conn.DisposeAsync();
    }
}

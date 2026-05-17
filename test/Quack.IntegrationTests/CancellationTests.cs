using Quack;

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
    public async Task ExecuteAsync_CancellationFires_ThrowsAndMarksTransportBroken()
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

        // After cancellation, a fresh request on the same transport must
        // not be sent — the contract is that the connection is dead.
        QuackException broken = await Assert.ThrowsAsync<QuackException>(
            () => conn.ExecuteAsync("SELECT 1"));
        Assert.Contains("broken", broken.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeAsync_AfterCancellation_DoesNotSendDisconnect()
    {
        // Just exercising that dispose is clean after cancellation; the
        // server-side session leaks until the query naturally completes
        // (no in-protocol interrupt), so we only assert no exception.
        QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.ExecuteAsync(SlowQuery, cts.Token));

        await conn.DisposeAsync();
    }
}

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Quack.Adbc;
using Quack.Protocol;
using Quack.Transport;

namespace Quack.Adbc.Tests;

public class ReconnectTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public ReconnectTests(QuackServerFixture server) => _server = server;

    // The ADBC wrappers don't surface the underlying QuackConnection, so to
    // forge a session-loss we have to reach in. The Underlying property on
    // QuackAdbcConnection is internal — visible because Quack.Adbc.Tests has
    // InternalsVisibleTo. We then use Transport (also internal on
    // QuackConnection) to send a DisconnectMessage out of band.
    private static async Task ForceServerForgetSessionAsync(AdbcConnection adbcConn)
    {
        var quackConn = ((QuackAdbcConnection)adbcConn).Underlying;
        await quackConn.Transport.SendAsync<SuccessResponse>(
            new DisconnectMessage { ConnectionId = quackConn.ConnectionId },
            CancellationToken.None);
    }

    [Fact]
    public async Task DefaultBehaviour_NoReconnect_SurfacesAsAdbcError()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            // reconnect_on_session_loss omitted — defaults to false.
        });
        using AdbcConnection conn = db.Connect(options: null);
        await ForceServerForgetSessionAsync(conn);

        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT 1";
        // Surfaces as an exception — the ADBC layer doesn't promise the
        // specific type, just that the call fails.
        Assert.ThrowsAny<Exception>(() => stmt.ExecuteQuery());
    }

    [Fact]
    public async Task ReconnectEnabled_SurvivesSessionLoss()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.ReconnectOnSessionLossParameter] = "true",
        });
        using AdbcConnection conn = db.Connect(options: null);
        await ForceServerForgetSessionAsync(conn);

        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT 7 AS x";
        QueryResult qr = stmt.ExecuteQuery();
        using IArrowArrayStream stream = qr.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Int32Array col = Assert.IsType<Int32Array>(batch.Column(0));
        Assert.Equal(7, col.GetValue(0));
    }

    [Fact]
    public void InvalidReconnectValue_Throws()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.ReconnectOnSessionLossParameter] = "maybe",
        });
        Assert.ThrowsAny<Exception>(() => db.Connect(options: null));
    }
}

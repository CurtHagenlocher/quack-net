using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

public class CommandTimeoutTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public CommandTimeoutTests(QuackServerFixture server) => _server = server;

    // 1B-row range scan with a non-trivial predicate; reliably outlasts
    // a sub-second timeout regardless of host speed.
    private const string SlowQuery =
        "SELECT count(*) FROM range(1000000000) t(i) WHERE (i * i) % 7 = 0";

    [Fact]
    public void DatabaseLevelCommandTimeout_FiresAndPoisonsConnection()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.CommandTimeoutSecondsParameter] = "0.1",
        });
        using AdbcConnection conn = db.Connect(options: null);

        using (AdbcStatement slow = conn.CreateStatement())
        {
            slow.SqlQuery = SlowQuery;
            TimeoutException ex = Assert.Throws<TimeoutException>(() => slow.ExecuteQuery());
            Assert.Contains("command_timeout_seconds", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no longer usable", ex.Message, StringComparison.Ordinal);
        }

        // Subsequent statements on the same (now-broken) connection fail.
        using AdbcStatement after = conn.CreateStatement();
        after.SqlQuery = "SELECT 1";
        Assert.ThrowsAny<Exception>(() => after.ExecuteQuery());
    }

    [Fact]
    public async Task StatementLevelOverride_AppliesToOneStatement()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            // No database-level timeout; statement opts in.
        });
        using AdbcConnection conn = db.Connect(options: null);

        // Sanity: a fast query without a timeout succeeds.
        using (AdbcStatement fast = conn.CreateStatement())
        {
            fast.SqlQuery = "SELECT 1";
            QueryResult ok = fast.ExecuteQuery();
            using IArrowArrayStream stream = ok.Stream!;
            Assert.NotNull(await stream.ReadNextRecordBatchAsync());
        }

        // Per-statement override fires for the slow query and breaks the connection.
        using AdbcStatement slow = conn.CreateStatement();
        slow.SetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter, "0.1");
        slow.SqlQuery = SlowQuery;
        Assert.Throws<TimeoutException>(() => slow.ExecuteQuery());
    }

    [Fact]
    public void InvalidCommandTimeout_Throws()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.CommandTimeoutSecondsParameter] = "not-a-number",
        });
        Assert.ThrowsAny<Exception>(() => db.Connect(options: null));
    }
}

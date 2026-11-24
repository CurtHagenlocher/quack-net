// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Quack.Adbc;
using Quack.Data;

namespace Quack.Adbc.Tests;

public class CommandTimeoutTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public CommandTimeoutTests(QuackServerFixture server) => _server = server;

    // 1B-row range scan with a non-trivial predicate; reliably outlasts
    // a sub-second timeout regardless of host speed. The marker lets the
    // test find this statement in quack_active_connections().
    private static string SlowQuery(string marker) =>
        $"SELECT count(*) /* {marker} */ FROM range(1000000000) t(i) WHERE (i * i) % 7 = 0";

    [Fact]
    public async Task DatabaseLevelCommandTimeout_CancelsOnServerAndKeepsConnection()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            // Long enough to see the statement running on the server first.
            [QuackAdbcDriver.CommandTimeoutSecondsParameter] = "3",
        });
        using AdbcConnection conn = db.Connect(options: null);
        string connectionId = ((QuackAdbcConnection)conn).Underlying.ConnectionId;
        string marker = "timeout_" + Guid.NewGuid().ToString("N");

        using AdbcStatement slow = conn.CreateStatement();
        slow.SqlQuery = SlowQuery(marker);
        Task<QueryResult> running = Task.Run(() => slow.ExecuteQuery());
        Assert.True(await WaitUntilActiveAsync(marker), "the slow statement never showed up as active");

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() => running);
        Assert.Contains("command_timeout_seconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remains usable", ex.Message, StringComparison.Ordinal);

        // The server stopped the statement; it would still be 'active' if only
        // the local HTTP wait had been abandoned.
        List<(string ConnectionId, string Query, string State)> sessions = await SessionsAsync();
        Assert.Equal("cancelled", Assert.Single(sessions, r => r.ConnectionId == connectionId).State);
        Assert.DoesNotContain(sessions, r => r.Query.Contains(marker, StringComparison.Ordinal) && r.State == "active");

        // The same connection runs the next statement.
        using AdbcStatement after = conn.CreateStatement();
        after.SqlQuery = "SELECT 42::BIGINT AS v";
        QueryResult result = after.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(42L, ((Int64Array)batch!.Column(0)).GetValue(0));
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

        // Per-statement override fires for the slow query only.
        using (AdbcStatement slow = conn.CreateStatement())
        {
            slow.SetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter, "0.1");
            slow.SqlQuery = SlowQuery("override_" + Guid.NewGuid().ToString("N"));
            Assert.Throws<TimeoutException>(() => slow.ExecuteQuery());
        }

        // A statement without the override on the same connection still works.
        using AdbcStatement next = conn.CreateStatement();
        next.SqlQuery = "SELECT 1";
        QueryResult again = next.ExecuteQuery();
        using IArrowArrayStream nextStream = again.Stream!;
        Assert.NotNull(await nextStream.ReadNextRecordBatchAsync());
    }

    [Fact]
    public void ExecuteUpdate_Timeout_CancelsOnServer()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.CommandTimeoutSecondsParameter] = "0.1",
        });
        using AdbcConnection conn = db.Connect(options: null);
        using AdbcStatement slow = conn.CreateStatement();
        slow.SqlQuery = SlowQuery("update_" + Guid.NewGuid().ToString("N"));
        TimeoutException ex = Assert.Throws<TimeoutException>(() => slow.ExecuteUpdate());
        Assert.Contains("remains usable", ex.Message, StringComparison.Ordinal);
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

    private async Task<bool> WaitUntilActiveAsync(string marker)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if ((await SessionsAsync()).Any(r => r.Query.Contains(marker, StringComparison.Ordinal) && r.State == "active"))
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }

    // The server's own view of its sessions, read over a separate connection.
    private async Task<List<(string ConnectionId, string Query, string State)>> SessionsAsync()
    {
        await using QuackConnection observer = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        IReadOnlyList<DuckDbChunk> chunks = await (await observer.ExecuteAsync(
            "SELECT connection_id, coalesce(query, ''), state FROM quack_active_connections()")).ToListAsync();
        List<(string, string, string)> rows = [];
        foreach (DuckDbChunk chunk in chunks)
        {
            for (int i = 0; i < chunk.RowCount; i++)
            {
                rows.Add((Text(chunk, 0, i), Text(chunk, 1, i), Text(chunk, 2, i)));
            }
        }
        return rows;
    }

    private static string Text(DuckDbChunk chunk, int column, int row)
        => Encoding.UTF8.GetString(((VarBytesColumn)chunk.Columns[column]).Values[row]!.Value.Span);
}

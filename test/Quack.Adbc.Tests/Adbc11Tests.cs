// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// ADBC 1.1: GetInfo's API version, StatementCancel / ConnectionCancel,
// GetOption, the current catalog and schema, ExecuteSchema, statistics.
public class Adbc11Tests : IClassFixture<QuackServerFixture>
{
    // Runs for minutes unless cancelled.
    private const string SlowQuery = "SELECT sum(a.range * b.range) FROM range(1000000) a, range(1000000) b";

    private readonly QuackServerFixture _server;

    public Adbc11Tests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task GetInfo_ReportsAdbc110()
    {
        using AdbcConnection conn = OpenConnection();
        using IArrowArrayStream stream = conn.GetInfo([AdbcInfoCode.DriverAdbcVersion]);
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        DenseUnionArray values = (DenseUnionArray)batch.Column(1);
        Assert.Equal(2, values.TypeIds[0]); // int64_value
        Assert.Equal(1_001_000L, ((Int64Array)values.Fields[2]).GetValue(values.ValueOffsets[0]));
    }

    [Fact]
    public async Task StatementCancel_StopsARunningQuery_AndTheConnectionStaysUsable()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = SlowQuery;
        Exception e = await CancelledWithin(() => ReadAll(stmt.ExecuteQuery()), stmt.Cancel, conn.Cancel);
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.IsType<AdbcException>(e).Status);
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    [Fact]
    public async Task StatementCancel_StopsAResultBeingRead()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT hash(range) AS h FROM range(50000000)";
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        Assert.NotNull(await stream.ReadNextRecordBatchAsync());
        Exception e = await CancelledWithin(() => ReadAll(stream), stmt.Cancel, conn.Cancel);
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.IsType<AdbcException>(e).Status);
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    [Fact]
    public async Task ConnectionCancel_StopsItsStatements()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = SlowQuery;
        Exception e = await CancelledWithin(() => ReadAll(stmt.ExecuteQuery()), conn.Cancel, stmt.Cancel);
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.IsType<AdbcException>(e).Status);
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    // Runs work on another thread and cancels it (repeatedly, so a cancel that
    // lands before the query starts does not matter) until it ends. A cancel
    // that does not stop it within 30 s fails the test, after the fallback (a
    // different cancel path) has stopped the server's query.
    private static async Task<Exception> CancelledWithin(Action work, Action cancel, Action fallback)
    {
        Task task = Task.Run(work);
        Stopwatch sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.WhenAny(task, Task.Delay(200));
            if (!task.IsCompleted)
            {
                cancel();
            }
        }
        if (!task.IsCompleted)
        {
            fallback();
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Fail("The cancel did not stop the work within 30 s.");
        }
        return task.Exception?.InnerException ?? throw new Xunit.Sdk.XunitException("The work finished without being cancelled.");
    }

    // A cancel stops the running call only; the next one runs normally.
    [Fact]
    public void Cancel_WithNothingRunning_IsANoOp()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.Cancel();
        conn.Cancel();
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    // A value without an Arrow form (an INTERVAL in a VARIANT) fails with
    // NOT_IMPLEMENTED, and the query is closed on the server, so the
    // connection goes on.
    [Fact]
    public void UnsupportedType_IsNotImplemented_AndTheConnectionGoesOn()
    {
        using AdbcConnection conn = OpenConnection();
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SqlQuery = "SELECT (INTERVAL '1 day')::VARIANT AS v";
            Assert.Equal(AdbcStatusCode.NotImplemented, Assert.Throws<AdbcException>(() => ReadAll(stmt.ExecuteQuery())).Status);
        }
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    // A result stream read to its end, or left with its statement, does not
    // stay registered with the connection.
    [Fact]
    public async Task FinishedOrOrphanedResults_LeaveNoOperations()
    {
        using AdbcConnection conn = OpenConnection();
        System.Reflection.FieldInfo field = typeof(QuackAdbcConnection).GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int Count()
        {
            object set = field.GetValue(conn)!;
            return (int)set.GetType().GetProperty("Count")!.GetValue(set)!;
        }
        for (int i = 0; i < 5; i++)
        {
            AdbcStatement stmt = conn.CreateStatement();
            stmt.SqlQuery = "SELECT 1";
            IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
            while (await stream.ReadNextRecordBatchAsync() is not null) { }
            // Read to its end, neither the stream nor the statement disposed yet.
            Assert.Equal(0, Count());
            stmt.Dispose();
        }
        for (int i = 0; i < 5; i++)
        {
            using AdbcStatement stmt = conn.CreateStatement();
            stmt.SqlQuery = "SELECT 1";
            _ = stmt.ExecuteQuery();
        }
        Assert.Equal(0, Count());
    }

    // A result stream disposed without being read closes its query on the
    // server (the CANCEL itself is covered by ResultCloseTests).
    [Fact]
    public void DisposedUnreadStream_ClosesItsResult()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT hash(range) FROM range(20000000)";
        IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        object result = typeof(QuackArrowArrayStream).GetField("_result", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(stream)!;
        System.Reflection.FieldInfo closed = result.GetType().GetField("_closed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(0, closed.GetValue(result));
        stream.Dispose();
        Assert.Equal(1, closed.GetValue(result));
        Assert.Equal(1, ScalarInt(conn, "SELECT 1"));
    }

    [Fact]
    public void StatementGetOption_ReturnsWhatWasSet()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => stmt.GetOption("adbc.ingest.target_table")).Status);
        Assert.Equal("adbc.ingest.mode.create", stmt.GetOption("adbc.ingest.mode"));
        Assert.Equal("false", stmt.GetOption("adbc.ingest.temporary"));
        stmt.SetOption("adbc.ingest.target_table", "t");
        stmt.SetOption("adbc.ingest.mode", "adbc.ingest.mode.create_append");
        stmt.SetOption("adbc.ingest.target_db_schema", "s");
        stmt.SetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter, "2.5");
        Assert.Equal("t", stmt.GetOption("adbc.ingest.target_table"));
        Assert.Equal("adbc.ingest.mode.create_append", stmt.GetOption("adbc.ingest.mode"));
        Assert.Equal("s", stmt.GetOption("adbc.ingest.target_db_schema"));
        Assert.Equal("2.5", stmt.GetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter));
    }

    [Fact]
    public void DatabaseGetOption_ReturnsOptions_ButNotTheToken()
    {
        QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        Assert.Equal(_server.QuackUri, db.GetOption(QuackAdbcDriver.UriParameter));
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => db.GetOption(QuackAdbcDriver.TokenParameter)).Status);
    }

    [Fact]
    public void CurrentCatalogAndSchema_FollowTheSession()
    {
        using AdbcConnection conn = OpenConnection();
        string schema = "cs_" + Guid.NewGuid().ToString("N")[..8];
        string catalog = "cc_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal("memory", conn.GetOption("adbc.connection.catalog"));
        Assert.Equal("main", conn.GetOption("adbc.connection.db_schema"));

        Execute(conn, $"CREATE SCHEMA {schema}");
        conn.SetOption("adbc.connection.db_schema", schema);
        Assert.Equal(schema, conn.CurrentDbSchema);
        Assert.Equal(schema, ScalarText(conn, "SELECT current_schema()"));

        Execute(conn, $"ATTACH ':memory:' AS {catalog}");
        conn.CurrentCatalog = catalog;
        Assert.Equal(catalog, conn.GetOption("adbc.connection.catalog"));
        Assert.Equal("main", conn.GetOption("adbc.connection.db_schema"));

        conn.CurrentCatalog = "memory";
        Execute(conn, $"DETACH {catalog}");
        Execute(conn, $"DROP SCHEMA {schema}");
    }

    [Fact]
    public void ExecuteSchema_DescribesWithoutRunning()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "es_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TABLE {table} (i INTEGER)");
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SqlQuery = "SELECT 1::INTEGER AS a, 'x' AS b, [1.5::DOUBLE] AS c -- trailing comment\n;";
            Schema schema = stmt.ExecuteSchema();
            Assert.Equal(["a", "b", "c"], schema.FieldsList.Select(f => f.Name));
            Assert.IsType<Int32Type>(schema.FieldsList[0].DataType);
            Assert.IsType<StringType>(schema.FieldsList[1].DataType);
            Assert.IsType<ListType>(schema.FieldsList[2].DataType);
        }
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SqlQuery = $"INSERT INTO {table} VALUES (1)";
            Assert.ThrowsAny<Exception>(() => stmt.ExecuteSchema());
        }
        Assert.Equal(0, ScalarInt(conn, $"SELECT count(*)::INTEGER FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task GetStatistics_ReportsTableRowCounts()
    {
        using AdbcConnection conn = OpenConnection();
        string schema = "st_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE SCHEMA {schema}");
        Execute(conn, $"CREATE TABLE {schema}.t1 AS SELECT range AS i FROM range(100)");
        Execute(conn, $"CREATE TABLE {schema}.t2 AS SELECT range AS i FROM range(7)");

        using IArrowArrayStream stream = conn.GetStatistics(null!, schema, null!, approximate: true);
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        Assert.Null(await stream.ReadNextRecordBatchAsync());
        Assert.Equal(1, batch.Length);
        Assert.Equal("memory", ((StringArray)batch.Column(0)).GetString(0));
        ListArray catalogSchemas = (ListArray)batch.Column(1);
        StructArray schemas = (StructArray)catalogSchemas.Values;
        Assert.Equal(1, catalogSchemas.GetValueLength(0));
        Assert.Equal(schema, ((StringArray)schemas.Fields[0]).GetString(0));
        StructArray stats = (StructArray)((ListArray)schemas.Fields[1]).Values;
        Assert.Equal(2, stats.Length);
        DenseUnionArray values = (DenseUnionArray)stats.Fields[3];
        Dictionary<string, long> rows = [];
        for (int i = 0; i < stats.Length; i++)
        {
            Assert.True(stats.Fields[1].IsNull(i));
            Assert.Equal((short)6, ((Int16Array)stats.Fields[2]).GetValue(i));
            Assert.True(((BooleanArray)stats.Fields[4]).GetValue(i));
            Assert.Equal(0, values.TypeIds[i]);
            rows[((StringArray)stats.Fields[0]).GetString(i)] = ((Int64Array)values.Fields[0]).GetValue(values.ValueOffsets[i])!.Value;
        }
        Assert.Equal(new Dictionary<string, long> { ["t1"] = 100, ["t2"] = 7 }, rows);

        using IArrowArrayStream names = conn.GetStatisticsNames();
        Assert.Equal(["statistic_name", "statistic_key"], names.Schema.FieldsList.Select(f => f.Name));
        Execute(conn, $"DROP SCHEMA {schema} CASCADE");
    }

    private static void ReadAll(QueryResult result)
    {
        using IArrowArrayStream stream = result.Stream!;
        ReadAll(stream);
    }

    private static void ReadAll(IArrowArrayStream stream)
    {
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            batch.Dispose();
        }
    }

    private static int ScalarInt(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return ((Int32Array)batch.Column(0)).GetValue(0)!.Value;
    }

    private static string ScalarText(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return ((StringArray)batch.Column(0)).GetString(0);
    }

    private AdbcConnection OpenConnection()
    {
        QuackAdbcDriver driver = new();
        AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        return db.Connect(options: null);
    }

    private static void Execute(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        stmt.ExecuteUpdate();
    }
}

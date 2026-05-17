// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

public class DriverSmokeTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public DriverSmokeTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task ExecuteSelect_ReturnsArrowRecordBatch_WithExpectedValues()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        using AdbcConnection conn = db.Connect(options: null);
        using AdbcStatement stmt = conn.CreateStatement();
        // Explicit casts because DuckDB infers DECIMAL for the literal 2.5
        // and BIGINT (rather than INTEGER) for unsuffixed integer literals;
        // both of those types are out of scope for the stage-1 driver.
        stmt.SqlQuery =
            "SELECT CAST(1 AS INTEGER) AS a, " +
            "CAST(2.5 AS DOUBLE) AS b, " +
            "'hello' AS c, " +
            "CAST(NULL AS BIGINT) AS d";

        QueryResult queryResult = stmt.ExecuteQuery();
        using IArrowArrayStream stream = queryResult.Stream!;
        Schema schema = stream.Schema;

        Assert.Equal(4, schema.FieldsList.Count);
        Assert.Equal("a", schema.FieldsList[0].Name);
        Assert.Equal("b", schema.FieldsList[1].Name);
        Assert.Equal("c", schema.FieldsList[2].Name);
        Assert.Equal("d", schema.FieldsList[3].Name);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(1, batch.Length);

        Int32Array aCol = Assert.IsType<Int32Array>(batch.Column(0));
        DoubleArray bCol = Assert.IsType<DoubleArray>(batch.Column(1));
        StringArray cCol = Assert.IsType<StringArray>(batch.Column(2));
        Int64Array dCol = Assert.IsType<Int64Array>(batch.Column(3));

        Assert.Equal(1, aCol.GetValue(0));
        Assert.Equal(2.5, bCol.GetValue(0));
        Assert.Equal("hello", cCol.GetString(0));
        Assert.True(dCol.IsNull(0));

        // End-of-stream: a second pull returns null.
        Assert.Null(await stream.ReadNextRecordBatchAsync());
    }

    [Fact]
    public async Task ExecuteUpdate_CreateAndInsert_RunsWithoutError()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        using AdbcConnection conn = db.Connect(options: null);

        string table = "t_adbc_" + Guid.NewGuid().ToString("N");

        using (AdbcStatement create = conn.CreateStatement())
        {
            create.SqlQuery = $"CREATE TABLE {table} (id INTEGER, name VARCHAR)";
            _ = create.ExecuteUpdate();
        }
        using (AdbcStatement insert = conn.CreateStatement())
        {
            insert.SqlQuery = $"INSERT INTO {table} VALUES (1, 'alpha'), (2, 'beta')";
            _ = insert.ExecuteUpdate();
        }

        using AdbcStatement select = conn.CreateStatement();
        select.SqlQuery = $"SELECT id, name FROM {table} ORDER BY id";
        QueryResult result = select.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(2, batch.Length);

        Int32Array ids = Assert.IsType<Int32Array>(batch.Column(0));
        StringArray names = Assert.IsType<StringArray>(batch.Column(1));
        Assert.Equal(1, ids.GetValue(0));
        Assert.Equal(2, ids.GetValue(1));
        Assert.Equal("alpha", names.GetString(0));
        Assert.Equal("beta", names.GetString(1));
    }
}

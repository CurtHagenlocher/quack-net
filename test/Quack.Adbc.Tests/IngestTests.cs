// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// Exercises the Bind + ExecuteUpdate ingest path: build an Arrow
// RecordBatch on the client, ingest it through the ADBC driver into a
// DuckDB table, then SELECT back via the same driver to verify both
// directions agree on the underlying types and values.
public class IngestTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public IngestTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task BindAndExecuteUpdate_AppendsPrimitiveColumns()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_ingest_" + Guid.NewGuid().ToString("N");
        await CreateTable(conn, table, "(i INTEGER, d DOUBLE, s VARCHAR, b BOOLEAN)");

        // Build a 3-row RecordBatch with one NULL each.
        Int32Array.Builder ib = new();
        ib.Append(1).AppendNull().Append(3);
        DoubleArray.Builder db = new();
        db.Append(1.5).Append(2.5).AppendNull();
        StringArray.Builder sb = new();
        sb.Append("alpha").Append("beta").Append("gamma");
        BooleanArray.Builder bb = new();
        bb.Append(true).Append(false).Append(true);

        Schema schema = new Schema.Builder()
            .Field(new Field("i", Int32Type.Default, nullable: true))
            .Field(new Field("d", DoubleType.Default, nullable: true))
            .Field(new Field("s", StringType.Default, nullable: true))
            .Field(new Field("b", BooleanType.Default, nullable: true))
            .Build();
        using RecordBatch batch = new(schema,
            [ib.Build(), db.Build(), sb.Build(), bb.Build()],
            length: 3);

        using AdbcStatement ingest = conn.CreateStatement();
        ingest.SqlQuery = table;
        ingest.Bind(batch, schema);
        UpdateResult ur = ingest.ExecuteUpdate();
        Assert.Equal(3L, ur.AffectedRows);

        // Read back.
        using AdbcStatement select = conn.CreateStatement();
        select.SqlQuery = $"SELECT i, d, s, b FROM {table} ORDER BY s";
        QueryResult qr = select.ExecuteQuery();
        using IArrowArrayStream stream = qr.Stream!;
        RecordBatch? back = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(back);

        Int32Array iCol = Assert.IsType<Int32Array>(back.Column(0));
        DoubleArray dCol = Assert.IsType<DoubleArray>(back.Column(1));
        StringArray sCol = Assert.IsType<StringArray>(back.Column(2));
        BooleanArray bCol = Assert.IsType<BooleanArray>(back.Column(3));

        // ORDER BY s -> alpha, beta, gamma.
        Assert.Equal(1, iCol.GetValue(0));
        Assert.Null(iCol.GetValue(1));
        Assert.Equal(3, iCol.GetValue(2));
        Assert.Equal(1.5, dCol.GetValue(0));
        Assert.Equal(2.5, dCol.GetValue(1));
        Assert.Null(dCol.GetValue(2));
        Assert.Equal("alpha", sCol.GetString(0));
        Assert.True(bCol.GetValue(0));
        Assert.False(bCol.GetValue(1));
        Assert.True(bCol.GetValue(2));
    }

    [Fact]
    public async Task BindAndExecuteUpdate_AppendsDecimal()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_ingest_dec_" + Guid.NewGuid().ToString("N");
        await CreateTable(conn, table, "(d DECIMAL(9, 2))");

        Decimal128Array.Builder dec = new(new Decimal128Type(9, 2));
        dec.Append("123.45");
        dec.Append("-67.89");

        Schema schema = new Schema.Builder()
            .Field(new Field("d", new Decimal128Type(9, 2), nullable: true))
            .Build();
        using RecordBatch batch = new(schema, [dec.Build()], length: 2);

        using AdbcStatement ingest = conn.CreateStatement();
        ingest.SqlQuery = table;
        ingest.Bind(batch, schema);
        Assert.Equal(2L, ingest.ExecuteUpdate().AffectedRows);

        using AdbcStatement select = conn.CreateStatement();
        select.SqlQuery = $"SELECT d FROM {table} ORDER BY d";
        QueryResult qr = select.ExecuteQuery();
        using IArrowArrayStream stream = qr.Stream!;
        RecordBatch? back = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(back);
        Decimal128Array col = Assert.IsType<Decimal128Array>(back.Column(0));
        Assert.Equal("-67.89", col.GetString(0));
        Assert.Equal("123.45", col.GetString(1));
    }

    [Fact]
    public async Task BindAndExecuteUpdate_AppendsList()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_ingest_list_" + Guid.NewGuid().ToString("N");
        await CreateTable(conn, table, "(xs INTEGER[])");

        // Build [[10,20,30], [40]].
        ListArray.Builder lb = new(Int32Type.Default);
        Int32Array.Builder ib = (Int32Array.Builder)lb.ValueBuilder;
        lb.Append();
        ib.Append(10).Append(20).Append(30);
        lb.Append();
        ib.Append(40);

        ListType listType = new(new Field("item", Int32Type.Default, nullable: true));
        Schema schema = new Schema.Builder()
            .Field(new Field("xs", listType, nullable: true))
            .Build();
        using RecordBatch batch = new(schema, [lb.Build()], length: 2);

        using AdbcStatement ingest = conn.CreateStatement();
        ingest.SqlQuery = table;
        ingest.Bind(batch, schema);
        Assert.Equal(2L, ingest.ExecuteUpdate().AffectedRows);

        using AdbcStatement select = conn.CreateStatement();
        select.SqlQuery = $"SELECT len(xs) AS n, xs FROM {table} ORDER BY n DESC";
        QueryResult qr = select.ExecuteQuery();
        using IArrowArrayStream stream = qr.Stream!;
        RecordBatch? back = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(back);
        Int64Array nCol = Assert.IsType<Int64Array>(back.Column(0));
        Assert.Equal(3L, nCol.GetValue(0));
        Assert.Equal(1L, nCol.GetValue(1));
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

    private static async Task CreateTable(AdbcConnection conn, string table, string columns)
    {
        using AdbcStatement create = conn.CreateStatement();
        create.SqlQuery = $"CREATE TABLE {table} {columns}";
        _ = create.ExecuteUpdate();
        await Task.CompletedTask;
    }
}

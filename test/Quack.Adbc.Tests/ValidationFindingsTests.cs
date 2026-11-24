// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// What the ADBC validation suite (adbc-drivers/quack) found: the NULL type,
// reusing a statement after an ingest, unknown options, GetInfo versions, and
// the Arrow types an ingest takes.
public class ValidationFindingsTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public ValidationFindingsTests(QuackServerFixture server) => _server = server;

    // DuckDB 2.0 stores a column of type NULL (one INT32 per row underneath).
    [Fact]
    public async Task NullType_ReadsAndIngests()
    {
        using AdbcConnection conn = Open();
        string t = "nt_" + Guid.NewGuid().ToString("N")[..8];
        Assert.IsType<NullArray>((await First(conn, "SELECT NULL AS a")).Column(0));
        Execute(conn, $"CREATE TABLE {t} AS SELECT NULL AS n, range AS i FROM range(3000)");
        int rows = 0;
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SqlQuery = $"SELECT n, i FROM {t}";
            using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
            while (await stream.ReadNextRecordBatchAsync() is RecordBatch b)
            {
                Assert.IsType<NullArray>(b.Column(0));
                rows += b.Length;
            }
        }
        Assert.Equal(3000, rows);

        Schema schema = new Schema.Builder().Field(new Field("n", NullType.Default, true)).Field(new Field("i", Int32Type.Default, true)).Build();
        Ingest(conn, t + "_in", new RecordBatch(schema, [new NullArray(3), new Int32Array.Builder().AppendRange([1, 2, 3]).Build()], 3));
        Assert.Equal("\"NULL\"|3|0", await Text(conn,
            $"SELECT (SELECT data_type FROM duckdb_columns() WHERE table_name = '{t}_in' AND column_name = 'n') || '|' || count(*) || '|' || count(n) FROM {t}_in"));
        Execute(conn, $"DROP TABLE {t}");
        Execute(conn, $"DROP TABLE {t}_in");
    }

    // Setting SqlQuery ends ingest mode on the statement.
    [Fact]
    public async Task Statement_QueriesAfterAnIngest()
    {
        using AdbcConnection conn = Open();
        string t = "sq_" + Guid.NewGuid().ToString("N")[..8];
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SetOption("adbc.ingest.target_table", t);
        Schema schema = new Schema.Builder().Field(new Field("i", Int32Type.Default, true)).Build();
        stmt.Bind(new RecordBatch(schema, [new Int32Array.Builder().AppendRange([1, 2]).Build()], 2), schema);
        stmt.ExecuteUpdate();
        stmt.SqlQuery = $"SELECT sum(i)::INTEGER AS s FROM {t}";
        using (IArrowArrayStream stream = stmt.ExecuteQuery().Stream!)
        {
            Assert.Equal(3, ((Int32Array)(await stream.ReadNextRecordBatchAsync())!.Column(0)).GetValue(0));
        }
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => stmt.GetOption("adbc.ingest.target_table")).Status);
        Execute(conn, $"DROP TABLE {t}");
    }

    // A temporary table is listed under DuckDB's temp catalog; without one, temp is not listed.
    [Fact]
    public async Task GetObjects_ListsTemporaryTables()
    {
        using AdbcConnection conn = Open();
        Assert.DoesNotContain("temp", await Catalogs(conn));
        string t = "gt_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TEMP TABLE {t} (i INTEGER)");
        Assert.Contains("temp", await Catalogs(conn));
        using (IArrowArrayStream stream = conn.GetObjects(AdbcConnection.GetObjectsDepth.Tables, "temp", "main", t, null, null))
        {
            RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
            StructArray schemas = (StructArray)((ListArray)batch.Column(1)).Values;
            StructArray tables = (StructArray)((ListArray)schemas.Fields[1]).Values;
            Assert.Equal(t, ((StringArray)tables.Fields[0]).GetString(0));
        }
        Execute(conn, $"DROP TABLE temp.main.{t}");
    }

    private static async Task<List<string>> Catalogs(AdbcConnection conn)
    {
        using IArrowArrayStream stream = conn.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, null, null, null, null, null);
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        StringArray names = (StringArray)batch.Column(0);
        return Enumerable.Range(0, names.Length).Select(i => names.GetString(i)).ToList();
    }

    [Fact]
    public void UnknownDatabaseOption_IsNotImplemented()
    {
        using AdbcDatabase db = new QuackAdbcDriver().Open(Options());
        Assert.Equal(AdbcStatusCode.NotImplemented, Assert.Throws<AdbcException>(() => db.SetOption("no_such_option", "x")).Status);
        db.SetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter, "5");
        Assert.Equal("5", db.GetOption(QuackAdbcDriver.CommandTimeoutSecondsParameter));
    }

    [Fact]
    public async Task GetInfo_VersionsHaveNoBuildMetadata()
    {
        using AdbcConnection conn = Open();
        using IArrowArrayStream stream = conn.GetInfo([AdbcInfoCode.DriverVersion, AdbcInfoCode.DriverArrowVersion]);
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        DenseUnionArray values = (DenseUnionArray)batch.Column(1);
        StringArray strings = (StringArray)values.Fields[0];
        string driver = strings.GetString(values.ValueOffsets[0]);
        string arrow = strings.GetString(values.ValueOffsets[1]);
        Assert.DoesNotContain("+", driver, StringComparison.Ordinal);
        Assert.StartsWith("v", arrow, StringComparison.Ordinal);
        Assert.DoesNotContain("+", arrow, StringComparison.Ordinal);
    }

    // Large and view strings / binary, Time32, dictionaries and negative decimal scales.
    [Fact]
    public async Task Ingest_TakesMoreArrowTypes()
    {
        using AdbcConnection conn = Open();
        string t = "ty_" + Guid.NewGuid().ToString("N")[..8];
        LargeStringArray largeString = new LargeStringArray.Builder().Append("ab").AppendNull().Append("cde").Build();
        StringViewArray viewString = new StringViewArray.Builder().Append("a string longer than twelve bytes").Append("x").AppendNull().Build();
        LargeBinaryArray largeBinary = new LargeBinaryArray.Builder().Append([1, 2]).Append([3]).AppendNull().Build();
        BinaryViewArray viewBinary = new BinaryViewArray.Builder().AppendNull().Append([9, 8, 7]).Append([]).Build();
        Time32Array seconds = new Time32Array.Builder(TimeUnit.Second).Append(3661).Append(0).AppendNull().Build();
        Time32Array millis = new Time32Array.Builder(TimeUnit.Millisecond).Append(1500).AppendNull().Append(86_399_999).Build();
        DictionaryArray dictionary = new(new DictionaryType(Int32Type.Default, StringType.Default, false),
            new Int32Array.Builder().Append(1).Append(0).Append(1).Build(), new StringArray.Builder().Append("red").Append("blue").Build());
        // Unscaled 123 and -4 at scale -2 (12300, -400); the builder takes no negative scale.
        byte[] unscaled = new byte[3 * 16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(unscaled.AsSpan(0, 16), 123);
        System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(unscaled.AsSpan(16, 16), -4);
        Decimal128Array negativeScale = new(new ArrayData(new Decimal128Type(5, -2), 3, 1, 0,
            [new ArrowBuffer(new byte[] { 0b011 }), new ArrowBuffer(unscaled)]));
        Schema schema = new Schema.Builder()
            .Field(new Field("ls", LargeStringType.Default, true))
            .Field(new Field("vs", StringViewType.Default, true))
            .Field(new Field("lb", LargeBinaryType.Default, true))
            .Field(new Field("vb", BinaryViewType.Default, true))
            .Field(new Field("ts", new Time32Type(TimeUnit.Second), true))
            .Field(new Field("tm", new Time32Type(TimeUnit.Millisecond), true))
            .Field(new Field("d", dictionary.Data.DataType, true))
            .Field(new Field("ns", new Decimal128Type(5, -2), true))
            .Build();
        Ingest(conn, t, new RecordBatch(schema, [largeString, viewString, largeBinary, viewBinary, seconds, millis, dictionary, negativeScale], 3));
        Assert.Equal("ls VARCHAR, vs VARCHAR, lb BLOB, vb BLOB, ts TIME, tm TIME, d VARCHAR, ns DECIMAL(7,0)", await Text(conn,
            $"SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY column_index) FROM duckdb_columns() WHERE table_name = '{t}'"));
        Assert.Equal("ab|a string longer than twelve bytes|0102|NULL|01:01:01|00:00:01.5|blue|12300", await Text(conn,
            $"SELECT concat_ws('|', ls, vs, hex(lb), coalesce(hex(vb), 'NULL'), ts, tm, d, ns) FROM {t} WHERE rowid = 0"));
        Assert.Equal("NULL|x|03|090807|00:00:00|NULL|red|-400", await Text(conn,
            $"SELECT concat_ws('|', coalesce(ls, 'NULL'), vs, hex(lb), hex(vb), ts, coalesce(tm::VARCHAR, 'NULL'), d, ns) FROM {t} WHERE rowid = 1"));
        Execute(conn, $"DROP TABLE {t}");
    }

    private static void Ingest(AdbcConnection conn, string table, RecordBatch batch)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SetOption("adbc.ingest.target_table", table);
        stmt.Bind(batch, batch.Schema);
        stmt.ExecuteUpdate();
    }

    private Dictionary<string, string> Options() => new()
    {
        [QuackAdbcDriver.UriParameter] = _server.QuackUri,
        [QuackAdbcDriver.TokenParameter] = _server.Token,
    };

    private AdbcConnection Open() => new QuackAdbcDriver().Open(Options()).Connect(options: null);

    private static void Execute(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        stmt.ExecuteUpdate();
    }

    private static async Task<RecordBatch> First(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        return (await stream.ReadNextRecordBatchAsync())!;
    }

    private static async Task<string> Text(AdbcConnection conn, string sql)
        => ((StringArray)(await First(conn, sql)).Column(0)).GetString(0);
}

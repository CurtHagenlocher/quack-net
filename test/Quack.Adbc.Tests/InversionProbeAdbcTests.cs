// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;
using Xunit.Abstractions;

namespace Quack.Adbc.Tests;

// Inversion pass over the protocol version 3 append path, through ADBC ingest.
public class InversionProbeAdbcTests : IClassFixture<QuackServerFixture>
{
    private const int Rows = 5000;

    private readonly QuackServerFixture _server;
    private readonly ITestOutputHelper _out;

    public InversionProbeAdbcTests(QuackServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _out = output;
    }

    // B: one batch of 5000 rows of nested types (cut at 2048 rows on the way)
    // must arrive exactly: list offsets, child validity and struct/array/map
    // children across the cut.
    [Fact]
    public async Task B_NestedTypesAcrossTheVectorCut_ArriveExactly()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_inv_nested_" + Guid.NewGuid().ToString("N");
        Execute(conn, $"CREATE TABLE {table} (id INTEGER, l VARCHAR[], st STRUCT(a INTEGER, b VARCHAR), fx INTEGER[2], m MAP(VARCHAR, BIGINT))");

        using RecordBatch batch = BuildNestedBatch(out Schema schema);
        using (AdbcStatement ingest = conn.CreateStatement())
        {
            ingest.SqlQuery = table;
            ingest.Bind(batch, schema);
            Assert.Equal(Rows, ingest.ExecuteUpdate().AffectedRows);
        }

        string expected = ExpectedSql(listNullEvery: 11);
        long missing = await CountAsync(conn, $"SELECT count(*)::BIGINT FROM (SELECT * FROM {table} EXCEPT ALL {expected})");
        long extra = await CountAsync(conn, $"SELECT count(*)::BIGINT FROM ({expected} EXCEPT ALL SELECT * FROM {table})");
        // Positive control: the comparison notices a difference.
        long control = await CountAsync(conn, $"SELECT count(*)::BIGINT FROM (SELECT * FROM {table} EXCEPT ALL {ExpectedSql(listNullEvery: 12)})");
        _out.WriteLine($"rows not expected: {missing}, expected rows missing: {extra}, control: {control}");
        foreach (string col in new[] { "l", "st", "fx", "m" })
        {
            long diff = await CountAsync(conn, $"SELECT count(*)::BIGINT FROM (SELECT id, {col} FROM {table} EXCEPT ALL SELECT id, {col} FROM ({expected}))");
            _out.WriteLine($"  column {col}: {diff} rows differ");
        }
        _out.WriteLine("  id 13: st IS NULL = " + await CountAsync(conn, $"SELECT (st IS NULL)::BIGINT FROM {table} WHERE id = 13")
            + ", st.a IS NULL = " + await CountAsync(conn, $"SELECT (st.a IS NULL)::BIGINT FROM {table} WHERE id = 13")
            + ", count(st) = " + await CountAsync(conn, $"SELECT count(st)::BIGINT FROM {table}"));
        Assert.Equal(0, missing);
        Assert.Equal(0, extra);
        Assert.True(control > 0);
    }

    // E: an Arrow struct field with an empty name (legal in Arrow).
    [Fact]
    public async Task E_StructWithAnEmptyFieldName()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_inv_empty_" + Guid.NewGuid().ToString("N");
        Execute(conn, $"CREATE TABLE {table} (st STRUCT(x INTEGER))");

        Int32Array.Builder ib = new();
        ib.Append(1);
        StructType structType = new([new Field("", Int32Type.Default, nullable: true)]);
        StructArray st = new(structType, 1, [ib.Build()], ArrowBuffer.Empty, 0);
        Schema schema = new Schema.Builder().Field(new Field("st", structType, nullable: true)).Build();
        using RecordBatch batch = new(schema, [st], 1);
        using AdbcStatement ingest = conn.CreateStatement();
        ingest.SqlQuery = table;
        ingest.Bind(batch, schema);
        try
        {
            _out.WriteLine("affected: " + ingest.ExecuteUpdate().AffectedRows);
        }
        catch (Exception ex)
        {
            _out.WriteLine("failed: " + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        // DuckDB reads an all-unnamed STRUCT as TUPLE; it lands positionally.
        Assert.Equal(1, await CountAsync(conn, $"SELECT st.x::BIGINT FROM {table}"));
    }

    // Found while reading the converter: a zoned Arrow timestamp is declared
    // TIMESTAMP_TZ (microseconds) whatever its unit, and its raw values copied.
    [Theory]
    [InlineData(TimeUnit.Nanosecond)]
    [InlineData(TimeUnit.Millisecond)]
    [InlineData(TimeUnit.Microsecond)]
    public async Task ZonedTimestampUnits_KeepTheirValue(TimeUnit unit)
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_inv_tz_" + Guid.NewGuid().ToString("N");
        Execute(conn, $"CREATE TABLE {table} (ts TIMESTAMPTZ)");

        DateTimeOffset value = new(2024, 1, 2, 3, 4, 5, 123, TimeSpan.Zero);
        long raw = unit switch
        {
            TimeUnit.Nanosecond => value.ToUnixTimeMilliseconds() * 1_000_000,
            TimeUnit.Millisecond => value.ToUnixTimeMilliseconds(),
            _ => value.ToUnixTimeMilliseconds() * 1_000,
        };
        TimestampType type = new(unit, "UTC");
        ArrowBuffer.Builder<long> values = new();
        values.Append(raw);
        TimestampArray ts = new(new ArrayData(type, 1, 0, 0, [ArrowBuffer.Empty, values.Build()]));
        Schema schema = new Schema.Builder().Field(new Field("ts", type, nullable: true)).Build();
        using RecordBatch batch = new(schema, [ts], 1);
        using (AdbcStatement ingest = conn.CreateStatement())
        {
            ingest.SqlQuery = table;
            ingest.Bind(batch, schema);
            try
            {
                ingest.ExecuteUpdate();
            }
            catch (Exception ex)
            {
                _out.WriteLine($"{unit}: ingest failed: {ex.Message}");
                throw;
            }
        }
        long epochMs = await CountAsync(conn, $"SELECT epoch_ms(ts)::BIGINT FROM {table}");
        _out.WriteLine($"{unit}: stored epoch_ms {epochMs}, sent {value.ToUnixTimeMilliseconds()}");
        Assert.Equal(value.ToUnixTimeMilliseconds(), epochMs);
    }

    private static string ExpectedSql(int listNullEvery) =>
        "SELECT i::INTEGER AS id, "
        + $"CASE WHEN i % {listNullEvery} = 0 THEN NULL ELSE list_transform(range(i % 4), lambda k: CASE WHEN k = 1 AND i % 5 = 0 THEN NULL ELSE 'v' || i || '_' || k END) END AS l, "
        + "CASE WHEN i % 13 = 0 THEN NULL ELSE {'a': i::INTEGER, 'b': CASE WHEN i % 3 = 0 THEN NULL ELSE 's' || i END} END AS st, "
        + "CASE WHEN i % 17 = 0 THEN NULL ELSE [i::INTEGER, (2 * i)::INTEGER]::INTEGER[2] END AS fx, "
        + "CASE WHEN i % 7 = 0 THEN NULL ELSE MAP {'k' || i: i::BIGINT} END AS m "
        + $"FROM range({Rows}) t(i)";

    private static RecordBatch BuildNestedBatch(out Schema schema)
    {
        Int32Array.Builder id = new();
        ListArray.Builder list = new(StringType.Default);
        StringArray.Builder listValues = (StringArray.Builder)list.ValueBuilder;
        Int32Array.Builder stA = new();
        StringArray.Builder stB = new();
        ArrowBuffer.BitmapBuilder stValid = new();
        Int32Array.Builder fxValues = new();
        ArrowBuffer.BitmapBuilder fxValid = new();
        MapArray.Builder map = new(new MapType(StringType.Default, Int64Type.Default));
        StringArray.Builder mapKeys = (StringArray.Builder)map.KeyBuilder;
        Int64Array.Builder mapValues = (Int64Array.Builder)map.ValueBuilder;
        int stNulls = 0;
        int fxNulls = 0;

        for (int i = 0; i < Rows; i++)
        {
            id.Append(i);

            if (i % 11 == 0)
            {
                list.AppendNull();
            }
            else
            {
                list.Append();
                for (int k = 0; k < i % 4; k++)
                {
                    if (k == 1 && i % 5 == 0) listValues.AppendNull();
                    else listValues.Append($"v{i}_{k}");
                }
            }

            bool stNull = i % 13 == 0;
            stValid.Append(!stNull);
            stNulls += stNull ? 1 : 0;
            stA.Append(i);
            if (i % 3 == 0) stB.AppendNull();
            else stB.Append($"s{i}");

            bool fxNull = i % 17 == 0;
            fxValid.Append(!fxNull);
            fxNulls += fxNull ? 1 : 0;
            fxValues.Append(i).Append(2 * i);

            if (i % 7 == 0)
            {
                map.AppendNull();
            }
            else
            {
                map.Append();
                mapKeys.Append($"k{i}");
                mapValues.Append(i);
            }
        }

        StructType stType = new([new Field("a", Int32Type.Default, true), new Field("b", StringType.Default, true)]);
        StructArray st = new(stType, Rows, [stA.Build(), stB.Build()], stValid.Build(), stNulls);
        FixedSizeListType fxType = new(new Field("item", Int32Type.Default, true), 2);
        FixedSizeListArray fx = new(fxType, Rows, fxValues.Build(), fxValid.Build(), fxNulls);
        MapArray m = map.Build();
        ListArray l = list.Build();

        schema = new Schema.Builder()
            .Field(new Field("id", Int32Type.Default, true))
            .Field(new Field("l", l.Data.DataType, true))
            .Field(new Field("st", stType, true))
            .Field(new Field("fx", fxType, true))
            .Field(new Field("m", m.Data.DataType, true))
            .Build();
        return new RecordBatch(schema, [id.Build(), l, st, fx, m], Rows);
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

    private static async Task<long> CountAsync(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        QueryResult qr = stmt.ExecuteQuery();
        using IArrowArrayStream stream = qr.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        return ((Int64Array)batch!.Column(0)).GetValue(0)!.Value;
    }
}

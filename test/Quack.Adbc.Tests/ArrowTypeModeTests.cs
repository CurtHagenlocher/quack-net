// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Arrays;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// The Arrow types of a result are DuckDB's own Arrow export's, measured
// against DuckDB 2.0.1: by default its default forms, with
// quack.arrow_lossless_conversion its arrow_lossless_conversion forms. And a
// lossless result ingests back to the same DuckDB types and values.
public class ArrowTypeModeTests : IClassFixture<QuackServerFixture>
{
    private const string Row =
        "SELECT '12:34:56.789+02:30'::TIMETZ AS ttz, " +
        "340282366920938463463374607431768211455::UHUGEINT AS uh, " +
        "(-170141183460469231731687303715884105728)::HUGEINT AS hh, " +
        "'00000000-0000-0000-0000-00000000002a'::UUID AS id, " +
        "'101'::BIT AS b, " +
        "'-12345678901234567890123456789012345678901234567890'::BIGNUM AS bn, " +
        "'POINT(1 2)'::GEOMETRY AS g, " +
        "['POINT(3 4)'::GEOMETRY] AS gl, " +
        "union_value(s := 'x')::UNION(i INTEGER, s VARCHAR) AS u, " +
        "true AS bo";

    // 12:34:56.789 in micros, and the offset +02:30 as DuckDB packs it.
    private const long Micros = 45_296_789_000L;
    private const long TimeTzBits = (Micros << 24) | (16 * 60 * 60 - 1 - 9000);

    private static readonly byte[] PointWkb = Convert.FromHexString("0101000000000000000000F03F0000000000000040");

    private readonly QuackServerFixture _server;

    public ArrowTypeModeTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task Default_IsDuckDbsArrowExport()
    {
        using AdbcConnection conn = Open(lossless: null);
        RecordBatch batch = await First(conn, Row);

        Time64Array ttz = Assert.IsType<Time64Array>(batch.Column("ttz"));
        Assert.Equal(Micros, ttz.GetValue(0));
        // UHUGEINT's bits as a Decimal128: the maximum reads as -1, as in DuckDB.
        Assert.Equal("-1", Assert.IsType<Decimal128Array>(batch.Column("uh")).GetString(0));
        Assert.Equal("-170141183460469231731687303715884105728", Assert.IsType<Decimal128Array>(batch.Column("hh")).GetString(0));
        Assert.Equal("00000000-0000-0000-0000-00000000002a", Assert.IsType<StringArray>(batch.Column("id")).GetString(0));
        Assert.Equal(new byte[] { 0x05, 0xFD }, Assert.IsType<BinaryArray>(batch.Column("b")).GetBytes(0).ToArray());
        Assert.Equal(PointWkb, Assert.IsType<BinaryArray>(batch.Column("g")).GetBytes(0).ToArray());

        foreach (string plain in new[] { "ttz", "uh", "hh", "id", "b", "bo" })
        {
            Assert.False(batch.Schema.GetFieldByName(plain).HasMetadata, plain);
        }
        AssertOpaque(batch.Schema.GetFieldByName("bn"), "bignum");
        AssertGeometry(batch.Schema.GetFieldByName("g"));
        AssertGeometry(((ListType)batch.Schema.GetFieldByName("gl").DataType).ValueField);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lossless_IsDuckDbsLosslessExport(bool asDatabaseOption)
    {
        using AdbcConnection conn = Open(lossless: asDatabaseOption ? "true" : null);
        if (!asDatabaseOption)
        {
            Assert.Equal("false", conn.GetOption(QuackAdbcDriver.ArrowLosslessConversionParameter));
            conn.SetOption(QuackAdbcDriver.ArrowLosslessConversionParameter, "true");
        }
        Assert.Equal("true", conn.GetOption(QuackAdbcDriver.ArrowLosslessConversionParameter));
        RecordBatch batch = await First(conn, Row);

        FixedSizeBinaryArray ttz = Assert.IsType<FixedSizeBinaryArray>(batch.Column("ttz"));
        Assert.Equal(TimeTzBits, BitConverter.ToInt64(ttz.GetBytes(0)));
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 16), Assert.IsType<FixedSizeBinaryArray>(batch.Column("uh")).GetBytes(0).ToArray());
        byte[] minHuge = new byte[16];
        minHuge[15] = 0x80;
        Assert.Equal(minHuge, Assert.IsType<FixedSizeBinaryArray>(batch.Column("hh")).GetBytes(0).ToArray());
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-00000000002a"), Assert.IsType<GuidArray>(batch.Column("id")).GetGuid(0));
        Assert.Equal(new byte[] { 0x05, 0xFD }, Assert.IsType<BinaryArray>(batch.Column("b")).GetBytes(0).ToArray());

        AssertOpaque(batch.Schema.GetFieldByName("ttz"), "time_tz");
        AssertOpaque(batch.Schema.GetFieldByName("uh"), "uhugeint");
        AssertOpaque(batch.Schema.GetFieldByName("hh"), "hugeint");
        AssertOpaque(batch.Schema.GetFieldByName("b"), "bit");
        AssertOpaque(batch.Schema.GetFieldByName("bn"), "bignum");
        AssertGeometry(batch.Schema.GetFieldByName("g"));
        Assert.Equal((sbyte)1, Assert.IsType<Int8Array>(batch.Column("bo")).GetValue(0));
        Assert.Equal("arrow.bool8", batch.Schema.GetFieldByName("bo").Metadata[ArrowConverter.ExtensionNameKey]);
    }

    [Fact]
    public void Lossless_RejectsOtherValues()
    {
        using AdbcConnection conn = Open(lossless: null);
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.Throws<AdbcException>(
            () => conn.SetOption(QuackAdbcDriver.ArrowLosslessConversionParameter, "yes")).Status);
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.Throws<AdbcException>(() => Open(lossless: "1")).Status);
    }

    // A sparse union, type ids = member indexes; a NULL row is type 0 with member 0 null.
    [Fact]
    public async Task Union_IsASparseUnion()
    {
        using AdbcConnection conn = Open(lossless: null);
        RecordBatch batch = await First(conn,
            "SELECT u FROM (VALUES (1, union_value(i := 7)::UNION(i INTEGER, s VARCHAR)), " +
            "(2, NULL::UNION(i INTEGER, s VARCHAR)), (3, union_value(s := 'x')::UNION(i INTEGER, s VARCHAR))) t(k, u) ORDER BY k");
        SparseUnionArray u = Assert.IsType<SparseUnionArray>(batch.Column(0));
        UnionType type = (UnionType)u.Data.DataType;
        Assert.Equal(["i", "s"], type.Fields.Select(f => f.Name));
        Assert.Equal([0, 1], type.TypeIds);
        Assert.Equal(new byte[] { 0, 0, 1 }, u.TypeIds.ToArray());
        Assert.Equal(7, ((Int32Array)u.Fields[0]).GetValue(0));
        Assert.True(u.Fields[0].IsNull(1));
        Assert.Equal("x", ((StringArray)u.Fields[1]).GetString(2));
    }

    // What the lossless mode reads, an ingest writes back: the same types and values.
    [Fact]
    public async Task LosslessResult_IngestsBack()
    {
        using AdbcConnection conn = Open(lossless: "true");
        string t = "lr_" + Guid.NewGuid().ToString("N")[..8];
        RecordBatch batch = await First(conn,
            $"SELECT * FROM ({Row} UNION ALL SELECT NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL) ORDER BY ttz NULLS LAST");
        Assert.Equal(2, batch.Length);
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SetOption("adbc.ingest.target_table", t);
            stmt.Bind(batch, batch.Schema);
            stmt.ExecuteUpdate();
        }
        Assert.Equal(
            "ttz TIME WITH TIME ZONE, uh UHUGEINT, hh HUGEINT, id UUID, b BIT, bn BIGNUM, g GEOMETRY, gl GEOMETRY[], u UNION(i INTEGER, s VARCHAR), bo BOOLEAN",
            await Text(conn, $"SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY column_index) FROM duckdb_columns() WHERE table_name = '{t}'"));
        const string Values = "concat_ws('|', ttz, uh, hh, id, b, bn, g, gl, u, bo)";
        string expected = await Text(conn, $"SELECT {Values} FROM ({Row})");
        Assert.Equal(expected, await Text(conn, $"SELECT {Values} FROM {t} WHERE ttz IS NOT NULL"));
        Assert.Equal("1|1|1|1|1|1|1|1|1|1", await Text(conn, $"SELECT concat_ws('|', count(*) - count(ttz), count(*) - count(uh), count(*) - count(hh), count(*) - count(id), count(*) - count(b), count(*) - count(bn), count(*) - count(g), count(*) - count(gl), count(*) - count(u), count(*) - count(bo)) FROM {t}"));
        Execute(conn, $"DROP TABLE {t}");
    }

    // Imported over the C interface, arrow.uuid is a fixed-size binary with the
    // extension name as field metadata; it still ingests as a UUID.
    [Fact]
    public async Task UuidAsFieldMetadata_IngestsAsUuid()
    {
        using AdbcConnection conn = Open(lossless: null);
        string t = "um_" + Guid.NewGuid().ToString("N")[..8];
        byte[] rfc = Convert.FromHexString("0000000000000000000000000000002A");
        FixedSizeBinaryArray ids = new(new ArrayData(new FixedSizeBinaryType(16), 2, 1, 0,
            [new ArrowBuffer(new byte[] { 0b01 }), new ArrowBuffer(rfc.Concat(new byte[16]).ToArray())]));
        Field field = new("id", new FixedSizeBinaryType(16), true,
            new Dictionary<string, string> { [ArrowConverter.ExtensionNameKey] = "arrow.uuid", [ArrowConverter.ExtensionMetadataKey] = "" });
        Schema schema = new Schema.Builder().Field(field).Build();
        using (AdbcStatement stmt = conn.CreateStatement())
        {
            stmt.SetOption("adbc.ingest.target_table", t);
            stmt.Bind(new RecordBatch(schema, [ids], 2), schema);
            stmt.ExecuteUpdate();
        }
        Assert.Equal("UUID|00000000-0000-0000-0000-00000000002a|1", await Text(conn,
            $"SELECT any_value(typeof(id)) || '|' || min(id) || '|' || (count(*) - count(id)) FROM {t}"));
        Execute(conn, $"DROP TABLE {t}");
    }

    private static void AssertOpaque(Field field, string typeName)
    {
        Assert.Equal("arrow.opaque", field.Metadata[ArrowConverter.ExtensionNameKey]);
        Assert.Equal($"{{\"type_name\":\"{typeName}\",\"vendor_name\":\"DuckDB\"}}", field.Metadata[ArrowConverter.ExtensionMetadataKey]);
    }

    private static void AssertGeometry(Field field)
    {
        Assert.Equal("geoarrow.wkb", field.Metadata[ArrowConverter.ExtensionNameKey]);
        Assert.Equal("{}", field.Metadata[ArrowConverter.ExtensionMetadataKey]);
    }

    private AdbcConnection Open(string? lossless)
    {
        Dictionary<string, string> options = new()
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        };
        if (lossless is not null)
        {
            options[QuackAdbcDriver.ArrowLosslessConversionParameter] = lossless;
        }
        return new QuackAdbcDriver().Open(options).Connect(options: null);
    }

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

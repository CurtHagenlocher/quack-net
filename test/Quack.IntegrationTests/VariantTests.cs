// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack;
using Quack.Data;
using Xunit.Abstractions;

namespace Quack.IntegrationTests;

// VARIANT columns, unshredded (the canonical STRUCT layout) and shredded
// (VectorType SHREDDED: typed columns plus an untyped overlay). DuckDB emits
// shredded vectors when it casts a column to VARIANT and when it scans a
// VARIANT column that a checkpoint shredded (row groups >= 30000 rows).
public class VariantTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;
    private readonly ITestOutputHelper _out;

    public VariantTests(QuackServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _out = output;
    }

    public static IEnumerable<object[]> Primitives()
    {
        static object[] Row(string sql, DuckDbVariantType type, object value) => [sql, type, value];
        int Days(int y, int m, int d) => new DateOnly(y, m, d).DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        yield return Row("true", DuckDbVariantType.BoolTrue, true);
        yield return Row("false", DuckDbVariantType.BoolFalse, false);
        yield return Row("(-7)::TINYINT", DuckDbVariantType.Int8, (sbyte)-7);
        yield return Row("(-300)::SMALLINT", DuckDbVariantType.Int16, (short)-300);
        yield return Row("(-70000)::INTEGER", DuckDbVariantType.Int32, -70000);
        yield return Row("(-5000000000)::BIGINT", DuckDbVariantType.Int64, -5000000000L);
        yield return Row("(-170141183460469231731687303715884105727)::HUGEINT", DuckDbVariantType.Int128, -Int128.MaxValue);
        yield return Row("200::UTINYINT", DuckDbVariantType.UInt8, (byte)200);
        yield return Row("60000::USMALLINT", DuckDbVariantType.UInt16, (ushort)60000);
        yield return Row("4000000000::UINTEGER", DuckDbVariantType.UInt32, 4000000000u);
        yield return Row("18000000000000000000::UBIGINT", DuckDbVariantType.UInt64, 18000000000000000000ul);
        yield return Row("340282366920938463463374607431768211455::UHUGEINT", DuckDbVariantType.UInt128, UInt128.MaxValue);
        yield return Row("1.5::FLOAT", DuckDbVariantType.Float, 1.5f);
        yield return Row("2.25::DOUBLE", DuckDbVariantType.Double, 2.25);
        yield return Row("12.34::DECIMAL(4,2)", DuckDbVariantType.Decimal, new DuckDbVariantDecimal(1234, 4, 2));
        yield return Row("(-123456.789)::DECIMAL(9,3)", DuckDbVariantType.Decimal, new DuckDbVariantDecimal(-123456789, 9, 3));
        yield return Row("1234567890123.4567::DECIMAL(18,4)", DuckDbVariantType.Decimal, new DuckDbVariantDecimal(12345678901234567, 18, 4));
        yield return Row("12345678901234567890123.45678::DECIMAL(30,5)", DuckDbVariantType.Decimal,
            new DuckDbVariantDecimal(Int128.Parse("1234567890123456789012345678"), 30, 5));
        yield return Row("'hi'", DuckDbVariantType.Varchar, "hi");
        yield return Row("repeat('ü', 100)", DuckDbVariantType.Varchar, new string('ü', 100));
        yield return Row("''", DuckDbVariantType.Varchar, "");
        yield return Row("'\\x00\\xFF'::BLOB", DuckDbVariantType.Blob, new byte[] { 0x00, 0xFF });
        yield return Row("'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'::UUID", DuckDbVariantType.Uuid, Guid.Parse("a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11"));
        yield return Row("DATE '2024-01-02'", DuckDbVariantType.Date, Days(2024, 1, 2));
        yield return Row("DATE '1900-03-04'", DuckDbVariantType.Date, Days(1900, 3, 4));
        yield return Row("TIME '01:02:03.456789'", DuckDbVariantType.TimeMicros, 3723456789L);
        yield return Row("'01:02:03.456789123'::TIME_NS", DuckDbVariantType.TimeNanos, 3723456789123L);
        yield return Row("'2024-01-02 03:04:05'::TIMESTAMP_S", DuckDbVariantType.TimestampSec, 1704164645L);
        yield return Row("'2024-01-02 03:04:05.123'::TIMESTAMP_MS", DuckDbVariantType.TimestampMillis, 1704164645123L);
        yield return Row("TIMESTAMP '2024-01-02 03:04:05.123456'", DuckDbVariantType.TimestampMicros, 1704164645123456L);
        yield return Row("'2024-01-02 03:04:05.123456789'::TIMESTAMP_NS", DuckDbVariantType.TimestampNanos, 1704164645123456789L);
        yield return Row("'2024-01-02 03:04:05.123456+00'::TIMESTAMPTZ", DuckDbVariantType.TimestampMicrosTz, 1704164645123456L);
        yield return Row("'2024-01-02 03:04:05.123456789+00'::TIMESTAMPTZ_NS", DuckDbVariantType.TimestampNanosTz, 1704164645123456789L);
        yield return Row("INTERVAL '1 month 2 days 3 seconds'", DuckDbVariantType.Interval, new DuckDbInterval(1, 2, 3_000_000));
    }

    // Each primitive as a constant (unshredded) and as a cast column (shredded).
    // The cast column is 2048 rows: the quack server copies chunks under half a
    // vector into a staging chunk, and that copy unshreds them.
    [Theory]
    [MemberData(nameof(Primitives))]
    public async Task Primitive_UnshreddedAndShredded(string sql, DuckDbVariantType type, object? expected)
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        DuckDbVariant constant = Assert.Single(await Values(conn, $"SELECT ({sql})::VARIANT"))!;
        AssertValue(type, expected, constant);

        // A table column, not an expression: the optimizer folds a cast of a constant.
        string table = "vprim_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} AS SELECT ({sql}) AS x FROM range(2048)");
        long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
        IReadOnlyList<DuckDbVariant?> column = await Values(conn, $"SELECT x::VARIANT FROM {table}");
        long shredded = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) - before;
        await Drain(conn, $"DROP TABLE {table}");
        _out.WriteLine($"{sql}: shredded vectors {shredded}");
        Assert.True(shredded > 0, "the cast column did not arrive shredded");
        Assert.Equal(2048, column.Count);
        foreach (DuckDbVariant? v in column)
        {
            AssertValue(type, expected, v!);
        }
    }

    // Types without a simple expected value: both paths must agree.
    [Theory]
    [InlineData("TIMETZ '01:02:03.456+02'", DuckDbVariantType.TimeMicrosTz)]
    [InlineData("123456789012345678901234567890123456789012345::BIGNUM", DuckDbVariantType.BigNum)]
    [InlineData("'10110'::BIT", DuckDbVariantType.BitString)]
    [InlineData("'POINT (1 2)'::GEOMETRY", DuckDbVariantType.Geometry)]
    public async Task OtherTypes_UnshreddedEqualsShredded(string sql, DuckDbVariantType type)
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        DuckDbVariant constant = Assert.Single(await Values(conn, $"SELECT ({sql})::VARIANT"))!;
        Assert.Equal(type, constant.Type);
        string table = "voth_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} AS SELECT ({sql}) AS x FROM range(2048)");
        long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
        IReadOnlyList<DuckDbVariant?> column = await Values(conn, $"SELECT x::VARIANT FROM {table}");
        long shredded = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) - before;
        await Drain(conn, $"DROP TABLE {table}");
        _out.WriteLine($"{sql}: {constant}, shredded vectors {shredded}");
        Assert.All(column, v => Assert.Equal(constant, v));
    }

    [Fact]
    public async Task Primitives_CastColumns_ArriveShredded()
    {
        // Positive control for the theory above: the column casts really took the shredded path.
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
        _ = await Values(conn, "SELECT (r * 2)::VARIANT FROM range(2048) t(r)");
        Assert.True(Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) > before);
    }

    [Fact]
    public async Task SqlNull_VariantNull_AndNesting()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        IReadOnlyList<DuckDbVariant?> values = await Values(conn,
            "SELECT v FROM (VALUES (1, NULL::VARIANT), (2, {'k': NULL::VARIANT, 'l': [1::VARIANT, 'x'::VARIANT, NULL::VARIANT], 'o': {'p': 2.5}}::VARIANT)) t(i, v) ORDER BY i");
        Assert.Null(values[0]);
        DuckDbVariant obj = values[1]!;
        Assert.Equal(DuckDbVariantType.Object, obj.Type);
        Dictionary<string, DuckDbVariant> fields = obj.Fields.ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal(DuckDbVariantType.Null, fields["k"].Type);
        Assert.Equal([DuckDbVariantType.Int32, DuckDbVariantType.Varchar, DuckDbVariantType.Null], fields["l"].Elements.Select(e => e.Type));
        Assert.Equal(DuckDbVariantType.Decimal, Assert.Single(fields["o"].Fields).Value.Type);
    }

    // The same irregular data read shredded (checkpointed, 40000 rows) and
    // unshredded (an in-memory table): missing fields, type changes, leftover
    // fields, nested arrays, VARIANT nulls and SQL NULL rows.
    [Fact]
    public async Task ShreddedTable_MatchesUnshreddedTable()
    {
        const string data =
            "SELECT i, CASE i % 7 " +
            "WHEN 0 THEN NULL " +
            "WHEN 1 THEN ('s' || i)::VARIANT " +
            "WHEN 2 THEN {'a': 'text' || i, 'b': i}::VARIANT " +
            "WHEN 3 THEN {'b': i, 'z': [1::VARIANT, 'x'::VARIANT, NULL::VARIANT]}::VARIANT " +
            "WHEN 4 THEN {'a': i, 'b': NULL::VARIANT, 'n': {'d': i * 1.5, 'e': [i, i + 1]}}::VARIANT " +
            // An empty key is never shredded, so it travels as a leftover (overlay) field.
            "WHEN 5 THEN ('{\"a\": ' || i || ', \"\": \"empty' || i || '\", \"b\": \"y\"}')::JSON::VARIANT " +
            "ELSE {'a': i, 'b': 'x' || i, 'c': [i, i + 1], 'n': {'d': i * 1.5}}::VARIANT END AS v " +
            "FROM range(40000) t(i)";

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string path = Path.Combine(Path.GetTempPath(), "quack_variant_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
        string alias = "vdb_" + Guid.NewGuid().ToString("N")[..8];
        string reference = "vref_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"ATTACH '{path}' AS {alias}");
        try
        {
            await Drain(conn, $"CREATE TABLE {alias}.v AS {data}");
            await Drain(conn, $"CHECKPOINT {alias}");
            await Drain(conn, $"CREATE TABLE {reference} AS {data}");

            long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
            // No ORDER BY: a sort materializes the rows, which unshreds them.
            IReadOnlyList<DuckDbVariant?> shredded = await ValuesById(conn, $"SELECT i, v FROM {alias}.v");
            long shreddedVectors = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) - before;
            before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
            IReadOnlyList<DuckDbVariant?> unshredded = await ValuesById(conn, $"SELECT i, v FROM {reference}");
            long referenceShredded = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) - before;
            _out.WriteLine($"shredded vectors: checkpointed {shreddedVectors}, reference {referenceShredded}");

            Assert.True(shreddedVectors > 0, "the checkpointed scan did not arrive shredded");
            Assert.Equal(0, referenceShredded);
            Assert.Equal(unshredded.Count, shredded.Count);
            int orderDiffers = 0;
            for (int i = 0; i < shredded.Count; i++)
            {
                Assert.True(Equals(Normalize(unshredded[i]), Normalize(shredded[i])), $"row {i}: {unshredded[i]} vs {shredded[i]}");
                orderDiffers += Equals(unshredded[i], shredded[i]) ? 0 : 1;
            }
            _out.WriteLine($"rows whose object field order differs: {orderDiffers}");
        }
        finally
        {
            await Drain(conn, $"DROP TABLE IF EXISTS {reference}");
            await Drain(conn, $"DETACH {alias}");
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DictionaryAndConstantVariants_AreExpanded()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        IReadOnlyList<DuckDbVariant?> constant = await Values(conn, "SELECT {'a': [1, 2]}::VARIANT FROM range(5000)");
        Assert.Equal(5000, constant.Count);
        Assert.All(constant, v => Assert.Equal("{a: [1, 2]}", v!.ToString()));
    }

    private static void AssertValue(DuckDbVariantType type, object? expected, DuckDbVariant actual)
    {
        Assert.Equal(type, actual.Type);
        if (expected is byte[] bytes)
        {
            Assert.Equal(bytes, (byte[])actual.Value!);
        }
        else
        {
            Assert.Equal(expected, actual.Value);
        }
    }

    // Object fields sorted by key, recursively, so field order does not count.
    private static DuckDbVariant? Normalize(DuckDbVariant? v) => v?.Type switch
    {
        null => null,
        DuckDbVariantType.Object => new DuckDbVariant(DuckDbVariantType.Object,
            v.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new KeyValuePair<string, DuckDbVariant>(f.Key, Normalize(f.Value)!)).ToList()),
        DuckDbVariantType.Array => new DuckDbVariant(DuckDbVariantType.Array, v.Elements.Select(e => Normalize(e)!).ToList()),
        _ => v,
    };

    private static async Task<IReadOnlyList<DuckDbVariant?>> Values(QuackConnection conn, string sql)
    {
        List<DuckDbVariant?> values = [];
        foreach (DuckDbChunk chunk in await (await conn.ExecuteAsync(sql)).ToListAsync())
        {
            if (chunk.RowCount > 0)
            {
                values.AddRange(Assert.IsType<VariantColumn>(chunk.Columns[0]).Values);
            }
        }
        return values;
    }

    // Rows of (BIGINT i, VARIANT v), returned in i order.
    private static async Task<IReadOnlyList<DuckDbVariant?>> ValuesById(QuackConnection conn, string sql)
    {
        SortedDictionary<long, DuckDbVariant?> byId = [];
        foreach (DuckDbChunk chunk in await (await conn.ExecuteAsync(sql)).ToListAsync())
        {
            FixedSizeColumn ids = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
            VariantColumn values = Assert.IsType<VariantColumn>(chunk.Columns[1]);
            for (int r = 0; r < chunk.RowCount; r++)
            {
                byId[System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(ids.GetBytes(r))] = values.Values[r];
            }
        }
        return byId.Values.ToList();
    }

    private static async Task Drain(QuackConnection conn, string sql) => _ = await (await conn.ExecuteAsync(sql)).ToListAsync();
}

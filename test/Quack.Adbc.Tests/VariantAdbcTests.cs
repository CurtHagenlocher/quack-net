// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Scalars.Variant;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// VARIANT through ADBC: arrow.parquet.variant with duckdb's type mapping.
public class VariantAdbcTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public VariantAdbcTests(QuackServerFixture server) => _server = server;

    public static IEnumerable<object[]> Mapped()
    {
        yield return ["true", VariantValue.True];
        yield return ["(-7)::TINYINT", VariantValue.FromInt8(-7)];
        yield return ["(-300)::SMALLINT", VariantValue.FromInt16(-300)];
        yield return ["(-70000)::INTEGER", VariantValue.FromInt32(-70000)];
        yield return ["(-5000000000)::BIGINT", VariantValue.FromInt64(-5000000000)];
        // Unsigned and 128-bit integers widen, as duckdb widens them.
        yield return ["200::UTINYINT", VariantValue.FromInt16(200)];
        yield return ["60000::USMALLINT", VariantValue.FromInt32(60000)];
        yield return ["4000000000::UINTEGER", VariantValue.FromInt64(4000000000)];
        yield return ["9000000000000000000::UBIGINT", VariantValue.FromInt64(9000000000000000000)];
        yield return ["(-12)::HUGEINT", VariantValue.FromInt64(-12)];
        yield return ["1.5::FLOAT", VariantValue.FromFloat(1.5f)];
        yield return ["2.25::DOUBLE", VariantValue.FromDouble(2.25)];
        yield return ["12.34::DECIMAL(4,2)", VariantValue.FromDecimal4(12.34m)];
        yield return ["(-1234567.89)::DECIMAL(12,2)", VariantValue.FromDecimal8(-1234567.89m)];
        yield return ["12345678901234567890123.45678::DECIMAL(30,5)",
            VariantValue.FromDecimal16(SqlDecimal.Parse("12345678901234567890123.45678"))];
        yield return ["'hi'", VariantValue.FromString("hi")];
        yield return ["'\\x00\\xFF'::BLOB", VariantValue.FromBinary([0x00, 0xFF])];
        yield return ["'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'::UUID", VariantValue.FromUuid(Guid.Parse("a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11"))];
        yield return ["DATE '2024-01-02'", VariantValue.FromDate(19724)];
        yield return ["TIME '01:02:03.456789'", VariantValue.FromTimeNtz(3723456789)];
        yield return ["TIMESTAMP '2024-01-02 03:04:05.123456'", VariantValue.FromTimestampNtz(1704164645123456)];
        yield return ["'2024-01-02 03:04:05.123456789'::TIMESTAMP_NS", VariantValue.FromTimestampNtzNanos(1704164645123456789)];
        yield return ["'2024-01-02 03:04:05.123456+00'::TIMESTAMPTZ", VariantValue.FromTimestamp(1704164645123456)];
    }

    [Theory]
    [MemberData(nameof(Mapped))]
    public async Task Primitives_MapLikeDuckDb(string sql, VariantValue expected)
    {
        using AdbcConnection conn = OpenConnection();
        // Constant (unshredded) and a 2048-row table column cast (shredded on the wire).
        VariantArray constant = await QueryVariant(conn, $"SELECT ({sql})::VARIANT AS v");
        Assert.Equal(expected, constant.GetVariantValue(0));

        string table = "va_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TABLE {table} AS SELECT ({sql}) AS x FROM range(2048)");
        VariantArray column = await QueryVariant(conn, $"SELECT x::VARIANT AS v FROM {table}");
        Execute(conn, $"DROP TABLE {table}");
        Assert.Equal(expected, column.GetVariantValue(column.Length - 1));
    }

    [Fact]
    public async Task Schema_Nulls_AndNesting()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT v FROM (VALUES (1, NULL::VARIANT), (2, {'k': NULL::VARIANT, 'l': [1::VARIANT, 'x'::VARIANT], 'o': {'p': 2.5}}::VARIANT)) t(i, v) ORDER BY i";
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        Assert.Equal("arrow.parquet.variant", Assert.IsType<VariantType>(stream.Schema.GetFieldByName("v").DataType).Name);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        VariantArray array = Assert.IsType<VariantArray>(batch!.Column(0));
        Assert.True(array.IsNull(0));
        VariantValue obj = array.GetVariantValue(1);
        IReadOnlyDictionary<string, VariantValue> fields = obj.AsObject();
        Assert.True(fields["k"].IsNull);
        Assert.Equal([VariantValue.FromInt32(1), VariantValue.FromString("x")], fields["l"].AsArray());
        Assert.Equal(VariantValue.FromDecimal4(2.5m), fields["o"].AsObject()["p"]);
    }

    [Theory]
    [InlineData("INTERVAL '1 day'")]
    [InlineData("'10110'::BIT")]
    [InlineData("TIMETZ '01:02:03+02'")]
    [InlineData("'2024-01-02 03:04:05'::TIMESTAMP_S")]
    [InlineData("340282366920938463463374607431768211455::UHUGEINT")]
    public async Task Unrepresentable_IsRefused(string sql)
    {
        using AdbcConnection conn = OpenConnection();
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => QueryVariant(conn, $"SELECT ({sql})::VARIANT AS v"));
        Assert.True(ex is NotSupportedException or OverflowException, ex.ToString());
    }

    private static async Task<VariantArray> QueryVariant(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        VariantArray? last = null;
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            last = Assert.IsType<VariantArray>(batch.Column(0));
        }
        return last!;
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

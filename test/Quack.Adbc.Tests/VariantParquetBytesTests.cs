// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Scalars.Variant;
using Apache.Arrow.Types;
using Quack.Adbc;
using Quack.Data;

namespace Quack.Adbc.Tests;

// The arrow.parquet.variant bytes against duckdb's own encoder
// (variant_to_parquet_variant, the function DuckDB's Arrow export uses).
public class VariantParquetBytesTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public VariantParquetBytesTests(QuackServerFixture server) => _server = server;

    public static readonly string[] Values =
    [
        "true", "false", "'null'::JSON",
        "(-7)::TINYINT", "(-300)::SMALLINT", "(-70000)::INTEGER", "(-5000000000)::BIGINT",
        "200::UTINYINT", "60000::USMALLINT", "4000000000::UINTEGER", "9000000000000000000::UBIGINT",
        "(-12)::HUGEINT", "12::UHUGEINT",
        "1.5::FLOAT", "2.25::DOUBLE",
        "12.34::DECIMAL(4,2)", "(-1234.5678)::DECIMAL(9,4)", "(-1234567.89)::DECIMAL(12,2)",
        "12345678901234567890123.45678::DECIMAL(30,5)", "(-1.5)::DECIMAL(38,10)",
        "''", "'hi ü'", "repeat('y', 63)", "repeat('y', 64)",
        "''::BLOB", "'\\x00\\xFF'::BLOB",
        "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'::UUID",
        "DATE '2024-01-02'", "TIME '01:02:03.456789'",
        "TIMESTAMP '2024-01-02 03:04:05.123456'", "'2024-01-02 03:04:05.123456789'::TIMESTAMP_NS",
        "'2024-01-02 03:04:05.123456+00'::TIMESTAMPTZ",
        "'{}'::JSON", "'[]'::JSON",
        "{'b': 1, 'a': 2}",
        // Keys in UTF-8 byte order, case-sensitive (a STRUCT literal's are not).
        "'{\"z\": 1, \"ü\": 2, \"é\": 3, \"Z\": 4}'::JSON",
        "[1::VARIANT, 'x'::VARIANT, NULL::VARIANT]",
        "'{\"a\": {\"b\": [{\"c\": 1}, {\"c\": [2, 3]}]}, \"d\": null}'::JSON",
        "{'a': {'b': [{'c': 1::VARIANT}, {'c': [2, 3]::VARIANT}]}, 'd': NULL::VARIANT}",
        // Field offsets of 2 and 3 bytes beside 1-byte field ids (the object
        // header Apache.Arrow 23.0.0 gets wrong).
        "{'k': repeat('x', 300), 'n': 1}",
        "{'k': repeat('x', 70000), 'n': [1, 2]}",
        // More than 255 elements: a 4-byte count, and 2-byte field ids.
        "range(300)",
        "(" + Json300Keys() + ")::JSON",
        // More than 16 fields out of key order (the sort for larger objects).
        "(" + JsonDescendingKeys(20) + ")::JSON",
    ];

    private static string JsonDescendingKeys(int count)
        => "'{" + string.Join(",", Enumerable.Range(0, count).Reverse().Select(i => $"\"k{i:D2}\":{i}")) + "}'";

    private static string Json300Keys()
        => "'{" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"k{i:D3}\":{i}")) + "}'";

    [Fact]
    public async Task Bytes_AreDuckDbs()
    {
        using AdbcConnection conn = OpenConnection();
        List<Row> rows = await Query(conn, Union(Values));
        Assert.Equal(Values.Length, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].OursIsNull)
            {
                // A root VARIANT_NULL is a SQL NULL in duckdb ('null'::JSON::VARIANT IS NULL).
                Assert.Equal("'null'::JSON", Values[i]);
                Assert.Equal([0x01, 0x00, 0x00], rows[i].DuckDbMetadata);
                Assert.Equal([0x00], rows[i].DuckDbValue);
                continue;
            }
            Assert.True(rows[i].DuckDbMetadata.AsSpan().SequenceEqual(rows[i].OursMetadata), $"metadata of {Values[i]}");
            Assert.True(rows[i].DuckDbValue.AsSpan().SequenceEqual(rows[i].OursValue), $"value of {Values[i]}");
        }
    }

    [Fact]
    public async Task SqlNull_IsAStructNull()
    {
        using AdbcConnection conn = OpenConnection();
        List<Row> rows = await Query(conn, "SELECT 0 AS i, NULL::VARIANT AS v UNION ALL SELECT 1, 1::VARIANT");
        Assert.True(rows[0].OursIsNull);
        Assert.False(rows[1].OursIsNull);
    }

    // Apache.Arrow 23.0.0 can't read an object whose field-id and field-offset
    // sizes differ (it swaps them). When this starts to fail, an Apache.Arrow
    // with apache/arrow-dotnet#421 is in, and Arrow's own reader and writer
    // could do the job again.
    [Fact]
    public async Task Arrow23_ReadsLargeObjectHeadersSwapped()
    {
        using AdbcConnection conn = OpenConnection();
        Row row = Assert.Single(await Query(conn, Union(["{'k': repeat('x', 300), 'n': 1}"])));
        // An object with 2-byte field offsets and 1-byte field ids.
        Assert.Equal(0b0000_0110, row.OursValue[0]);
        Assert.ThrowsAny<Exception>(() => new VariantReader(row.OursMetadata, row.OursValue).ToVariantValue());
        DuckDbVariant read = ParquetVariantReader.Read(row.OursMetadata, row.OursValue);
        Assert.Equal(new string('x', 300), read.Fields[0].Value.Value);
    }

    // A shredded vector (a checkpointed table, scanned without a sort, which
    // would unshred it) converts through the canonical layout; its keys may be
    // ordered differently, so compare decoded values.
    [Fact]
    public async Task Shredded_DecodesToDuckDbsValues()
    {
        using AdbcConnection conn = OpenConnection();
        string path = Path.Combine(Path.GetTempPath(), "quack_vps_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
        string alias = "vps_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"ATTACH '{path}' AS {alias}");
        try
        {
            Execute(conn, $"CREATE TABLE {alias}.t AS SELECT r AS i, {{'b': r, 'a': repeat('x', 300), 'c': [r, r + 1]}}::VARIANT AS v FROM range(40000) t(r)");
            Execute(conn, $"CHECKPOINT {alias}");
            long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
            Dictionary<long, (byte[] Metadata, byte[] Value)> ours = await ById(conn, $"SELECT i, v FROM {alias}.t");
            Assert.True(Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) > before, "the column did not arrive shredded");
            Dictionary<long, (byte[] Metadata, byte[] Value)> duckdb = await ById(conn,
                $"SELECT i, variant_to_parquet_variant(v)::STRUCT(metadata BLOB, value BLOB) AS p FROM {alias}.t");
            Assert.Equal(40000, ours.Count);
            Assert.Equal(40000, duckdb.Count);
            foreach ((long id, (byte[] metadata, byte[] value)) in ours)
            {
                Assert.Equal(
                    ParquetVariantReader.Read(duckdb[id].Metadata, duckdb[id].Value),
                    ParquetVariantReader.Read(metadata, value));
            }
        }
        finally
        {
            Execute(conn, $"DETACH {alias}");
            File.Delete(path);
        }
    }

    private static async Task<Dictionary<long, (byte[] Metadata, byte[] Value)>> ById(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        Dictionary<long, (byte[], byte[])> rows = [];
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            Int64Array ids = (Int64Array)batch.Column(0);
            IArrowArray p = batch.Column(1);
            for (int i = 0; i < batch.Length; i++)
            {
                rows[ids.GetValue(i)!.Value] = p is VariantArray v
                    ? (v.GetMetadataBytes(i).ToArray(), v.GetValueBytes(i).ToArray())
                    : (((BinaryArray)((StructArray)p).Fields[0]).GetBytes(i).ToArray(), ((BinaryArray)((StructArray)p).Fields[1]).GetBytes(i).ToArray());
            }
        }
        return rows;
    }

    // Ingest reads the spec layout: duckdb's own bytes go in, and duckdb
    // encodes the ingested value to the same bytes again.
    [Fact]
    public async Task Ingest_DuckDbsBytes_RoundTrip()
    {
        using AdbcConnection conn = OpenConnection();
        List<Row> rows = await Query(conn, Union(Values));
        string table = "vpi_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TABLE {table} (i INTEGER, v VARIANT)");
        BinaryArray.Builder metadata = new();
        BinaryArray.Builder value = new();
        foreach (Row row in rows)
        {
            metadata.Append(row.DuckDbMetadata.AsSpan());
            value.Append(row.DuckDbValue.AsSpan());
        }
        VariantArray variants = new(VariantType.Default, new StructArray(
            (StructType)VariantType.Default.StorageType, rows.Count, [metadata.Build(), value.Build()], ArrowBuffer.Empty, 0));
        Int32Array ids = new Int32Array.Builder().AppendRange(Enumerable.Range(0, rows.Count)).Build();
        Schema schema = new Schema.Builder()
            .Field(new Field("i", Int32Type.Default, true))
            .Field(new Field("v", VariantType.Default, true))
            .Build();
        using (RecordBatch batch = new(schema, [ids, variants], rows.Count))
        using (AdbcStatement ingest = conn.CreateStatement())
        {
            ingest.SqlQuery = table;
            ingest.Bind(batch, schema);
            Assert.Equal(rows.Count, ingest.ExecuteUpdate().AffectedRows);
        }

        List<Row> back = await Query(conn, $"SELECT i, v FROM {table}");
        Execute(conn, $"DROP TABLE {table}");
        Assert.Equal(rows.Count, back.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].OursIsNull)
            {
                Assert.True(back[i].OursIsNull, Values[i]);
                continue;
            }
            // Same value; a DECIMAL4/8 comes back at the width of its digits.
            Assert.Equal(
                WithoutDecimalWidth(ParquetVariantReader.Read(rows[i].DuckDbMetadata, rows[i].DuckDbValue)),
                WithoutDecimalWidth(ParquetVariantReader.Read(back[i].DuckDbMetadata, back[i].DuckDbValue)));
        }
    }

    private static DuckDbVariant WithoutDecimalWidth(DuckDbVariant v) => v.Type switch
    {
        DuckDbVariantType.Decimal => new(v.Type, (DuckDbVariantDecimal)v.Value! with { Width = 0 }),
        DuckDbVariantType.Object => new(v.Type, v.Fields.Select(f => new KeyValuePair<string, DuckDbVariant>(f.Key, WithoutDecimalWidth(f.Value))).ToList()),
        DuckDbVariantType.Array => new(v.Type, v.Elements.Select(WithoutDecimalWidth).ToList()),
        _ => v,
    };

    private static string Union(IEnumerable<string> values)
        => string.Join(" UNION ALL ", values.Select((v, i) => $"SELECT {i} AS i, ({v})::VARIANT AS v"));

    private sealed record Row(bool OursIsNull, byte[] OursMetadata, byte[] OursValue, byte[] DuckDbMetadata, byte[] DuckDbValue);

    // Per row: our arrow.parquet.variant for v, and duckdb's encoding of v.
    private static async Task<List<Row>> Query(AdbcConnection conn, string rows, bool ordered = true)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = $"SELECT v, variant_to_parquet_variant(v)::STRUCT(metadata BLOB, value BLOB) AS p FROM ({rows}) {(ordered ? "ORDER BY i" : "")}";
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        List<Row> list = [];
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            VariantArray ours = Assert.IsType<VariantArray>(batch.Column(0));
            StructArray duckdb = Assert.IsType<StructArray>(batch.Column(1));
            for (int i = 0; i < batch.Length; i++)
            {
                list.Add(new Row(ours.IsNull(i), ours.GetMetadataBytes(i).ToArray(), ours.GetValueBytes(i).ToArray(),
                    ((BinaryArray)duckdb.Fields[0]).GetBytes(i).ToArray(), ((BinaryArray)duckdb.Fields[1]).GetBytes(i).ToArray()));
            }
        }
        return list;
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

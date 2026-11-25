// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Quack;
using Quack.Data;
using Quack.Types;
using Xunit.Abstractions;

namespace Quack.IntegrationTests;

// Inversion pass over the VARIANT support: the cases the feature tests left out.
[Collection(nameof(VariantTests))]
public class VariantInversionTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;
    private readonly ITestOutputHelper _out;

    public VariantInversionTests(QuackServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _out = output;
    }

    // 1: VARIANT inside STRUCT, LIST and MAP, read unshredded (small) and from
    // a 2048-row cast (shredded children), compared with the bare VARIANT.
    [Theory]
    [InlineData(3)]
    [InlineData(2048)]
    public async Task VariantInsideContainers_Read(int rows)
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "vin_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} AS SELECT i, CASE WHEN i % 2 = 0 THEN i::VARCHAR ELSE NULL END AS s FROM range({rows}) t(i)");
        long before = Shredded();
        IReadOnlyList<DuckDbChunk> chunks = await All(conn,
            $"SELECT i::BIGINT, i::VARIANT AS bare, {{'v': i::VARIANT, 'w': s::VARIANT}} AS st, [i::VARIANT, s::VARIANT] AS li, MAP {{'k': i::VARIANT}} AS m FROM {table}");
        _out.WriteLine($"rows {rows}: shredded vectors {Shredded() - before}");
        int seen = 0;
        foreach (DuckDbChunk chunk in chunks)
        {
            for (int r = 0; r < chunk.RowCount; r++)
            {
                long i = BinaryPrimitives.ReadInt64LittleEndian(((FixedSizeColumn)chunk.Columns[0]).GetBytes(r));
                DuckDbVariant bare = ((VariantColumn)chunk.Columns[1]).Values[r]!;
                StructColumn st = (StructColumn)chunk.Columns[2];
                Assert.Equal(bare, ((VariantColumn)st.Fields[0]).Values[r]);
                Assert.Equal(i % 2 == 0 ? new DuckDbVariant(DuckDbVariantType.Varchar, i.ToString()) : null, ((VariantColumn)st.Fields[1]).Values[r]);
                ListColumn li = (ListColumn)chunk.Columns[3];
                (ulong offset, ulong length) = li.Entries[r];
                Assert.Equal(2UL, length);
                Assert.Equal(bare, ((VariantColumn)li.Child).Values[(int)offset]);
                ListColumn m = (ListColumn)chunk.Columns[4];
                StructColumn entry = (StructColumn)m.Child;
                Assert.Equal(bare, ((VariantColumn)entry.Fields[1]).Values[(int)m.Entries[r].Offset]);
                seen++;
            }
        }
        Assert.Equal(rows, seen);
        await Drain(conn, $"DROP TABLE {table}");
    }

    // 1: sending VARIANT inside STRUCT and LIST, checked on the server.
    [Fact]
    public async Task VariantInsideContainers_Write()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "vinw_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} (st STRUCT(v VARIANT, n INTEGER), li VARIANT[])");
        const int rows = 3000; // across the 2048-row cut
        DuckDbVariant?[] structValues = new DuckDbVariant?[rows];
        byte[] ints = new byte[rows * 4];
        List<DuckDbVariant?> listValues = [];
        (ulong, ulong)[] entries = new (ulong, ulong)[rows];
        for (int i = 0; i < rows; i++)
        {
            structValues[i] = i % 3 == 0 ? null : Obj(("a", V(DuckDbVariantType.Int32, i)));
            BinaryPrimitives.WriteInt32LittleEndian(ints.AsSpan(i * 4), i);
            entries[i] = ((ulong)listValues.Count, 2);
            listValues.Add(V(DuckDbVariantType.Varchar, "e" + i));
            listValues.Add(i % 5 == 0 ? null : Arr(V(DuckDbVariantType.Int64, (long)i)));
        }
        LogicalType intType = new(LogicalTypeId.Integer);
        LogicalType structType = new(LogicalTypeId.Struct, new StructTypeInfo { ChildTypes = [new("v", LogicalType.Variant), new("n", intType)] });
        LogicalType listType = new(LogicalTypeId.List, new ListTypeInfo { ChildType = LogicalType.Variant });
        DuckDbChunk chunk = new()
        {
            Types = [structType, listType],
            Columns =
            [
                new StructColumn
                {
                    Type = structType,
                    Count = rows,
                    Fields =
                    [
                        Variants(structValues),
                        new FixedSizeColumn { Type = intType, Count = rows, ElementSize = 4, Data = ints },
                    ],
                },
                new ListColumn { Type = listType, Count = rows, Entries = entries, Child = Variants(listValues.ToArray()) },
            ],
            RowCount = rows,
        };
        Assert.Equal(rows, await conn.AppendAsync(table, chunk));
        Assert.Equal("0", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table} WHERE (st.n % 3 = 0) <> (st.v IS NULL)"));
        Assert.Equal("0", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table} WHERE st.n % 3 <> 0 AND NOT (st.v IS NOT DISTINCT FROM {{'a': st.n}}::VARIANT)"));
        Assert.Equal("0", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table} WHERE NOT (li[1] IS NOT DISTINCT FROM ('e' || st.n)::VARIANT)"));
        Assert.Equal("0", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table} WHERE (st.n % 5 = 0) <> (li[2] IS NULL)"));
        await Drain(conn, $"DROP TABLE {table}");
    }

    // 2: partially shredded arrays and deep shredded objects, checkpointed
    // (shredded) against an in-memory copy (unshredded).
    [Fact]
    public async Task ShreddedArraysAndDeepObjects_MatchUnshredded()
    {
        const string data =
            "SELECT i, CASE i % 9 " +
            "WHEN 0 THEN NULL " +
            // arrays: mostly ints, some strings, nulls, objects, empty
            "WHEN 1 THEN [i::VARIANT, (i + 1)::VARIANT]::VARIANT " +
            "WHEN 2 THEN [i::VARIANT, ('s' || i)::VARIANT, NULL::VARIANT]::VARIANT " +
            "WHEN 3 THEN [{'k': i}::VARIANT, {'k': 'x', 'extra': i}::VARIANT]::VARIANT " +
            "WHEN 4 THEN []::INTEGER[]::VARIANT " +
            // deep objects with consistent decimals and varying leaves
            "WHEN 5 THEN {'a': {'b': {'c': {'d': (i / 100)::DECIMAL(12,2), 'e': [i, i]}}}}::VARIANT " +
            "WHEN 6 THEN {'a': {'b': {'c': {'d': 'not a number', 'f': true}}}}::VARIANT " +
            "WHEN 7 THEN {'a': {'b': NULL::VARIANT}}::VARIANT " +
            "ELSE [[i, i + 1], [i + 2]]::VARIANT END AS v " +
            "FROM range(45000) t(i)";
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string path = Path.Combine(Path.GetTempPath(), "quack_vinv_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
        string alias = "vinv_" + Guid.NewGuid().ToString("N")[..8];
        string reference = "vinvref_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"ATTACH '{path}' AS {alias}");
        try
        {
            await Drain(conn, $"CREATE TABLE {alias}.v AS {data}");
            await Drain(conn, $"CHECKPOINT {alias}");
            await Drain(conn, $"CREATE TABLE {reference} AS {data}");
            long before = Shredded();
            SortedDictionary<long, DuckDbVariant?> shredded = await ById(conn, $"SELECT i::BIGINT, v FROM {alias}.v");
            long vectors = Shredded() - before;
            SortedDictionary<long, DuckDbVariant?> plain = await ById(conn, $"SELECT i::BIGINT, v FROM {reference}");
            _out.WriteLine($"shredded vectors {vectors}");
            Assert.True(vectors > 0);
            Assert.Equal(plain.Count, shredded.Count);
            foreach ((long i, DuckDbVariant? v) in plain)
            {
                Assert.True(Equals(Sorted(v), Sorted(shredded[i])), $"row {i}: {v} vs {shredded[i]}");
            }
        }
        finally
        {
            await Drain(conn, $"DROP TABLE IF EXISTS {reference}");
            await Drain(conn, $"DETACH {alias}");
            File.Delete(path);
        }
    }

    // 4: boundaries — strings around 64 bytes, NaN / infinities / -0.0, deep
    // nesting, many keys, a large value; read and written, checked on the server.
    [Theory]
    [InlineData("repeat('x', 63)")]
    [InlineData("repeat('x', 64)")]
    [InlineData("repeat('x', 65)")]
    [InlineData("'NaN'::DOUBLE")]
    [InlineData("'Infinity'::DOUBLE")]
    [InlineData("'-Infinity'::FLOAT")]
    [InlineData("(-0.0)::DOUBLE")]
    [InlineData("repeat('[', 120) || '1' || repeat(']', 120)")]
    [InlineData("'{' || (SELECT string_agg('\"k' || i || '\": ' || i, ',') FROM range(2000) t(i)) || '}'")]
    [InlineData("repeat('ü', 600000)")]
    public async Task Boundaries_ReadAndWrite(string sql)
    {
        bool json = sql.Contains("'['") || sql.Contains("'{'");
        string variantSql = json ? $"({sql})::JSON::VARIANT" : $"({sql})::VARIANT";
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        DuckDbVariant read = (await Values(conn, $"SELECT {variantSql}"))[0]!;
        string table = "vb_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} (v VARIANT)");
        await conn.AppendAsync(table, new DuckDbChunk { Types = [LogicalType.Variant], Columns = [Variants([read])], RowCount = 1 });
        string same = await Text(conn, $"SELECT (v IS NOT DISTINCT FROM {variantSql})::VARCHAR FROM {table}");
        string typeServer = await Text(conn, $"SELECT variant_typeof({variantSql})");
        string typeSent = await Text(conn, $"SELECT variant_typeof(v) FROM {table}");
        await Drain(conn, $"DROP TABLE {table}");
        _out.WriteLine($"{sql[..Math.Min(sql.Length, 40)]}: read {read.Type}, equal on server {same}, type {typeSent} vs {typeServer}");
        Assert.Equal("true", same);
        Assert.Equal(typeServer, typeSent);
    }

    // 6: cost per row against a STRUCT of the same shape.
    [Fact]
    public async Task Measure_Throughput()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        const int rows = 1_000_000;
        foreach (string expr in new[] { "{'a': i, 'b': 'x' || i, 'c': [i, i + 1]}", "{'a': i, 'b': 'x' || i, 'c': [i, i + 1]}::VARIANT" })
        {
            string table = "vt_" + Guid.NewGuid().ToString("N")[..8];
            await Drain(conn, $"CREATE TABLE {table} AS SELECT {expr} AS v FROM range({rows}) t(i)");
            Stopwatch sw = Stopwatch.StartNew();
            long count = 0;
            await foreach (DuckDbChunk chunk in (await conn.ExecuteAsync($"SELECT v FROM {table}")).GetChunksAsync())
            {
                count += chunk.RowCount;
            }
            _out.WriteLine($"read {rows} rows of {(expr.EndsWith("VARIANT") ? "VARIANT" : "STRUCT ")}: {sw.ElapsedMilliseconds} ms");
            Assert.Equal(rows, count);
            await Drain(conn, $"DROP TABLE {table}");
        }

        DuckDbVariant?[] values = new DuckDbVariant?[rows];
        for (int i = 0; i < rows; i++)
        {
            values[i] = Obj(("a", V(DuckDbVariantType.Int64, (long)i)), ("b", V(DuckDbVariantType.Varchar, "x" + i)),
                ("c", Arr(V(DuckDbVariantType.Int64, (long)i), V(DuckDbVariantType.Int64, (long)i + 1))));
        }
        string target = "vtw_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {target} (v VARIANT)");
        Stopwatch write = Stopwatch.StartNew();
        await conn.AppendAsync(target, new DuckDbChunk { Types = [LogicalType.Variant], Columns = [Variants(values)], RowCount = rows });
        _out.WriteLine($"append {rows} VARIANT rows: {write.ElapsedMilliseconds} ms");
        Assert.Equal(rows.ToString(), await Text(conn, $"SELECT count(*)::VARCHAR FROM {target}"));
        await Drain(conn, $"DROP TABLE {target}");
    }

    private static long Shredded() => Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);

    private static VariantColumn Variants(DuckDbVariant?[] values)
    {
        byte[] validity = new byte[ValidityMask.RequiredByteCount(values.Length)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not null) validity[i >> 3] |= (byte)(1 << (i & 7));
        }
        return new VariantColumn { Type = LogicalType.Variant, Count = values.Length, Validity = new ValidityMask(validity), Values = values };
    }

    private static DuckDbVariant V(DuckDbVariantType type, object value) => new(type, value);

    private static DuckDbVariant Obj(params (string Key, DuckDbVariant Value)[] fields)
        => new(DuckDbVariantType.Object, fields.Select(f => new KeyValuePair<string, DuckDbVariant>(f.Key, f.Value)).ToList());

    private static DuckDbVariant Arr(params DuckDbVariant[] elements) => new(DuckDbVariantType.Array, elements.ToList());

    private static DuckDbVariant? Sorted(DuckDbVariant? v) => v?.Type switch
    {
        null => null,
        DuckDbVariantType.Object => new DuckDbVariant(DuckDbVariantType.Object,
            v.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new KeyValuePair<string, DuckDbVariant>(f.Key, Sorted(f.Value)!)).ToList()),
        DuckDbVariantType.Array => new DuckDbVariant(DuckDbVariantType.Array, v.Elements.Select(e => Sorted(e)!).ToList()),
        _ => v,
    };

    private static async Task<SortedDictionary<long, DuckDbVariant?>> ById(QuackConnection conn, string sql)
    {
        SortedDictionary<long, DuckDbVariant?> result = [];
        foreach (DuckDbChunk chunk in await All(conn, sql))
        {
            for (int r = 0; r < chunk.RowCount; r++)
            {
                result[BinaryPrimitives.ReadInt64LittleEndian(((FixedSizeColumn)chunk.Columns[0]).GetBytes(r))] = ((VariantColumn)chunk.Columns[1]).Values[r];
            }
        }
        return result;
    }

    private static async Task<IReadOnlyList<DuckDbVariant?>> Values(QuackConnection conn, string sql)
        => (await All(conn, sql)).SelectMany(c => ((VariantColumn)c.Columns[0]).Values).ToList();

    private static async Task<IReadOnlyList<DuckDbChunk>> All(QuackConnection conn, string sql)
        => (await (await conn.ExecuteAsync(sql)).ToListAsync()).Where(c => c.RowCount > 0).ToList();

    private static async Task<string> Text(QuackConnection conn, string sql)
    {
        DuckDbChunk chunk = (await All(conn, sql))[0];
        VarBytesColumn col = Assert.IsType<VarBytesColumn>(chunk.Columns[0]);
        return col.IsNull(0) ? "<NULL>" : Encoding.UTF8.GetString(col.Values[0]!.Value.Span);
    }

    private static async Task Drain(QuackConnection conn, string sql) => _ = await (await conn.ExecuteAsync(sql)).ToListAsync();
}

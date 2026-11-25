// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Quack;
using Quack.Data;
using Quack.Types;
using Xunit.Abstractions;

namespace Quack.IntegrationTests;

// Sending VARIANT values: the server must understand the encoding, so every
// check runs on the server (comparison with the same value built in SQL,
// variant_typeof, key lookup, JSON, checkpoint shredding) rather than by
// reading our own bytes back.
[Collection(nameof(VariantTests))]
public class VariantWriteTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;
    private readonly ITestOutputHelper _out;

    public VariantWriteTests(QuackServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _out = output;
    }

    public static IEnumerable<object[]> Primitives() => VariantTests.Primitives();

    [Theory]
    [MemberData(nameof(Primitives))]
    public async Task Primitive_IsTheSameValueOnTheServer(string sql, DuckDbVariantType type, object expected)
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "vw_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} (v VARIANT)");
        Assert.Equal(1, await conn.AppendAsync(table, Chunk(new DuckDbVariant(type, expected))));

        Assert.Equal("true", await Text(conn, $"SELECT (v IS NOT DISTINCT FROM ({sql})::VARIANT)::VARCHAR FROM {table}"));
        Assert.Equal(await Text(conn, $"SELECT variant_typeof(({sql})::VARIANT)"), await Text(conn, $"SELECT variant_typeof(v) FROM {table}"));
        Assert.Equal(await Text(conn, $"SELECT (({sql})::VARIANT)::VARCHAR"), await Text(conn, $"SELECT v::VARCHAR FROM {table}"));
        await Drain(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task Nested_UnsortedKeys_Nulls_AndKeyLookup()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "vwn_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} (i INTEGER, v VARIANT)");

        // Keys out of order on purpose: the encoder sorts them, as duckdb requires.
        DuckDbVariant nested = Obj(
            ("zeta", V(DuckDbVariantType.Varchar, "last")),
            ("alpha", Arr(V(DuckDbVariantType.Int32, 1), DuckDbVariant.VariantNull, Obj(("x", V(DuckDbVariantType.Double, 2.5))))),
            ("mid", DuckDbVariant.VariantNull),
            ("", V(DuckDbVariantType.Varchar, "empty key")),
            ("ü", V(DuckDbVariantType.Int64, 7L)));
        DuckDbVariant?[] values = [nested, null, DuckDbVariant.VariantNull, Arr(), Obj()];
        Assert.Equal(5, await conn.AppendAsync(table, Chunk(values, withIds: true)));

        Assert.Equal("true", await Text(conn, $"SELECT (variant_extract(v, 'zeta') IS NOT DISTINCT FROM 'last'::VARIANT)::VARCHAR FROM {table} WHERE i = 0"));
        Assert.Contains("2.5", await Text(conn, $"SELECT variant_extract(variant_extract(v, 'alpha'), 3)::VARCHAR FROM {table} WHERE i = 0"));
        Assert.Equal("empty key", await Text(conn, $"SELECT variant_extract(v, '')::VARCHAR FROM {table} WHERE i = 0"));
        Assert.Equal("7", await Text(conn, $"SELECT variant_extract(v, 'ü')::VARCHAR FROM {table} WHERE i = 0"));
        string json = await Text(conn, $"SELECT v::JSON::VARCHAR FROM {table} WHERE i = 0");
        _out.WriteLine(json);
        Assert.Contains("\"alpha\":[1,null,{\"x\":2.5}]", json.Replace(" ", ""));

        // SQL NULL and a root variant null are both SQL NULL; [] and {} survive.
        Assert.Equal("2", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table} WHERE v IS NULL"));
        string emptyArray = await Text(conn, $"SELECT variant_typeof(v) FROM {table} WHERE i = 3");
        string emptyObject = await Text(conn, $"SELECT variant_typeof(v) FROM {table} WHERE i = 4");
        _out.WriteLine($"[] -> {emptyArray}, {{}} -> {emptyObject}");
        Assert.Equal("ARRAY(0)", emptyArray);
        Assert.StartsWith("OBJECT", emptyObject);
    }

    // An object sent with its keys out of order equals the same object built in
    // SQL: in comparisons, DISTINCT and GROUP BY. NOTE: this passes with the
    // encoder's key sort removed too (measured against DuckDB v2.0.1): these
    // operations do not depend on the stored field order. The encoder sorts
    // anyway, because duckdb's own builder writes sorted keys and assumes them.
    [Fact]
    public async Task Object_EqualsTheSqlBuiltObject()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "vwe_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"CREATE TABLE {table} (v VARIANT)");
        DuckDbVariant sent = Obj(("b", V(DuckDbVariantType.Int32, 1)), ("a", V(DuckDbVariantType.Int32, 2)), ("c", Obj(("y", V(DuckDbVariantType.Varchar, "q")), ("x", V(DuckDbVariantType.Int32, 3)))));
        await conn.AppendAsync(table, Chunk(sent));
        const string sql = "{'a': 2, 'b': 1, 'c': {'x': 3, 'y': 'q'}}::VARIANT";
        Assert.Equal("true", await Text(conn, $"SELECT (v IS NOT DISTINCT FROM {sql})::VARCHAR FROM {table}"));
        Assert.Equal("1", await Text(conn, $"SELECT count(DISTINCT v)::VARCHAR FROM (SELECT v FROM {table} UNION ALL SELECT {sql})"));
        Assert.Equal("1", await Text(conn, $"SELECT count(*)::VARCHAR FROM (SELECT v FROM (SELECT v FROM {table} UNION ALL SELECT {sql}) GROUP BY v)"));
        Assert.Equal(await Text(conn, $"SELECT ({sql})::VARCHAR"), await Text(conn, $"SELECT v::VARCHAR FROM {table}"));
    }

    // 40000 appended irregular rows, checkpointed (so the server shreds what we
    // sent), read back shredded and compared with what was sent.
    [Fact]
    public async Task AppendedRows_SurviveACheckpoint()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string path = Path.Combine(Path.GetTempPath(), "quack_vw_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
        string alias = "vwdb_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"ATTACH '{path}' AS {alias}");
        try
        {
            await Drain(conn, $"CREATE TABLE {alias}.t (i INTEGER, v VARIANT)");
            const int rows = 40000;
            DuckDbVariant?[] sent = new DuckDbVariant?[rows];
            for (int i = 0; i < rows; i++)
            {
                sent[i] = (i % 6) switch
                {
                    0 => null,
                    1 => V(DuckDbVariantType.Varchar, "s" + i),
                    2 => Obj(("a", V(DuckDbVariantType.Varchar, "t" + i)), ("b", V(DuckDbVariantType.Int64, (long)i))),
                    3 => Obj(("b", V(DuckDbVariantType.Int64, (long)i)), ("z", Arr(V(DuckDbVariantType.Int32, 1), V(DuckDbVariantType.Varchar, "x")))),
                    4 => Obj(("a", V(DuckDbVariantType.Int64, (long)i)), ("", V(DuckDbVariantType.Varchar, "e" + i))),
                    _ => Obj(("a", V(DuckDbVariantType.Int64, (long)i)), ("b", V(DuckDbVariantType.Varchar, "x" + i)),
                        ("c", Arr(V(DuckDbVariantType.Int64, (long)i), V(DuckDbVariantType.Int64, (long)i + 1)))),
                };
            }
            Assert.Equal(rows, await conn.AppendAsync(alias, "t", Chunk(sent, withIds: true)));
            await Drain(conn, $"CHECKPOINT {alias}");

            long before = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded);
            SortedDictionary<long, DuckDbVariant?> back = [];
            foreach (DuckDbChunk chunk in await (await conn.ExecuteAsync($"SELECT i::BIGINT, v FROM {alias}.t")).ToListAsync())
            {
                FixedSizeColumn ids = (FixedSizeColumn)chunk.Columns[0];
                VariantColumn vs = (VariantColumn)chunk.Columns[1];
                for (int r = 0; r < chunk.RowCount; r++) back[BinaryPrimitives.ReadInt64LittleEndian(ids.GetBytes(r))] = vs.Values[r];
            }
            long shredded = Interlocked.Read(ref VariantDecoder.ShreddedVectorsDecoded) - before;
            _out.WriteLine($"shredded vectors after checkpoint: {shredded}");
            Assert.True(shredded > 0, "the checkpoint did not shred the appended rows");
            Assert.Equal(rows, back.Count);
            foreach ((long i, DuckDbVariant? v) in back)
            {
                Assert.True(Equals(Sorted(sent[i]), Sorted(v)), $"row {i}: sent {sent[i]}, read {v}");
            }
        }
        finally
        {
            await Drain(conn, $"DETACH {alias}");
            File.Delete(path);
        }
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

    private static DuckDbChunk Chunk(DuckDbVariant value) => Chunk([value], withIds: false);

    private static DuckDbChunk Chunk(DuckDbVariant?[] values, bool withIds)
    {
        byte[] validity = new byte[ValidityMask.RequiredByteCount(values.Length)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not null) validity[i >> 3] |= (byte)(1 << (i & 7));
        }
        VariantColumn column = new() { Type = LogicalType.Variant, Count = values.Length, Validity = new ValidityMask(validity), Values = values };
        if (!withIds)
        {
            return new DuckDbChunk { Types = [LogicalType.Variant], Columns = [column], RowCount = values.Length };
        }
        LogicalType intType = new(LogicalTypeId.Integer);
        byte[] ids = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(ids.AsSpan(i * 4), i);
        return new DuckDbChunk
        {
            Types = [intType, LogicalType.Variant],
            Columns = [new FixedSizeColumn { Type = intType, Count = values.Length, ElementSize = 4, Data = ids }, column],
            RowCount = values.Length,
        };
    }

    private static async Task<string> Text(QuackConnection conn, string sql)
    {
        DuckDbChunk chunk = (await (await conn.ExecuteAsync(sql)).ToListAsync()).First(c => c.RowCount > 0);
        VarBytesColumn col = Assert.IsType<VarBytesColumn>(chunk.Columns[0]);
        return col.IsNull(0) ? "<NULL>" : Encoding.UTF8.GetString(col.Values[0]!.Value.Span);
    }

    private static async Task Drain(QuackConnection conn, string sql) => _ = await (await conn.ExecuteAsync(sql)).ToListAsync();
}

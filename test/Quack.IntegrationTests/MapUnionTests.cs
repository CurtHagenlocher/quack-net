using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class MapUnionTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public MapUnionTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_MapVarcharToInteger()
    {
        // MAP(VARCHAR, INTEGER) is physically a LIST<STRUCT<key VARCHAR, value INTEGER>>.
        // Row 0: {"a": 1, "b": 2}
        // Row 1: {}             (empty map, not NULL)
        // Row 2: NULL
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_map_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v MAP(VARCHAR, INTEGER))"));

        LogicalType keyType = new(LogicalTypeId.Varchar);
        LogicalType valueType = new(LogicalTypeId.Integer);
        LogicalType entryType = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes =
            [
                new("key", keyType),
                new("value", valueType),
            ],
        });
        LogicalType mapType = new(LogicalTypeId.Map,
            new ListTypeInfo { ChildType = entryType });

        byte[] mapValidity = new byte[ValidityMask.RequiredByteCount(3)];
        mapValidity[0] = 0b0000_0011; // rows 0, 1 valid; row 2 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };

        ListColumn mapCol = new()
        {
            Type = mapType,
            Count = 3,
            Validity = new ValidityMask(mapValidity),
            Entries = [(0UL, 2UL), (2UL, 0UL), (0UL, 0UL)],
            Child = new StructColumn
            {
                Type = entryType,
                Count = 2,
                Fields =
                [
                    new VarBytesColumn
                    {
                        Type = keyType,
                        Count = 2,
                        Values = [(ReadOnlyMemory<byte>)"a"u8.ToArray(), (ReadOnlyMemory<byte>)"b"u8.ToArray()],
                    },
                    new FixedSizeColumn
                    {
                        Type = valueType,
                        Count = 2,
                        ElementSize = 4,
                        Data = Int32Bytes(1, 2),
                    },
                ],
            },
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, mapType],
            Columns = [idCol, mapCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        // Project to expose the map's keys / values so the assertions don't
        // depend on map-equality semantics or in-map key order.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync(
                $"SELECT id, map_keys(v) AS k, map_values(v) AS x, len(map_keys(v)) AS n " +
                $"FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(3, result.RowCount);
        FixedSizeColumn lenCol = Assert.IsType<FixedSizeColumn>(result.Columns[3]);

        // Row 0: two entries
        Assert.False(lenCol.IsNull(0));
        Assert.Equal(2L, BinaryPrimitives.ReadInt64LittleEndian(lenCol.GetBytes(0)));

        // Row 1: empty map -> map_keys returns an empty list of length 0
        Assert.False(lenCol.IsNull(1));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(lenCol.GetBytes(1)));

        // Row 2: NULL map -> map_keys is NULL
        Assert.True(lenCol.IsNull(2));

        // Spot-check key/value contents of row 0 via the projected list columns.
        ListColumn kList = Assert.IsType<ListColumn>(result.Columns[1]);
        ListColumn xList = Assert.IsType<ListColumn>(result.Columns[2]);
        VarBytesColumn kChild = Assert.IsType<VarBytesColumn>(kList.Child);
        FixedSizeColumn xChild = Assert.IsType<FixedSizeColumn>(xList.Child);

        (ulong kOff, ulong kLen) = kList.Entries[0];
        Assert.Equal(2UL, kLen);
        byte[] k0 = kChild.Values[(int)kOff]!.Value.ToArray();
        byte[] k1 = kChild.Values[(int)kOff + 1]!.Value.ToArray();
        Assert.Equal("a"u8.ToArray(), k0);
        Assert.Equal("b"u8.ToArray(), k1);

        (ulong xOff, _) = xList.Entries[0];
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(xChild.GetBytes((int)xOff)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(xChild.GetBytes((int)xOff + 1)));
    }

    [Fact]
    public async Task RoundTrip_UnionIntegerOrVarchar()
    {
        // UNION(i INTEGER, s VARCHAR) is physically a STRUCT(tag UTINYINT,
        // i INTEGER, s VARCHAR) where exactly one non-tag member is non-null
        // per row and `tag` selects which.
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_union_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, v UNION(i INTEGER, s VARCHAR))"));

        LogicalType tagType = new(LogicalTypeId.UTinyInt);
        LogicalType intMember = new(LogicalTypeId.Integer);
        LogicalType strMember = new(LogicalTypeId.Varchar);
        LogicalType unionType = new(LogicalTypeId.Union, new StructTypeInfo
        {
            ChildTypes =
            [
                new("", tagType),       // tag has empty key in DuckDB's internal repr
                new("i", intMember),
                new("s", strMember),
            ],
        });

        // 3 rows:
        //   row 0 = UNION holding i=42
        //   row 1 = UNION holding s="hello"
        //   row 2 = UNION holding i=-7
        byte[] iValidity = new byte[ValidityMask.RequiredByteCount(3)];
        iValidity[0] = 0b0000_0101; // i is set for rows 0 and 2
        byte[] sValidity = new byte[ValidityMask.RequiredByteCount(3)];
        sValidity[0] = 0b0000_0010; // s is set for row 1 only

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };

        StructColumn unionCol = new()
        {
            Type = unionType,
            Count = 3,
            Fields =
            [
                new FixedSizeColumn
                {
                    Type = tagType,
                    Count = 3,
                    ElementSize = 1,
                    Data = new byte[] { 0, 1, 0 },
                },
                new FixedSizeColumn
                {
                    Type = intMember,
                    Count = 3,
                    ElementSize = 4,
                    Data = Int32Bytes(42, 0, -7),
                    Validity = new ValidityMask(iValidity),
                },
                new VarBytesColumn
                {
                    Type = strMember,
                    Count = 3,
                    Validity = new ValidityMask(sValidity),
                    Values =
                    [
                        null,
                        (ReadOnlyMemory<byte>)"hello"u8.ToArray(),
                        null,
                    ],
                },
            ],
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, unionType],
            Columns = [idCol, unionCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        // SELECT the union cast to VARCHAR so we can compare via string projection
        // — this avoids depending on the wire shape of UNION on the return path.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync(
                $"SELECT id, CAST(union_tag(v) AS VARCHAR) AS tag, CAST(v AS VARCHAR) AS s " +
                $"FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(3, result.RowCount);

        VarBytesColumn tags = Assert.IsType<VarBytesColumn>(result.Columns[1]);
        VarBytesColumn vals = Assert.IsType<VarBytesColumn>(result.Columns[2]);

        Assert.Equal("i"u8.ToArray(), tags.Values[0]!.Value.ToArray());
        Assert.Equal("s"u8.ToArray(), tags.Values[1]!.Value.ToArray());
        Assert.Equal("i"u8.ToArray(), tags.Values[2]!.Value.ToArray());

        Assert.Equal("42"u8.ToArray(), vals.Values[0]!.Value.ToArray());
        Assert.Equal("hello"u8.ToArray(), vals.Values[1]!.Value.ToArray());
        Assert.Equal("-7"u8.ToArray(), vals.Values[2]!.Value.ToArray());
    }

    private static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static async Task Drain(QuackQueryResult result)
    {
        await foreach (DuckDbChunk _ in result.GetChunksAsync()) { }
    }
}

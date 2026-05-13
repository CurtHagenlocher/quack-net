using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class NestedTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public NestedTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_IntegerList_WithEmptyAndNullRows()
    {
        // 4 rows in this chunk:
        //   row 0: [10, 20, 30]
        //   row 1: []            (empty list, not NULL)
        //   row 2: NULL          (validity bit cleared)
        //   row 3: [40]
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_intlist_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v INTEGER[])"));

        LogicalType listType = new(LogicalTypeId.List,
            new ListTypeInfo { ChildType = new LogicalType(LogicalTypeId.Integer) });

        byte[] mask = new byte[ValidityMask.RequiredByteCount(4)];
        mask[0] = 0b0000_1011; // rows 0, 1, 3 valid; row 2 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 4,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2, 3),
        };

        ListColumn listCol = new()
        {
            Type = listType,
            Count = 4,
            Validity = new ValidityMask(mask),
            // NULL row's entry is unused; the (0,0) sentinel is conventional.
            Entries = [(0UL, 3UL), (3UL, 0UL), (0UL, 0UL), (3UL, 1UL)],
            Child = new FixedSizeColumn
            {
                Type = new LogicalType(LogicalTypeId.Integer),
                Count = 4,
                ElementSize = 4,
                Data = Int32Bytes(10, 20, 30, 40),
            },
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, listType],
            Columns = [idCol, listCol],
            RowCount = 4,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(4, result.RowCount);
        ListColumn outList = Assert.IsType<ListColumn>(result.Columns[1]);
        FixedSizeColumn outChild = Assert.IsType<FixedSizeColumn>(outList.Child);

        // Row 0: [10, 20, 30]
        Assert.False(outList.IsNull(0));
        (ulong off0, ulong len0) = outList.Entries[0];
        Assert.Equal(3UL, len0);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(outChild.GetBytes((int)off0)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(outChild.GetBytes((int)off0 + 1)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(outChild.GetBytes((int)off0 + 2)));

        // Row 1: empty list (not NULL)
        Assert.False(outList.IsNull(1));
        Assert.Equal(0UL, outList.Entries[1].Length);

        // Row 2: NULL
        Assert.True(outList.IsNull(2));

        // Row 3: [40]
        Assert.False(outList.IsNull(3));
        (ulong off3, ulong len3) = outList.Entries[3];
        Assert.Equal(1UL, len3);
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(outChild.GetBytes((int)off3)));
    }

    [Fact]
    public async Task RoundTrip_StructWithChildNulls()
    {
        // 3 rows of STRUCT(a INT, b VARCHAR):
        //   row 0: (a=1, b="alpha")
        //   row 1: (a=NULL, b="beta")    -- only the a child is null
        //   row 2: (a=3, b=NULL)          -- only the b child is null
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_struct_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, v STRUCT(a INTEGER, b VARCHAR))"));

        LogicalType structType = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes =
            [
                new("a", new LogicalType(LogicalTypeId.Integer)),
                new("b", new LogicalType(LogicalTypeId.Varchar)),
            ],
        });

        byte[] aMask = new byte[ValidityMask.RequiredByteCount(3)];
        aMask[0] = 0b0000_0101; // a: rows 0 and 2 valid; row 1 NULL
        byte[] bMask = new byte[ValidityMask.RequiredByteCount(3)];
        bMask[0] = 0b0000_0011; // b: rows 0 and 1 valid; row 2 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };
        StructColumn structCol = new()
        {
            Type = structType,
            Count = 3,
            Fields =
            [
                new FixedSizeColumn
                {
                    Type = new LogicalType(LogicalTypeId.Integer),
                    Count = 3,
                    ElementSize = 4,
                    Data = Int32Bytes(1, 0, 3),
                    Validity = new ValidityMask(aMask),
                },
                new VarBytesColumn
                {
                    Type = new LogicalType(LogicalTypeId.Varchar),
                    Count = 3,
                    Validity = new ValidityMask(bMask),
                    Values =
                    [
                        (ReadOnlyMemory<byte>)"alpha"u8.ToArray(),
                        (ReadOnlyMemory<byte>)"beta"u8.ToArray(),
                        null,
                    ],
                },
            ],
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, structType],
            Columns = [idCol, structCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v.a, v.b FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(3, result.RowCount);
        FixedSizeColumn aOut = Assert.IsType<FixedSizeColumn>(result.Columns[1]);
        VarBytesColumn bOut = Assert.IsType<VarBytesColumn>(result.Columns[2]);

        Assert.False(aOut.IsNull(0));
        Assert.True(aOut.IsNull(1));
        Assert.False(aOut.IsNull(2));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(aOut.GetBytes(0)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(aOut.GetBytes(2)));

        Assert.False(bOut.IsNull(0));
        Assert.False(bOut.IsNull(1));
        Assert.True(bOut.IsNull(2));
        Assert.Equal("alpha"u8.ToArray(), bOut.Values[0]!.Value.ToArray());
        Assert.Equal("beta"u8.ToArray(), bOut.Values[1]!.Value.ToArray());
    }

    [Fact]
    public async Task RoundTrip_FixedSizeArray()
    {
        // INTEGER[3]: every row is exactly 3 ints. Child.Count = 3 * row_count.
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_array_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v INTEGER[3])"));

        LogicalType arrayType = new(LogicalTypeId.Array,
            new ArrayTypeInfo { ChildType = new LogicalType(LogicalTypeId.Integer), Size = 3 });

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 2,
            ElementSize = 4,
            Data = Int32Bytes(0, 1),
        };
        ArrayColumn arrayCol = new()
        {
            Type = arrayType,
            Count = 2,
            ArraySize = 3,
            Child = new FixedSizeColumn
            {
                Type = new LogicalType(LogicalTypeId.Integer),
                Count = 6,
                ElementSize = 4,
                Data = Int32Bytes(10, 20, 30, 40, 50, 60),
            },
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, arrayType],
            Columns = [idCol, arrayCol],
            RowCount = 2,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(2, result.RowCount);
        ArrayColumn outArray = Assert.IsType<ArrayColumn>(result.Columns[1]);
        Assert.Equal(3U, outArray.ArraySize);
        FixedSizeColumn outChild = Assert.IsType<FixedSizeColumn>(outArray.Child);
        Assert.Equal(6, outChild.Count);
        int[] expected = [10, 20, 30, 40, 50, 60];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], BinaryPrimitives.ReadInt32LittleEndian(outChild.GetBytes(i)));
        }
    }

    [Fact]
    public async Task RoundTrip_StructContainingList()
    {
        // STRUCT(a INT, b INT[]):
        //   row 0: (a=1, b=[10, 20])
        //   row 1: (a=2, b=[30])
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_struct_list_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, v STRUCT(a INTEGER, b INTEGER[]))"));

        LogicalType listType = new(LogicalTypeId.List,
            new ListTypeInfo { ChildType = new LogicalType(LogicalTypeId.Integer) });
        LogicalType structType = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes =
            [
                new("a", new LogicalType(LogicalTypeId.Integer)),
                new("b", listType),
            ],
        });

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 2,
            ElementSize = 4,
            Data = Int32Bytes(0, 1),
        };
        StructColumn structCol = new()
        {
            Type = structType,
            Count = 2,
            Fields =
            [
                new FixedSizeColumn
                {
                    Type = new LogicalType(LogicalTypeId.Integer),
                    Count = 2,
                    ElementSize = 4,
                    Data = Int32Bytes(1, 2),
                },
                new ListColumn
                {
                    Type = listType,
                    Count = 2,
                    Entries = [(0UL, 2UL), (2UL, 1UL)],
                    Child = new FixedSizeColumn
                    {
                        Type = new LogicalType(LogicalTypeId.Integer),
                        Count = 3,
                        ElementSize = 4,
                        Data = Int32Bytes(10, 20, 30),
                    },
                },
            ],
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, structType],
            Columns = [idCol, structCol],
            RowCount = 2,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v.a, v.b FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(2, result.RowCount);
        FixedSizeColumn aOut = Assert.IsType<FixedSizeColumn>(result.Columns[1]);
        ListColumn bOut = Assert.IsType<ListColumn>(result.Columns[2]);
        FixedSizeColumn bChild = Assert.IsType<FixedSizeColumn>(bOut.Child);

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(aOut.GetBytes(0)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(aOut.GetBytes(1)));

        (ulong off0, ulong len0) = bOut.Entries[0];
        Assert.Equal(2UL, len0);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(bChild.GetBytes((int)off0)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(bChild.GetBytes((int)off0 + 1)));

        (ulong off1, ulong len1) = bOut.Entries[1];
        Assert.Equal(1UL, len1);
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(bChild.GetBytes((int)off1)));
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

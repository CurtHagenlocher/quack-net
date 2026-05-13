using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class BitTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public BitTests(QuackServerFixture server) => _server = server;

    // Wire layout (duckdb/src/common/types/bit.cpp Bit::ToBit + Finalize):
    //   byte 0      : padding count = (8 - len % 8) % 8 in [0, 7]
    //   bytes 1..N  : `padding` bits of 1 at the front, then value bits MSB-first.
    //                 padding bits are explicitly set to 1, not 0 — surprising
    //                 and easy to get wrong if you reason from the C++ struct alone.
    [Theory]
    [InlineData("11111111", new byte[] { 0x00, 0xFF })]
    [InlineData("00000000", new byte[] { 0x00, 0x00 })]
    [InlineData("1010", new byte[] { 0x04, 0xFA })]      // 1111_1010
    [InlineData("0", new byte[] { 0x07, 0xFE })]          // 1111111_0
    [InlineData("1", new byte[] { 0x07, 0xFF })]          // 1111111_1
    [InlineData("101010101", new byte[] { 0x07, 0xFF, 0x55 })]
    public async Task SelectBit_HasExpectedWireFormat(string literal, byte[] expected)
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT BIT '{literal}'"))
            .ToListAsync();

        VarBytesColumn col = Assert.IsType<VarBytesColumn>(back[0].Columns[0]);
        byte[] actual = col.Values[0]!.Value.ToArray();
        Assert.Equal(
            BitConverter.ToString(expected),
            BitConverter.ToString(actual));
    }

    [Fact]
    public async Task RoundTrip_BitThroughAppendAndSelect()
    {
        // 3 rows:
        //   row 0: BIT '1010'   -> [0x04, 0xA0]
        //   row 1: BIT '11111111' (8 bits, no padding)
        //   row 2: NULL
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_bit_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v BIT)"));

        LogicalType bitType = new(LogicalTypeId.Bit);
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0011; // rows 0, 1 valid; row 2 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };

        VarBytesColumn bitCol = new()
        {
            Type = bitType,
            Count = 3,
            Validity = new ValidityMask(mask),
            Values =
            [
                (ReadOnlyMemory<byte>)DuckDbBit.Encode("1010"),
                (ReadOnlyMemory<byte>)DuckDbBit.Encode("11111111"),
                null,
            ],
        };

        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [idCol.Type, bitType],
            Columns = [idCol, bitCol],
            RowCount = 3,
        });

        // Cast each row to VARCHAR for an assertion that doesn't depend on the
        // bit-storage layout coming back unchanged.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, CAST(v AS VARCHAR) FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        VarBytesColumn text = Assert.IsType<VarBytesColumn>(result.Columns[1]);
        Assert.False(text.IsNull(0));
        Assert.False(text.IsNull(1));
        Assert.True(text.IsNull(2));
        Assert.Equal("1010"u8.ToArray(), text.Values[0]!.Value.ToArray());
        Assert.Equal("11111111"u8.ToArray(), text.Values[1]!.Value.ToArray());
    }

    private static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static async Task Drain(QuackQueryResult result)
    {
        await foreach (DuckDbChunk _ in result.GetChunksAsync()) { }
    }
}

using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class BlobTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public BlobTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_BlobWithNonUtf8Bytes_PreservesBinaryFidelity()
    {
        // Byte sequences that are NOT valid UTF-8. If we ever round-tripped
        // through System.String these would either error or be silently
        // replaced with U+FFFD.
        byte[][] payloads =
        [
            [0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD],
            [0xC0, 0x80],                  // overlong NUL — invalid UTF-8
            [0xED, 0xA0, 0x80],            // unpaired high surrogate U+D800 in UTF-8 form — invalid
            [0xFF, 0xFE, 0x00, 0x00],      // UTF-32 BE BOM — invalid as UTF-8
            [],                            // empty blob (distinct from NULL)
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_blob_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v BLOB)"));

        ReadOnlyMemory<byte>?[] values = payloads.Select(p => (ReadOnlyMemory<byte>?)p).ToArray();
        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = payloads.Length,
            ElementSize = 4,
            Data = Int32Bytes(Enumerable.Range(0, payloads.Length).ToArray()),
        };
        VarBytesColumn blobCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Blob),
            Count = payloads.Length,
            Values = values,
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, blobCol.Type],
            Columns = [idCol, blobCol],
            RowCount = payloads.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        VarBytesColumn received = Assert.IsType<VarBytesColumn>(back[0].Columns[1]);
        Assert.Equal(payloads.Length, received.Count);
        for (int i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(payloads[i], received.Values[i]!.Value.ToArray());
        }
    }

    [Fact]
    public async Task RoundTrip_BlobWithNullRow_DistinguishesNullFromEmpty()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_blob_null_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v BLOB)"));

        // 3 rows: row 0 has bytes, row 1 is NULL, row 2 is empty.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };
        VarBytesColumn blobCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Blob),
            Count = 3,
            Validity = new ValidityMask(mask),
            Values =
            [
                (ReadOnlyMemory<byte>)new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                null,
                ReadOnlyMemory<byte>.Empty,
            ],
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, blobCol.Type],
            Columns = [idCol, blobCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        VarBytesColumn received = Assert.IsType<VarBytesColumn>(back[0].Columns[1]);
        Assert.False(received.IsNull(0));
        Assert.True(received.IsNull(1));
        Assert.False(received.IsNull(2));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, received.Values[0]!.Value.ToArray());
        Assert.Equal(0, received.Values[2]!.Value.Length);
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

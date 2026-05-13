using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class DecimalTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public DecimalTests(QuackServerFixture server) => _server = server;

    [Theory]
    [InlineData(4, 2, new[] { "0", "12.34", "-12.34", "99.99", "-99.99" })]
    [InlineData(9, 2, new[] { "0", "1234567.89", "-1234567.89" })]
    [InlineData(18, 4, new[] { "0", "12345678901234.5678", "-12345678901234.5678" })]
    public async Task RoundTrip_DecimalThroughAppendAndSelect(byte width, byte scale, string[] texts)
    {
        decimal[] values = texts
            .Select(t => decimal.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_decimal_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v DECIMAL({width}, {scale}))"));

        FixedSizeColumn col = DuckDbDecimal.CreateColumn(values, width, scale);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v"))
            .ToListAsync();

        decimal[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbDecimal.GetDecimal((FixedSizeColumn)c.Columns[0], i)))
            .ToArray();

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Fact]
    public async Task RoundTrip_HighPrecisionDecimal_ViaInt128Mantissa()
    {
        // DECIMAL(38, 10) is the largest precision; mantissa requires Int128.
        Int128 mantissa = Int128.Parse("12345678901234567890123456789");
        Int128[] mantissas = [mantissa, -mantissa, Int128.Zero];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_decimal_huge_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v DECIMAL(38, 10))"));

        FixedSizeColumn col = DuckDbDecimal.CreateColumn(mantissas, width: 38, scale: 10);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = mantissas.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v"))
            .ToListAsync();

        Int128[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbDecimal.GetMantissa((FixedSizeColumn)c.Columns[0], i)))
            .ToArray();

        Array.Sort(mantissas);
        Assert.Equal(mantissas, received);
    }

    [Fact]
    public async Task AppendAsync_DecimalWithNulls_PreservesNullness()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_decimal_nulls_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v DECIMAL(18, 4))"));

        // 3 rows. Middle decimal is NULL.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(1, 2, 3),
        };
        FixedSizeColumn decimalCol = DuckDbDecimal.CreateColumn(
            new[] { 1.5m, 0m, 3.5m },
            width: 18,
            scale: 4,
            validity: new ValidityMask(mask));

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, decimalCol.Type],
            Columns = [idCol, decimalCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        FixedSizeColumn dec = Assert.IsType<FixedSizeColumn>(result.Columns[1]);
        Assert.Equal(3, result.RowCount);
        Assert.False(dec.IsNull(0));
        Assert.True(dec.IsNull(1));
        Assert.False(dec.IsNull(2));
        Assert.Equal(1.5m, DuckDbDecimal.GetDecimal(dec, 0));
        Assert.Equal(3.5m, DuckDbDecimal.GetDecimal(dec, 2));
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

using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class TimeNsTzTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public TimeNsTzTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_TimeNsThroughAppendAndSelect()
    {
        TimeOnly[] values =
        [
            new(0, 0, 0),
            new(6, 30, 0),
            new(12, 34, 56, 789, 123),
            new(23, 59, 59, 999, 999),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_timens_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v TIME_NS)"));

        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(values);
        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v"))
            .ToListAsync();

        TimeOnly[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbTimeNs.GetTime((FixedSizeColumn)c.Columns[0], i)))
            .ToArray();

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Fact]
    public async Task RoundTrip_TimeTzThroughAppendAndSelect()
    {
        // micros = (12h*3600 + 34m*60 + 56s) * 1e6 = 45,296,000,000
        DuckDbTimeTz[] values =
        [
            new(0L, 0),                       // 00:00:00 UTC
            new(45_296_000_000L, 7200),       // 12:34:56 +02:00
            new(45_296_000_000L, -25200),     // 12:34:56 -07:00
            new(86_399_000_000L, 0),          // 23:59:59 UTC
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_timetz_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v TIMETZ)"));

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = values.Length,
            ElementSize = 4,
            Data = Int32Bytes(Enumerable.Range(0, values.Length).ToArray()),
        };
        FixedSizeColumn tzCol = DuckDbTimeTz.CreateColumn(values);
        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [idCol.Type, tzCol.Type],
            Columns = [idCol, tzCol],
            RowCount = values.Length,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbTimeTz[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbTimeTz.Get((FixedSizeColumn)c.Columns[1], i)))
            .ToArray();

        Assert.Equal(values, received);
    }

    [Fact]
    public async Task RoundTrip_TimeTzCastVerification()
    {
        // Verify that the server interprets our offset correctly by casting
        // to VARCHAR and comparing against the expected ISO-style rendering.
        DuckDbTimeTz value = new(45_296_000_000L, 7200);
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_timetz_str_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v TIMETZ)"));

        FixedSizeColumn col = DuckDbTimeTz.CreateColumn(new[] { value });
        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = 1,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT CAST(v AS VARCHAR) FROM {table}"))
            .ToListAsync();

        VarBytesColumn names = Assert.IsType<VarBytesColumn>(back[0].Columns[0]);
        Assert.Equal("12:34:56+02"u8.ToArray(), names.Values[0]!.Value.ToArray());
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

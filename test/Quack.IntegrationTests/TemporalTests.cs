using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class TemporalTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public TemporalTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_DateThroughAppendAndSelect()
    {
        DateOnly[] values =
        [
            new(1970, 1, 1),
            new(2026, 5, 13),
            new(1999, 12, 31),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_date_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v DATE)"));

        FixedSizeColumn col = DuckDbDate.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        DateOnly[] received = await ReadAllAsync(conn, $"SELECT v FROM {table} ORDER BY v",
            (c, i) => DuckDbDate.GetDate((FixedSizeColumn)c.Columns[0], i));

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Fact]
    public async Task RoundTrip_TimeThroughAppendAndSelect()
    {
        TimeOnly[] values =
        [
            new(0, 0, 0),
            new(12, 34, 56, 789),
            new(23, 59, 59, 999),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_time_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v TIME)"));

        FixedSizeColumn col = DuckDbTime.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        TimeOnly[] received = await ReadAllAsync(conn, $"SELECT v FROM {table} ORDER BY v",
            (c, i) => DuckDbTime.GetTime((FixedSizeColumn)c.Columns[0], i));

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Theory]
    [InlineData(LogicalTypeId.Timestamp, "TIMESTAMP")]
    [InlineData(LogicalTypeId.TimestampSec, "TIMESTAMP_S")]
    [InlineData(LogicalTypeId.TimestampMs, "TIMESTAMP_MS")]
    [InlineData(LogicalTypeId.TimestampNs, "TIMESTAMP_NS")]
    public async Task RoundTrip_TimestampVariantsThroughAppendAndSelect(LogicalTypeId kind, string sqlType)
    {
        // Values without sub-second precision so SECOND-precision can store them.
        DateTime[] values =
        [
            new(1970, 1, 1, 0, 0, 0),
            new(2026, 5, 13, 12, 34, 56),
            new(1999, 12, 31, 23, 59, 59),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_ts_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v {sqlType})"));

        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(values, kind);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        DateTime[] received = await ReadAllAsync(conn, $"SELECT v FROM {table} ORDER BY v",
            (c, i) => DuckDbTimestamp.GetDateTime((FixedSizeColumn)c.Columns[0], i));

        DateTime[] expected = values
            .Select(v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified))
            .OrderBy(v => v)
            .ToArray();
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task RoundTrip_TimestampTzPreservesUtcInstant()
    {
        DateTimeOffset[] values =
        [
            new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 5, 13, 12, 34, 56, TimeSpan.FromHours(-7)),
            new(1999, 12, 31, 23, 59, 59, TimeSpan.FromHours(9)),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_tstz_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v TIMESTAMPTZ)"));

        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        DateTimeOffset[] received = await ReadAllAsync(conn, $"SELECT v FROM {table} ORDER BY v",
            (c, i) => DuckDbTimestamp.GetDateTimeOffset((FixedSizeColumn)c.Columns[0], i));

        DateTimeOffset[] expected = values.OrderBy(v => v.UtcDateTime).ToArray();
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task RoundTrip_IntervalThroughAppendAndSelect()
    {
        DuckDbInterval[] values =
        [
            new(0, 0, 0),
            new(1, 2, 3_000_000),
            new(-13, -5, -1_500_000_000L),
            new(12, 30, 86_400_000_000L),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_iv_" + Guid.NewGuid().ToString("N");
        // Append rows with a per-row id so we can recover insertion order even
        // though INTERVAL has no total ordering DuckDB exposes via ORDER BY.
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v INTERVAL)"));

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = values.Length,
            ElementSize = 4,
            Data = Int32Bytes(Enumerable.Range(0, values.Length).ToArray()),
        };
        FixedSizeColumn intervalCol = DuckDbInterval.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, intervalCol.Type],
            Columns = [idCol, intervalCol],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbInterval[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbInterval.Get((FixedSizeColumn)c.Columns[1], i)))
            .ToArray();

        Assert.Equal(values, received);
    }

    [Fact]
    public async Task AppendAsync_TemporalWithNulls_PreservesNullness()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_temporal_nulls_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, d DATE, t TIME, ts TIMESTAMP)"));

        // 3 rows. Middle d, t, and ts are NULL.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(1, 2, 3),
        };
        FixedSizeColumn dateCol = DuckDbDate.CreateColumn(
            new[] { new DateOnly(2026, 5, 13), new(2000, 1, 1), new(1999, 12, 31) },
            new ValidityMask(mask));
        FixedSizeColumn timeCol = DuckDbTime.CreateColumn(
            new[] { new TimeOnly(1, 2, 3), new(0, 0, 0), new(23, 59, 59) },
            new ValidityMask(mask));
        FixedSizeColumn tsCol = DuckDbTimestamp.CreateColumn(
            new[] { new DateTime(2026, 5, 13, 12, 0, 0), new(2000, 1, 1, 0, 0, 0), new(1999, 12, 31, 23, 59, 59) },
            LogicalTypeId.Timestamp,
            new ValidityMask(mask));

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, dateCol.Type, timeCol.Type, tsCol.Type],
            Columns = [idCol, dateCol, timeCol, tsCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, d, t, ts FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        FixedSizeColumn dResult = Assert.IsType<FixedSizeColumn>(result.Columns[1]);
        FixedSizeColumn tResult = Assert.IsType<FixedSizeColumn>(result.Columns[2]);
        FixedSizeColumn tsResult = Assert.IsType<FixedSizeColumn>(result.Columns[3]);

        Assert.Equal(3, result.RowCount);
        Assert.False(dResult.IsNull(0));
        Assert.True(dResult.IsNull(1));
        Assert.False(dResult.IsNull(2));
        Assert.True(tResult.IsNull(1));
        Assert.True(tsResult.IsNull(1));

        Assert.Equal(new DateOnly(2026, 5, 13), DuckDbDate.GetDate(dResult, 0));
        Assert.Equal(new DateOnly(1999, 12, 31), DuckDbDate.GetDate(dResult, 2));
        Assert.Equal(new TimeOnly(1, 2, 3), DuckDbTime.GetTime(tResult, 0));
        Assert.Equal(new DateTime(2026, 5, 13, 12, 0, 0), DuckDbTimestamp.GetDateTime(tsResult, 0));
    }

    private static async Task<T[]> ReadAllAsync<T>(QuackConnection conn, string sql, Func<DuckDbChunk, int, T> read)
    {
        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync(sql)).ToListAsync();
        return back
            .SelectMany(c => Enumerable.Range(0, c.RowCount).Select(i => read(c, i)))
            .ToArray();
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

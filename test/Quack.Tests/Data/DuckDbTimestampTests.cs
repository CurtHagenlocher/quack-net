using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbTimestampTests
{
    [Theory]
    [InlineData(LogicalTypeId.Timestamp)]
    [InlineData(LogicalTypeId.TimestampSec)]
    [InlineData(LogicalTypeId.TimestampMs)]
    [InlineData(LogicalTypeId.TimestampNs)]
    [InlineData(LogicalTypeId.TimestampTz)]
    public void RoundTrip_DateTimeThroughCreateAndGet(LogicalTypeId kind)
    {
        // Value chosen to be representable in all five variants (no sub-second
        // fraction so SECOND-precision doesn't truncate).
        DateTime value = new(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value }, kind);

        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        // Naive variants come back as Unspecified; TZ comes back as Utc.
        DateTimeKind expectedKind = kind == LogicalTypeId.TimestampTz
            ? DateTimeKind.Utc
            : DateTimeKind.Unspecified;
        Assert.Equal(expectedKind, back.Kind);
        Assert.Equal(value.Year, back.Year);
        Assert.Equal(value.Month, back.Month);
        Assert.Equal(value.Day, back.Day);
        Assert.Equal(value.Hour, back.Hour);
        Assert.Equal(value.Minute, back.Minute);
        Assert.Equal(value.Second, back.Second);
    }

    [Fact]
    public void Microsecond_PrecisionPreservedForDefaultTimestamp()
    {
        // 1234 micros = 12340 ticks; survives the micros round-trip.
        DateTime value = new DateTime(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddTicks(12340);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value });
        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        Assert.Equal(12340L, back.Ticks - new DateTime(2026, 5, 13, 12, 34, 56).Ticks);
    }

    [Fact]
    public void SubMicrosecond_TruncatedForDefaultTimestamp()
    {
        // 7 ticks (700 ns) is below TIMESTAMP's microsecond resolution.
        DateTime value = new DateTime(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddTicks(7);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value });
        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        Assert.Equal(new DateTime(2026, 5, 13, 12, 34, 56), back);
    }

    [Fact]
    public void NanosecondPrecision_PreservedForTimestampNs()
    {
        // 7 ticks = 700 nanoseconds. TIMESTAMP_NS stores nanoseconds so this
        // should survive a round-trip (rounded to nearest 100ns = 1 tick).
        DateTime value = new DateTime(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddTicks(7);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value }, LogicalTypeId.TimestampNs);
        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        Assert.Equal(7, back.Ticks - new DateTime(2026, 5, 13, 12, 34, 56).Ticks);
    }

    [Fact]
    public void SecondPrecision_TruncatesSubSecondForTimestampSec()
    {
        DateTime value = new DateTime(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddMilliseconds(789);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value }, LogicalTypeId.TimestampSec);
        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        Assert.Equal(new DateTime(2026, 5, 13, 12, 34, 56), back);
    }

    [Fact]
    public void TimestampTz_FromDateTimeOffset_RoundTripsAsUtc()
    {
        DateTimeOffset value = new(2026, 5, 13, 12, 34, 56, TimeSpan.FromHours(-7));
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { value });

        Assert.Equal(LogicalTypeId.TimestampTz, col.Type.Id);
        DateTimeOffset back = DuckDbTimestamp.GetDateTimeOffset(col, 0);
        // DateTimeOffset comparison is by UTC instant, ignoring local offset.
        Assert.Equal(value, back);
        Assert.Equal(TimeSpan.Zero, back.Offset);
    }

    [Fact]
    public void GetDateTimeOffset_OnNaiveTimestamp_Throws()
    {
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { DateTime.UnixEpoch });
        Assert.Throws<InvalidOperationException>(() => DuckDbTimestamp.GetDateTimeOffset(col, 0));
    }

    [Fact]
    public void TimestampTz_FromLocalDateTime_ConvertsToUtc()
    {
        DateTime local = new(2026, 5, 13, 12, 0, 0, DateTimeKind.Local);
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { local }, LogicalTypeId.TimestampTz);
        DateTime back = DuckDbTimestamp.GetDateTime(col, 0);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
        Assert.Equal(local.ToUniversalTime(), back);
    }

    [Theory]
    [InlineData(LogicalTypeId.Timestamp)]
    [InlineData(LogicalTypeId.TimestampSec)]
    [InlineData(LogicalTypeId.TimestampMs)]
    [InlineData(LogicalTypeId.TimestampNs)]
    [InlineData(LogicalTypeId.TimestampTz)]
    public void InfinitySentinel_GetRawAccessibleButGetDateTimeThrows(LogicalTypeId kind)
    {
        FixedSizeColumn pos = DuckDbTimestamp.CreateColumn(new[] { long.MaxValue }, kind);
        FixedSizeColumn neg = DuckDbTimestamp.CreateColumn(new[] { -long.MaxValue }, kind);

        Assert.Equal(long.MaxValue, DuckDbTimestamp.GetRaw(pos, 0));
        Assert.Equal(-long.MaxValue, DuckDbTimestamp.GetRaw(neg, 0));
        Assert.Throws<OverflowException>(() => DuckDbTimestamp.GetDateTime(pos, 0));
        Assert.Throws<OverflowException>(() => DuckDbTimestamp.GetDateTime(neg, 0));
    }

    [Fact]
    public void RoundTrip_RawMicrosArray()
    {
        long[] raw = [0L, 1_000_000L, -1_000_000L, 1_700_000_000_000_000L];
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(raw);

        for (int i = 0; i < raw.Length; i++)
        {
            Assert.Equal(raw[i], DuckDbTimestamp.GetRaw(col, i));
        }
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(
            new[] { DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1), DateTime.UnixEpoch.AddSeconds(2) },
            LogicalTypeId.Timestamp,
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void GetDateTime_OnNonTimestampColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbTimestamp.GetDateTime(intCol, 0));
        Assert.Throws<InvalidOperationException>(() => DuckDbTimestamp.GetRaw(intCol, 0));
    }

    [Fact]
    public void CreateColumn_WithNonTimestampKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbTimestamp.CreateColumn(new[] { DateTime.UnixEpoch }, LogicalTypeId.Integer));
    }

    [Fact]
    public void Storage_IsLittleEndianInt64MicrosSinceEpoch()
    {
        // 1970-01-01 00:00:01 UTC -> 1_000_000 micros.
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(new[] { DateTime.UnixEpoch.AddSeconds(1) });
        Assert.Equal(1_000_000L, BinaryPrimitives.ReadInt64LittleEndian(col.GetBytes(0)));
    }

    [Theory]
    [InlineData(LogicalTypeId.Timestamp)]
    [InlineData(LogicalTypeId.TimestampSec)]
    [InlineData(LogicalTypeId.TimestampMs)]
    [InlineData(LogicalTypeId.TimestampNs)]
    [InlineData(LogicalTypeId.TimestampTz)]
    public void WireRoundTrip_TimestampColumnThroughDataChunkSerialization(LogicalTypeId kind)
    {
        DateTime[] values =
        [
            new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 5, 13, 12, 34, 56, DateTimeKind.Utc),
            new(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
        ];
        FixedSizeColumn col = DuckDbTimestamp.CreateColumn(values, kind);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };

        ArrayBufferWriter<byte> buffer = new();
        DataChunkWriter.Write(new BinarySerializer(buffer), chunk);
        DuckDbChunk round = DataChunkReader.Read(new BinaryDeserializer(buffer.WrittenMemory));

        FixedSizeColumn back = Assert.IsType<FixedSizeColumn>(round.Columns[0]);
        Assert.Equal(kind, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            DateTime expected = DateTime.SpecifyKind(values[i],
                kind == LogicalTypeId.TimestampTz ? DateTimeKind.Utc : DateTimeKind.Unspecified);
            Assert.Equal(expected, DuckDbTimestamp.GetDateTime(back, i));
        }
    }
}

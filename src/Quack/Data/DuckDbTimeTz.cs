using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// DuckDB TIME WITH TIME ZONE value: a 64-bit packed integer storing a
// time-of-day plus a UTC offset.
// Wire layout (see duckdb/common/types/datetime.hpp dtime_tz_t):
//   bits 24..63 (high 40 bits) : `micros` since 00:00:00
//   bits 0..23  (low 24 bits)  : encoded offset = MAX_OFFSET - offset_seconds
//                                where MAX_OFFSET = 16*3600 - 1 = 57599
// The offset encoding inverts the sign so that ascending bit-order sort
// agrees with chronological-instant order.
public readonly record struct DuckDbTimeTz(long Microseconds, int OffsetSeconds)
{
    private const int TimeBits = 40;
    private const int OffsetBits = 24;
    private const int MaxOffset = 16 * 60 * 60 - 1;
    private const long OffsetMask = (1L << OffsetBits) - 1;

    public static DuckDbTimeTz Get(FixedSizeColumn column, int rowIndex)
    {
        EnsureTimeTzColumn(column);
        long bits = BinaryPrimitives.ReadInt64LittleEndian(column.GetBytes(rowIndex));
        long micros = bits >> OffsetBits;
        int encodedOffset = (int)(bits & OffsetMask);
        int offsetSeconds = MaxOffset - encodedOffset;
        return new DuckDbTimeTz(micros, offsetSeconds);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<DuckDbTimeTz> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            long bits = values[i].ToBits();
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), bits);
        }
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.TimeTz),
            Count = values.Length,
            ElementSize = sizeof(long),
            Data = data,
            Validity = validity,
        };
    }

    internal long ToBits()
    {
        if (OffsetSeconds < -(MaxOffset + 1) || OffsetSeconds > MaxOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OffsetSeconds),
                $"TIME_TZ offset {OffsetSeconds}s exceeds DuckDB's range (±{MaxOffset + 1}s).");
        }
        if (Microseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Microseconds),
                $"TIME_TZ microseconds must be non-negative; got {Microseconds}.");
        }
        long encodedOffset = MaxOffset - OffsetSeconds;
        return (Microseconds << OffsetBits) | (encodedOffset & OffsetMask);
    }

    private static void EnsureTimeTzColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.TimeTz)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbTimeTz helpers require TIME WITH TIME ZONE.");
        }
    }

    public override string ToString()
    {
        int totalSeconds = (int)(Microseconds / 1_000_000);
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        int frac = (int)(Microseconds % 1_000_000);
        int offsetHours = OffsetSeconds / 3600;
        int offsetMinutes = Math.Abs(OffsetSeconds % 3600) / 60;
        string offsetSign = OffsetSeconds >= 0 ? "+" : "-";
        return frac == 0
            ? $"{hours:D2}:{minutes:D2}:{seconds:D2}{offsetSign}{Math.Abs(offsetHours):D2}:{offsetMinutes:D2}"
            : $"{hours:D2}:{minutes:D2}:{seconds:D2}.{frac:D6}{offsetSign}{Math.Abs(offsetHours):D2}:{offsetMinutes:D2}";
    }
}

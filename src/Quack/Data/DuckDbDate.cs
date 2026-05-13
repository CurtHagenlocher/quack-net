using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing DATE columns. DuckDB stores a DATE as a
// signed int32 day count relative to 1970-01-01. Sentinels are +/-INT32_MAX
// (NOT INT32_MIN — INT32_MIN is a valid finite date) representing infinity.
public static class DuckDbDate
{
    private static readonly DateOnly UnixEpoch = new(1970, 1, 1);
    private const int InfinityDays = int.MaxValue;
    private const int NegativeInfinityDays = -int.MaxValue;

    public static int GetDays(FixedSizeColumn column, int rowIndex)
    {
        EnsureDateColumn(column);
        return BinaryPrimitives.ReadInt32LittleEndian(column.GetBytes(rowIndex));
    }

    public static DateOnly GetDate(FixedSizeColumn column, int rowIndex)
    {
        return DaysToDateOnly(GetDays(column, rowIndex));
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<DateOnly> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * sizeof(int)];
        for (int i = 0; i < values.Length; i++)
        {
            int days = values[i].DayNumber - UnixEpoch.DayNumber;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * sizeof(int), sizeof(int)), days);
        }
        return BuildColumn(data, values.Length, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<int> days, ValidityMask validity = default)
    {
        byte[] data = new byte[days.Length * sizeof(int)];
        for (int i = 0; i < days.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * sizeof(int), sizeof(int)), days[i]);
        }
        return BuildColumn(data, days.Length, validity);
    }

    internal static DateOnly DaysToDateOnly(int days)
    {
        if (days == InfinityDays || days == NegativeInfinityDays)
        {
            throw new OverflowException(
                $"DATE value {days} is the {(days > 0 ? "positive" : "negative")} infinity sentinel; use GetDays for raw access.");
        }
        try
        {
            return UnixEpoch.AddDays(days);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new OverflowException(
                $"DATE value {days} days since 1970-01-01 is outside DateOnly's [0001-01-01, 9999-12-31] range.", ex);
        }
    }

    private static FixedSizeColumn BuildColumn(byte[] data, int count, ValidityMask validity)
    {
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.Date),
            Count = count,
            ElementSize = sizeof(int),
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureDateColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.Date)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbDate helpers require DATE.");
        }
    }
}

using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// DuckDB INTERVAL value: 16-byte struct { int32 months; int32 days; int64 micros }.
// The three components are independent: DuckDB does not normalize months into
// days or days into micros because their length is calendar-dependent.
public readonly record struct DuckDbInterval(int Months, int Days, long Microseconds)
{
    public static DuckDbInterval Get(FixedSizeColumn column, int rowIndex)
    {
        EnsureIntervalColumn(column);
        ReadOnlySpan<byte> bytes = column.GetBytes(rowIndex);
        int months = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        int days = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
        long micros = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8));
        return new DuckDbInterval(months, days, micros);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<DuckDbInterval> values, ValidityMask validity = default)
    {
        const int ElementSize = 16;
        byte[] data = new byte[values.Length * ElementSize];
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> slot = data.AsSpan(i * ElementSize, ElementSize);
            BinaryPrimitives.WriteInt32LittleEndian(slot[..4], values[i].Months);
            BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(4, 4), values[i].Days);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(8, 8), values[i].Microseconds);
        }
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.Interval),
            Count = values.Length,
            ElementSize = ElementSize,
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureIntervalColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.Interval)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbInterval helpers require INTERVAL.");
        }
    }
}

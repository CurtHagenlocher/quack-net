using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing HUGEINT columns. DuckDB stores
// HUGEINT as a 16-byte hugeint_t struct (see HugeintLayout). The logical
// type is signed; an unsigned 128-bit value should go through DuckDbUHugeInt.
public static class DuckDbHugeInt
{
    public static Int128 Get(FixedSizeColumn column, int rowIndex)
    {
        EnsureHugeIntColumn(column);
        return HugeintLayout.ReadSigned(column.GetBytes(rowIndex));
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<Int128> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * HugeintLayout.ByteSize];
        for (int i = 0; i < values.Length; i++)
        {
            HugeintLayout.WriteSigned(
                data.AsSpan(i * HugeintLayout.ByteSize, HugeintLayout.ByteSize),
                values[i]);
        }
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.HugeInt),
            Count = values.Length,
            ElementSize = HugeintLayout.ByteSize,
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureHugeIntColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.HugeInt)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbHugeInt helpers require HUGEINT.");
        }
    }
}

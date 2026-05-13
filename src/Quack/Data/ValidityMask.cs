namespace Quack.Data;

// DuckDB validity-bitmap layout:
//   - Packed in 64-bit words, written little-endian. byte 0 carries bits 0-7
//     (row 0 in the LSB), byte 1 carries bits 8-15, etc.
//   - 1 means the row is valid; 0 means NULL.
//   - Total bytes = ((count + 63) / 64) * 8.
// An empty mask means "all rows valid" (matches duckdb::ValidityMask::AllValid).
public readonly struct ValidityMask
{
    public static readonly ValidityMask AllValid = default;

    public ReadOnlyMemory<byte> Bytes { get; }

    public ValidityMask(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }

    public bool IsAllValid => Bytes.IsEmpty;

    public bool IsValid(int rowIndex)
    {
        if (Bytes.IsEmpty) return true;
        int byteIndex = rowIndex >> 3;
        int bitIndex = rowIndex & 7;
        return (Bytes.Span[byteIndex] & (1 << bitIndex)) != 0;
    }

    public bool IsNull(int rowIndex) => !IsValid(rowIndex);

    public static int RequiredByteCount(int rowCount)
    {
        // ValidityMask::ValidityMaskSize: round up to 64-bit boundary.
        return ((rowCount + 63) / 64) * 8;
    }
}

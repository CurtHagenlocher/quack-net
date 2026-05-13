using Quack.Types;

namespace Quack.Data;

// A materialized column of a DuckDB DataChunk. The shape mirrors DuckDB's
// physical storage: each subclass corresponds to a PhysicalType family.
public abstract class DuckDbColumn
{
    public required LogicalType Type { get; init; }
    public required int Count { get; init; }
    public ValidityMask Validity { get; init; } = ValidityMask.AllValid;

    public bool IsNull(int rowIndex) => Validity.IsNull(rowIndex);
}

// Constant-size primitive column. `Data` holds Count * ElementSize bytes,
// laid out exactly as DuckDB's in-memory storage:
//   INT8/UINT8/BOOL: 1 byte each
//   INT16/UINT16: 2 bytes (LE)
//   INT32/UINT32/FLOAT/DATE: 4 bytes (LE)
//   INT64/UINT64/DOUBLE/TIME/TIMESTAMP*: 8 bytes (LE)
//   INT128/UINT128/UUID: 16 bytes, struct layout {int64 upper; uint64 lower}
//                       — i.e. bytes 0..7 = upper LE, bytes 8..15 = lower LE.
//   INTERVAL: 16 bytes, struct {int32 months; int32 days; int64 micros} LE.
public sealed class FixedSizeColumn : DuckDbColumn
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required int ElementSize { get; init; }

    public ReadOnlySpan<byte> GetBytes(int rowIndex) =>
        Data.Span.Slice(rowIndex * ElementSize, ElementSize);
}

// VARCHAR / BLOB / BIT / GEOMETRY-as-WKB column. Holds raw UTF-8 / binary
// payloads — decoding to System.String is left to the consuming layer so
// that BLOB is not lossy and so an Arrow handoff doesn't pay for a string
// round-trip. Null entries are represented by null elements in the array
// (independently of the validity mask); empty payloads are an empty slice.
// Reader-returned slices alias the wire buffer; copy with .ToArray() if you
// need to outlive that buffer.
public sealed class VarBytesColumn : DuckDbColumn
{
    public required ReadOnlyMemory<byte>?[] Values { get; init; }
}

// LIST or MAP column. Each row carries (offset, length) into the flat child
// column. MAP is just a LIST<STRUCT<key, value>> internally.
public sealed class ListColumn : DuckDbColumn
{
    public required (ulong Offset, ulong Length)[] Entries { get; init; }
    public required DuckDbColumn Child { get; init; }
}

// STRUCT or UNION column. UNION is a STRUCT with the first field being a
// uint8 tag.
public sealed class StructColumn : DuckDbColumn
{
    public required DuckDbColumn[] Fields { get; init; }
}

// Fixed-size ARRAY column. Child.Count = ArraySize * parent Count.
public sealed class ArrayColumn : DuckDbColumn
{
    public required uint ArraySize { get; init; }
    public required DuckDbColumn Child { get; init; }
}

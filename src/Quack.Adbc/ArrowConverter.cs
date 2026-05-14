using Apache.Arrow;
using Apache.Arrow.Types;
using Quack.Data;
using Quack.Types;

namespace Quack.Adbc;

// Converts Quack's DuckDbChunk / DuckDbColumn types to Apache Arrow Schema
// and RecordBatch. Stage 1 of the type-coverage rollout: Int32, Int64,
// Double, and Varchar — chosen because they're the most common and let us
// prove the end-to-end pipeline. Subsequent stages will add decimal,
// temporal, hugeint, nested, etc.
//
// Zero-copy strategy: Quack's FixedSizeColumn.Data is stored in the same
// physical layout (little-endian, packed) that Arrow expects, so we wrap
// it in an ArrowBuffer rather than re-encoding. The validity bitmap is
// likewise wire-compatible (LSB-first, 1 = valid, 64-bit padded — both
// formats agree). VarBytesColumn requires building an offsets buffer
// because Quack stores per-row byte slices while Arrow stores
// concatenated bytes + int32 offsets.
internal static class ArrowConverter
{
    public static Schema BuildSchema(IReadOnlyList<string> names, IReadOnlyList<LogicalType> types)
    {
        if (names.Count != types.Count)
        {
            throw new InvalidOperationException(
                $"Column name count ({names.Count}) does not match type count ({types.Count}).");
        }
        Schema.Builder builder = new();
        for (int i = 0; i < names.Count; i++)
        {
            builder.Field(new Field(names[i], MapType(types[i]), nullable: true));
        }
        return builder.Build();
    }

    public static RecordBatch ToRecordBatch(Schema schema, DuckDbChunk chunk)
    {
        IArrowArray[] columns = new IArrowArray[chunk.Columns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = ToArray(chunk.Columns[i], chunk.RowCount);
        }
        return new RecordBatch(schema, columns, chunk.RowCount);
    }

    private static IArrowType MapType(LogicalType logical) => logical.Id switch
    {
        LogicalTypeId.Integer => Int32Type.Default,
        LogicalTypeId.BigInt => Int64Type.Default,
        LogicalTypeId.Double => DoubleType.Default,
        LogicalTypeId.Varchar => StringType.Default,
        _ => throw new NotSupportedException(
            $"Arrow conversion for DuckDB type '{logical.Id}' is not implemented yet."),
    };

    private static IArrowArray ToArray(DuckDbColumn column, int rowCount)
    {
        ArrowBuffer validity = BuildValidityBuffer(column, rowCount, out int nullCount);

        return column switch
        {
            FixedSizeColumn fixedCol when column.Type.Id == LogicalTypeId.Integer
                => new Int32Array(new ArrowBuffer(fixedCol.Data), validity, length: rowCount, nullCount: nullCount, offset: 0),
            FixedSizeColumn fixedCol when column.Type.Id == LogicalTypeId.BigInt
                => new Int64Array(new ArrowBuffer(fixedCol.Data), validity, length: rowCount, nullCount: nullCount, offset: 0),
            FixedSizeColumn fixedCol when column.Type.Id == LogicalTypeId.Double
                => new DoubleArray(new ArrowBuffer(fixedCol.Data), validity, length: rowCount, nullCount: nullCount, offset: 0),
            VarBytesColumn bytesCol when column.Type.Id == LogicalTypeId.Varchar
                => BuildStringArray(bytesCol, validity, nullCount, rowCount),
            _ => throw new NotSupportedException(
                $"Arrow array build for '{column.Type.Id}' on column kind '{column.GetType().Name}' is not implemented yet."),
        };
    }

    private static ArrowBuffer BuildValidityBuffer(DuckDbColumn column, int rowCount, out int nullCount)
    {
        if (column.Validity.IsAllValid)
        {
            nullCount = 0;
            return ArrowBuffer.Empty;
        }
        // Quack stores validity as packed LSB-first bits with 1 = valid,
        // rounded up to 64-bit words. Arrow uses the same convention, so the
        // buffer is wire-compatible. We do still need to count nulls so the
        // Arrow array's NullCount is correct.
        int nulls = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (column.Validity.IsNull(i)) nulls++;
        }
        nullCount = nulls;
        return new ArrowBuffer(column.Validity.Bytes);
    }

    private static StringArray BuildStringArray(VarBytesColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        // Arrow's StringArray layout: { validity?; offsets[rowCount+1] of int32;
        // values[total_bytes] of UTF-8 bytes }. Quack's VarBytesColumn stores
        // a per-row ReadOnlyMemory<byte>; we concatenate the value bytes and
        // build the offsets buffer in one pass.
        int totalBytes = 0;
        for (int i = 0; i < rowCount; i++)
        {
            ReadOnlyMemory<byte>? slot = column.Values[i];
            if (slot.HasValue) totalBytes += slot.Value.Length;
        }

        ArrowBuffer.Builder<int> offsets = new(capacity: rowCount + 1);
        byte[] values = new byte[totalBytes];
        int cursor = 0;
        offsets.Append(0);
        for (int i = 0; i < rowCount; i++)
        {
            ReadOnlyMemory<byte>? slot = column.Values[i];
            if (slot.HasValue && !column.Validity.IsNull(i))
            {
                slot.Value.Span.CopyTo(values.AsSpan(cursor));
                cursor += slot.Value.Length;
            }
            offsets.Append(cursor);
        }
        return new StringArray(
            valueOffsetsBuffer: offsets.Build(),
            dataBuffer: new ArrowBuffer(values),
            nullBitmapBuffer: validity,
            length: rowCount,
            nullCount: nullCount,
            offset: 0);
    }
}

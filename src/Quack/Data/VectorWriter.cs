using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Write path for one Vector. Mirrors duckdb::Vector::Serialize but only emits
// FLAT vectors — we leave compression to the server (`compressed_serialization`
// would be false in the C++ call, which skips the field 90 vector_type tag).
internal static class VectorWriter
{
    public static void Write(BinarySerializer s, DuckDbColumn column, int count)
    {
        if (column.Count != count)
        {
            throw new ArgumentException(
                $"Column count ({column.Count}) does not match the chunk row count ({count}).", nameof(column));
        }

        bool hasValidity = !column.Validity.IsAllValid;
        s.WriteProperty(fieldId: 100, hasValidity);
        if (hasValidity)
        {
            int expected = ValidityMask.RequiredByteCount(count);
            if (column.Validity.Bytes.Length != expected)
            {
                throw new SerializationException(
                    $"Validity mask is {column.Validity.Bytes.Length} bytes; expected {expected} for {count} rows.");
            }
            s.WriteFieldId(101);
            s.WriteBlob(column.Validity.Bytes.Span);
        }

        switch (column)
        {
            case FixedSizeColumn fixedCol:
                WriteFixedSizeColumn(s, fixedCol, count);
                break;
            case StringColumn stringCol:
                WriteStringColumn(s, stringCol, count);
                break;
            case StructColumn structCol:
                WriteStructColumn(s, structCol, count);
                break;
            case ListColumn listCol:
                WriteListColumn(s, listCol, count);
                break;
            case ArrayColumn arrayCol:
                WriteArrayColumn(s, arrayCol, count);
                break;
            default:
                throw new SerializationException(
                    $"VectorWriter does not support column kind '{column.GetType().Name}'.");
        }
    }

    private static void WriteFixedSizeColumn(BinarySerializer s, FixedSizeColumn col, int count)
    {
        int expectedBytes = col.ElementSize * count;
        if (col.Data.Length != expectedBytes)
        {
            throw new SerializationException(
                $"FixedSizeColumn data is {col.Data.Length} bytes; expected {expectedBytes} ({col.ElementSize} * {count}).");
        }
        s.WriteFieldId(102);
        s.WriteBlob(col.Data.Span);
    }

    private static void WriteStringColumn(BinarySerializer s, StringColumn col, int count)
    {
        if (col.Values.Length != count)
        {
            throw new SerializationException(
                $"StringColumn has {col.Values.Length} values; expected {count}.");
        }
        s.WriteFieldId(102);
        s.BeginList((ulong)count);
        for (int i = 0; i < count; i++)
        {
            // Null entries are represented by the validity mask; the string
            // slot itself is written as the empty string (matching duckdb's
            // NullValue<string_t>() which is empty).
            s.WriteString(col.Values[i] ?? string.Empty);
        }
        s.EndList();
    }

    private static void WriteStructColumn(BinarySerializer s, StructColumn col, int count)
    {
        s.WriteFieldId(103);
        s.BeginList((ulong)col.Fields.Length);
        foreach (DuckDbColumn child in col.Fields)
        {
            s.BeginObject();
            Write(s, child, count);
            s.EndObject();
        }
        s.EndList();
    }

    private static void WriteListColumn(BinarySerializer s, ListColumn col, int count)
    {
        if (col.Entries.Length != count)
        {
            throw new SerializationException(
                $"ListColumn has {col.Entries.Length} entries; expected {count}.");
        }
        s.WriteProperty(fieldId: 104, (ulong)col.Child.Count);

        s.WriteFieldId(105);
        s.BeginList((ulong)count);
        for (int i = 0; i < count; i++)
        {
            (ulong offset, ulong length) = col.Entries[i];
            s.BeginObject();
            s.WriteProperty(fieldId: 100, offset);
            s.WriteProperty(fieldId: 101, length);
            s.EndObject();
        }
        s.EndList();

        s.WriteFieldId(106);
        s.BeginObject();
        Write(s, col.Child, col.Child.Count);
        s.EndObject();
    }

    private static void WriteArrayColumn(BinarySerializer s, ArrayColumn col, int count)
    {
        s.WriteProperty(fieldId: 103, (ulong)col.ArraySize);
        s.WriteFieldId(104);
        s.BeginObject();
        Write(s, col.Child, col.Child.Count);
        s.EndObject();
    }
}

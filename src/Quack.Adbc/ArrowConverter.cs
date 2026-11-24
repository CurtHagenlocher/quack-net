// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using Quack.Data;
using Quack.Types;

namespace Quack.Adbc;

// Converts Quack's DuckDbChunk / DuckDbColumn types to Apache Arrow Schema
// and RecordBatch.
//
// Zero-copy strategy: most fixed-width DuckDB types have a binary layout
// identical to the corresponding Arrow array's value buffer (little-endian
// packed primitives, Decimal128 byte order matching hugeint_t, Date32 as
// i32 days, Time64 as i64 micros, Timestamp as i64 in the chosen unit).
// We wrap FixedSizeColumn.Data in an ArrowBuffer rather than re-encoding.
// Validity bitmaps are likewise wire-compatible (LSB-first, 1 = valid,
// 64-bit padded — both formats agree). Conversions are only required for
// types whose physical layout differs from Arrow's: Boolean (byte->bit),
// Interval (micros->nanos), narrow Decimal (widen to 16 bytes), TimeTz
// (micros of the time of day), Enum (dictionary), and the variable-width and
// nested types.
//
// The Arrow types are DuckDB's own Arrow export's. With `lossless` they are
// its arrow_lossless_conversion forms: HUGEINT, UHUGEINT, TIME_TZ and BIT as
// arrow.opaque (vendor DuckDB) over their raw bytes, UUID as arrow.uuid,
// BOOLEAN as arrow.bool8.
internal static class ArrowConverter
{
    public static Schema BuildSchema(IReadOnlyList<string> names, IReadOnlyList<LogicalType> types, bool lossless = false)
    {
        if (names.Count != types.Count)
        {
            throw new InvalidOperationException(
                $"Column name count ({names.Count}) does not match type count ({types.Count}).");
        }
        Schema.Builder builder = new();
        for (int i = 0; i < names.Count; i++)
        {
            builder.Field(MakeField(names[i], types[i], nullable: true, lossless));
        }
        return builder.Build();
    }

    public static RecordBatch ToRecordBatch(Schema schema, DuckDbChunk chunk, bool lossless = false)
    {
        IArrowArray[] columns = new IArrowArray[chunk.Columns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = ToArray(chunk.Columns[i], chunk.RowCount, lossless);
        }
        return new RecordBatch(schema, columns, chunk.RowCount);
    }

    // A field, with the extension metadata DuckDB's export puts on it.
    internal static Field MakeField(string name, LogicalType logical, bool nullable, bool lossless)
        => new(name, MapType(logical, lossless), nullable, ExtensionMetadata(logical, lossless));

    internal const string ExtensionNameKey = "ARROW:extension:name";
    internal const string ExtensionMetadataKey = "ARROW:extension:metadata";

    private static Dictionary<string, string>? ExtensionMetadata(LogicalType logical, bool lossless) => logical.Id switch
    {
        LogicalTypeId.Geometry => new() { [ExtensionNameKey] = "geoarrow.wkb", [ExtensionMetadataKey] = "{}" },
        LogicalTypeId.BigNum => Opaque("bignum"),
        LogicalTypeId.HugeInt when lossless => Opaque("hugeint"),
        LogicalTypeId.UHugeInt when lossless => Opaque("uhugeint"),
        LogicalTypeId.TimeTz when lossless => Opaque("time_tz"),
        LogicalTypeId.Bit when lossless => Opaque("bit"),
        LogicalTypeId.Boolean when lossless => new() { [ExtensionNameKey] = "arrow.bool8", [ExtensionMetadataKey] = "" },
        _ => null,
    };

    private static Dictionary<string, string> Opaque(string typeName) => new()
    {
        [ExtensionNameKey] = "arrow.opaque",
        [ExtensionMetadataKey] = $"{{\"type_name\":\"{typeName}\",\"vendor_name\":\"DuckDB\"}}",
    };

    internal static IArrowType MapType(LogicalType logical, bool lossless = false) => logical.Id switch
    {
        // Booleans + primitive numerics (matching DuckDB physical types).
        // Lossless: arrow.bool8, one byte per value as DuckDB keeps it.
        LogicalTypeId.Boolean => lossless ? Int8Type.Default : BooleanType.Default,
        LogicalTypeId.TinyInt => Int8Type.Default,
        LogicalTypeId.SmallInt => Int16Type.Default,
        LogicalTypeId.Integer => Int32Type.Default,
        LogicalTypeId.BigInt => Int64Type.Default,
        LogicalTypeId.UTinyInt => UInt8Type.Default,
        LogicalTypeId.USmallInt => UInt16Type.Default,
        LogicalTypeId.UInteger => UInt32Type.Default,
        LogicalTypeId.UBigInt => UInt64Type.Default,
        LogicalTypeId.Float => FloatType.Default,
        LogicalTypeId.Double => DoubleType.Default,

        // 16-byte integers. As DuckDB exports them: Decimal128(38, 0), which for
        // UHUGEINT reinterprets the bits (values from 2^127 come out negative);
        // lossless, the raw 16 bytes as arrow.opaque.
        LogicalTypeId.HugeInt or LogicalTypeId.UHugeInt => lossless
            ? new FixedSizeBinaryType(16)
            : new Decimal128Type(precision: 38, scale: 0),
        // Text; lossless, arrow.uuid.
        LogicalTypeId.Uuid => lossless ? GuidType.Default : StringType.Default,

        // DECIMAL maps to Arrow Decimal128 regardless of DuckDB's physical
        // backing (INT16/INT32/INT64/INT128). Smaller widths require
        // widening at conversion time; wider DuckDB DECIMALs (none yet) would
        // need Decimal256.
        LogicalTypeId.Decimal when logical.TypeInfo is DecimalTypeInfo dti
            => new Decimal128Type(precision: dti.Width, scale: dti.Scale),

        // Temporal.
        LogicalTypeId.Date => Date32Type.Default,
        LogicalTypeId.Time => new Time64Type(TimeUnit.Microsecond),
        LogicalTypeId.TimeNs => new Time64Type(TimeUnit.Nanosecond),
        // The time of day without its offset; lossless, the raw 8 bytes as arrow.opaque.
        LogicalTypeId.TimeTz => lossless ? new FixedSizeBinaryType(8) : new Time64Type(TimeUnit.Microsecond),
        LogicalTypeId.Timestamp => new TimestampType(TimeUnit.Microsecond, (string?)null),
        LogicalTypeId.TimestampSec => new TimestampType(TimeUnit.Second, (string?)null),
        LogicalTypeId.TimestampMs => new TimestampType(TimeUnit.Millisecond, (string?)null),
        LogicalTypeId.TimestampNs => new TimestampType(TimeUnit.Nanosecond, (string?)null),
        LogicalTypeId.TimestampTz => new TimestampType(TimeUnit.Microsecond, "UTC"),
        LogicalTypeId.TimestampTzNs => new TimestampType(TimeUnit.Nanosecond, "UTC"),
        LogicalTypeId.Interval => new IntervalType(IntervalUnit.MonthDayNanosecond),

        // Variable-width text + binary. BLOB, BIT, and GEOMETRY all ride
        // VarBytesColumn; ADBC consumers can interpret BLOB/BIT/GEOMETRY
        // payloads themselves (raw bytes / WKB).
        LogicalTypeId.Varchar or LogicalTypeId.Char or LogicalTypeId.StringLiteral
            => StringType.Default,
        // GEOMETRY is WKB (geoarrow.wkb); BIGNUM is arrow.opaque in both modes,
        // BIT when lossless.
        LogicalTypeId.Blob or LogicalTypeId.Bit or LogicalTypeId.Geometry or LogicalTypeId.BigNum
            => BinaryType.Default,

        // Enums map to DictionaryArrays keyed by the physical index type.
        LogicalTypeId.Enum when logical.TypeInfo is EnumTypeInfo et
            => new DictionaryType(IndexTypeFor(et.PhysicalType), StringType.Default, ordered: false),

        // Nested. A UNION is a sparse union whose type ids are the member
        // indexes, as DuckDB exports it.
        LogicalTypeId.List when logical.TypeInfo is ListTypeInfo lti
            => new ListType(MakeField("item", lti.ChildType, nullable: true, lossless)),
        LogicalTypeId.Map when logical.TypeInfo is ListTypeInfo lti && lti.ChildType.TypeInfo is StructTypeInfo entryInfo
            => new MapType(
                MakeField("key", entryInfo.ChildTypes[0].Value, nullable: false, lossless),
                MakeField("value", entryInfo.ChildTypes[1].Value, nullable: true, lossless),
                keySorted: false),
        LogicalTypeId.Union when logical.TypeInfo is StructTypeInfo sti => UnionTypeFor(sti, lossless),
        LogicalTypeId.Struct or LogicalTypeId.Tuple when logical.TypeInfo is StructTypeInfo sti
            => new StructType(BuildStructFields(sti, lossless)),
        LogicalTypeId.Array when logical.TypeInfo is ArrayTypeInfo ati
            => new FixedSizeListType(
                MakeField("item", ati.ChildType, nullable: true, lossless),
                listSize: (int)ati.Size),

        LogicalTypeId.SqlNull => NullType.Default,

        // arrow.parquet.variant, as DuckDB's own Arrow export (see VariantArrowConverter).
        LogicalTypeId.Variant => VariantType.Default,

        _ => throw new NotSupportedException(
            $"Arrow conversion for DuckDB type '{logical.Id}' is not implemented yet."),
    };

    private static IReadOnlyList<Field> BuildStructFields(StructTypeInfo sti, bool lossless)
    {
        Field[] fields = new Field[sti.ChildTypes.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            KeyValuePair<string, LogicalType> child = sti.ChildTypes[i];
            // Empty-string keys (DuckDB's UNION tag uses one) would produce a
            // malformed Arrow schema; substitute a synthetic name.
            string name = string.IsNullOrEmpty(child.Key) ? $"_{i}" : child.Key;
            fields[i] = MakeField(name, child.Value, nullable: true, lossless);
        }
        return fields;
    }

    // DuckDB's UNION is STRUCT(tag, member...); the union's children are the members.
    private static UnionType UnionTypeFor(StructTypeInfo sti, bool lossless)
    {
        Field[] members = new Field[sti.ChildTypes.Count - 1];
        for (int i = 0; i < members.Length; i++)
        {
            KeyValuePair<string, LogicalType> member = sti.ChildTypes[i + 1];
            members[i] = MakeField(member.Key, member.Value, nullable: true, lossless);
        }
        return new UnionType(members, Enumerable.Range(0, members.Length), UnionMode.Sparse);
    }

    private static IArrowType IndexTypeFor(PhysicalType physical) => physical switch
    {
        PhysicalType.UInt8 => UInt8Type.Default,
        PhysicalType.UInt16 => UInt16Type.Default,
        PhysicalType.UInt32 => UInt32Type.Default,
        _ => throw new InvalidOperationException($"Unexpected enum index physical type '{physical}'."),
    };

    private static IArrowArray ToArray(DuckDbColumn column, int rowCount, bool lossless)
    {
        ArrowBuffer validity = BuildValidityBuffer(column, rowCount, out int nullCount);
        LogicalTypeId id = column.Type.Id;

        return id switch
        {
            LogicalTypeId.Boolean => lossless
                ? new Int8Array(WrapData(column), validity, rowCount, nullCount, 0)
                : BuildBooleanArray((FixedSizeColumn)column, validity, nullCount, rowCount),

            // Zero-copy: wrap FixedSizeColumn.Data as the value buffer; Arrow
            // and DuckDB both use native-endian packed primitives.
            LogicalTypeId.TinyInt => new Int8Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.SmallInt => new Int16Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.Integer => new Int32Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.BigInt => new Int64Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.UTinyInt => new UInt8Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.USmallInt => new UInt16Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.UInteger => new UInt32Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.UBigInt => new UInt64Array(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.Float => new FloatArray(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.Double => new DoubleArray(WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.Date => new Date32Array(WrapData(column), validity, rowCount, nullCount, 0),

            // 16-byte signed integer family. Both HUGEINT and INT128-backed
            // DECIMAL share Quack's hugeint_t layout (uint64 lower; int64 upper
            // — see HugeintLayout), which matches Arrow Decimal128's
            // little-endian two's-complement value buffer byte-for-byte.
            LogicalTypeId.HugeInt or LogicalTypeId.UHugeInt
                => BuildFixedSize(MapType(column.Type, lossless), column, validity, nullCount, rowCount),
            LogicalTypeId.Uuid => lossless
                ? BuildUuidGuids((FixedSizeColumn)column, validity, nullCount, rowCount)
                : BuildUuidStrings((FixedSizeColumn)column, rowCount),

            LogicalTypeId.Decimal => BuildDecimalArray((FixedSizeColumn)column, validity, nullCount, rowCount),

            LogicalTypeId.Time => new Time64Array(
                new Time64Type(TimeUnit.Microsecond),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimeNs => new Time64Array(
                new Time64Type(TimeUnit.Nanosecond),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimeTz => lossless
                ? BuildFixedSize(new FixedSizeBinaryType(8), column, validity, nullCount, rowCount)
                : BuildTimeTzArray((FixedSizeColumn)column, validity, nullCount, rowCount),

            LogicalTypeId.Timestamp => new TimestampArray(
                new TimestampType(TimeUnit.Microsecond, (string?)null),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimestampSec => new TimestampArray(
                new TimestampType(TimeUnit.Second, (string?)null),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimestampMs => new TimestampArray(
                new TimestampType(TimeUnit.Millisecond, (string?)null),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimestampNs => new TimestampArray(
                new TimestampType(TimeUnit.Nanosecond, (string?)null),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimestampTz => new TimestampArray(
                new TimestampType(TimeUnit.Microsecond, "UTC"),
                WrapData(column), validity, rowCount, nullCount, 0),
            LogicalTypeId.TimestampTzNs => new TimestampArray(
                new TimestampType(TimeUnit.Nanosecond, "UTC"),
                WrapData(column), validity, rowCount, nullCount, 0),

            LogicalTypeId.Interval => BuildIntervalArray((FixedSizeColumn)column, validity, nullCount, rowCount),

            LogicalTypeId.Varchar or LogicalTypeId.Char or LogicalTypeId.StringLiteral
                => BuildStringArray((VarBytesColumn)column, validity, nullCount, rowCount),
            LogicalTypeId.Blob or LogicalTypeId.Bit or LogicalTypeId.Geometry or LogicalTypeId.BigNum
                => BuildBinaryArray((VarBytesColumn)column, validity, nullCount, rowCount),

            LogicalTypeId.Enum => BuildDictionaryArray((FixedSizeColumn)column, validity, nullCount, rowCount),

            LogicalTypeId.List => BuildListArray((ListColumn)column, validity, nullCount, rowCount, lossless),
            LogicalTypeId.Map => BuildMapArray((ListColumn)column, validity, nullCount, rowCount, lossless),
            LogicalTypeId.Union => BuildUnionArray((StructColumn)column, rowCount, lossless),
            LogicalTypeId.Struct or LogicalTypeId.Tuple => BuildStructArray((StructColumn)column, validity, nullCount, rowCount, lossless),
            LogicalTypeId.Array => BuildFixedSizeListArray((ArrayColumn)column, validity, nullCount, rowCount, lossless),

            LogicalTypeId.SqlNull => new NullArray(rowCount),

            LogicalTypeId.Variant => VariantArrowConverter.Build((VariantColumn)column, rowCount),

            _ => throw new NotSupportedException(
                $"Arrow array build for '{id}' on column kind '{column.GetType().Name}' is not implemented yet."),
        };
    }

    private static BinaryArray BuildBinaryArray(VarBytesColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
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
        return new BinaryArray(BinaryType.Default,
            length: rowCount,
            valueOffsetsBuffer: offsets.Build(),
            dataBuffer: new ArrowBuffer(values),
            nullBitmapBuffer: validity,
            nullCount: nullCount,
            offset: 0);
    }

    private static DictionaryArray BuildDictionaryArray(FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        if (column.Type.TypeInfo is not EnumTypeInfo et)
        {
            throw new InvalidOperationException("ENUM column missing EnumTypeInfo.");
        }
        DictionaryType dictType = new(IndexTypeFor(et.PhysicalType), StringType.Default, ordered: false);

        // Build the dictionary's string array (the symbol table).
        StringArray.Builder dictBuilder = new();
        foreach (string symbol in et.Values) dictBuilder.Append(symbol);
        StringArray dictArray = dictBuilder.Build();

        // Indices are zero-copy: Quack's column.Data is already the right
        // index width (uint8/16/32 LE).
        IArrowArray indices = et.PhysicalType switch
        {
            PhysicalType.UInt8 => new UInt8Array(new ArrowBuffer(column.Data), validity, rowCount, nullCount, 0),
            PhysicalType.UInt16 => new UInt16Array(new ArrowBuffer(column.Data), validity, rowCount, nullCount, 0),
            PhysicalType.UInt32 => new UInt32Array(new ArrowBuffer(column.Data), validity, rowCount, nullCount, 0),
            _ => throw new InvalidOperationException($"Unexpected enum index physical type '{et.PhysicalType}'."),
        };
        return new DictionaryArray(dictType, indices, dictArray);
    }

    private static ListArray BuildListArray(ListColumn column, ArrowBuffer validity, int nullCount, int rowCount, bool lossless)
    {
        if (column.Type.TypeInfo is not ListTypeInfo lti)
        {
            throw new InvalidOperationException("LIST column missing ListTypeInfo.");
        }
        // Build the int32 offsets buffer from Quack's (offset, length) entries.
        // Arrow ListArray expects offsets[i+1] - offsets[i] = row i's length,
        // with row data starting at offsets[i] in the flat child array.
        int[] offsets = new int[rowCount + 1];
        int cursor = 0;
        for (int i = 0; i < rowCount; i++)
        {
            offsets[i] = cursor;
            cursor += (int)column.Entries[i].Length;
        }
        offsets[rowCount] = cursor;

        IArrowArray childArray = ToArray(column.Child, column.Child.Count, lossless);
        Field itemField = MakeField("item", lti.ChildType, nullable: true, lossless);
        ListType listType = new(itemField);
        return new ListArray(listType, length: rowCount,
            valueOffsetsBuffer: new ArrowBuffer(OffsetsToBytes(offsets)),
            values: childArray,
            nullBitmapBuffer: validity,
            nullCount: nullCount, offset: 0);
    }

    private static MapArray BuildMapArray(ListColumn column, ArrowBuffer validity, int nullCount, int rowCount, bool lossless)
    {
        if (column.Type.TypeInfo is not ListTypeInfo lti ||
            lti.ChildType.TypeInfo is not StructTypeInfo entryInfo)
        {
            throw new InvalidOperationException("MAP column missing LIST<STRUCT> type info.");
        }
        if (column.Child is not StructColumn entryStruct)
        {
            throw new InvalidOperationException("MAP column's child must be a STRUCT(key, value).");
        }

        int[] offsets = new int[rowCount + 1];
        int cursor = 0;
        for (int i = 0; i < rowCount; i++)
        {
            offsets[i] = cursor;
            cursor += (int)column.Entries[i].Length;
        }
        offsets[rowCount] = cursor;

        IArrowArray keyArray = ToArray(entryStruct.Fields[0], entryStruct.Count, lossless);
        IArrowArray valueArray = ToArray(entryStruct.Fields[1], entryStruct.Count, lossless);
        Field keyField = MakeField("key", entryInfo.ChildTypes[0].Value, nullable: false, lossless);
        Field valueField = MakeField("value", entryInfo.ChildTypes[1].Value, nullable: true, lossless);
        StructType entryType = new([keyField, valueField]);
        StructArray entryArray = new(
            entryType, entryStruct.Count,
            [keyArray, valueArray],
            ArrowBuffer.Empty, nullCount: 0);

        MapType mapType = new(keyField, valueField, keySorted: false);
        return new MapArray(mapType, length: rowCount,
            valueOffsetsBuffer: new ArrowBuffer(OffsetsToBytes(offsets)),
            structs: entryArray,
            nullBitmapBuffer: validity,
            nullCount: nullCount, offset: 0);
    }

    private static StructArray BuildStructArray(StructColumn column, ArrowBuffer validity, int nullCount, int rowCount, bool lossless)
    {
        if (column.Type.TypeInfo is not StructTypeInfo sti)
        {
            throw new InvalidOperationException("STRUCT/UNION column missing StructTypeInfo.");
        }
        IArrowArray[] children = new IArrowArray[column.Fields.Length];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = ToArray(column.Fields[i], rowCount, lossless);
        }
        StructType structType = new(BuildStructFields(sti, lossless));
        return new StructArray(structType, rowCount, children, validity, nullCount, 0);
    }

    // The tag column is the type ids (UTINYINT member indexes), as is; a NULL
    // union row has tag 0 and every member null, which reads as null.
    private static SparseUnionArray BuildUnionArray(StructColumn column, int rowCount, bool lossless)
    {
        if (column.Type.TypeInfo is not StructTypeInfo sti)
        {
            throw new InvalidOperationException("UNION column missing StructTypeInfo.");
        }
        IArrowArray[] members = new IArrowArray[column.Fields.Length - 1];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = ToArray(column.Fields[i + 1], rowCount, lossless);
        }
        FixedSizeColumn tags = (FixedSizeColumn)column.Fields[0];
        return new SparseUnionArray(UnionTypeFor(sti, lossless), rowCount, members, new ArrowBuffer(tags.Data[..rowCount]));
    }

    private static FixedSizeListArray BuildFixedSizeListArray(ArrayColumn column, ArrowBuffer validity, int nullCount, int rowCount, bool lossless)
    {
        if (column.Type.TypeInfo is not ArrayTypeInfo ati)
        {
            throw new InvalidOperationException("ARRAY column missing ArrayTypeInfo.");
        }
        IArrowArray childArray = ToArray(column.Child, column.Child.Count, lossless);
        Field itemField = MakeField("item", ati.ChildType, nullable: true, lossless);
        FixedSizeListType listType = new(itemField, (int)column.ArraySize);
        return new FixedSizeListArray(listType, length: rowCount,
            values: childArray,
            nullBitmapBuffer: validity,
            nullCount: nullCount, offset: 0);
    }

    private static byte[] OffsetsToBytes(int[] offsets)
    {
        byte[] bytes = new byte[offsets.Length * 4];
        for (int i = 0; i < offsets.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), offsets[i]);
        }
        return bytes;
    }

    private static Decimal128Array BuildDecimalArray(FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        if (column.Type.TypeInfo is not DecimalTypeInfo info)
        {
            throw new InvalidOperationException("DECIMAL column missing DecimalTypeInfo.");
        }
        Decimal128Type arrowType = new(precision: info.Width, scale: info.Scale);
        ArrowBuffer valueBuffer;
        if (column.ElementSize == 16)
        {
            // 16-byte mantissas already match Arrow's Decimal128 LE byte order.
            valueBuffer = new ArrowBuffer(column.Data);
        }
        else
        {
            // Widen INT16/INT32/INT64 mantissas to 16-byte sign-extended LE.
            byte[] widened = new byte[rowCount * 16];
            ReadOnlySpan<byte> src = column.Data.Span;
            for (int i = 0; i < rowCount; i++)
            {
                long mantissa = column.ElementSize switch
                {
                    2 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2)),
                    4 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(src.Slice(i * 4, 4)),
                    8 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src.Slice(i * 8, 8)),
                    _ => throw new InvalidOperationException($"Unexpected DECIMAL element size {column.ElementSize}."),
                };
                HugeintLayout.WriteSigned(widened.AsSpan(i * 16, 16), mantissa);
            }
            valueBuffer = new ArrowBuffer(widened);
        }
        return new Decimal128Array(new ArrayData(arrowType, rowCount, nullCount, 0,
            buffers: [validity, valueBuffer]));
    }

    // The column's bytes as is: Decimal128 (HUGEINT, and UHUGEINT's bits) or
    // fixed-size binary (the lossless forms).
    private static IArrowArray BuildFixedSize(IArrowType arrowType, DuckDbColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        if (column is not FixedSizeColumn fixedCol)
        {
            throw new InvalidOperationException(
                $"Expected FixedSizeColumn for '{column.Type.Id}'; got '{column.GetType().Name}'.");
        }
        ArrayData data = new(arrowType, rowCount, nullCount, 0, buffers: [validity, new ArrowBuffer(fixedCol.Data)]);
        return arrowType is Decimal128Type ? new Decimal128Array(data) : new FixedSizeBinaryArray(data);
    }

    // TIME_TZ packs micros in the high 40 bits and the offset in the low 24;
    // DuckDB's Arrow export keeps the micros (the time of day, without the
    // offset).
    private static Time64Array BuildTimeTzArray(FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        byte[] micros = new byte[rowCount * sizeof(long)];
        ReadOnlySpan<byte> src = column.Data.Span;
        for (int i = 0; i < rowCount; i++)
        {
            long bits = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(src.Slice(i * 8, 8));
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(micros.AsSpan(i * 8, 8), bits >> 24);
        }
        return new Time64Array(new Time64Type(TimeUnit.Microsecond), new ArrowBuffer(micros), validity, rowCount, nullCount, 0);
    }

    private static MonthDayNanosecondIntervalArray BuildIntervalArray(
        FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        // Quack INTERVAL: { i32 months; i32 days; i64 micros }, 16 bytes.
        // Arrow MonthDayNanosecond: { i32 months; i32 days; i64 nanos }, 16 bytes.
        // Same layout — just multiply micros by 1000 to widen to nanoseconds.
        byte[] dest = new byte[rowCount * 16];
        ReadOnlySpan<byte> src = column.Data.Span;
        for (int i = 0; i < rowCount; i++)
        {
            ReadOnlySpan<byte> rowSrc = src.Slice(i * 16, 16);
            Span<byte> rowDest = dest.AsSpan(i * 16, 16);
            // months, days: pass through (4 bytes each, same offsets).
            rowSrc[..8].CopyTo(rowDest[..8]);
            long micros = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(rowSrc.Slice(8, 8));
            // Wraps on overflow, as DuckDB's own Arrow export does.
            long nanos = unchecked(micros * 1000L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(rowDest.Slice(8, 8), nanos);
        }
        return new MonthDayNanosecondIntervalArray(
            new ArrowBuffer(dest), validity, rowCount, nullCount, 0);
    }

    // DuckDB keeps a UUID as a hugeint with the top bit flipped (so it sorts as
    // unsigned); its text is that value's 16 bytes, big-endian.
    private static StringArray BuildUuidStrings(FixedSizeColumn column, int rowCount)
    {
        StringArray.Builder builder = new();
        Span<byte> bigEndian = stackalloc byte[16];
        for (int i = 0; i < rowCount; i++)
        {
            if (column.IsNull(i))
            {
                builder.AppendNull();
                continue;
            }
            UInt128 bits = HugeintLayout.ReadUnsigned(column.GetBytes(i)) ^ ((UInt128)1 << 127);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(bigEndian, bits);
            builder.Append(new Guid(bigEndian, bigEndian: true).ToString("D"));
        }
        return builder.Build();
    }

    // arrow.uuid: the RFC 4122 bytes, i.e. the UUID's big-endian value.
    private static GuidArray BuildUuidGuids(FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        byte[] data = new byte[rowCount * 16];
        for (int i = 0; i < rowCount; i++)
        {
            if (column.IsNull(i))
            {
                continue;
            }
            UInt128 bits = HugeintLayout.ReadUnsigned(column.GetBytes(i)) ^ ((UInt128)1 << 127);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(data.AsSpan(i * 16, 16), bits);
        }
        FixedSizeBinaryArray storage = new(new ArrayData(GuidType.Default.StorageType, rowCount, nullCount, 0,
            buffers: [validity, new ArrowBuffer(data)]));
        return new GuidArray(storage);
    }

    private static ArrowBuffer WrapData(DuckDbColumn column)
    {
        if (column is not FixedSizeColumn fixedCol)
        {
            throw new InvalidOperationException(
                $"Expected FixedSizeColumn for zero-copy conversion; got '{column.GetType().Name}'.");
        }
        return new ArrowBuffer(fixedCol.Data);
    }

    private static ArrowBuffer BuildValidityBuffer(DuckDbColumn column, int rowCount, out int nullCount)
    {
        if (column.Validity.IsAllValid)
        {
            nullCount = 0;
            return ArrowBuffer.Empty;
        }
        int nulls = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (column.Validity.IsNull(i)) nulls++;
        }
        nullCount = nulls;
        return new ArrowBuffer(column.Validity.Bytes);
    }

    private static BooleanArray BuildBooleanArray(FixedSizeColumn column, ArrowBuffer validity, int nullCount, int rowCount)
    {
        // DuckDB stores BOOLEAN as one byte per row; Arrow packs eight rows
        // into one byte (LSB-first, 1 = true). Re-pack.
        byte[] packed = new byte[ValidityMask.RequiredByteCount(rowCount)];
        ReadOnlySpan<byte> src = column.Data.Span;
        for (int i = 0; i < rowCount; i++)
        {
            if (src[i] != 0)
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
        return new BooleanArray(new ArrowBuffer(packed), validity, rowCount, nullCount, 0);
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

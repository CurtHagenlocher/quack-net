// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using Quack.Data;
using Quack.Types;

namespace Quack.Adbc;

// Converts an Apache Arrow RecordBatch back into a Quack DuckDbChunk so it
// can be appended via QuackConnection.AppendAsync. Mirrors ArrowConverter
// in the opposite direction. Zero-copy wherever the layouts already match
// (most fixed-width primitives, Date32, Time64, Timestamps, Decimal128
// with 16-byte width, FixedSizeBinary16, validity bitmap). Conversions
// are needed only for layout-different cases (Boolean bit unpack, Interval
// nanos->micros, narrow-width Decimal narrowing, String/Binary offsets->
// per-row slices, nested children).
internal static class RecordBatchConverter
{
    public static DuckDbChunk ToDuckDbChunk(RecordBatch batch)
    {
        int rowCount = batch.Length;
        LogicalType[] types = new LogicalType[batch.ColumnCount];
        DuckDbColumn[] columns = new DuckDbColumn[batch.ColumnCount];
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            Field field = batch.Schema.GetFieldByIndex(i);
            types[i] = MapField(field);
            columns[i] = ToColumn(batch.Column(i), types[i], rowCount);
        }
        return new DuckDbChunk
        {
            Types = types,
            Columns = columns,
            RowCount = rowCount,
        };
    }

    // The field's extension metadata first: DuckDB's own arrow.opaque types
    // (its lossless export) and geoarrow.wkb; otherwise the Arrow type.
    internal static LogicalType MapField(Field field)
    {
        if (field.HasMetadata && field.Metadata.TryGetValue(ArrowConverter.ExtensionNameKey, out string? extension))
        {
            field.Metadata.TryGetValue(ArrowConverter.ExtensionMetadataKey, out string? metadata);
            LogicalTypeId? id = (extension, field.DataType) switch
            {
                ("geoarrow.wkb", BinaryType or LargeBinaryType or BinaryViewType) => LogicalTypeId.Geometry,
                ("arrow.bool8", Int8Type) => LogicalTypeId.Boolean,
                // Over the C interface arrow.uuid arrives as this metadata on a fixed-size binary.
                ("arrow.uuid", FixedSizeBinaryType { ByteWidth: 16 }) => LogicalTypeId.Uuid,
                ("arrow.opaque", _) => DuckDbOpaque(metadata, field.DataType),
                _ => null,
            };
            if (id is { } known)
            {
                return new LogicalType(known);
            }
        }
        return MapType(field.DataType);
    }

    private static LogicalTypeId? DuckDbOpaque(string? metadata, IArrowType storage)
    {
        if (string.IsNullOrEmpty(metadata))
        {
            return null;
        }
        string? typeName;
        string? vendor;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(metadata);
            typeName = doc.RootElement.TryGetProperty("type_name", out System.Text.Json.JsonElement t) ? t.GetString() : null;
            vendor = doc.RootElement.TryGetProperty("vendor_name", out System.Text.Json.JsonElement v) ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (vendor != "DuckDB")
        {
            return null;
        }
        return (typeName, storage) switch
        {
            ("hugeint", FixedSizeBinaryType { ByteWidth: 16 }) => LogicalTypeId.HugeInt,
            ("uhugeint", FixedSizeBinaryType { ByteWidth: 16 }) => LogicalTypeId.UHugeInt,
            ("time_tz", FixedSizeBinaryType { ByteWidth: 8 }) => LogicalTypeId.TimeTz,
            ("bit", BinaryType) => LogicalTypeId.Bit,
            ("bignum", BinaryType) => LogicalTypeId.BigNum,
            _ => null,
        };
    }

    internal static LogicalType MapType(IArrowType arrowType) => arrowType switch
    {
        Int8Type => new LogicalType(LogicalTypeId.TinyInt),
        Int16Type => new LogicalType(LogicalTypeId.SmallInt),
        Int32Type => new LogicalType(LogicalTypeId.Integer),
        Int64Type => new LogicalType(LogicalTypeId.BigInt),
        UInt8Type => new LogicalType(LogicalTypeId.UTinyInt),
        UInt16Type => new LogicalType(LogicalTypeId.USmallInt),
        UInt32Type => new LogicalType(LogicalTypeId.UInteger),
        UInt64Type => new LogicalType(LogicalTypeId.UBigInt),
        FloatType => new LogicalType(LogicalTypeId.Float),
        DoubleType => new LogicalType(LogicalTypeId.Double),
        BooleanType => new LogicalType(LogicalTypeId.Boolean),
        Date32Type => new LogicalType(LogicalTypeId.Date),
        // A negative scale (unscaled * 10^-scale) becomes DECIMAL(p + |s|, 0), as DuckDB reads it.
        Decimal128Type dt when dt.Scale < 0 => dt.Precision - dt.Scale <= 38
            ? new LogicalType(LogicalTypeId.Decimal, new DecimalTypeInfo { Width = (byte)(dt.Precision - dt.Scale), Scale = 0 })
            : throw new NotSupportedException($"Arrow {dt} needs more than 38 digits."),
        Decimal128Type dt => new LogicalType(LogicalTypeId.Decimal,
            new DecimalTypeInfo { Width = (byte)dt.Precision, Scale = (byte)dt.Scale }),
        LargeStringType or StringViewType => new LogicalType(LogicalTypeId.Varchar),
        LargeBinaryType or BinaryViewType => new LogicalType(LogicalTypeId.Blob),
        Time32Type => new LogicalType(LogicalTypeId.Time),
        // DuckDB 2.0 has a storable NULL type.
        NullType => new LogicalType(LogicalTypeId.SqlNull),
        // arrow.uuid is a UUID; any other fixed-size binary is a BLOB, as DuckDB
        // reads it.
        GuidType => new LogicalType(LogicalTypeId.Uuid),
        FixedSizeBinaryType => new LogicalType(LogicalTypeId.Blob),
        StringType => new LogicalType(LogicalTypeId.Varchar),
        BinaryType => new LogicalType(LogicalTypeId.Blob),
        Time64Type t when t.Unit == TimeUnit.Microsecond => new LogicalType(LogicalTypeId.Time),
        Time64Type t when t.Unit == TimeUnit.Nanosecond => new LogicalType(LogicalTypeId.TimeNs),
        // DuckDB stores zoned timestamps in microseconds (TIMESTAMPTZ) or, since
        // 2.0, nanoseconds (TIMESTAMPTZ_NS). Seconds and milliseconds are scaled
        // to microseconds in ToColumn.
        TimestampType ts when ts.Timezone is not null => ts.Unit == TimeUnit.Nanosecond
            ? new LogicalType(LogicalTypeId.TimestampTzNs)
            : new LogicalType(LogicalTypeId.TimestampTz),
        TimestampType ts => ts.Unit switch
        {
            TimeUnit.Second => new LogicalType(LogicalTypeId.TimestampSec),
            TimeUnit.Millisecond => new LogicalType(LogicalTypeId.TimestampMs),
            TimeUnit.Microsecond => new LogicalType(LogicalTypeId.Timestamp),
            TimeUnit.Nanosecond => new LogicalType(LogicalTypeId.TimestampNs),
            _ => throw new NotSupportedException($"Unsupported timestamp unit '{ts.Unit}'."),
        },
        IntervalType it when it.Unit == IntervalUnit.MonthDayNanosecond => new LogicalType(LogicalTypeId.Interval),
        ListType lt => new LogicalType(LogicalTypeId.List,
            new ListTypeInfo { ChildType = MapField(lt.ValueField) }),
        FixedSizeListType fsl => new LogicalType(LogicalTypeId.Array,
            new ArrayTypeInfo { ChildType = MapField(fsl.ValueField), Size = (uint)fsl.ListSize }),
        MapType mt => BuildMapLogicalType(mt),
        VariantType => LogicalType.Variant,
        StructType st => new LogicalType(LogicalTypeId.Struct, BuildStructTypeInfo(st)),
        // A sparse union is DuckDB's UNION: STRUCT(tag, member...).
        UnionType ut when ut.Mode == UnionMode.Sparse => BuildUnionLogicalType(ut),
        // Decoded to the value type, as DuckDB reads a dictionary (an existing
        // ENUM column takes the strings by DuckDB's own cast).
        DictionaryType dt when dt.ValueType is StringType or LargeStringType or StringViewType
            or BinaryType or LargeBinaryType or BinaryViewType => MapType(dt.ValueType),
        _ => throw new NotSupportedException(
            $"Arrow type '{arrowType.GetType().Name}' is not yet supported on the append path."),
    };

    private static LogicalType BuildMapLogicalType(MapType mt)
    {
        StructType st = (StructType)mt.KeyValueType;
        StructTypeInfo entryInfo = new()
        {
            ChildTypes =
            [
                new("key", MapField(st.Fields[0])),
                new("value", MapField(st.Fields[1])),
            ],
        };
        LogicalType entryType = new(LogicalTypeId.Struct, entryInfo);
        return new LogicalType(LogicalTypeId.Map, new ListTypeInfo { ChildType = entryType });
    }

    private static LogicalType BuildUnionLogicalType(UnionType ut)
    {
        KeyValuePair<string, LogicalType>[] children = new KeyValuePair<string, LogicalType>[ut.Fields.Count + 1];
        children[0] = new("", new LogicalType(LogicalTypeId.UTinyInt));
        for (int i = 0; i < ut.Fields.Count; i++)
        {
            children[i + 1] = new(ut.Fields[i].Name, MapField(ut.Fields[i]));
        }
        return new LogicalType(LogicalTypeId.Union, new StructTypeInfo { ChildTypes = children });
    }

    private static StructTypeInfo BuildStructTypeInfo(StructType st)
    {
        KeyValuePair<string, LogicalType>[] children = new KeyValuePair<string, LogicalType>[st.Fields.Count];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = new(st.Fields[i].Name, MapField(st.Fields[i]));
        }
        return new StructTypeInfo { ChildTypes = children };
    }

    private static DuckDbColumn ToColumn(IArrowArray array, LogicalType targetType, int rowCount)
    {
        ValidityMask validity = ExtractValidity(array, rowCount);
        return array switch
        {
            VariantArray v => VariantArrowConverter.ToColumn(v, targetType, rowCount, validity),
            Int8Array a when targetType.Id == LogicalTypeId.Boolean => Bool8(a, targetType, rowCount, validity),
            Int8Array a => Fixed(a.Data, targetType, rowCount, sizeof(sbyte), validity),
            Int16Array a => Fixed(a.Data, targetType, rowCount, sizeof(short), validity),
            Int32Array a => Fixed(a.Data, targetType, rowCount, sizeof(int), validity),
            Int64Array a => Fixed(a.Data, targetType, rowCount, sizeof(long), validity),
            UInt8Array a => Fixed(a.Data, targetType, rowCount, sizeof(byte), validity),
            UInt16Array a => Fixed(a.Data, targetType, rowCount, sizeof(ushort), validity),
            UInt32Array a => Fixed(a.Data, targetType, rowCount, sizeof(uint), validity),
            UInt64Array a => Fixed(a.Data, targetType, rowCount, sizeof(ulong), validity),
            FloatArray a => Fixed(a.Data, targetType, rowCount, sizeof(float), validity),
            DoubleArray a => Fixed(a.Data, targetType, rowCount, sizeof(double), validity),
            Date32Array a => Fixed(a.Data, targetType, rowCount, sizeof(int), validity),
            Time64Array a => Fixed(a.Data, targetType, rowCount, sizeof(long), validity),
            TimestampArray a when targetType.Id == LogicalTypeId.TimestampTz
                                  && ((TimestampType)a.Data.DataType).Unit is TimeUnit.Second or TimeUnit.Millisecond
                => ScaledTimestamps(a, targetType, rowCount, validity),
            TimestampArray a => Fixed(a.Data, targetType, rowCount, sizeof(long), validity),
            // Decimal128Array : FixedSizeBinaryArray, MapArray : ListArray —
            // the subclass arms must come before their base-class arms.
            Decimal128Array a => NarrowDecimal(a, targetType, rowCount, validity),
            // DuckDB's opaque 16- and 8-byte types: the bytes are its own layout.
            FixedSizeBinaryArray a when targetType.Id is LogicalTypeId.HugeInt or LogicalTypeId.UHugeInt or LogicalTypeId.TimeTz
                => Fixed(a.Data, targetType, rowCount, ((FixedSizeBinaryType)a.Data.DataType).ByteWidth, validity),
            FixedSizeBinaryArray a when targetType.Id == LogicalTypeId.Uuid => Uuids(a, targetType, rowCount, validity),
            FixedSizeBinaryArray a => FixedToBlobs(a, targetType, rowCount, validity),
            SparseUnionArray a => BuildUnionColumn(a, targetType, rowCount),
            GuidArray a => Uuids(a.StorageArray, targetType, rowCount, validity),
            BooleanArray a => UnpackBoolean(a, targetType, rowCount, validity),
            MonthDayNanosecondIntervalArray a => IntervalNanosToMicros(a, targetType, rowCount, validity),
            StringArray a => BuildVarBytes(a, targetType, rowCount, validity),
            BinaryArray a => BuildVarBytes(a, targetType, rowCount, validity),
            MapArray a => BuildMapColumn(a, targetType, rowCount, validity),
            ListArray a => BuildListColumn(a, targetType, rowCount, validity),
            StructArray a => BuildStructColumn(a, targetType, rowCount, validity),
            FixedSizeListArray a => BuildFixedSizeListColumn(a, targetType, rowCount, validity),
            DictionaryArray a => DecodeDictionary(a, targetType, rowCount, validity),
            LargeBinaryArray a => CopyVarBytes(i => a.GetBytes(i), a, targetType, rowCount, validity),
            BinaryViewArray a => CopyVarBytes(i => a.GetBytes(i), a, targetType, rowCount, validity),
            Time32Array a => Time32ToMicros(a, targetType, rowCount, validity),
            // Every row null; DuckDB keeps an INT32 per row underneath.
            NullArray => new FixedSizeColumn
            {
                Type = targetType,
                Count = rowCount,
                ElementSize = sizeof(int),
                Data = new byte[rowCount * sizeof(int)],
                Validity = new ValidityMask(new byte[ValidityMask.RequiredByteCount(rowCount)]),
            },
            _ => throw new NotSupportedException(
                $"Arrow array kind '{array.GetType().Name}' is not yet supported on the append path."),
        };
    }

    private static ValidityMask ExtractValidity(IArrowArray array, int rowCount)
    {
        if (array.NullCount == 0)
        {
            return ValidityMask.AllValid;
        }
        // A NullArray has no buffers: every row is null.
        if (array is NullArray)
        {
            return new ValidityMask(new byte[ValidityMask.RequiredByteCount(rowCount)]);
        }
        // Arrow stores the validity bitmap at Buffers[0]; non-primitive arrays
        // may have it empty when NullCount is 0 but we already returned above.
        ReadOnlyMemory<byte> mem = array.Data.Buffers[0].Memory;
        int expected = ValidityMask.RequiredByteCount(rowCount);
        if (mem.Length >= expected)
        {
            return new ValidityMask(mem[..expected]);
        }
        // Pad to Quack's 8-byte-rounded length.
        byte[] padded = new byte[expected];
        mem.Span.CopyTo(padded);
        return new ValidityMask(padded);
    }

    private static FixedSizeColumn Fixed(ArrayData data, LogicalType type, int rowCount, int elementSize, ValidityMask validity)
    {
        ReadOnlyMemory<byte> values = data.Buffers[1].Memory;
        // Honor non-zero array offsets by slicing.
        int byteOffset = data.Offset * elementSize;
        int byteLength = rowCount * elementSize;
        if (byteOffset != 0 || values.Length != byteLength)
        {
            values = values.Slice(byteOffset, byteLength);
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = rowCount,
            ElementSize = elementSize,
            Data = values,
            Validity = validity,
        };
    }

    // Zoned Arrow timestamps in seconds or milliseconds, scaled to DuckDB's
    // TIMESTAMPTZ microseconds.
    private static FixedSizeColumn ScaledTimestamps(TimestampArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        long factor = ((TimestampType)array.Data.DataType).Unit == TimeUnit.Second ? 1_000_000L : 1_000L;
        ReadOnlySpan<long> source = array.Values;
        byte[] bytes = new byte[rowCount * sizeof(long)];
        for (int i = 0; i < rowCount; i++)
        {
            long micros = validity.IsValid(i) ? checked(source[i] * factor) : 0;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), micros);
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = rowCount,
            ElementSize = sizeof(long),
            Data = bytes,
            Validity = validity,
        };
    }

    private static FixedSizeColumn UnpackBoolean(BooleanArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        byte[] bytes = new byte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            bytes[i] = array.GetValue(i) == true ? (byte)1 : (byte)0;
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = rowCount,
            ElementSize = 1,
            Data = bytes,
            Validity = validity,
        };
    }

    private static FixedSizeColumn NarrowDecimal(Decimal128Array array, LogicalType type, int rowCount, ValidityMask validity)
    {
        DecimalTypeInfo info = (DecimalTypeInfo)type.TypeInfo!;
        int elementSize = info.Width switch
        {
            <= 4 => 2,
            <= 9 => 4,
            <= 18 => 8,
            _ => 16,
        };
        // A negative Arrow scale: the value is unscaled * 10^-scale.
        int sourceScale = ((Decimal128Type)array.Data.DataType).Scale;
        Int128 factor = 1;
        for (int k = 0; k < -sourceScale; k++)
        {
            factor *= 10;
        }
        if (elementSize == 16 && factor == 1)
        {
            return Fixed(array.Data, type, rowCount, 16, validity);
        }
        // Read each 16-byte LE mantissa, narrow to the target physical width.
        ReadOnlySpan<byte> src = array.Data.Buffers[1].Memory.Span.Slice(array.Data.Offset * 16, rowCount * 16);
        byte[] dest = new byte[rowCount * elementSize];
        for (int i = 0; i < rowCount; i++)
        {
            Int128 v = checked(HugeintLayout.ReadSigned(src.Slice(i * 16, 16)) * factor);
            Span<byte> slot = dest.AsSpan(i * elementSize, elementSize);
            switch (elementSize)
            {
                case 16: HugeintLayout.WriteSigned(slot, v); break;
                case 2: System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(slot, checked((short)v)); break;
                case 4: System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(slot, checked((int)v)); break;
                case 8: System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot, checked((long)v)); break;
            }
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = rowCount,
            ElementSize = elementSize,
            Data = dest,
            Validity = validity,
        };
    }

    private delegate ReadOnlySpan<byte> ValueBytes(int index);

    // Variable-width values of any Arrow layout (large offsets, views), copied
    // into one buffer and sliced.
    private static VarBytesColumn CopyVarBytes(ValueBytes valueBytes, IArrowArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        int total = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (!array.IsNull(i)) total = checked(total + valueBytes(i).Length);
        }
        byte[] data = new byte[total];
        ReadOnlyMemory<byte>?[] values = new ReadOnlyMemory<byte>?[rowCount];
        int offset = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (array.IsNull(i)) continue;
            ReadOnlySpan<byte> value = valueBytes(i);
            value.CopyTo(data.AsSpan(offset));
            values[i] = data.AsMemory(offset, value.Length);
            offset += value.Length;
        }
        return new VarBytesColumn { Type = type, Count = rowCount, Values = values, Validity = validity };
    }

    // A dictionary of strings or bytes, decoded to its values.
    private static VarBytesColumn DecodeDictionary(DictionaryArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        IArrowArray dictionary = array.Dictionary;
        ValueBytes entry = dictionary switch
        {
            BinaryArray d => i => d.GetBytes(i),
            LargeBinaryArray d => i => d.GetBytes(i),
            BinaryViewArray d => i => d.GetBytes(i),
            _ => throw new NotSupportedException($"Dictionary of '{dictionary.Data.DataType}' is not supported on the append path."),
        };
        IArrowArray indices = array.Indices;
        long Index(int i) => indices switch
        {
            Int8Array x => x.GetValue(i)!.Value,
            Int16Array x => x.GetValue(i)!.Value,
            Int32Array x => x.GetValue(i)!.Value,
            Int64Array x => x.GetValue(i)!.Value,
            UInt8Array x => x.GetValue(i)!.Value,
            UInt16Array x => x.GetValue(i)!.Value,
            UInt32Array x => x.GetValue(i)!.Value,
            UInt64Array x => checked((long)x.GetValue(i)!.Value),
            _ => throw new NotSupportedException($"Dictionary index type '{indices.Data.DataType}' is not supported."),
        };
        return CopyVarBytes(i => entry(checked((int)Index(i))), array, type, rowCount, validity);
    }

    private static FixedSizeColumn Time32ToMicros(Time32Array array, LogicalType type, int rowCount, ValidityMask validity)
    {
        long factor = ((Time32Type)array.Data.DataType).Unit == TimeUnit.Second ? 1_000_000L : 1_000L;
        byte[] data = new byte[rowCount * sizeof(long)];
        for (int i = 0; i < rowCount; i++)
        {
            if (array.GetValue(i) is int value)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long)), value * factor);
            }
        }
        return new FixedSizeColumn { Type = type, Count = rowCount, ElementSize = sizeof(long), Data = data, Validity = validity };
    }

    private static FixedSizeColumn IntervalNanosToMicros(MonthDayNanosecondIntervalArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        // Arrow MonthDayNanosecond: { i32 months; i32 days; i64 nanos } LE.
        // DuckDB INTERVAL:           { i32 months; i32 days; i64 micros } LE.
        ReadOnlySpan<byte> src = array.Data.Buffers[1].Memory.Span.Slice(array.Data.Offset * 16, rowCount * 16);
        byte[] dest = new byte[rowCount * 16];
        for (int i = 0; i < rowCount; i++)
        {
            ReadOnlySpan<byte> rowSrc = src.Slice(i * 16, 16);
            Span<byte> rowDest = dest.AsSpan(i * 16, 16);
            rowSrc[..8].CopyTo(rowDest[..8]);
            long nanos = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(rowSrc.Slice(8, 8));
            // Truncate to micros toward zero — same convention DuckDB uses
            // when casting smaller-precision intervals.
            long micros = nanos / 1000L;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(rowDest.Slice(8, 8), micros);
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = rowCount,
            ElementSize = 16,
            Data = dest,
            Validity = validity,
        };
    }

    // arrow.bool8: any non-zero byte is true; DuckDB keeps 0 or 1.
    private static FixedSizeColumn Bool8(Int8Array array, LogicalType type, int rowCount, ValidityMask validity)
    {
        byte[] data = new byte[rowCount];
        ReadOnlySpan<sbyte> values = array.Values;
        for (int i = 0; i < rowCount; i++)
        {
            data[i] = values[i] != 0 ? (byte)1 : (byte)0;
        }
        return new FixedSizeColumn { Type = type, Count = rowCount, ElementSize = 1, Data = data, Validity = validity };
    }

    // A fixed-size binary as a BLOB: one slice of the value buffer per row.
    private static VarBytesColumn FixedToBlobs(FixedSizeBinaryArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        int width = ((FixedSizeBinaryType)array.Data.DataType).ByteWidth;
        ReadOnlyMemory<byte> bytes = array.Data.Buffers[1].Memory;
        int o = array.Data.Offset;
        ReadOnlyMemory<byte>?[] values = new ReadOnlyMemory<byte>?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            values[i] = array.IsNull(i) ? null : bytes.Slice((o + i) * width, width);
        }
        return new VarBytesColumn { Type = type, Count = rowCount, Values = values, Validity = validity };
    }

    // arrow.uuid holds RFC 4122 bytes; DuckDB a hugeint with the top bit flipped.
    private static FixedSizeColumn Uuids(FixedSizeBinaryArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        byte[] data = new byte[rowCount * HugeintLayout.ByteSize];
        for (int i = 0; i < rowCount; i++)
        {
            if (!array.IsNull(i))
            {
                // The RFC 4122 bytes are the UUID's big-endian value.
                UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(array.GetBytes(i)) ^ ((UInt128)1 << 127);
                HugeintLayout.WriteUnsigned(data.AsSpan(i * HugeintLayout.ByteSize, HugeintLayout.ByteSize), bits);
            }
        }
        return new FixedSizeColumn { Type = type, Count = rowCount, ElementSize = HugeintLayout.ByteSize, Data = data, Validity = validity };
    }

    private static VarBytesColumn BuildVarBytes<TArr>(TArr array, LogicalType type, int rowCount, ValidityMask validity)
        where TArr : IArrowArray
    {
        ReadOnlyMemory<byte>?[] values = new ReadOnlyMemory<byte>?[rowCount];
        ArrayData data = array.Data;
        // Both StringArray and BinaryArray share { offsets[i32]; values[bytes] }.
        ReadOnlySpan<int> offsets = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(data.Buffers[1].Memory.Span);
        ReadOnlyMemory<byte> bytes = data.Buffers[2].Memory;
        int o = data.Offset;
        for (int i = 0; i < rowCount; i++)
        {
            if (array.IsNull(i))
            {
                values[i] = null;
                continue;
            }
            int start = offsets[o + i];
            int end = offsets[o + i + 1];
            values[i] = bytes.Slice(start, end - start);
        }
        return new VarBytesColumn
        {
            Type = type,
            Count = rowCount,
            Validity = validity,
            Values = values,
        };
    }

    private static ListColumn BuildListColumn(ListArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        ReadOnlySpan<int> offsets = array.ValueOffsets;
        (ulong, ulong)[] entries = new (ulong, ulong)[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            int start = offsets[i];
            int end = offsets[i + 1];
            entries[i] = ((ulong)start, (ulong)(end - start));
        }
        LogicalType childType = ((ListTypeInfo)type.TypeInfo!).ChildType;
        DuckDbColumn child = ToColumn(array.Values, childType, array.Values.Length);
        return new ListColumn
        {
            Type = type,
            Count = rowCount,
            Validity = validity,
            Entries = entries,
            Child = child,
        };
    }

    private static ListColumn BuildMapColumn(MapArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        // Quack represents MAP as a List<Struct<key, value>>; Arrow's MapArray
        // exposes Keys and Values separately but stores them as a single
        // StructArray under the hood.
        ReadOnlySpan<int> offsets = array.ValueOffsets;
        (ulong, ulong)[] entries = new (ulong, ulong)[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            entries[i] = ((ulong)offsets[i], (ulong)(offsets[i + 1] - offsets[i]));
        }
        ListTypeInfo lti = (ListTypeInfo)type.TypeInfo!;
        StructTypeInfo entryInfo = (StructTypeInfo)lti.ChildType.TypeInfo!;
        IArrowArray keysArr = array.Keys;
        IArrowArray valsArr = array.Values;
        DuckDbColumn keyCol = ToColumn(keysArr, entryInfo.ChildTypes[0].Value, keysArr.Length);
        DuckDbColumn valCol = ToColumn(valsArr, entryInfo.ChildTypes[1].Value, valsArr.Length);
        StructColumn entryStruct = new()
        {
            Type = lti.ChildType,
            Count = keysArr.Length,
            Fields = [keyCol, valCol],
        };
        return new ListColumn
        {
            Type = type,
            Count = rowCount,
            Validity = validity,
            Entries = entries,
            Child = entryStruct,
        };
    }

    private static StructColumn BuildStructColumn(StructArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        StructTypeInfo sti = (StructTypeInfo)type.TypeInfo!;
        DuckDbColumn[] children = new DuckDbColumn[array.Fields.Count];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = ToColumn(array.Fields[i], sti.ChildTypes[i].Value, rowCount);
        }
        return new StructColumn
        {
            Type = type,
            Count = rowCount,
            Validity = validity,
            Fields = children,
        };
    }

    // The type ids map to member indexes (the tag); a row is null when its
    // selected member is (Arrow unions have no validity of their own).
    private static StructColumn BuildUnionColumn(SparseUnionArray array, LogicalType type, int rowCount)
    {
        StructTypeInfo sti = (StructTypeInfo)type.TypeInfo!;
        UnionType ut = (UnionType)array.Data.DataType;
        Dictionary<int, byte> memberOf = [];
        for (int m = 0; m < ut.TypeIds.Length; m++)
        {
            memberOf[ut.TypeIds[m]] = checked((byte)m);
        }
        DuckDbColumn[] members = new DuckDbColumn[array.Fields.Count];
        for (int m = 0; m < members.Length; m++)
        {
            members[m] = ToColumn(array.Fields[m], sti.ChildTypes[m + 1].Value, rowCount);
        }
        byte[] tags = new byte[rowCount];
        byte[] valid = new byte[ValidityMask.RequiredByteCount(rowCount)];
        bool anyNull = false;
        for (int i = 0; i < rowCount; i++)
        {
            byte member = memberOf.TryGetValue(array.TypeIds[i], out byte found)
                ? found
                : throw new InvalidDataException($"Union type id {array.TypeIds[i]} is not one of the union's.");
            tags[i] = member;
            if (array.Fields[member].IsValid(i))
            {
                valid[i >> 3] |= (byte)(1 << (i & 7));
            }
            else
            {
                anyNull = true;
            }
        }
        FixedSizeColumn tag = new()
        {
            Type = sti.ChildTypes[0].Value,
            Count = rowCount,
            ElementSize = 1,
            Data = tags,
            Validity = ValidityMask.AllValid,
        };
        return new StructColumn
        {
            Type = type,
            Count = rowCount,
            Validity = anyNull ? new ValidityMask(valid) : ValidityMask.AllValid,
            Fields = [tag, .. members],
        };
    }

    private static ArrayColumn BuildFixedSizeListColumn(FixedSizeListArray array, LogicalType type, int rowCount, ValidityMask validity)
    {
        ArrayTypeInfo ati = (ArrayTypeInfo)type.TypeInfo!;
        DuckDbColumn child = ToColumn(array.Values, ati.ChildType, array.Values.Length);
        return new ArrayColumn
        {
            Type = type,
            Count = rowCount,
            Validity = validity,
            ArraySize = ati.Size,
            Child = child,
        };
    }

}

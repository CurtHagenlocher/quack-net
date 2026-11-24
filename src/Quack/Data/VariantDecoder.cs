// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Decodes DuckDB 2.0 VARIANT vectors into DuckDbVariant values.
//
// Unshredded (the canonical layout) is a STRUCT of four per-row lists:
//   keys     LIST(VARCHAR)                                   object keys of the row
//   children LIST(STRUCT(keys_index UINTEGER, values_index UINTEGER))
//   values   LIST(STRUCT(type_id UTINYINT, byte_offset UINTEGER))
//   data     BLOB                                            the row's payload
// values[0] is the root. A primitive's payload sits at byte_offset in data:
// fixed-size types little-endian, string-like types as varint length + bytes,
// DECIMAL as varint width, varint scale, then the value, OBJECT/ARRAY as
// varint child_count, varint children_idx (an index into children).
//
// Shredded (VectorType SHREDDED) is STRUCT(unshredded <the layout above>,
// shredded T), where T is a flattened primitive or a wrapper
// STRUCT(typed_value, [untyped_value_index UINTEGER]); an OBJECT's
// typed_value is a STRUCT of field wrappers, an ARRAY's a LIST of element
// wrappers. untyped_value_index is 1-based into the row's unshredded values
// (0 = the field is missing); for an OBJECT whose typed_value is set, it names
// the leftover fields. Port of duckdb's VariantIterator / VariantNode.
internal static class VariantDecoder
{
    public static VariantColumn FromUnshredded(StructColumn variant, LogicalType type)
    {
        Unshredded u = new(variant);
        DuckDbVariant?[] values = new DuckDbVariant?[variant.Count];
        for (int row = 0; row < variant.Count; row++)
        {
            values[row] = variant.IsNull(row) ? null : u.Decode(row, 0);
        }
        return new VariantColumn { Type = type, Count = variant.Count, Validity = variant.Validity, Values = values };
    }

    // How many shredded VARIANT vectors were decoded in this process (a test hook:
    // proves that a test read the shredded path).
    internal static long ShreddedVectorsDecoded;

    public static VariantColumn FromShredded(StructColumn shreddedVector, LogicalType type)
    {
        Interlocked.Increment(ref ShreddedVectorsDecoded);
        if (shreddedVector.Fields.Length != 2 || shreddedVector.Fields[0] is not StructColumn unshreddedPart)
        {
            throw new SerializationException("A shredded VARIANT vector must be STRUCT(unshredded, shredded).");
        }
        Unshredded u = new(unshreddedPart);
        DuckDbColumn shredded = shreddedVector.Fields[1];
        int count = shreddedVector.Count;
        DuckDbVariant?[] values = new DuckDbVariant?[count];
        byte[] validity = new byte[ValidityMask.RequiredByteCount(count)];
        for (int row = 0; row < count; row++)
        {
            DuckDbVariant? value = null;
            // The shredded component's own validity is the row validity.
            if (shredded.Validity.IsValid(row))
            {
                Node root = Resolve(u, shredded, row, row);
                // A root that is missing or VARIANT_NULL is a SQL NULL.
                value = root.Kind is NodeKind.Missing ? null : Materialize(u, root);
                if (value is { Type: DuckDbVariantType.Null })
                {
                    value = null;
                }
            }
            values[row] = value;
            if (value is not null)
            {
                validity[row >> 3] |= (byte)(1 << (row & 7));
            }
        }
        return new VariantColumn { Type = type, Count = count, Validity = new ValidityMask(validity), Values = values };
    }

    // ---------------------------------------------------------------- shredded

    private enum NodeKind { Null, Missing, Unshredded, Shredded }

    private readonly record struct Node(NodeKind Kind, int Row, int ValueIndex, DuckDbColumn? Column, int Index, uint Overlay);

    // `node` is a flattened primitive or a STRUCT(typed_value, [untyped_value_index]) wrapper.
    private static Node Resolve(Unshredded u, DuckDbColumn node, int index, int row)
    {
        if (node.Type.Id != LogicalTypeId.Struct)
        {
            return node.Validity.IsValid(index)
                ? new Node(NodeKind.Shredded, row, 0, node, index, 0)
                : new Node(NodeKind.Null, row, 0, null, 0, 0);
        }
        StructColumn wrapper = (StructColumn)node;
        DuckDbColumn typed = wrapper.Fields[0];
        DuckDbColumn? untyped = wrapper.Fields.Length > 1 ? wrapper.Fields[1] : null;
        if (typed.Validity.IsValid(index))
        {
            if (untyped is null || typed.Type.Id != LogicalTypeId.Struct || !untyped.Validity.IsValid(index))
            {
                return new Node(NodeKind.Shredded, row, 0, typed, index, 0);
            }
            return new Node(NodeKind.Shredded, row, 0, typed, index, ReadUInt32((FixedSizeColumn)untyped, index));
        }
        if (untyped is null || !untyped.Validity.IsValid(index))
        {
            return new Node(NodeKind.Null, row, 0, null, 0, 0);
        }
        uint overlay = ReadUInt32((FixedSizeColumn)untyped, index);
        return overlay == 0
            ? new Node(NodeKind.Missing, row, 0, null, 0, 0)
            : new Node(NodeKind.Unshredded, row, checked((int)(overlay - 1)), null, 0, 0);
    }

    private static DuckDbVariant Materialize(Unshredded u, Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Null:
                return DuckDbVariant.VariantNull;
            case NodeKind.Unshredded:
                return u.Decode(node.Row, node.ValueIndex);
            case NodeKind.Shredded:
                break;
            default:
                throw new SerializationException("A missing VARIANT field cannot be materialized.");
        }
        DuckDbColumn column = node.Column!;
        int index = node.Index;
        switch (column)
        {
            case StructColumn obj when column.Type.Id == LogicalTypeId.Struct:
            {
                StructTypeInfo info = (StructTypeInfo)column.Type.TypeInfo!;
                List<KeyValuePair<string, DuckDbVariant>> fields = new(obj.Fields.Length);
                for (int i = 0; i < obj.Fields.Length; i++)
                {
                    Node child = Resolve(u, obj.Fields[i], index, node.Row);
                    if (child.Kind != NodeKind.Missing)
                    {
                        fields.Add(new(info.ChildTypes[i].Key, Materialize(u, child)));
                    }
                }
                if (node.Overlay != 0)
                {
                    fields.AddRange(u.DecodeObjectFields(node.Row, checked((int)(node.Overlay - 1))));
                }
                return new DuckDbVariant(DuckDbVariantType.Object, fields);
            }
            case ListColumn list:
            {
                (ulong offset, ulong length) = list.Entries[index];
                List<DuckDbVariant> elements = new((int)length);
                for (ulong i = 0; i < length; i++)
                {
                    Node element = Resolve(u, list.Child, checked((int)(offset + i)), node.Row);
                    elements.Add(element.Kind == NodeKind.Missing ? DuckDbVariant.VariantNull : Materialize(u, element));
                }
                return new DuckDbVariant(DuckDbVariantType.Array, elements);
            }
            default:
                return TypedPrimitive(column, index);
        }
    }

    // A value of a typed (shredded) column, as the VARIANT type duckdb maps it to.
    private static DuckDbVariant TypedPrimitive(DuckDbColumn column, int index)
    {
        if (column is VarBytesColumn bytes)
        {
            ReadOnlySpan<byte> raw = bytes.Values[index]!.Value.Span;
            return column.Type.Id switch
            {
                LogicalTypeId.Varchar => new DuckDbVariant(DuckDbVariantType.Varchar, Encoding.UTF8.GetString(raw)),
                LogicalTypeId.Blob => new DuckDbVariant(DuckDbVariantType.Blob, raw.ToArray()),
                LogicalTypeId.BigNum => new DuckDbVariant(DuckDbVariantType.BigNum, raw.ToArray()),
                LogicalTypeId.Bit => new DuckDbVariant(DuckDbVariantType.BitString, raw.ToArray()),
                LogicalTypeId.Geometry => new DuckDbVariant(DuckDbVariantType.Geometry, raw.ToArray()),
                _ => throw new SerializationException($"Shredded VARIANT type '{column.Type}' is not supported."),
            };
        }
        FixedSizeColumn fixedCol = (FixedSizeColumn)column;
        ReadOnlySpan<byte> v = fixedCol.GetBytes(index);
        return column.Type.Id switch
        {
            LogicalTypeId.Boolean => v[0] != 0 ? new DuckDbVariant(DuckDbVariantType.BoolTrue, true) : new DuckDbVariant(DuckDbVariantType.BoolFalse, false),
            LogicalTypeId.Decimal => Decimal(v, (DecimalTypeInfo)column.Type.TypeInfo!),
            _ => Primitive(ShreddedVariantType(column.Type.Id), v),
        };
    }

    private static DuckDbVariantType ShreddedVariantType(LogicalTypeId id) => id switch
    {
        LogicalTypeId.TinyInt => DuckDbVariantType.Int8,
        LogicalTypeId.SmallInt => DuckDbVariantType.Int16,
        LogicalTypeId.Integer => DuckDbVariantType.Int32,
        LogicalTypeId.BigInt => DuckDbVariantType.Int64,
        LogicalTypeId.HugeInt => DuckDbVariantType.Int128,
        LogicalTypeId.UTinyInt => DuckDbVariantType.UInt8,
        LogicalTypeId.USmallInt => DuckDbVariantType.UInt16,
        LogicalTypeId.UInteger => DuckDbVariantType.UInt32,
        LogicalTypeId.UBigInt => DuckDbVariantType.UInt64,
        LogicalTypeId.UHugeInt => DuckDbVariantType.UInt128,
        LogicalTypeId.Float => DuckDbVariantType.Float,
        LogicalTypeId.Double => DuckDbVariantType.Double,
        LogicalTypeId.Uuid => DuckDbVariantType.Uuid,
        LogicalTypeId.Date => DuckDbVariantType.Date,
        LogicalTypeId.Time => DuckDbVariantType.TimeMicros,
        LogicalTypeId.TimeNs => DuckDbVariantType.TimeNanos,
        LogicalTypeId.TimeTz => DuckDbVariantType.TimeMicrosTz,
        LogicalTypeId.TimestampSec => DuckDbVariantType.TimestampSec,
        LogicalTypeId.TimestampMs => DuckDbVariantType.TimestampMillis,
        LogicalTypeId.Timestamp => DuckDbVariantType.TimestampMicros,
        LogicalTypeId.TimestampNs => DuckDbVariantType.TimestampNanos,
        LogicalTypeId.TimestampTz => DuckDbVariantType.TimestampMicrosTz,
        LogicalTypeId.TimestampTzNs => DuckDbVariantType.TimestampNanosTz,
        LogicalTypeId.Interval => DuckDbVariantType.Interval,
        _ => throw new SerializationException($"Shredded VARIANT type '{id}' is not supported."),
    };

    // ------------------------------------------------------- shared encodings

    // A fixed-size primitive from its little-endian bytes.
    private static DuckDbVariant Primitive(DuckDbVariantType type, ReadOnlySpan<byte> v) => type switch
    {
        DuckDbVariantType.Int8 => new(type, (sbyte)v[0]),
        DuckDbVariantType.Int16 => new(type, BinaryPrimitives.ReadInt16LittleEndian(v)),
        DuckDbVariantType.Int32 => new(type, BinaryPrimitives.ReadInt32LittleEndian(v)),
        DuckDbVariantType.Int64 => new(type, BinaryPrimitives.ReadInt64LittleEndian(v)),
        DuckDbVariantType.Int128 => new(type, HugeintLayout.ReadSigned(v)),
        DuckDbVariantType.UInt8 => new(type, v[0]),
        DuckDbVariantType.UInt16 => new(type, BinaryPrimitives.ReadUInt16LittleEndian(v)),
        DuckDbVariantType.UInt32 => new(type, BinaryPrimitives.ReadUInt32LittleEndian(v)),
        DuckDbVariantType.UInt64 => new(type, BinaryPrimitives.ReadUInt64LittleEndian(v)),
        DuckDbVariantType.UInt128 => new(type, HugeintLayout.ReadUnsigned(v)),
        DuckDbVariantType.Float => new(type, BinaryPrimitives.ReadSingleLittleEndian(v)),
        DuckDbVariantType.Double => new(type, BinaryPrimitives.ReadDoubleLittleEndian(v)),
        DuckDbVariantType.Uuid => new(type, UuidFromHugeint(HugeintLayout.ReadSigned(v))),
        DuckDbVariantType.Date => new(type, BinaryPrimitives.ReadInt32LittleEndian(v)),
        DuckDbVariantType.TimeMicrosTz => new(type, BinaryPrimitives.ReadUInt64LittleEndian(v)),
        DuckDbVariantType.Interval => new(type, new DuckDbInterval(
            BinaryPrimitives.ReadInt32LittleEndian(v),
            BinaryPrimitives.ReadInt32LittleEndian(v[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(v[8..]))),
        DuckDbVariantType.TimeMicros or DuckDbVariantType.TimeNanos
            or DuckDbVariantType.TimestampSec or DuckDbVariantType.TimestampMillis
            or DuckDbVariantType.TimestampMicros or DuckDbVariantType.TimestampNanos
            or DuckDbVariantType.TimestampMicrosTz or DuckDbVariantType.TimestampNanosTz
            => new(type, BinaryPrimitives.ReadInt64LittleEndian(v)),
        _ => throw new SerializationException($"VARIANT type {type} is not a fixed-size primitive."),
    };

    private static int PrimitiveSize(DuckDbVariantType type) => type switch
    {
        DuckDbVariantType.Int8 or DuckDbVariantType.UInt8 => 1,
        DuckDbVariantType.Int16 or DuckDbVariantType.UInt16 => 2,
        DuckDbVariantType.Int32 or DuckDbVariantType.UInt32 or DuckDbVariantType.Float or DuckDbVariantType.Date => 4,
        DuckDbVariantType.Int128 or DuckDbVariantType.UInt128 or DuckDbVariantType.Uuid or DuckDbVariantType.Interval => 16,
        _ => 8,
    };

    private static DuckDbVariant Decimal(ReadOnlySpan<byte> value, DecimalTypeInfo info)
        => new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(ReadUnscaled(value, info.Width), info.Width, info.Scale));

    private static Int128 ReadUnscaled(ReadOnlySpan<byte> v, int width) => width switch
    {
        <= 4 => BinaryPrimitives.ReadInt16LittleEndian(v),
        <= 9 => BinaryPrimitives.ReadInt32LittleEndian(v),
        <= 18 => BinaryPrimitives.ReadInt64LittleEndian(v),
        _ => HugeintLayout.ReadSigned(v),
    };

    // DuckDB stores a UUID as a hugeint with the top bit flipped (so it sorts as unsigned).
    private static Guid UuidFromHugeint(Int128 value)
    {
        UInt128 bits = (UInt128)value ^ ((UInt128)1 << 127);
        Span<byte> bigEndian = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bigEndian, bits);
        return new Guid(bigEndian, bigEndian: true);
    }

    private static uint ReadUInt32(FixedSizeColumn column, int index) => BinaryPrimitives.ReadUInt32LittleEndian(column.GetBytes(index));

    private static uint ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= data.Length)
            {
                throw new SerializationException("VARIANT payload ended inside a varint.");
            }
            byte b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
            if (shift > 32)
            {
                throw new SerializationException("VARIANT varint is too large.");
            }
        }
    }

    // ------------------------------------------------------------- unshredded

    private sealed class Unshredded
    {
        private readonly ListColumn _keys;
        private readonly VarBytesColumn _keyStrings;
        private readonly ListColumn _children;
        private readonly FixedSizeColumn _keysIndex;
        private readonly FixedSizeColumn _valuesIndex;
        private readonly ListColumn _values;
        private readonly FixedSizeColumn _typeIds;
        private readonly FixedSizeColumn _byteOffsets;
        private readonly VarBytesColumn _data;

        public Unshredded(StructColumn variant)
        {
            if (variant.Fields.Length != 4)
            {
                throw new SerializationException($"A VARIANT vector must have 4 parts, found {variant.Fields.Length}.");
            }
            _keys = (ListColumn)variant.Fields[0];
            _keyStrings = (VarBytesColumn)_keys.Child;
            _children = (ListColumn)variant.Fields[1];
            StructColumn children = (StructColumn)_children.Child;
            _keysIndex = (FixedSizeColumn)children.Fields[0];
            _valuesIndex = (FixedSizeColumn)children.Fields[1];
            _values = (ListColumn)variant.Fields[2];
            StructColumn values = (StructColumn)_values.Child;
            _typeIds = (FixedSizeColumn)values.Fields[0];
            _byteOffsets = (FixedSizeColumn)values.Fields[1];
            _data = (VarBytesColumn)variant.Fields[3];
        }

        public DuckDbVariant Decode(int row, int valueIndex)
        {
            int v = checked((int)_values.Entries[row].Offset + valueIndex);
            DuckDbVariantType type = (DuckDbVariantType)_typeIds.GetBytes(v)[0];
            int offset = checked((int)ReadUInt32(_byteOffsets, v));
            ReadOnlySpan<byte> data = _data.Values[row].GetValueOrDefault().Span;
            switch (type)
            {
                case DuckDbVariantType.Null:
                    return DuckDbVariant.VariantNull;
                case DuckDbVariantType.BoolTrue:
                    return new DuckDbVariant(type, true);
                case DuckDbVariantType.BoolFalse:
                    return new DuckDbVariant(type, false);
                case DuckDbVariantType.Varchar:
                    return new DuckDbVariant(type, Encoding.UTF8.GetString(StringPayload(data, offset)));
                case DuckDbVariantType.Blob:
                case DuckDbVariantType.BigNum:
                case DuckDbVariantType.BitString:
                case DuckDbVariantType.Geometry:
                    return new DuckDbVariant(type, StringPayload(data, offset).ToArray());
                case DuckDbVariantType.Decimal:
                {
                    int pos = offset;
                    uint width = ReadVarint(data, ref pos);
                    uint scale = ReadVarint(data, ref pos);
                    return new DuckDbVariant(type, new DuckDbVariantDecimal(ReadUnscaled(data[pos..], (int)width), (byte)width, (byte)scale));
                }
                case DuckDbVariantType.Object:
                    return new DuckDbVariant(type, DecodeObjectFields(row, valueIndex));
                case DuckDbVariantType.Array:
                {
                    (int count, int childrenIdx) = Nested(data, offset);
                    List<DuckDbVariant> elements = new(count);
                    for (int i = 0; i < count; i++)
                    {
                        int c = checked((int)_children.Entries[row].Offset + childrenIdx + i);
                        elements.Add(Decode(row, checked((int)ReadUInt32(_valuesIndex, c))));
                    }
                    return new DuckDbVariant(type, elements);
                }
                default:
                {
                    int size = PrimitiveSize(type);
                    if (offset + size > data.Length)
                    {
                        throw new SerializationException($"VARIANT {type} payload exceeds the row's data.");
                    }
                    return Primitive(type, data.Slice(offset, size));
                }
            }
        }

        // The fields of the OBJECT at valueIndex.
        public List<KeyValuePair<string, DuckDbVariant>> DecodeObjectFields(int row, int valueIndex)
        {
            int v = checked((int)_values.Entries[row].Offset + valueIndex);
            int offset = checked((int)ReadUInt32(_byteOffsets, v));
            (int count, int childrenIdx) = Nested(_data.Values[row].GetValueOrDefault().Span, offset);
            List<KeyValuePair<string, DuckDbVariant>> fields = new(count);
            for (int i = 0; i < count; i++)
            {
                int c = checked((int)_children.Entries[row].Offset + childrenIdx + i);
                int key = checked((int)_keys.Entries[row].Offset + (int)ReadUInt32(_keysIndex, c));
                string name = Encoding.UTF8.GetString(_keyStrings.Values[key].GetValueOrDefault().Span);
                fields.Add(new(name, Decode(row, checked((int)ReadUInt32(_valuesIndex, c)))));
            }
            return fields;
        }

        private static (int Count, int ChildrenIdx) Nested(ReadOnlySpan<byte> data, int offset)
        {
            int pos = offset;
            int count = checked((int)ReadVarint(data, ref pos));
            int childrenIdx = count > 0 ? checked((int)ReadVarint(data, ref pos)) : 0;
            return (count, childrenIdx);
        }

        private static ReadOnlySpan<byte> StringPayload(ReadOnlySpan<byte> data, int offset)
        {
            int pos = offset;
            int length = checked((int)ReadVarint(data, ref pos));
            if (pos + length > data.Length)
            {
                throw new SerializationException("VARIANT string payload exceeds the row's data.");
            }
            return data.Slice(pos, length);
        }
    }
}

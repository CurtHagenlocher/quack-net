// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Quack.Types;

namespace Quack.Data;

// Encodes DuckDbVariant values into DuckDB's canonical (unshredded) VARIANT
// layout, as duckdb's VariantBuilder does (variant_builder.hpp):
//   - values in pre-order, the root at values[0];
//   - an OBJECT / ARRAY reserves one contiguous block of `children` before its
//     children are emitted, and stores (child_count, children_idx) as varints;
//   - an OBJECT's fields are sorted by key (byte-wise on UTF-8), and every
//     object child gets its own key slot in `keys` (keys_index); an ARRAY
//     element's keys_index is NULL;
//   - primitives are written at byte_offset into `data` exactly as
//     VariantDecoder reads them.
// A root VARIANT null is sent as a SQL NULL row: duckdb treats a root that
// resolves to a variant null as SQL NULL.
internal static class VariantEncoder
{
    public static StructColumn ToStruct(VariantColumn column)
    {
        int count = column.Count;
        StructTypeInfo info = column.Type.TypeInfo as StructTypeInfo ?? (StructTypeInfo)LogicalType.Variant.TypeInfo!;
        LogicalType keysType = info.ChildTypes[0].Value;
        LogicalType childrenType = info.ChildTypes[1].Value;
        LogicalType valuesType = info.ChildTypes[2].Value;
        LogicalType dataType = info.ChildTypes[3].Value;
        StructTypeInfo childInfo = (StructTypeInfo)((ListTypeInfo)childrenType.TypeInfo!).ChildType.TypeInfo!;
        StructTypeInfo valueInfo = (StructTypeInfo)((ListTypeInfo)valuesType.TypeInfo!).ChildType.TypeInfo!;

        Builder b = new();
        (ulong, ulong)[] keyEntries = new (ulong, ulong)[count];
        (ulong, ulong)[] childEntries = new (ulong, ulong)[count];
        (ulong, ulong)[] valueEntries = new (ulong, ulong)[count];
        ReadOnlyMemory<byte>?[] data = new ReadOnlyMemory<byte>?[count];
        byte[] rowValidity = new byte[ValidityMask.RequiredByteCount(count)];

        for (int row = 0; row < count; row++)
        {
            DuckDbVariant? value = column.IsNull(row) ? null : column.Values[row];
            int keyStart = b.Keys.Count, childStart = b.ChildValue.Count, valueStart = b.TypeIds.Count;
            if (value is not null && value.Type != DuckDbVariantType.Null)
            {
                b.BeginRow();
                b.Emit(value);
                data[row] = b.Blob.ToArray();
                rowValidity[row >> 3] |= (byte)(1 << (row & 7));
            }
            keyEntries[row] = ((ulong)keyStart, (ulong)(b.Keys.Count - keyStart));
            childEntries[row] = ((ulong)childStart, (ulong)(b.ChildValue.Count - childStart));
            valueEntries[row] = ((ulong)valueStart, (ulong)(b.TypeIds.Count - valueStart));
        }

        ValidityMask validity = new(rowValidity);
        int children = b.ChildValue.Count;
        byte[] keysIndexValidity = new byte[ValidityMask.RequiredByteCount(children)];
        for (int i = 0; i < children; i++)
        {
            if (b.ChildKey[i] != uint.MaxValue) keysIndexValidity[i >> 3] |= (byte)(1 << (i & 7));
        }

        return new StructColumn
        {
            Type = column.Type,
            Count = count,
            Validity = validity,
            Fields =
            [
                new ListColumn
                {
                    Type = keysType,
                    Count = count,
                    Validity = validity,
                    Entries = keyEntries,
                    Child = new VarBytesColumn
                    {
                        Type = ((ListTypeInfo)keysType.TypeInfo!).ChildType,
                        Count = b.Keys.Count,
                        Values = b.Keys.Select(k => (ReadOnlyMemory<byte>?)k).ToArray(),
                    },
                },
                new ListColumn
                {
                    Type = childrenType,
                    Count = count,
                    Validity = validity,
                    Entries = childEntries,
                    Child = new StructColumn
                    {
                        Type = ((ListTypeInfo)childrenType.TypeInfo!).ChildType,
                        Count = children,
                        Fields =
                        [
                            UInt32Column(childInfo.ChildTypes[0].Value, b.ChildKey, new ValidityMask(keysIndexValidity)),
                            UInt32Column(childInfo.ChildTypes[1].Value, b.ChildValue, ValidityMask.AllValid),
                        ],
                    },
                },
                new ListColumn
                {
                    Type = valuesType,
                    Count = count,
                    Validity = validity,
                    Entries = valueEntries,
                    Child = new StructColumn
                    {
                        Type = ((ListTypeInfo)valuesType.TypeInfo!).ChildType,
                        Count = b.TypeIds.Count,
                        Fields =
                        [
                            new FixedSizeColumn { Type = valueInfo.ChildTypes[0].Value, Count = b.TypeIds.Count, ElementSize = 1, Data = b.TypeIds.ToArray() },
                            UInt32Column(valueInfo.ChildTypes[1].Value, b.ByteOffsets, ValidityMask.AllValid),
                        ],
                    },
                },
                new VarBytesColumn { Type = dataType, Count = count, Validity = validity, Values = data },
            ],
        };
    }

    private static FixedSizeColumn UInt32Column(LogicalType type, List<uint> values, ValidityMask validity)
    {
        byte[] bytes = new byte[values.Count * sizeof(uint)];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), values[i]);
        }
        return new FixedSizeColumn { Type = type, Count = values.Count, ElementSize = sizeof(uint), Data = bytes, Validity = validity };
    }

    private sealed class Builder
    {
        public readonly List<byte> TypeIds = [];
        public readonly List<uint> ByteOffsets = [];
        // keys_index per child (uint.MaxValue = NULL, an array element) and values_index.
        public readonly List<uint> ChildKey = [];
        public readonly List<uint> ChildValue = [];
        public readonly List<byte[]> Keys = [];
        public readonly List<byte> Blob = [];
        private int _rowValues, _rowChildren, _rowKeys;

        public void BeginRow()
        {
            _rowValues = TypeIds.Count;
            _rowChildren = ChildValue.Count;
            _rowKeys = Keys.Count;
            Blob.Clear();
        }

        private uint LocalValue => (uint)(TypeIds.Count - _rowValues);
        private uint LocalChild => (uint)(ChildValue.Count - _rowChildren);
        private uint LocalKey => (uint)(Keys.Count - _rowKeys);

        public void Emit(DuckDbVariant v)
        {
            TypeIds.Add((byte)v.Type);
            ByteOffsets.Add(checked((uint)Blob.Count));
            switch (v.Type)
            {
                case DuckDbVariantType.Null:
                case DuckDbVariantType.BoolTrue:
                case DuckDbVariantType.BoolFalse:
                    return;
                case DuckDbVariantType.Object:
                {
                    // Sorted by key, byte-wise on UTF-8, as duckdb expects.
                    var fields = v.Fields
                        .Select(f => (Key: Encoding.UTF8.GetBytes(f.Key), f.Value))
                        .OrderBy(f => f.Key, ByteComparer.Instance)
                        .ToList();
                    for (int i = 1; i < fields.Count; i++)
                    {
                        if (fields[i].Key.AsSpan().SequenceEqual(fields[i - 1].Key))
                        {
                            throw new ArgumentException($"VARIANT object has the key '{Encoding.UTF8.GetString(fields[i].Key)}' twice.");
                        }
                    }
                    int block = BeginNested(fields.Count);
                    for (int i = 0; i < fields.Count; i++)
                    {
                        ChildValue[block + i] = LocalValue;
                        ChildKey[block + i] = LocalKey;
                        Keys.Add(fields[i].Key);
                        Emit(fields[i].Value);
                    }
                    return;
                }
                case DuckDbVariantType.Array:
                {
                    IReadOnlyList<DuckDbVariant> elements = v.Elements;
                    int block = BeginNested(elements.Count);
                    for (int i = 0; i < elements.Count; i++)
                    {
                        ChildValue[block + i] = LocalValue;
                        ChildKey[block + i] = uint.MaxValue;
                        Emit(elements[i] ?? DuckDbVariant.VariantNull);
                    }
                    return;
                }
                case DuckDbVariantType.Varchar:
                    WriteString(Encoding.UTF8.GetBytes((string)v.Value!));
                    return;
                case DuckDbVariantType.Blob:
                case DuckDbVariantType.BigNum:
                case DuckDbVariantType.BitString:
                case DuckDbVariantType.Geometry:
                    WriteString((byte[])v.Value!);
                    return;
                case DuckDbVariantType.Decimal:
                {
                    DuckDbVariantDecimal d = (DuckDbVariantDecimal)v.Value!;
                    if (d.Width is 0 or > 38 || d.Scale > d.Width)
                    {
                        throw new ArgumentException($"Invalid VARIANT DECIMAL({d.Width}, {d.Scale}).");
                    }
                    WriteVarint(d.Width);
                    WriteVarint(d.Scale);
                    Span<byte> buf = stackalloc byte[16];
                    switch (d.Width)
                    {
                        case <= 4: BinaryPrimitives.WriteInt16LittleEndian(buf, checked((short)d.Unscaled)); Blob.AddRange(buf[..2].ToArray()); break;
                        case <= 9: BinaryPrimitives.WriteInt32LittleEndian(buf, checked((int)d.Unscaled)); Blob.AddRange(buf[..4].ToArray()); break;
                        case <= 18: BinaryPrimitives.WriteInt64LittleEndian(buf, checked((long)d.Unscaled)); Blob.AddRange(buf[..8].ToArray()); break;
                        default: HugeintLayout.WriteSigned(buf, d.Unscaled); Blob.AddRange(buf.ToArray()); break;
                    }
                    return;
                }
                default:
                    Blob.AddRange(FixedPayload(v));
                    return;
            }
        }

        // Writes (child_count[, children_idx]) and reserves the children block.
        private int BeginNested(int n)
        {
            WriteVarint((uint)n);
            int block = ChildValue.Count;
            if (n == 0)
            {
                return block;
            }
            WriteVarint(LocalChild);
            for (int i = 0; i < n; i++)
            {
                ChildValue.Add(0);
                ChildKey.Add(0);
            }
            return block;
        }

        private void WriteString(byte[] bytes)
        {
            WriteVarint(checked((uint)bytes.Length));
            Blob.AddRange(bytes);
        }

        private void WriteVarint(uint value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                Blob.Add(value != 0 ? (byte)(b | 0x80) : b);
            }
            while (value != 0);
        }
    }

    private static byte[] FixedPayload(DuckDbVariant v)
    {
        byte[] b;
        switch (v.Type)
        {
            case DuckDbVariantType.Int8: return [(byte)(sbyte)v.Value!];
            case DuckDbVariantType.UInt8: return [(byte)v.Value!];
            case DuckDbVariantType.Int16: b = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, (short)v.Value!); return b;
            case DuckDbVariantType.UInt16: b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v.Value!); return b;
            case DuckDbVariantType.Int32: b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, (int)v.Value!); return b;
            case DuckDbVariantType.UInt32: b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v.Value!); return b;
            case DuckDbVariantType.Date: b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, (int)v.Value!); return b;
            case DuckDbVariantType.Float: b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, (float)v.Value!); return b;
            case DuckDbVariantType.Double: b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, (double)v.Value!); return b;
            case DuckDbVariantType.UInt64: b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v.Value!); return b;
            case DuckDbVariantType.TimeMicrosTz: b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v.Value!); return b;
            case DuckDbVariantType.Int64:
            case DuckDbVariantType.TimeMicros:
            case DuckDbVariantType.TimeNanos:
            case DuckDbVariantType.TimestampSec:
            case DuckDbVariantType.TimestampMillis:
            case DuckDbVariantType.TimestampMicros:
            case DuckDbVariantType.TimestampNanos:
            case DuckDbVariantType.TimestampMicrosTz:
            case DuckDbVariantType.TimestampNanosTz:
                b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, (long)v.Value!); return b;
            case DuckDbVariantType.Int128: b = new byte[16]; HugeintLayout.WriteSigned(b, (Int128)v.Value!); return b;
            case DuckDbVariantType.UInt128: b = new byte[16]; HugeintLayout.WriteSigned(b, (Int128)(UInt128)v.Value!); return b;
            case DuckDbVariantType.Uuid:
            {
                // DuckDB stores a UUID as a hugeint with the top bit flipped.
                Span<byte> bigEndian = stackalloc byte[16];
                ((Guid)v.Value!).TryWriteBytes(bigEndian, bigEndian: true, out _);
                UInt128 bits = BinaryPrimitives.ReadUInt128BigEndian(bigEndian) ^ ((UInt128)1 << 127);
                b = new byte[16];
                HugeintLayout.WriteSigned(b, (Int128)bits);
                return b;
            }
            case DuckDbVariantType.Interval:
            {
                DuckDbInterval i = (DuckDbInterval)v.Value!;
                b = new byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(b, i.Months);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), i.Days);
                BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), i.Microseconds);
                return b;
            }
            default:
                throw new ArgumentException($"VARIANT type {v.Type} has no fixed payload.");
        }
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Quack.Data;
using Array = System.Array;

namespace Quack.Adbc;

// DuckDB's canonical VARIANT vector to the arrow.parquet.variant extension
// (metadata + value, Parquet Variant binary encoding). A port of duckdb's
// ParquetVariantConversion::ToParquetVariant (parquet_variant_encoding.cpp),
// so the bytes are the ones DuckDB's own Arrow export writes:
//   - the row's keys list is the metadata dictionary, unchanged and unsorted
//     (sorted_strings = 0);
//   - object fields are ordered by the UTF-8 bytes of their keys;
//   - unsigned and 128-bit integers widen to the next signed type (UINT32,
//     UINT64, UINT128 and INT128 to INT64 when they fit);
//   - the types the Parquet Variant spec cannot express (INTERVAL, BIGNUM,
//     BITSTRING, TIMESTAMP_S/_MS, TIME_TZ, TIME_NS, TIMESTAMP_NS WITH TIME
//     ZONE, GEOMETRY) are refused.
// Each value is measured first and then written once into one buffer per
// column, straight from the vector's buffers: no per-value objects.
internal sealed class ParquetVariantWriter
{
    private static readonly StructType StorageType = (StructType)VariantType.Default.StorageType;

    private readonly StructColumn _variant;
    private readonly ListColumn _keys;
    private readonly VarBytesColumn _keyStrings;
    private readonly ListColumn _children;
    private readonly ListColumn _values;
    private readonly VarBytesColumn _data;
    private readonly Buffer _keysIndex;
    private readonly Buffer _valuesIndex;
    private readonly Buffer _typeIds;
    private readonly Buffer _byteOffsets;

    // The row being written.
    private int _keysStart;
    private int _childrenStart;
    private int _valuesStart;
    private Buffer _rowData;

    // Per row, in pre-order of its nested values: the field offsets that the
    // measuring pass computes for the writing pass, and each object's field order.
    private uint[] _offsets = new uint[64];
    private int _offsetCount;
    private int[] _order = new int[64];
    private int _orderCount;
    private KeyOrder? _keyOrder;

    private ParquetVariantWriter(StructColumn variant)
    {
        if (variant.Fields.Length != 4)
        {
            throw new InvalidOperationException($"A VARIANT vector must have 4 parts, found {variant.Fields.Length}.");
        }
        _variant = variant;
        _keys = (ListColumn)variant.Fields[0];
        _keyStrings = (VarBytesColumn)_keys.Child;
        _children = (ListColumn)variant.Fields[1];
        StructColumn children = (StructColumn)_children.Child;
        _keysIndex = new Buffer(((FixedSizeColumn)children.Fields[0]).Data);
        _valuesIndex = new Buffer(((FixedSizeColumn)children.Fields[1]).Data);
        _values = (ListColumn)variant.Fields[2];
        StructColumn values = (StructColumn)_values.Child;
        _typeIds = new Buffer(((FixedSizeColumn)values.Fields[0]).Data);
        _byteOffsets = new Buffer(((FixedSizeColumn)values.Fields[1]).Data);
        _data = (VarBytesColumn)variant.Fields[3];
    }

    public static VariantArray Build(VariantColumn column, int rowCount)
    {
        // A column read from the wire converts from its vector; a shredded one,
        // or one built from values, goes through the canonical layout first.
        StructColumn canonical = column.Source is { } source && !column.SourceIsShredded
            ? source
            : VariantEncoder.ToStruct(column);
        return new ParquetVariantWriter(canonical).WriteColumn(rowCount);
    }

    private VariantArray WriteColumn(int rowCount)
    {
        long dataBytes = 0;
        long keyBytes = 0;
        for (int row = 0; row < rowCount; row++)
        {
            dataBytes += _data.Values[row]?.Length ?? 0;
        }
        foreach (ReadOnlyMemory<byte>? key in _keyStrings.Values)
        {
            keyBytes += key?.Length ?? 0;
        }
        Output metadata = new(rowCount, keyBytes + 8L * rowCount);
        Output value = new(rowCount, dataBytes + 8L * rowCount);
        int nullCount = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if (_variant.IsNull(row))
            {
                // A placeholder VARIANT null under the struct's null bit, as Arrow's builder writes.
                metadata.Append([0x01, 0x00, 0x00]);
                value.Append([0x00]);
                nullCount++;
            }
            else
            {
                _keysStart = checked((int)_keys.Entries[row].Offset);
                _childrenStart = checked((int)_children.Entries[row].Offset);
                _valuesStart = checked((int)_values.Entries[row].Offset);
                _rowData = new Buffer(_data.Values[row] ?? ReadOnlyMemory<byte>.Empty);
                WriteMetadata(row, metadata);
                WriteValue(value);
            }
            metadata.EndRow(row);
            value.EndRow(row);
        }

        ArrowBuffer validity = nullCount == 0
            ? ArrowBuffer.Empty
            : new ArrowBuffer(_variant.Validity.Bytes[..ValidityMask.RequiredByteCount(rowCount)]);
        StructArray storage = new(StorageType, rowCount, [metadata.Build(rowCount), value.Build(rowCount)], validity, nullCount);
        return new VariantArray(VariantType.Default, storage);
    }

    // ------------------------------------------------------------- metadata

    private void WriteMetadata(int row, Output output)
    {
        int count = checked((int)_keys.Entries[row].Length);
        long size = 0;
        for (int i = 0; i < count; i++)
        {
            size += Key(i).Length;
        }
        if (size >= uint.MaxValue)
        {
            throw new InvalidOperationException("The total length of the VARIANT keys exceeds 4 bytes; can't convert it to Parquet VARIANT.");
        }
        // duckdb sizes the offsets by the dictionary's byte size alone; the
        // count shares that width, so it counts here too.
        int width = ByteLength((ulong)Math.Max(size, count));
        int total = checked(1 + width * (count + 2) + (int)size);
        Span<byte> o = output.Reserve(total);
        o[0] = (byte)(1 | ((width - 1) << 6));
        WriteLittleEndian(o[1..], (uint)count, width);
        int offsets = 1 + width;
        int strings = offsets + (count + 1) * width;
        uint keyOffset = 0;
        for (int i = 0; i < count; i++)
        {
            WriteLittleEndian(o[(offsets + i * width)..], keyOffset, width);
            ReadOnlySpan<byte> key = Key(i);
            key.CopyTo(o[(strings + (int)keyOffset)..]);
            keyOffset += (uint)key.Length;
        }
        WriteLittleEndian(o[(offsets + count * width)..], keyOffset, width);
    }

    private ReadOnlySpan<byte> Key(int keysIndex)
        => (_keyStrings.Values[_keysStart + keysIndex] ?? ReadOnlyMemory<byte>.Empty).Span;

    // ---------------------------------------------------------------- value

    private void WriteValue(Output output)
    {
        _offsetCount = 0;
        _orderCount = 0;
        int size = Measure(0);
        Span<byte> o = output.Reserve(size);
        int pos = 0, offsetIndex = 0, orderIndex = 0;
        Write(0, o, ref pos, ref offsetIndex, ref orderIndex);
        if (pos != size)
        {
            throw new InvalidOperationException($"Parquet VARIANT value measured {size} bytes but wrote {pos}.");
        }
    }

    // The encoded size of the value at valueIndex; for a nested value, records
    // its field offsets (and an object's field order) for Write.
    private int Measure(int valueIndex)
    {
        DuckDbVariantType type = TypeOf(valueIndex);
        if (type is not (DuckDbVariantType.Object or DuckDbVariantType.Array))
        {
            return PrimitiveSize(type, valueIndex);
        }
        (int count, int childrenIdx) = Nested(valueIndex);
        int offsetStart = Reserve(ref _offsets, ref _offsetCount, count + 1);
        bool isObject = type == DuckDbVariantType.Object;
        int orderStart = isObject ? OrderFields(count, childrenIdx) : 0;
        uint total = 0;
        uint highestKey = 0;
        for (int i = 0; i < count; i++)
        {
            _offsets[offsetStart + i] = total;
            int child = _childrenStart + childrenIdx + (isObject ? _order[orderStart + i] : i);
            if (isObject)
            {
                highestKey = Math.Max(highestKey, KeysIndex(child));
            }
            total = checked(total + (uint)Measure((int)ValuesIndex(child)));
        }
        _offsets[offsetStart + count] = total;
        int fieldIdSize = isObject ? ByteLength(highestKey) : 0;
        return checked(NestedHeaderSize(count, fieldIdSize, ByteLength(total)) + (int)total);
    }

    private void Write(int valueIndex, Span<byte> o, ref int pos, ref int offsetIndex, ref int orderIndex)
    {
        DuckDbVariantType type = TypeOf(valueIndex);
        if (type is not (DuckDbVariantType.Object or DuckDbVariantType.Array))
        {
            WritePrimitive(type, valueIndex, o, ref pos);
            return;
        }
        (int count, int childrenIdx) = Nested(valueIndex);
        int offsetStart = offsetIndex;
        offsetIndex += count + 1;
        bool isObject = type == DuckDbVariantType.Object;
        int orderStart = orderIndex;
        if (isObject)
        {
            orderIndex += count;
        }
        int fieldOffsetSize = ByteLength(_offsets[offsetStart + count]);
        int fieldIdSize = 0;
        if (isObject)
        {
            uint highestKey = 0;
            for (int i = 0; i < count; i++)
            {
                highestKey = Math.Max(highestKey, KeysIndex(_childrenStart + childrenIdx + _order[orderStart + i]));
            }
            fieldIdSize = ByteLength(highestKey);
        }
        bool isLarge = count > byte.MaxValue;
        o[pos++] = isObject
            ? (byte)(0b10 | ((isLarge ? 1 : 0) << 6) | ((fieldIdSize - 1) << 4) | ((fieldOffsetSize - 1) << 2))
            : (byte)(0b11 | ((isLarge ? 1 : 0) << 4) | ((fieldOffsetSize - 1) << 2));
        if (isLarge)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o[pos..], (uint)count);
            pos += 4;
        }
        else
        {
            o[pos++] = (byte)count;
        }
        if (isObject)
        {
            for (int i = 0; i < count; i++)
            {
                WriteLittleEndian(o[pos..], KeysIndex(_childrenStart + childrenIdx + _order[orderStart + i]), fieldIdSize);
                pos += fieldIdSize;
            }
        }
        for (int i = 0; i <= count; i++)
        {
            WriteLittleEndian(o[pos..], _offsets[offsetStart + i], fieldOffsetSize);
            pos += fieldOffsetSize;
        }
        for (int i = 0; i < count; i++)
        {
            int child = _childrenStart + childrenIdx + (isObject ? _order[orderStart + i] : i);
            Write((int)ValuesIndex(child), o, ref pos, ref offsetIndex, ref orderIndex);
        }
    }

    // Reserves the object's field order and sorts it by key bytes.
    private int OrderFields(int count, int childrenIdx)
    {
        int start = Reserve(ref _order, ref _orderCount, count);
        for (int i = 0; i < count; i++)
        {
            _order[start + i] = i;
        }
        if (count <= 16)
        {
            for (int i = 1; i < count; i++)
            {
                int field = _order[start + i];
                int j = i - 1;
                while (j >= 0 && CompareKeys(childrenIdx, _order[start + j], field) > 0)
                {
                    _order[start + j + 1] = _order[start + j];
                    j--;
                }
                _order[start + j + 1] = field;
            }
        }
        else
        {
            _keyOrder ??= new KeyOrder(this);
            _keyOrder.ChildrenIdx = childrenIdx;
            Array.Sort(_order, start, count, _keyOrder);
        }
        return start;
    }

    private int CompareKeys(int childrenIdx, int left, int right)
        => Key((int)KeysIndex(_childrenStart + childrenIdx + left))
            .SequenceCompareTo(Key((int)KeysIndex(_childrenStart + childrenIdx + right)));

    private sealed class KeyOrder(ParquetVariantWriter writer) : IComparer<int>
    {
        public int ChildrenIdx;

        public int Compare(int x, int y) => writer.CompareKeys(ChildrenIdx, x, y);
    }

    private static int NestedHeaderSize(int count, int fieldIdSize, int fieldOffsetSize)
        => 1 + (count > byte.MaxValue ? 4 : 1) + count * fieldIdSize + (count + 1) * fieldOffsetSize;

    // ------------------------------------------------------------ primitives

    private int PrimitiveSize(DuckDbVariantType type, int valueIndex)
    {
        switch (type)
        {
            case DuckDbVariantType.Null:
            case DuckDbVariantType.BoolTrue:
            case DuckDbVariantType.BoolFalse:
                return 1;
            case DuckDbVariantType.Varchar:
            {
                int length = StringPayload(valueIndex).Length;
                return length < 64 ? 1 + length : 5 + length;
            }
            case DuckDbVariantType.Blob:
                return 5 + StringPayload(valueIndex).Length;
            case DuckDbVariantType.Int8:
                return 2;
            case DuckDbVariantType.Int16:
            case DuckDbVariantType.UInt8:
                return 3;
            case DuckDbVariantType.Int32:
            case DuckDbVariantType.UInt16:
            case DuckDbVariantType.Float:
            case DuckDbVariantType.Date:
                return 5;
            case DuckDbVariantType.Int64:
            case DuckDbVariantType.UInt32:
            case DuckDbVariantType.UInt64:
            case DuckDbVariantType.UInt128:
            case DuckDbVariantType.Int128:
            case DuckDbVariantType.Double:
            case DuckDbVariantType.TimeMicros:
            case DuckDbVariantType.TimestampMicros:
            case DuckDbVariantType.TimestampNanos:
            case DuckDbVariantType.TimestampMicrosTz:
                return 9;
            case DuckDbVariantType.Uuid:
                return 17;
            case DuckDbVariantType.Decimal:
            {
                (int width, int scale, _) = DecimalPayload(valueIndex);
                return width switch
                {
                    <= 9 => 6,
                    <= 18 => 10,
                    <= 38 => 18,
                    _ => throw new NotSupportedException($"Can't convert VARIANT DECIMAL({width}, {scale}) to Parquet VARIANT."),
                };
            }
            default:
                throw new NotSupportedException($"Can't convert VARIANT of type '{type}' to Parquet VARIANT.");
        }
    }

    private void WritePrimitive(DuckDbVariantType type, int valueIndex, Span<byte> o, ref int pos)
    {
        switch (type)
        {
            case DuckDbVariantType.Null:
                o[pos++] = Primitive(0);
                return;
            case DuckDbVariantType.BoolTrue:
                o[pos++] = Primitive(1);
                return;
            case DuckDbVariantType.BoolFalse:
                o[pos++] = Primitive(2);
                return;
            case DuckDbVariantType.Varchar:
            case DuckDbVariantType.Blob:
            {
                ReadOnlySpan<byte> payload = StringPayload(valueIndex);
                if (type == DuckDbVariantType.Varchar && payload.Length < 64)
                {
                    o[pos++] = (byte)(0b01 | (payload.Length << 2));
                }
                else
                {
                    o[pos++] = Primitive(type == DuckDbVariantType.Blob ? 15 : 16);
                    BinaryPrimitives.WriteUInt32LittleEndian(o[pos..], (uint)payload.Length);
                    pos += 4;
                }
                payload.CopyTo(o[pos..]);
                pos += payload.Length;
                return;
            }
            case DuckDbVariantType.Int8:
                Copy(o, ref pos, 3, valueIndex, 1);
                return;
            case DuckDbVariantType.Int16:
                Copy(o, ref pos, 4, valueIndex, 2);
                return;
            case DuckDbVariantType.Int32:
                Copy(o, ref pos, 5, valueIndex, 4);
                return;
            case DuckDbVariantType.Int64:
                Copy(o, ref pos, 6, valueIndex, 8);
                return;
            case DuckDbVariantType.Double:
                Copy(o, ref pos, 7, valueIndex, 8);
                return;
            case DuckDbVariantType.Date:
                Copy(o, ref pos, 11, valueIndex, 4);
                return;
            case DuckDbVariantType.TimestampMicrosTz:
                Copy(o, ref pos, 12, valueIndex, 8);
                return;
            case DuckDbVariantType.TimestampMicros:
                Copy(o, ref pos, 13, valueIndex, 8);
                return;
            case DuckDbVariantType.Float:
                Copy(o, ref pos, 14, valueIndex, 4);
                return;
            case DuckDbVariantType.TimeMicros:
                Copy(o, ref pos, 17, valueIndex, 8);
                return;
            case DuckDbVariantType.TimestampNanos:
                Copy(o, ref pos, 19, valueIndex, 8);
                return;
            case DuckDbVariantType.Uuid:
            {
                // DuckDB stores a UUID as a hugeint with the top bit flipped; Parquet as its 16 bytes.
                UInt128 bits = HugeintLayout.ReadUnsigned(Fixed(valueIndex, 16)) ^ ((UInt128)1 << 127);
                o[pos++] = Primitive(20);
                BinaryPrimitives.WriteUInt128BigEndian(o[pos..], bits);
                pos += 16;
                return;
            }
            case DuckDbVariantType.UInt8:
                o[pos++] = Primitive(4);
                BinaryPrimitives.WriteInt16LittleEndian(o[pos..], Fixed(valueIndex, 1)[0]);
                pos += 2;
                return;
            case DuckDbVariantType.UInt16:
                o[pos++] = Primitive(5);
                BinaryPrimitives.WriteInt32LittleEndian(o[pos..], BinaryPrimitives.ReadUInt16LittleEndian(Fixed(valueIndex, 2)));
                pos += 4;
                return;
            case DuckDbVariantType.UInt32:
                WriteInt64(o, ref pos, BinaryPrimitives.ReadUInt32LittleEndian(Fixed(valueIndex, 4)));
                return;
            case DuckDbVariantType.UInt64:
                WriteInt64(o, ref pos, ToInt64(BinaryPrimitives.ReadUInt64LittleEndian(Fixed(valueIndex, 8)), type));
                return;
            case DuckDbVariantType.UInt128:
                WriteInt64(o, ref pos, ToInt64(HugeintLayout.ReadUnsigned(Fixed(valueIndex, 16)), type));
                return;
            case DuckDbVariantType.Int128:
            {
                Int128 value = HugeintLayout.ReadSigned(Fixed(valueIndex, 16));
                WriteInt64(o, ref pos, value >= long.MinValue && value <= long.MaxValue
                    ? (long)value
                    : throw new OverflowException($"VARIANT {type} value {value} does not fit the Parquet VARIANT INT64."));
                return;
            }
            case DuckDbVariantType.Decimal:
            {
                (int width, int scale, int start) = DecimalPayload(valueIndex);
                o[pos++] = Primitive(width <= 9 ? 8 : width <= 18 ? 9 : 10);
                o[pos++] = (byte)scale;
                if (width <= 4)
                {
                    // DuckDB stores DECIMAL(<=4) as INT16; Parquet's smallest is DECIMAL4.
                    BinaryPrimitives.WriteInt32LittleEndian(o[pos..], BinaryPrimitives.ReadInt16LittleEndian(_rowData.At(start, 2)));
                    pos += 4;
                }
                else
                {
                    int size = width <= 9 ? 4 : width <= 18 ? 8 : 16;
                    _rowData.At(start, size).CopyTo(o[pos..]);
                    pos += size;
                }
                return;
            }
            default:
                throw new NotSupportedException($"Can't convert VARIANT of type '{type}' to Parquet VARIANT.");
        }
    }

    private static byte Primitive(int primitiveType) => (byte)(primitiveType << 2);

    private void Copy(Span<byte> o, ref int pos, int primitiveType, int valueIndex, int size)
    {
        o[pos++] = Primitive(primitiveType);
        Fixed(valueIndex, size).CopyTo(o[pos..]);
        pos += size;
    }

    private static void WriteInt64(Span<byte> o, ref int pos, long value)
    {
        o[pos++] = Primitive(6);
        BinaryPrimitives.WriteInt64LittleEndian(o[pos..], value);
        pos += 8;
    }

    private static long ToInt64(UInt128 value, DuckDbVariantType type)
        => value <= long.MaxValue
            ? (long)value
            : throw new OverflowException($"VARIANT {type} value {value} does not fit the Parquet VARIANT INT64.");

    // ------------------------------------------------------ vector access

    private DuckDbVariantType TypeOf(int valueIndex) => (DuckDbVariantType)_typeIds.At(_valuesStart + valueIndex, 1)[0];

    private uint KeysIndex(int child) => BinaryPrimitives.ReadUInt32LittleEndian(_keysIndex.At(child * 4, 4));

    private uint ValuesIndex(int child) => BinaryPrimitives.ReadUInt32LittleEndian(_valuesIndex.At(child * 4, 4));

    private int ByteOffset(int valueIndex)
        => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_byteOffsets.At((_valuesStart + valueIndex) * 4, 4)));

    private ReadOnlySpan<byte> Fixed(int valueIndex, int size) => _rowData.At(ByteOffset(valueIndex), size);

    private (int Count, int ChildrenIdx) Nested(int valueIndex)
    {
        ReadOnlySpan<byte> data = _rowData.Span;
        int pos = ByteOffset(valueIndex);
        int count = checked((int)ReadVarint(data, ref pos));
        int childrenIdx = count > 0 ? checked((int)ReadVarint(data, ref pos)) : 0;
        return (count, childrenIdx);
    }

    private ReadOnlySpan<byte> StringPayload(int valueIndex)
    {
        ReadOnlySpan<byte> data = _rowData.Span;
        int pos = ByteOffset(valueIndex);
        int length = checked((int)ReadVarint(data, ref pos));
        return data.Slice(pos, length);
    }

    // Width, scale, and where the unscaled value starts in the row's data.
    private (int Width, int Scale, int Start) DecimalPayload(int valueIndex)
    {
        ReadOnlySpan<byte> data = _rowData.Span;
        int pos = ByteOffset(valueIndex);
        int width = checked((int)ReadVarint(data, ref pos));
        int scale = checked((int)ReadVarint(data, ref pos));
        return (width, scale, pos);
    }

    private static uint ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        uint result = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }
        throw new InvalidOperationException("VARIANT varint is too large.");
    }

    // --------------------------------------------------------------- helpers

    // Bytes needed for value, at least one (duckdb's CalculateByteLength).
    private static int ByteLength(ulong value) => value == 0 ? 1 : (64 - BitOperations.LeadingZeroCount(value) + 7) / 8;

    private static void WriteLittleEndian(Span<byte> destination, uint value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            destination[i] = (byte)(value >> (8 * i));
        }
    }

    private static int Reserve<T>(ref T[] array, ref int used, int count)
    {
        int start = used;
        used = checked(used + count);
        if (used > array.Length)
        {
            Array.Resize(ref array, Math.Max(used, array.Length * 2));
        }
        return start;
    }

    // A vector buffer as its backing array, so the hot paths index an array
    // instead of going through ReadOnlyMemory.Span on every access.
    private readonly struct Buffer
    {
        private readonly byte[] _array;
        private readonly int _offset;
        private readonly int _length;

        public Buffer(ReadOnlyMemory<byte> memory)
        {
            if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment))
            {
                _array = segment.Array!;
                _offset = segment.Offset;
                _length = segment.Count;
            }
            else
            {
                _array = memory.ToArray();
                _offset = 0;
                _length = _array.Length;
            }
        }

        public ReadOnlySpan<byte> Span => new(_array, _offset, _length);

        public ReadOnlySpan<byte> At(int index, int size)
        {
            if ((uint)index > (uint)(_length - size))
            {
                throw new InvalidOperationException("VARIANT vector index is out of range.");
            }
            return new ReadOnlySpan<byte>(_array, _offset + index, size);
        }
    }

    // One Arrow binary column, written row by row into a growable buffer.
    private sealed class Output
    {
        private readonly byte[] _offsets;
        private byte[] _data;
        private int _length;

        public Output(int rowCount, long estimatedBytes)
        {
            _offsets = new byte[(rowCount + 1) * sizeof(int)];
            _data = new byte[(int)Math.Clamp(estimatedBytes, 256, Array.MaxLength)];
        }

        public Span<byte> Reserve(int size)
        {
            int end = checked(_length + size);
            if (end > _data.Length)
            {
                Array.Resize(ref _data, (int)Math.Min(Math.Max(end, 2L * _data.Length), Array.MaxLength));
            }
            Span<byte> span = _data.AsSpan(_length, size);
            _length = end;
            return span;
        }

        public void Append(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        public void EndRow(int row) => BinaryPrimitives.WriteInt32LittleEndian(_offsets.AsSpan((row + 1) * sizeof(int)), _length);

        public BinaryArray Build(int rowCount)
            => new(BinaryType.Default, rowCount, new ArrowBuffer(_offsets), new ArrowBuffer(_data.AsMemory(0, _length)), ArrowBuffer.Empty, 0, 0);
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Quack.Data;

namespace Quack.Adbc;

// Parquet Variant binary (one row's metadata + value) to a DuckDbVariant, as
// duckdb's VariantBinaryDecoder reads it and with duckdb's mapping of the
// Parquet variant types (ParquetVariantIterator): TIMESTAMP(_NANOS) with a
// time zone to TIMESTAMP_TZ(_NS), DECIMAL4/8 to the width of the value's
// digits (at least the scale), DECIMAL16 to width 38.
internal static class ParquetVariantReader
{
    public static DuckDbVariant Read(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        try
        {
            Dictionary dictionary = new(metadata);
            return ReadValue(ref dictionary, value);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Malformed Parquet VARIANT value.", e);
        }
    }

    private ref struct Dictionary
    {
        private readonly ReadOnlySpan<byte> _metadata;
        private readonly int _offsetSize;
        private readonly int _offsetsStart;
        private readonly int _stringsStart;
        private string?[]? _keys;

        public Dictionary(ReadOnlySpan<byte> metadata)
        {
            byte header = metadata[0];
            if ((header & 0x0F) != 1)
            {
                throw new NotSupportedException($"Parquet VARIANT metadata version {header & 0x0F} is not supported.");
            }
            _metadata = metadata;
            _offsetSize = ((header >> 6) & 0x03) + 1;
            Count = checked((int)ReadLittleEndian(metadata[1..], _offsetSize));
            _offsetsStart = 1 + _offsetSize;
            _stringsStart = checked(_offsetsStart + (Count + 1) * _offsetSize);
        }

        public int Count { get; }

        public string Key(uint id)
        {
            if (id >= (uint)Count)
            {
                throw new InvalidDataException($"Parquet VARIANT field id {id} is outside the dictionary ({Count} keys).");
            }
            _keys ??= new string?[Count];
            return _keys[id] ??= Decode((int)id);
        }

        private readonly string Decode(int id)
        {
            int start = checked((int)ReadLittleEndian(_metadata[(_offsetsStart + id * _offsetSize)..], _offsetSize));
            int end = checked((int)ReadLittleEndian(_metadata[(_offsetsStart + (id + 1) * _offsetSize)..], _offsetSize));
            return Encoding.UTF8.GetString(_metadata[(_stringsStart + start)..(_stringsStart + end)]);
        }
    }

    private static DuckDbVariant ReadValue(ref Dictionary dictionary, ReadOnlySpan<byte> value)
    {
        byte header = value[0];
        int valueHeader = header >> 2;
        switch (header & 0x03)
        {
            case 0:
                return Primitive(valueHeader, value[1..]);
            case 1:
                return new(DuckDbVariantType.Varchar, Encoding.UTF8.GetString(value.Slice(1, valueHeader)));
            case 2:
            {
                int offsetSize = (valueHeader & 0x03) + 1;
                int idSize = ((valueHeader >> 2) & 0x03) + 1;
                bool isLarge = ((valueHeader >> 4) & 0x01) != 0;
                int pos = 1;
                int count = ReadCount(value, ref pos, isLarge);
                int ids = pos;
                int offsets = checked(ids + count * idSize);
                int children = checked(offsets + (count + 1) * offsetSize);
                List<KeyValuePair<string, DuckDbVariant>> fields = new(count);
                for (int i = 0; i < count; i++)
                {
                    string key = dictionary.Key(ReadLittleEndian(value[(ids + i * idSize)..], idSize));
                    int offset = checked((int)ReadLittleEndian(value[(offsets + i * offsetSize)..], offsetSize));
                    fields.Add(new(key, ReadValue(ref dictionary, value[(children + offset)..])));
                }
                return new(DuckDbVariantType.Object, fields);
            }
            default:
            {
                int offsetSize = (valueHeader & 0x03) + 1;
                bool isLarge = ((valueHeader >> 2) & 0x01) != 0;
                int pos = 1;
                int count = ReadCount(value, ref pos, isLarge);
                int offsets = pos;
                int children = checked(offsets + (count + 1) * offsetSize);
                List<DuckDbVariant> elements = new(count);
                for (int i = 0; i < count; i++)
                {
                    int offset = checked((int)ReadLittleEndian(value[(offsets + i * offsetSize)..], offsetSize));
                    elements.Add(ReadValue(ref dictionary, value[(children + offset)..]));
                }
                return new(DuckDbVariantType.Array, elements);
            }
        }
    }

    private static DuckDbVariant Primitive(int primitiveType, ReadOnlySpan<byte> v) => primitiveType switch
    {
        0 => DuckDbVariant.VariantNull,
        1 => new(DuckDbVariantType.BoolTrue, true),
        2 => new(DuckDbVariantType.BoolFalse, false),
        3 => new(DuckDbVariantType.Int8, (sbyte)v[0]),
        4 => new(DuckDbVariantType.Int16, BinaryPrimitives.ReadInt16LittleEndian(v)),
        5 => new(DuckDbVariantType.Int32, BinaryPrimitives.ReadInt32LittleEndian(v)),
        6 => new(DuckDbVariantType.Int64, BinaryPrimitives.ReadInt64LittleEndian(v)),
        7 => new(DuckDbVariantType.Double, BinaryPrimitives.ReadDoubleLittleEndian(v)),
        8 => SmallDecimal(BinaryPrimitives.ReadInt32LittleEndian(v[1..]), v[0]),
        9 => SmallDecimal(BinaryPrimitives.ReadInt64LittleEndian(v[1..]), v[0]),
        10 => new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(
            new Int128(BinaryPrimitives.ReadUInt64LittleEndian(v[9..]), BinaryPrimitives.ReadUInt64LittleEndian(v[1..])), 38, v[0])),
        11 => new(DuckDbVariantType.Date, BinaryPrimitives.ReadInt32LittleEndian(v)),
        12 => new(DuckDbVariantType.TimestampMicrosTz, BinaryPrimitives.ReadInt64LittleEndian(v)),
        13 => new(DuckDbVariantType.TimestampMicros, BinaryPrimitives.ReadInt64LittleEndian(v)),
        14 => new(DuckDbVariantType.Float, BinaryPrimitives.ReadSingleLittleEndian(v)),
        15 => new(DuckDbVariantType.Blob, LengthPrefixed(v).ToArray()),
        16 => new(DuckDbVariantType.Varchar, Encoding.UTF8.GetString(LengthPrefixed(v))),
        17 => new(DuckDbVariantType.TimeMicros, BinaryPrimitives.ReadInt64LittleEndian(v)),
        18 => new(DuckDbVariantType.TimestampNanosTz, BinaryPrimitives.ReadInt64LittleEndian(v)),
        19 => new(DuckDbVariantType.TimestampNanos, BinaryPrimitives.ReadInt64LittleEndian(v)),
        20 => new(DuckDbVariantType.Uuid, new Guid(v[..16], bigEndian: true)),
        _ => throw new NotSupportedException($"Parquet VARIANT primitive type {primitiveType} is not supported."),
    };

    internal static DuckDbVariant SmallDecimal(long unscaled, byte scale)
        => new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(unscaled, (byte)Math.Max(Digits(unscaled), scale), scale));

    internal static int Digits(Int128 value)
    {
        UInt128 abs = value < 0 ? (UInt128)(-(value + 1)) + 1 : (UInt128)value;
        int digits = 1;
        while (abs >= 10)
        {
            abs /= 10;
            digits++;
        }
        return digits;
    }

    private static ReadOnlySpan<byte> LengthPrefixed(ReadOnlySpan<byte> v)
        => v.Slice(4, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(v)));

    private static int ReadCount(ReadOnlySpan<byte> value, ref int pos, bool isLarge)
    {
        if (!isLarge)
        {
            return value[pos++];
        }
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[pos..]));
        pos += 4;
        return count;
    }

    private static uint ReadLittleEndian(ReadOnlySpan<byte> bytes, int size)
    {
        uint result = 0;
        for (int i = 0; i < size; i++)
        {
            result |= (uint)bytes[i] << (8 * i);
        }
        return result;
    }
}

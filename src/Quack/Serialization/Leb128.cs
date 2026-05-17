// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Serialization;

// LEB128 codec compatible with DuckDB's EncodingUtil. Signed values use
// sign-extending LEB128 — NOT ZigZag. Unsigned values use standard LEB128.
// See: src/include/duckdb/common/serializer/encoding_util.hpp in duckdb/duckdb.
internal static class Leb128
{
    public const int MaxBytes = 10;

    public static int WriteUnsigned(Span<byte> destination, ulong value)
    {
        int offset = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }
            destination[offset++] = b;
        } while (value != 0);
        return offset;
    }

    public static int WriteSigned(Span<byte> destination, long value)
    {
        int offset = 0;
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bool done = (value == 0 && (b & 0x40) == 0) ||
                        (value == -1 && (b & 0x40) != 0);
            if (!done)
            {
                b |= 0x80;
            }
            destination[offset++] = b;
            if (done)
            {
                return offset;
            }
        }
    }

    public static ulong ReadUnsigned(ReadOnlySpan<byte> source, out int bytesRead)
    {
        ulong result = 0;
        int shift = 0;
        int offset = 0;
        byte b;
        do
        {
            if (offset >= source.Length)
            {
                throw new SerializationException("Truncated unsigned LEB128 value.");
            }
            if (shift >= 64)
            {
                throw new SerializationException("Unsigned LEB128 value exceeds 64 bits.");
            }
            b = source[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        bytesRead = offset;
        return result;
    }

    public static long ReadSigned(ReadOnlySpan<byte> source, out int bytesRead)
    {
        long result = 0;
        int shift = 0;
        int offset = 0;
        byte b;
        do
        {
            if (offset >= source.Length)
            {
                throw new SerializationException("Truncated signed LEB128 value.");
            }
            if (shift >= 64)
            {
                throw new SerializationException("Signed LEB128 value exceeds 64 bits.");
            }
            b = source[offset++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0)
        {
            result |= -(1L << shift);
        }
        bytesRead = offset;
        return result;
    }
}

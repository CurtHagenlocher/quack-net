// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Types;

namespace Quack.Data;

// Helpers for encoding and decoding DuckDB BIT (bitstring) values on the
// wire. Storage layout (duckdb/src/common/types/bit.cpp):
//   byte 0      : padding count in [0, 7], = (8 - len % 8) % 8
//   bytes 1..N  : `padding` 1-bits at the front of byte 1, then `len` value
//                 bits packed MSB-first across the remaining bits.
// The padding bits are explicitly set to 1 (a duckdb quirk — not zero).
public static class DuckDbBit
{
    // Encode a bit string like "1010" into the wire payload bytes that can
    // be placed directly into a VarBytesColumn whose Type is BIT.
    public static byte[] Encode(ReadOnlySpan<char> bits)
    {
        int len = bits.Length;
        int padding = (8 - (len % 8)) % 8;
        int storageBytes = (len + padding) / 8;
        byte[] payload = new byte[1 + storageBytes];
        payload[0] = (byte)padding;

        for (int bitIndex = 0; bitIndex < padding; bitIndex++)
        {
            SetBit(payload, bitIndex, true);
        }
        for (int i = 0; i < len; i++)
        {
            char c = bits[i];
            if (c != '0' && c != '1')
            {
                throw new ArgumentException(
                    $"BIT literal must contain only '0' and '1' characters; got '{c}' at index {i}.",
                    nameof(bits));
            }
            SetBit(payload, padding + i, c == '1');
        }
        return payload;
    }

    // Decode a wire payload back to the bit-string representation.
    public static string Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new ArgumentException("BIT payload must have at least one byte (the padding count).", nameof(payload));
        }
        int padding = payload[0];
        if (padding > 7)
        {
            throw new ArgumentException($"BIT padding byte {padding} is out of range [0, 7].", nameof(payload));
        }
        int totalBits = (payload.Length - 1) * 8;
        int valueBits = totalBits - padding;
        if (valueBits < 0)
        {
            throw new ArgumentException("BIT payload too short for declared padding.", nameof(payload));
        }
        char[] chars = new char[valueBits];
        for (int i = 0; i < valueBits; i++)
        {
            chars[i] = GetBit(payload, padding + i) ? '1' : '0';
        }
        return new string(chars);
    }

    private static void SetBit(byte[] payload, int bitIndex, bool value)
    {
        // bitIndex 0 = MSB of byte 1, bitIndex 7 = LSB of byte 1, etc.
        int byteIndex = 1 + (bitIndex >> 3);
        int bitInByte = 7 - (bitIndex & 7);
        if (value)
        {
            payload[byteIndex] |= (byte)(1 << bitInByte);
        }
        else
        {
            payload[byteIndex] &= (byte)~(1 << bitInByte);
        }
    }

    private static bool GetBit(ReadOnlySpan<byte> payload, int bitIndex)
    {
        int byteIndex = 1 + (bitIndex >> 3);
        int bitInByte = 7 - (bitIndex & 7);
        return (payload[byteIndex] & (1 << bitInByte)) != 0;
    }
}

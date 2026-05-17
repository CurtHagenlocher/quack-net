// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Quack.Data;

// DuckDB stores all 128-bit values (HUGEINT, UHUGEINT, UUID, DECIMAL with
// width > 18) as a hugeint_t struct: { uint64 lower; int64 upper } little
// endian — so bytes 0..7 are the LOWER 64 bits and bytes 8..15 are the
// UPPER 64 bits. The intuitive "upper first" ordering is wrong: see
// duckdb/common/hugeint.hpp for the canonical field order.
internal static class HugeintLayout
{
    public const int ByteSize = 16;

    public static Int128 ReadSigned(ReadOnlySpan<byte> bytes)
    {
        ulong lower = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
        long upper = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8));
        return ((Int128)upper << 64) | lower;
    }

    public static UInt128 ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong lower = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
        ulong upper = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
        return ((UInt128)upper << 64) | lower;
    }

    public static void WriteSigned(Span<byte> destination, Int128 value)
    {
        ulong lower = (ulong)value;
        long upper = (long)(value >> 64);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], lower);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), upper);
    }

    public static void WriteUnsigned(Span<byte> destination, UInt128 value)
    {
        ulong lower = (ulong)value;
        ulong upper = (ulong)(value >> 64);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], lower);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), upper);
    }
}

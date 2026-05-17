// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbHugeIntTests
{
    [Fact]
    public void RoundTrip_ZeroAndSmallValues()
    {
        Int128[] values = [Int128.Zero, (Int128)1, (Int128)(-1), (Int128)long.MaxValue, (Int128)long.MinValue];
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(values);

        Assert.Equal(values.Length, col.Count);
        Assert.Equal(16, col.ElementSize);
        Assert.Equal(LogicalTypeId.HugeInt, col.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbHugeInt.Get(col, i));
        }
    }

    [Fact]
    public void RoundTrip_MinAndMaxInt128()
    {
        Int128[] values = [Int128.MinValue, Int128.MaxValue];
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(values);

        Assert.Equal(Int128.MinValue, DuckDbHugeInt.Get(col, 0));
        Assert.Equal(Int128.MaxValue, DuckDbHugeInt.Get(col, 1));
    }

    [Fact]
    public void RoundTrip_LargeNegativeAndPositive()
    {
        // A value that requires the full 128 bits to represent.
        Int128 big = Int128.Parse("123456789012345678901234567890");
        Int128[] values = [big, -big];
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(values);

        Assert.Equal(big, DuckDbHugeInt.Get(col, 0));
        Assert.Equal(-big, DuckDbHugeInt.Get(col, 1));
    }

    [Fact]
    public void Storage_IsLittleEndianHugeintStruct()
    {
        // Int128 value with upper = 0x1122334455667788, lower = 0x99AABBCCDDEEFF00.
        // duckdb's hugeint_t lays out { lower (bytes 0..7); upper (bytes 8..15) }.
        Int128 value = ((Int128)0x1122334455667788UL << 64) | 0x99AABBCCDDEEFF00UL;
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(new[] { value });
        ReadOnlySpan<byte> bytes = col.GetBytes(0);

        Assert.Equal(0x99AABBCCDDEEFF00UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]));
        Assert.Equal(0x1122334455667788L, BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8)));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(
            new Int128[] { 1, 2, 3 },
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void Get_OnNonHugeIntColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbHugeInt.Get(intCol, 0));
    }

    [Fact]
    public void WireRoundTrip_HugeIntColumn()
    {
        Int128[] values = [Int128.Zero, Int128.MaxValue, Int128.MinValue, (Int128)42, (Int128)(-42)];
        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };

        ArrayBufferWriter<byte> buffer = new();
        DataChunkWriter.Write(new BinarySerializer(buffer), chunk);
        DuckDbChunk round = DataChunkReader.Read(new BinaryDeserializer(buffer.WrittenMemory));

        FixedSizeColumn back = Assert.IsType<FixedSizeColumn>(round.Columns[0]);
        Assert.Equal(LogicalTypeId.HugeInt, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbHugeInt.Get(back, i));
        }
    }
}

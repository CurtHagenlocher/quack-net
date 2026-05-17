// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbUHugeIntTests
{
    [Fact]
    public void RoundTrip_ZeroAndSmallValues()
    {
        UInt128[] values = [UInt128.Zero, (UInt128)1, (UInt128)ulong.MaxValue, ((UInt128)1 << 64), ((UInt128)1 << 127)];
        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(values);

        Assert.Equal(values.Length, col.Count);
        Assert.Equal(16, col.ElementSize);
        Assert.Equal(LogicalTypeId.UHugeInt, col.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbUHugeInt.Get(col, i));
        }
    }

    [Fact]
    public void RoundTrip_MaxUInt128()
    {
        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(new[] { UInt128.MaxValue });
        Assert.Equal(UInt128.MaxValue, DuckDbUHugeInt.Get(col, 0));
    }

    [Fact]
    public void Storage_IsLittleEndianHugeintStruct()
    {
        // duckdb's hugeint_t lays out { lower (bytes 0..7); upper (bytes 8..15) }.
        UInt128 value = ((UInt128)0xFFEEDDCCBBAA9988UL << 64) | 0x7766554433221100UL;
        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(new[] { value });
        ReadOnlySpan<byte> bytes = col.GetBytes(0);

        Assert.Equal(0x7766554433221100UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]));
        Assert.Equal(0xFFEEDDCCBBAA9988UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(
            new UInt128[] { 1, 2, 3 },
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void Get_OnNonUHugeIntColumn_Throws()
    {
        FixedSizeColumn col = new()
        {
            Type = new LogicalType(LogicalTypeId.HugeInt),
            Count = 1,
            ElementSize = 16,
            Data = new byte[16],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbUHugeInt.Get(col, 0));
    }

    [Fact]
    public void WireRoundTrip_UHugeIntColumn()
    {
        UInt128[] values = [UInt128.Zero, UInt128.MaxValue, (UInt128)42, ((UInt128)1 << 100)];
        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.UHugeInt, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbUHugeInt.Get(back, i));
        }
    }
}

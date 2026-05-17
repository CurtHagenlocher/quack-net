// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbTimeTzTests
{
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(45_296_000_000L, 7200)]      // 12:34:56 +02:00
    [InlineData(45_296_000_000L, -25200)]    // 12:34:56 -07:00
    [InlineData(86_399_999_999L, 57599)]     // 23:59:59.999999 + max offset (15:59:59)
    [InlineData(86_399_999_999L, -57600)]    // min offset (-16:00:00)
    public void RoundTrip_PreservesMicrosAndOffset(long micros, int offsetSeconds)
    {
        DuckDbTimeTz value = new(micros, offsetSeconds);
        FixedSizeColumn col = DuckDbTimeTz.CreateColumn(new[] { value });

        DuckDbTimeTz back = DuckDbTimeTz.Get(col, 0);
        Assert.Equal(micros, back.Microseconds);
        Assert.Equal(offsetSeconds, back.OffsetSeconds);
    }

    [Fact]
    public void Storage_PacksMicrosInHighBitsAndOffsetInLow()
    {
        // 12:34:56 +02:00: micros = 45,296,000,000; offset = +7200s.
        // encoded_offset = MAX_OFFSET - offset = 57599 - 7200 = 50399.
        // bits = (micros << 24) | 50399.
        DuckDbTimeTz value = new(45_296_000_000L, 7200);
        FixedSizeColumn col = DuckDbTimeTz.CreateColumn(new[] { value });
        long bits = BinaryPrimitives.ReadInt64LittleEndian(col.GetBytes(0));

        long encodedOffset = bits & ((1L << 24) - 1);
        long encodedMicros = bits >> 24;

        Assert.Equal(50399L, encodedOffset);
        Assert.Equal(45_296_000_000L, encodedMicros);
    }

    [Fact]
    public void OffsetOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbTimeTz.CreateColumn(new[] { new DuckDbTimeTz(0, 100_000) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbTimeTz.CreateColumn(new[] { new DuckDbTimeTz(0, -100_000) }));
    }

    [Fact]
    public void NegativeMicros_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbTimeTz.CreateColumn(new[] { new DuckDbTimeTz(-1L, 0) }));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbTimeTz.CreateColumn(
            new DuckDbTimeTz[] { new(1, 0), new(2, 0), new(3, 0) },
            new ValidityMask(mask));
        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void Get_OnNonTimeTzColumn_Throws()
    {
        FixedSizeColumn col = new()
        {
            Type = new LogicalType(LogicalTypeId.Time),
            Count = 1,
            ElementSize = 8,
            Data = new byte[8],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbTimeTz.Get(col, 0));
    }

    [Fact]
    public void WireRoundTrip_TimeTzColumn()
    {
        DuckDbTimeTz[] values =
        [
            new(0L, 0),
            new(45_296_000_000L, 7200),
            new(86_399_000_000L, -25200),
        ];
        FixedSizeColumn col = DuckDbTimeTz.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.TimeTz, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbTimeTz.Get(back, i));
        }
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbIntervalTests
{
    [Theory]
    [InlineData(0, 0, 0L)]
    [InlineData(1, 0, 0L)]
    [InlineData(0, 1, 0L)]
    [InlineData(0, 0, 1L)]
    [InlineData(12, 30, 86_400_000_000L)]
    [InlineData(-1, -1, -1L)]
    [InlineData(int.MaxValue, int.MaxValue, long.MaxValue)]
    [InlineData(int.MinValue, int.MinValue, long.MinValue)]
    public void RoundTrip_IntervalThroughCreateAndGet(int months, int days, long micros)
    {
        DuckDbInterval value = new(months, days, micros);
        FixedSizeColumn col = DuckDbInterval.CreateColumn(new[] { value });

        Assert.Equal(value, DuckDbInterval.Get(col, 0));
    }

    [Fact]
    public void RoundTrip_ManyValuesInOneColumn()
    {
        DuckDbInterval[] values =
        [
            new(1, 2, 3),
            new(-1, -2, -3),
            new(0, 0, 0),
            new(12, 15, 86_400_000_000L),
        ];
        FixedSizeColumn col = DuckDbInterval.CreateColumn(values);

        Assert.Equal(values.Length, col.Count);
        Assert.Equal(16, col.ElementSize);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbInterval.Get(col, i));
        }
    }

    [Fact]
    public void Components_AreIndependent()
    {
        // 13 months is NOT normalized to "1 year 1 month" — interval keeps the
        // three components separate (calendar-dependent semantics).
        DuckDbInterval value = new(13, 32, 25 * 3_600_000_000L);
        FixedSizeColumn col = DuckDbInterval.CreateColumn(new[] { value });
        DuckDbInterval back = DuckDbInterval.Get(col, 0);

        Assert.Equal(13, back.Months);
        Assert.Equal(32, back.Days);
        Assert.Equal(25 * 3_600_000_000L, back.Microseconds);
    }

    [Fact]
    public void Storage_IsLittleEndianStructLayout()
    {
        DuckDbInterval value = new(Months: 1, Days: 2, Microseconds: 3);
        FixedSizeColumn col = DuckDbInterval.CreateColumn(new[] { value });
        ReadOnlySpan<byte> bytes = col.GetBytes(0);

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4)));
        Assert.Equal(3L, BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8)));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbInterval.CreateColumn(
            new DuckDbInterval[] { new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) },
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void Get_OnNonIntervalColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbInterval.Get(intCol, 0));
    }

    [Fact]
    public void WireRoundTrip_IntervalColumnThroughDataChunkSerialization()
    {
        DuckDbInterval[] values =
        [
            new(0, 0, 0),
            new(13, 5, 3_600_000_000L),
            new(-1, -2, -3L),
        ];
        FixedSizeColumn col = DuckDbInterval.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.Interval, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbInterval.Get(back, i));
        }
    }
}

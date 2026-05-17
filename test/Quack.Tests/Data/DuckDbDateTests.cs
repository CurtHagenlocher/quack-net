// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbDateTests
{
    [Theory]
    [InlineData(1970, 1, 1)]
    [InlineData(2026, 5, 13)]
    [InlineData(1, 1, 1)]
    [InlineData(9999, 12, 31)]
    [InlineData(1969, 12, 31)]
    public void RoundTrip_DateOnlyThroughCreateAndGet(int year, int month, int day)
    {
        DateOnly value = new(year, month, day);
        FixedSizeColumn col = DuckDbDate.CreateColumn(new[] { value });
        Assert.Equal(value, DuckDbDate.GetDate(col, 0));
    }

    [Fact]
    public void RoundTrip_ManyValuesInOneColumn()
    {
        DateOnly[] values = [new(1970, 1, 1), new(2026, 5, 13), new(1999, 12, 31)];
        FixedSizeColumn col = DuckDbDate.CreateColumn(values);

        Assert.Equal(values.Length, col.Count);
        Assert.Equal(sizeof(int), col.ElementSize);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbDate.GetDate(col, i));
        }
    }

    [Fact]
    public void GetDays_ReturnsRawDayCountSinceUnixEpoch()
    {
        FixedSizeColumn col = DuckDbDate.CreateColumn(new[] { new DateOnly(1970, 1, 2) });
        Assert.Equal(1, DuckDbDate.GetDays(col, 0));
    }

    [Fact]
    public void CreateColumn_FromRawDays_RoundTripsThroughGetDays()
    {
        int[] days = [0, 1, -1, 20000, -20000];
        FixedSizeColumn col = DuckDbDate.CreateColumn(days);

        for (int i = 0; i < days.Length; i++)
        {
            Assert.Equal(days[i], DuckDbDate.GetDays(col, i));
        }
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-int.MaxValue)]
    public void InfinitySentinel_GetDays_AccessibleButGetDateThrows(int sentinel)
    {
        FixedSizeColumn col = DuckDbDate.CreateColumn(new[] { sentinel });
        Assert.Equal(sentinel, DuckDbDate.GetDays(col, 0));
        Assert.Throws<OverflowException>(() => DuckDbDate.GetDate(col, 0));
    }

    [Fact]
    public void RawDays_OutsideDateOnlyRange_ThrowsFromGetDate()
    {
        // Day count well past 9999-12-31; not the infinity sentinel.
        int days = 4_000_000;
        FixedSizeColumn col = DuckDbDate.CreateColumn(new[] { days });
        Assert.Equal(days, DuckDbDate.GetDays(col, 0));
        Assert.Throws<OverflowException>(() => DuckDbDate.GetDate(col, 0));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbDate.CreateColumn(
            new[] { new DateOnly(2020, 1, 1), new(2020, 1, 2), new(2020, 1, 3) },
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void GetDate_OnNonDateColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbDate.GetDate(intCol, 0));
        Assert.Throws<InvalidOperationException>(() => DuckDbDate.GetDays(intCol, 0));
    }

    [Fact]
    public void Storage_IsLittleEndianInt32DaysSinceEpoch()
    {
        // 1970-01-02 -> 1 day -> bytes 01 00 00 00.
        FixedSizeColumn col = DuckDbDate.CreateColumn(new[] { new DateOnly(1970, 1, 2) });
        ReadOnlySpan<byte> bytes = col.GetBytes(0);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }

    [Fact]
    public void WireRoundTrip_DateColumnThroughDataChunkSerialization()
    {
        DateOnly[] values = [new(1970, 1, 1), new(2026, 5, 13), new(1999, 12, 31)];
        FixedSizeColumn col = DuckDbDate.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.Date, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbDate.GetDate(back, i));
        }
    }
}

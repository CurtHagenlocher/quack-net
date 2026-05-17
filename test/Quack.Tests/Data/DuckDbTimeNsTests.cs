// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbTimeNsTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 23, 45)]
    [InlineData(12, 0, 0)]
    [InlineData(23, 59, 59)]
    public void RoundTrip_TimeOnlyThroughCreateAndGet(int h, int m, int s)
    {
        TimeOnly value = new(h, m, s);
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(new[] { value });
        Assert.Equal(value, DuckDbTimeNs.GetTime(col, 0));
    }

    [Fact]
    public void RoundTrip_ManyValues()
    {
        TimeOnly[] values =
        [
            new(0, 0, 0),
            new(6, 30, 0),
            new(12, 0, 0),
            new(18, 45, 30),
            new(23, 59, 59),
        ];
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(values);
        Assert.Equal(values.Length, col.Count);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbTimeNs.GetTime(col, i));
        }
    }

    [Fact]
    public void GetNanoseconds_ReturnsRawNanos()
    {
        // 01:00:00 = 3,600,000,000,000 nanoseconds.
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(new[] { new TimeOnly(1, 0, 0) });
        Assert.Equal(3_600_000_000_000L, DuckDbTimeNs.GetNanoseconds(col, 0));
    }

    [Fact]
    public void CreateColumn_FromRawNanos_RoundTrips()
    {
        long[] nanos = [0L, 1_000_000_000L, 3_600_000_000_000L, 86_399_999_999_999L];
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(nanos);
        for (int i = 0; i < nanos.Length; i++)
        {
            Assert.Equal(nanos[i], DuckDbTimeNs.GetNanoseconds(col, i));
        }
    }

    [Fact]
    public void MidnightUpperBound_24h_AccessibleAsNanosButGetTimeThrows()
    {
        const long maxNanos = 86_400_000_000_000L;
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(new[] { maxNanos });
        Assert.Equal(maxNanos, DuckDbTimeNs.GetNanoseconds(col, 0));
        Assert.Throws<OverflowException>(() => DuckDbTimeNs.GetTime(col, 0));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(86_400_000_000_001L)]
    public void OutOfRange_GetTimeThrows(long nanos)
    {
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(new[] { nanos });
        Assert.Throws<OverflowException>(() => DuckDbTimeNs.GetTime(col, 0));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(
            new[] { new TimeOnly(1, 0, 0), new(2, 0, 0), new(3, 0, 0) },
            new ValidityMask(mask));
        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void Get_OnNonTimeNsColumn_Throws()
    {
        FixedSizeColumn col = new()
        {
            Type = new LogicalType(LogicalTypeId.Time),
            Count = 1,
            ElementSize = 8,
            Data = new byte[8],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbTimeNs.GetTime(col, 0));
        Assert.Throws<InvalidOperationException>(() => DuckDbTimeNs.GetNanoseconds(col, 0));
    }

    [Fact]
    public void Storage_IsLittleEndianInt64Nanoseconds()
    {
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(new[] { new TimeOnly(0, 0, 1) });
        Assert.Equal(1_000_000_000L, BinaryPrimitives.ReadInt64LittleEndian(col.GetBytes(0)));
    }

    [Fact]
    public void WireRoundTrip_TimeNsColumn()
    {
        TimeOnly[] values = [new(0, 0, 0), new(12, 34, 56), new(23, 59, 59)];
        FixedSizeColumn col = DuckDbTimeNs.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.TimeNs, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbTimeNs.GetTime(back, i));
        }
    }
}

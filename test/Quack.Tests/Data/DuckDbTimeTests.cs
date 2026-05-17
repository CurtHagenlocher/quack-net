// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbTimeTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(12, 34, 56, 789_000)]
    [InlineData(23, 59, 59, 999_999)]
    public void RoundTrip_TimeOnlyThroughCreateAndGet(int h, int m, int s, int us)
    {
        TimeOnly value = new(h, m, s, us / 1_000, us % 1_000);
        FixedSizeColumn col = DuckDbTime.CreateColumn(new[] { value });
        Assert.Equal(value, DuckDbTime.GetTime(col, 0));
    }

    [Fact]
    public void RoundTrip_ManyValuesInOneColumn()
    {
        TimeOnly[] values =
        [
            new(0, 0, 0),
            new(6, 30, 0),
            new(12, 0, 0),
            new(18, 45, 30),
            new(23, 59, 59),
        ];
        FixedSizeColumn col = DuckDbTime.CreateColumn(values);

        Assert.Equal(values.Length, col.Count);
        Assert.Equal(sizeof(long), col.ElementSize);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbTime.GetTime(col, i));
        }
    }

    [Fact]
    public void GetMicroseconds_ReturnsRawMicros()
    {
        // 01:00:00.000000 -> 3,600,000,000 micros.
        FixedSizeColumn col = DuckDbTime.CreateColumn(new[] { new TimeOnly(1, 0, 0) });
        Assert.Equal(3_600_000_000L, DuckDbTime.GetMicroseconds(col, 0));
    }

    [Fact]
    public void CreateColumn_FromRawMicros_RoundTrips()
    {
        long[] micros = [0L, 1_000_000L, 3_600_000_000L, 86_399_999_999L];
        FixedSizeColumn col = DuckDbTime.CreateColumn(micros);

        for (int i = 0; i < micros.Length; i++)
        {
            Assert.Equal(micros[i], DuckDbTime.GetMicroseconds(col, i));
        }
    }

    [Fact]
    public void MidnightUpperBound_24h_AccessibleAsMicrosButGetTimeThrows()
    {
        // 86_400_000_000 micros == 24:00:00.000000 — legal in DuckDB, exceeds TimeOnly.MaxValue.
        const long maxMicros = 86_400_000_000L;
        FixedSizeColumn col = DuckDbTime.CreateColumn(new[] { maxMicros });

        Assert.Equal(maxMicros, DuckDbTime.GetMicroseconds(col, 0));
        Assert.Throws<OverflowException>(() => DuckDbTime.GetTime(col, 0));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(86_400_000_001L)]
    public void OutOfRangeMicros_GetTimeThrows(long micros)
    {
        FixedSizeColumn col = DuckDbTime.CreateColumn(new[] { micros });
        Assert.Throws<OverflowException>(() => DuckDbTime.GetTime(col, 0));
    }

    [Fact]
    public void Validity_PreservedThroughCreateColumn()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        FixedSizeColumn col = DuckDbTime.CreateColumn(
            new[] { new TimeOnly(1, 0, 0), new(2, 0, 0), new(3, 0, 0) },
            new ValidityMask(mask));

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
    }

    [Fact]
    public void GetTime_OnNonTimeColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbTime.GetTime(intCol, 0));
        Assert.Throws<InvalidOperationException>(() => DuckDbTime.GetMicroseconds(intCol, 0));
    }

    [Fact]
    public void Storage_IsLittleEndianInt64Microseconds()
    {
        FixedSizeColumn col = DuckDbTime.CreateColumn(new[] { new TimeOnly(0, 0, 1) });
        Assert.Equal(1_000_000L, BinaryPrimitives.ReadInt64LittleEndian(col.GetBytes(0)));
    }

    [Fact]
    public void WireRoundTrip_TimeColumnThroughDataChunkSerialization()
    {
        TimeOnly[] values = [new(0, 0, 0), new(12, 34, 56), new(23, 59, 59)];
        FixedSizeColumn col = DuckDbTime.CreateColumn(values);
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
        Assert.Equal(LogicalTypeId.Time, back.Type.Id);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbTime.GetTime(back, i));
        }
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DuckDbDecimalTests
{
    [Theory]
    [InlineData(4, 2, 2)]
    [InlineData(9, 2, 4)]
    [InlineData(18, 4, 8)]
    [InlineData(38, 10, 16)]
    public void ElementSizeForWidth_MatchesPhysicalType(byte width, byte scale, int expectedSize)
    {
        Assert.Equal(expectedSize, DuckDbDecimal.ElementSizeForWidth(width));
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { 0m }, width, scale);
        Assert.Equal(expectedSize, col.ElementSize);
    }

    [Theory]
    [InlineData("0", 4, 2)]
    [InlineData("12.34", 4, 2)]
    [InlineData("-12.34", 4, 2)]
    [InlineData("99.99", 4, 2)]
    [InlineData("-99.99", 4, 2)]
    [InlineData("1234567.89", 9, 2)]
    [InlineData("-1234567.89", 9, 2)]
    [InlineData("123.4567", 18, 4)]
    [InlineData("12345678901234.5678", 18, 4)]
    [InlineData("-12345678901234.5678", 18, 4)]
    [InlineData("9999999999999.9999", 28, 4)]
    public void RoundTrip_DecimalThroughCreateAndGet(string text, byte width, byte scale)
    {
        decimal value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { value }, width, scale);
        Assert.Equal(value, DuckDbDecimal.GetDecimal(col, 0));
    }

    [Fact]
    public void RoundTrip_ManyValuesInOneColumn()
    {
        decimal[] values = [0m, 1.25m, -1.25m, 999999.99m, -999999.99m];
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(values, width: 18, scale: 4);

        Assert.Equal(values.Length, col.Count);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbDecimal.GetDecimal(col, i));
        }
    }

    [Fact]
    public void ScaleUp_PadsWithZeroFractionDigits()
    {
        // 1.5 written as DECIMAL(18, 4) should store mantissa 15000.
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { 1.5m }, width: 18, scale: 4);
        Assert.Equal((Int128)15000, DuckDbDecimal.GetMantissa(col, 0));
        Assert.Equal(1.5000m, DuckDbDecimal.GetDecimal(col, 0));
    }

    [Fact]
    public void ScaleDown_TruncatesTowardZero()
    {
        // 1.9999 written as DECIMAL(18, 2) should truncate to mantissa 199.
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { 1.9999m }, width: 18, scale: 2);
        Assert.Equal((Int128)199, DuckDbDecimal.GetMantissa(col, 0));
        Assert.Equal(1.99m, DuckDbDecimal.GetDecimal(col, 0));
    }

    [Fact]
    public void Width_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbDecimal.CreateColumn(new[] { 0m }, width: 0, scale: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbDecimal.CreateColumn(new[] { 0m }, width: 39, scale: 0));
    }

    [Fact]
    public void Scale_LargerThanWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DuckDbDecimal.CreateColumn(new[] { 0m }, width: 4, scale: 5));
    }

    [Fact]
    public void Mantissa_OverflowsPhysicalType_Throws()
    {
        // DECIMAL(4, 0) backs INT16 (max 32767). 999999 exceeds that.
        Assert.Throws<OverflowException>(() =>
            DuckDbDecimal.CreateColumn(new[] { 999999m }, width: 4, scale: 0));
    }

    [Fact]
    public void HighPrecision_ViaInt128Mantissa()
    {
        // DECIMAL(38, 10) value 1234567890123456789012345.6789012345 — too big
        // for System.Decimal but representable as an Int128 mantissa.
        Int128 mantissa = Int128.Parse("12345678901234567890123456789012345");
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { mantissa }, width: 38, scale: 10);

        Assert.Equal(mantissa, DuckDbDecimal.GetMantissa(col, 0));
        // GetDecimal should overflow because the value doesn't fit in 96 bits.
        Assert.Throws<OverflowException>(() => DuckDbDecimal.GetDecimal(col, 0));
    }

    [Fact]
    public void HugeintLayout_NegativeValueRoundTrips()
    {
        Int128 mantissa = Int128.Parse("-12345678901234567890123456789012345");
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(new[] { mantissa }, width: 38, scale: 0);
        Assert.Equal(mantissa, DuckDbDecimal.GetMantissa(col, 0));
    }

    [Fact]
    public void GetDecimal_OnNonDecimalColumn_Throws()
    {
        FixedSizeColumn intCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 1,
            ElementSize = 4,
            Data = new byte[4],
        };
        Assert.Throws<InvalidOperationException>(() => DuckDbDecimal.GetDecimal(intCol, 0));
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(9, 2)]
    [InlineData(18, 4)]
    [InlineData(38, 10)]
    public void WireRoundTrip_DecimalColumnThroughDataChunkSerialization(byte width, byte scale)
    {
        decimal[] values = [0m, 1.25m, -1.25m];
        FixedSizeColumn col = DuckDbDecimal.CreateColumn(values, width, scale);
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
        Assert.Equal(LogicalTypeId.Decimal, back.Type.Id);
        DecimalTypeInfo info = Assert.IsType<DecimalTypeInfo>(back.Type.TypeInfo);
        Assert.Equal(width, info.Width);
        Assert.Equal(scale, info.Scale);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], DuckDbDecimal.GetDecimal(back, i));
        }
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DataChunkRoundTripTests
{
    [Fact]
    public void RoundTrip_IntegerColumn_NoValidity()
    {
        DuckDbChunk original = ChunkBuilder.OneColumn(
            new LogicalType(LogicalTypeId.Integer),
            new FixedSizeColumn
            {
                Type = new LogicalType(LogicalTypeId.Integer),
                Count = 3,
                ElementSize = 4,
                Data = ChunkBuilder.Int32Bytes(11, 22, 33),
            });

        DuckDbChunk round = RoundTrip(original);

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(round.Columns[0]);
        Assert.Equal(3, round.RowCount);
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
        Assert.Equal(22, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(1)));
        Assert.Equal(33, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
    }

    [Fact]
    public void RoundTrip_IntegerColumn_WithNulls()
    {
        // Row 1 is null.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101; // rows 0 and 2 valid; row 1 null
        DuckDbChunk original = ChunkBuilder.OneColumn(
            new LogicalType(LogicalTypeId.Integer),
            new FixedSizeColumn
            {
                Type = new LogicalType(LogicalTypeId.Integer),
                Count = 3,
                ElementSize = 4,
                Data = ChunkBuilder.Int32Bytes(7, 0, 9),
                Validity = new ValidityMask(mask),
            });

        DuckDbChunk round = RoundTrip(original);

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(round.Columns[0]);
        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
    }

    [Fact]
    public void RoundTrip_VarcharColumn_NullsRepresentedViaValidity()
    {
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;
        DuckDbChunk original = ChunkBuilder.OneColumn(
            new LogicalType(LogicalTypeId.Varchar),
            new VarBytesColumn
            {
                Type = new LogicalType(LogicalTypeId.Varchar),
                Count = 3,
                Values = [(ReadOnlyMemory<byte>)"alpha"u8.ToArray(), null, (ReadOnlyMemory<byte>)"gamma"u8.ToArray()],
                Validity = new ValidityMask(mask),
            });

        DuckDbChunk round = RoundTrip(original);

        VarBytesColumn col = Assert.IsType<VarBytesColumn>(round.Columns[0]);
        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
        Assert.Equal("alpha"u8.ToArray(), col.Values[0]!.Value.ToArray());
        Assert.Equal("gamma"u8.ToArray(), col.Values[2]!.Value.ToArray());
    }

    [Fact]
    public void RoundTrip_ListOfInteger()
    {
        LogicalType listType = new(LogicalTypeId.List,
            new ListTypeInfo { ChildType = new LogicalType(LogicalTypeId.Integer) });
        DuckDbChunk original = ChunkBuilder.OneColumn(
            listType,
            new ListColumn
            {
                Type = listType,
                Count = 2,
                // Row 0 -> indices 0..2 (values 10,20); Row 1 -> index 2 (value 30).
                Entries = [(0UL, 2UL), (2UL, 1UL)],
                Child = new FixedSizeColumn
                {
                    Type = new LogicalType(LogicalTypeId.Integer),
                    Count = 3,
                    ElementSize = 4,
                    Data = ChunkBuilder.Int32Bytes(10, 20, 30),
                },
            });

        DuckDbChunk round = RoundTrip(original);

        ListColumn col = Assert.IsType<ListColumn>(round.Columns[0]);
        Assert.Equal((0UL, 2UL), col.Entries[0]);
        Assert.Equal((2UL, 1UL), col.Entries[1]);
        FixedSizeColumn child = Assert.IsType<FixedSizeColumn>(col.Child);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(0)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(1)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(2)));
    }

    [Fact]
    public void RoundTrip_StructOfIntegerAndVarchar()
    {
        LogicalType structType = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes =
            [
                new("a", new LogicalType(LogicalTypeId.Integer)),
                new("b", new LogicalType(LogicalTypeId.Varchar)),
            ],
        });
        DuckDbChunk original = ChunkBuilder.OneColumn(
            structType,
            new StructColumn
            {
                Type = structType,
                Count = 2,
                Fields =
                [
                    new FixedSizeColumn
                    {
                        Type = new LogicalType(LogicalTypeId.Integer),
                        Count = 2,
                        ElementSize = 4,
                        Data = ChunkBuilder.Int32Bytes(7, 8),
                    },
                    new VarBytesColumn
                    {
                        Type = new LogicalType(LogicalTypeId.Varchar),
                        Count = 2,
                        Values = [(ReadOnlyMemory<byte>)"x"u8.ToArray(), (ReadOnlyMemory<byte>)"y"u8.ToArray()],
                    },
                ],
            });

        DuckDbChunk round = RoundTrip(original);

        Assert.Equal(LogicalTypeId.Struct, round.Types[0].Id);
        StructTypeInfo info = Assert.IsType<StructTypeInfo>(round.Types[0].TypeInfo);
        Assert.Equal("a", info.ChildTypes[0].Key);
        Assert.Equal("b", info.ChildTypes[1].Key);

        StructColumn col = Assert.IsType<StructColumn>(round.Columns[0]);
        FixedSizeColumn ints = Assert.IsType<FixedSizeColumn>(col.Fields[0]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(ints.GetBytes(0)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(ints.GetBytes(1)));
        VarBytesColumn strs = Assert.IsType<VarBytesColumn>(col.Fields[1]);
        Assert.Equal("x"u8.ToArray(), strs.Values[0]!.Value.ToArray());
        Assert.Equal("y"u8.ToArray(), strs.Values[1]!.Value.ToArray());
    }

    [Fact]
    public void RoundTrip_MultipleColumns_PreservesOrder()
    {
        LogicalType intType = new(LogicalTypeId.Integer);
        LogicalType varcharType = new(LogicalTypeId.Varchar);
        DuckDbChunk original = new()
        {
            Types = [intType, varcharType],
            Columns =
            [
                new FixedSizeColumn { Type = intType, Count = 2, ElementSize = 4, Data = ChunkBuilder.Int32Bytes(100, 200) },
                new VarBytesColumn { Type = varcharType, Count = 2, Values = [(ReadOnlyMemory<byte>)"foo"u8.ToArray(), (ReadOnlyMemory<byte>)"bar"u8.ToArray()] },
            ],
            RowCount = 2,
        };

        DuckDbChunk round = RoundTrip(original);

        Assert.Equal(2, round.Columns.Count);
        Assert.Equal(LogicalTypeId.Integer, round.Columns[0].Type.Id);
        Assert.Equal(LogicalTypeId.Varchar, round.Columns[1].Type.Id);
    }

    [Fact]
    public void RoundTrip_DecimalTypeInfo_PreservesWidthAndScale()
    {
        LogicalType decimalType = new(LogicalTypeId.Decimal,
            new DecimalTypeInfo { Width = 18, Scale = 4 });
        DuckDbChunk original = ChunkBuilder.OneColumn(
            decimalType,
            new FixedSizeColumn
            {
                Type = decimalType,
                Count = 1,
                ElementSize = 8, // BIGINT-backed DECIMAL(18,4)
                Data = new byte[8],
            });

        DuckDbChunk round = RoundTrip(original);

        Assert.Equal(LogicalTypeId.Decimal, round.Types[0].Id);
        DecimalTypeInfo info = Assert.IsType<DecimalTypeInfo>(round.Types[0].TypeInfo);
        Assert.Equal((byte)18, info.Width);
        Assert.Equal((byte)4, info.Scale);
    }

    private static DuckDbChunk RoundTrip(DuckDbChunk chunk)
    {
        ArrayBufferWriter<byte> buffer = new();
        BinarySerializer s = new(buffer);
        DataChunkWriter.Write(s, chunk);
        return DataChunkReader.Read(new BinaryDeserializer(buffer.WrittenMemory));
    }
}

internal static class ChunkBuilder
{
    public static DuckDbChunk OneColumn(LogicalType type, DuckDbColumn column)
    {
        return new DuckDbChunk
        {
            Types = [type],
            Columns = [column],
            RowCount = column.Count,
        };
    }

    public static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }
}

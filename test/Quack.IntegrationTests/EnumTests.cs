// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class EnumTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public EnumTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_SmallEnum_Uint8Backed()
    {
        // 3-value dictionary -> physical UInt8 (size <= 255 -> 1 byte per row).
        string[] dictionary = ["happy", "sad", "meh"];
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_enum_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, v ENUM('happy', 'sad', 'meh'))"));

        LogicalType enumType = new(LogicalTypeId.Enum, new EnumTypeInfo
        {
            Values = dictionary,
            PhysicalType = PhysicalType.UInt8,
        });

        // 4 rows: indices 0, 2, 1, 0 -> "happy", "meh", "sad", "happy"
        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 4,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2, 3),
        };
        FixedSizeColumn enumCol = new()
        {
            Type = enumType,
            Count = 4,
            ElementSize = 1,
            Data = new byte[] { 0, 2, 1, 0 },
        };

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, enumType],
            Columns = [idCol, enumCol],
            RowCount = 4,
        };
        await conn.AppendAsync(table, chunk);

        // Cast to VARCHAR so the projection has a layout independent of the
        // ENUM wire representation, which is what we're really testing on append.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, CAST(v AS VARCHAR) FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        Assert.Equal(4, result.RowCount);
        VarBytesColumn names = Assert.IsType<VarBytesColumn>(result.Columns[1]);
        Assert.Equal("happy"u8.ToArray(), names.Values[0]!.Value.ToArray());
        Assert.Equal("meh"u8.ToArray(), names.Values[1]!.Value.ToArray());
        Assert.Equal("sad"u8.ToArray(), names.Values[2]!.Value.ToArray());
        Assert.Equal("happy"u8.ToArray(), names.Values[3]!.Value.ToArray());
    }

    [Fact]
    public async Task RoundTrip_EnumColumn_PreservesDictionaryOnRead()
    {
        // Round-trip the ENUM column itself (not the string projection) so we
        // confirm the dictionary survives the wire and the index is correct.
        string[] dictionary = ["red", "green", "blue"];
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_enum_dict_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (v ENUM('red', 'green', 'blue'))"));

        LogicalType enumType = new(LogicalTypeId.Enum, new EnumTypeInfo
        {
            Values = dictionary,
            PhysicalType = PhysicalType.UInt8,
        });

        FixedSizeColumn enumCol = new()
        {
            Type = enumType,
            Count = 3,
            ElementSize = 1,
            Data = new byte[] { 0, 1, 2 },
        };
        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [enumType],
            Columns = [enumCol],
            RowCount = 3,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v::VARCHAR"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        FixedSizeColumn outCol = Assert.IsType<FixedSizeColumn>(result.Columns[0]);
        Assert.Equal(LogicalTypeId.Enum, outCol.Type.Id);
        EnumTypeInfo info = Assert.IsType<EnumTypeInfo>(outCol.Type.TypeInfo);
        Assert.Equal(dictionary, info.Values);
        Assert.Equal(PhysicalType.UInt8, info.PhysicalType);
        // SELECT ... ORDER BY v::VARCHAR gives: "blue", "green", "red"
        // (alphabetical). Indices into our dictionary: 2, 1, 0.
        Assert.Equal(2, outCol.GetBytes(0)[0]);
        Assert.Equal(1, outCol.GetBytes(1)[0]);
        Assert.Equal(0, outCol.GetBytes(2)[0]);
    }

    [Fact]
    public async Task RoundTrip_EnumWithNulls()
    {
        string[] dictionary = ["a", "b", "c"];
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_enum_null_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync(
            $"CREATE TABLE {table} (id INTEGER, v ENUM('a', 'b', 'c'))"));

        LogicalType enumType = new(LogicalTypeId.Enum, new EnumTypeInfo
        {
            Values = dictionary,
            PhysicalType = PhysicalType.UInt8,
        });

        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101; // rows 0 and 2 valid; row 1 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };
        FixedSizeColumn enumCol = new()
        {
            Type = enumType,
            Count = 3,
            ElementSize = 1,
            Data = new byte[] { 0, 0, 2 },
            Validity = new ValidityMask(mask),
        };

        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [idCol.Type, enumType],
            Columns = [idCol, enumCol],
            RowCount = 3,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, CAST(v AS VARCHAR) FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        VarBytesColumn names = Assert.IsType<VarBytesColumn>(result.Columns[1]);
        Assert.False(names.IsNull(0));
        Assert.True(names.IsNull(1));
        Assert.False(names.IsNull(2));
        Assert.Equal("a"u8.ToArray(), names.Values[0]!.Value.ToArray());
        Assert.Equal("c"u8.ToArray(), names.Values[2]!.Value.ToArray());
    }

    private static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static async Task Drain(QuackQueryResult result)
    {
        await foreach (DuckDbChunk _ in result.GetChunksAsync()) { }
    }
}

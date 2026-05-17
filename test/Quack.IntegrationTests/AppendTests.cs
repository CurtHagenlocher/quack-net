// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class AppendTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public AppendTests(QuackServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Append_IntegerAndVarchar_RoundTripsThroughSelect()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        // Each test fixture shares one server, so use a fresh table name to
        // avoid clashes when tests are run together.
        string table = "t_append_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, name VARCHAR)"));

        DuckDbChunk chunk = BuildIntVarcharChunk(
            ids: [1, 2, 3],
            names: ["alpha", "beta", "gamma"]);

        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, name FROM {table} ORDER BY id"))
            .ToListAsync();

        int totalRows = back.Sum(c => c.RowCount);
        Assert.Equal(3, totalRows);

        DuckDbChunk first = back[0];
        FixedSizeColumn idCol = Assert.IsType<FixedSizeColumn>(first.Columns[0]);
        VarBytesColumn nameCol = Assert.IsType<VarBytesColumn>(first.Columns[1]);
        for (int i = 0; i < first.RowCount; i++)
        {
            Assert.Equal(i + 1, BinaryPrimitives.ReadInt32LittleEndian(idCol.GetBytes(i)));
        }
        Assert.Equal("alpha"u8.ToArray(), nameCol.Values[0]!.Value.ToArray());
        Assert.Equal("beta"u8.ToArray(), nameCol.Values[1]!.Value.ToArray());
        Assert.Equal("gamma"u8.ToArray(), nameCol.Values[2]!.Value.ToArray());
    }

    [Fact]
    public async Task Append_RowWithNull_PreservesNullness()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_append_null_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, name VARCHAR)"));

        // Three rows: middle row's id is NULL.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101; // bits 0 and 2 set -> rows 0 and 2 valid
        LogicalType intType = new(LogicalTypeId.Integer);
        LogicalType varcharType = new(LogicalTypeId.Varchar);

        DuckDbChunk chunk = new()
        {
            Types = [intType, varcharType],
            Columns =
            [
                new FixedSizeColumn
                {
                    Type = intType,
                    Count = 3,
                    ElementSize = 4,
                    Data = Int32Bytes(10, 0, 30),
                    Validity = new ValidityMask(mask),
                },
                new VarBytesColumn
                {
                    Type = varcharType,
                    Count = 3,
                    Values = [(ReadOnlyMemory<byte>)"a"u8.ToArray(), (ReadOnlyMemory<byte>)"b"u8.ToArray(), (ReadOnlyMemory<byte>)"c"u8.ToArray()],
                },
            ],
            RowCount = 3,
        };

        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, name FROM {table} ORDER BY name"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        FixedSizeColumn idCol = Assert.IsType<FixedSizeColumn>(result.Columns[0]);
        VarBytesColumn nameCol = Assert.IsType<VarBytesColumn>(result.Columns[1]);

        // ORDER BY name -> rows return as (a=10), (b=NULL), (c=30).
        Assert.Equal(3, result.RowCount);
        Assert.False(idCol.IsNull(0));
        Assert.True(idCol.IsNull(1));
        Assert.False(idCol.IsNull(2));
        Assert.Equal("a"u8.ToArray(), nameCol.Values[0]!.Value.ToArray());
        Assert.Equal("b"u8.ToArray(), nameCol.Values[1]!.Value.ToArray());
        Assert.Equal("c"u8.ToArray(), nameCol.Values[2]!.Value.ToArray());
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(idCol.GetBytes(0)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(idCol.GetBytes(2)));
    }

    [Fact]
    public async Task Append_ToNonExistentTable_Throws()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        DuckDbChunk chunk = BuildIntVarcharChunk(ids: [1], names: ["x"]);

        await Assert.ThrowsAsync<QuackException>(() =>
            conn.AppendAsync("does_not_exist", chunk));
    }

    private static DuckDbChunk BuildIntVarcharChunk(int[] ids, string?[] names)
    {
        if (ids.Length != names.Length)
        {
            throw new ArgumentException("ids and names must have the same length");
        }
        LogicalType intType = new(LogicalTypeId.Integer);
        LogicalType varcharType = new(LogicalTypeId.Varchar);
        ReadOnlyMemory<byte>?[] nameBytes = new ReadOnlyMemory<byte>?[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            nameBytes[i] = names[i] is null ? null : (ReadOnlyMemory<byte>?)System.Text.Encoding.UTF8.GetBytes(names[i]!);
        }
        return new DuckDbChunk
        {
            Types = [intType, varcharType],
            Columns =
            [
                new FixedSizeColumn
                {
                    Type = intType,
                    Count = ids.Length,
                    ElementSize = 4,
                    Data = Int32Bytes(ids),
                },
                new VarBytesColumn
                {
                    Type = varcharType,
                    Count = names.Length,
                    Values = nameBytes,
                },
            ],
            RowCount = ids.Length,
        };
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
        await foreach (DuckDbChunk _ in result.GetChunksAsync())
        {
        }
    }
}

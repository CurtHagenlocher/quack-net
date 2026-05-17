// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class HugeIntTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public HugeIntTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_HugeIntThroughAppendAndSelect()
    {
        Int128[] values =
        [
            Int128.Zero,
            (Int128)1,
            (Int128)(-1),
            Int128.MaxValue,
            Int128.MinValue,
            Int128.Parse("123456789012345678901234567890"),
            -Int128.Parse("123456789012345678901234567890"),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_hugeint_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v HUGEINT)"));

        FixedSizeColumn col = DuckDbHugeInt.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v"))
            .ToListAsync();

        Int128[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbHugeInt.Get((FixedSizeColumn)c.Columns[0], i)))
            .ToArray();

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Fact]
    public async Task RoundTrip_UHugeIntThroughAppendAndSelect()
    {
        UInt128[] values =
        [
            UInt128.Zero,
            (UInt128)1,
            (UInt128)ulong.MaxValue,
            UInt128.MaxValue,
            ((UInt128)1 << 100),
            UInt128.Parse("123456789012345678901234567890"),
        ];

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_uhugeint_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (v UHUGEINT)"));

        FixedSizeColumn col = DuckDbUHugeInt.CreateColumn(values);
        DuckDbChunk chunk = new()
        {
            Types = [col.Type],
            Columns = [col],
            RowCount = values.Length,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT v FROM {table} ORDER BY v"))
            .ToListAsync();

        UInt128[] received = back
            .SelectMany(c => Enumerable.Range(0, c.RowCount)
                .Select(i => DuckDbUHugeInt.Get((FixedSizeColumn)c.Columns[0], i)))
            .ToArray();

        Array.Sort(values);
        Assert.Equal(values, received);
    }

    [Fact]
    public async Task AppendAsync_HugeIntWithNulls_PreservesNullness()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_hugeint_nulls_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, v HUGEINT)"));

        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101;

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(1, 2, 3),
        };
        FixedSizeColumn hugeCol = DuckDbHugeInt.CreateColumn(
            new Int128[] { (Int128)10, (Int128)0, (Int128)30 },
            new ValidityMask(mask));

        DuckDbChunk chunk = new()
        {
            Types = [idCol.Type, hugeCol.Type],
            Columns = [idCol, hugeCol],
            RowCount = 3,
        };
        await conn.AppendAsync(table, chunk);

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, v FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        FixedSizeColumn dec = Assert.IsType<FixedSizeColumn>(result.Columns[1]);
        Assert.Equal(3, result.RowCount);
        Assert.False(dec.IsNull(0));
        Assert.True(dec.IsNull(1));
        Assert.False(dec.IsNull(2));
        Assert.Equal((Int128)10, DuckDbHugeInt.Get(dec, 0));
        Assert.Equal((Int128)30, DuckDbHugeInt.Get(dec, 2));
    }

    private static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static async Task Drain(QuackQueryResult result)
    {
        await foreach (DuckDbChunk _ in result.GetChunksAsync()) { }
    }
}

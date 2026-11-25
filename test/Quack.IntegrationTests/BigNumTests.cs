// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

// BIGNUM arrives as a VarBytesColumn holding DuckDB's own varint encoding.
public class BigNumTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public BigNumTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task BigNum_ArrivesAsDuckDbsBytes()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        const string big = "123456789012345678901234567890123456789012345";
        QuackQueryResult result = await conn.ExecuteAsync(
            $"SELECT {big}::BIGNUM AS a, {big}::BIGNUM AS b, ({big} + 1)::BIGNUM AS c, 7::BIGNUM AS small, NULL::BIGNUM AS n");
        Assert.Equal(LogicalTypeId.BigNum, result.ColumnTypes[0].Id);
        DuckDbChunk chunk = Assert.Single(await result.ToListAsync());
        ReadOnlySpan<byte> Bytes(int column) => Assert.IsType<VarBytesColumn>(chunk.Columns[column]).Values[0]!.Value.Span;
        Assert.True(Bytes(0).SequenceEqual(Bytes(1)));
        Assert.False(Bytes(0).SequenceEqual(Bytes(2)));
        Assert.True(Bytes(0).Length > Bytes(3).Length && Bytes(3).Length > 0);
        Assert.True(chunk.Columns[4].IsNull(0));
    }
}

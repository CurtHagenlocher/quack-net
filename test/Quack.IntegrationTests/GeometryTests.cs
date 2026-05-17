// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class GeometryTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public GeometryTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task RoundTrip_PointGeometry_AsWkb()
    {
        // WKB POINT(1.5, 2.5) and POINT(-3.25, 4.75).
        byte[] p1 = Point(1.5, 2.5);
        byte[] p2 = Point(-3.25, 4.75);

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_geom_pt_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, g GEOMETRY)"));

        LogicalType geomType = new(LogicalTypeId.Geometry);
        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 2,
            ElementSize = 4,
            Data = Int32Bytes(0, 1),
        };
        VarBytesColumn geomCol = new()
        {
            Type = geomType,
            Count = 2,
            Values = [(ReadOnlyMemory<byte>)p1, (ReadOnlyMemory<byte>)p2],
        };

        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [idCol.Type, geomType],
            Columns = [idCol, geomCol],
            RowCount = 2,
        });

        // ST_AsText converts back to a canonical WKT representation.
        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, ST_AsText(g) FROM {table} ORDER BY id"))
            .ToListAsync();

        DuckDbChunk result = back[0];
        VarBytesColumn texts = Assert.IsType<VarBytesColumn>(result.Columns[1]);
        Assert.Equal("POINT (1.5 2.5)"u8.ToArray(), texts.Values[0]!.Value.ToArray());
        Assert.Equal("POINT (-3.25 4.75)"u8.ToArray(), texts.Values[1]!.Value.ToArray());
    }

    [Fact]
    public async Task RoundTrip_LineStringGeometry_AsWkb()
    {
        // WKB LINESTRING(0 0, 1 1, 2 0).
        byte[] line = LineString((0, 0), (1, 1), (2, 0));

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_geom_ls_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (g GEOMETRY)"));

        LogicalType geomType = new(LogicalTypeId.Geometry);
        VarBytesColumn geomCol = new()
        {
            Type = geomType,
            Count = 1,
            Values = [(ReadOnlyMemory<byte>)line],
        };

        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [geomType],
            Columns = [geomCol],
            RowCount = 1,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT ST_AsText(g) FROM {table}"))
            .ToListAsync();

        VarBytesColumn texts = Assert.IsType<VarBytesColumn>(back[0].Columns[0]);
        Assert.Equal("LINESTRING (0 0, 1 1, 2 0)"u8.ToArray(), texts.Values[0]!.Value.ToArray());
    }

    [Fact]
    public async Task RoundTrip_GeometryWithNulls()
    {
        byte[] p1 = Point(10, 20);
        byte[] p2 = Point(30, 40);

        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        string table = "t_geom_null_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, g GEOMETRY)"));

        byte[] mask = new byte[ValidityMask.RequiredByteCount(3)];
        mask[0] = 0b0000_0101; // rows 0, 2 valid; row 1 NULL

        FixedSizeColumn idCol = new()
        {
            Type = new LogicalType(LogicalTypeId.Integer),
            Count = 3,
            ElementSize = 4,
            Data = Int32Bytes(0, 1, 2),
        };
        LogicalType geomType = new(LogicalTypeId.Geometry);
        VarBytesColumn geomCol = new()
        {
            Type = geomType,
            Count = 3,
            Validity = new ValidityMask(mask),
            Values = [(ReadOnlyMemory<byte>)p1, null, (ReadOnlyMemory<byte>)p2],
        };

        await conn.AppendAsync(table, new DuckDbChunk
        {
            Types = [idCol.Type, geomType],
            Columns = [idCol, geomCol],
            RowCount = 3,
        });

        IReadOnlyList<DuckDbChunk> back = await (await conn
            .ExecuteAsync($"SELECT id, ST_AsText(g) FROM {table} ORDER BY id"))
            .ToListAsync();

        VarBytesColumn texts = Assert.IsType<VarBytesColumn>(back[0].Columns[1]);
        Assert.False(texts.IsNull(0));
        Assert.True(texts.IsNull(1));
        Assert.False(texts.IsNull(2));
        Assert.Equal("POINT (10 20)"u8.ToArray(), texts.Values[0]!.Value.ToArray());
        Assert.Equal("POINT (30 40)"u8.ToArray(), texts.Values[2]!.Value.ToArray());
    }

    // ---- minimal little-endian WKB encoders (just enough for the tests) ----

    private static byte[] Point(double x, double y)
    {
        byte[] bytes = new byte[1 + 4 + 8 + 8];
        bytes[0] = 0x01;                                                    // byte order: 1 = little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), 1);    // geometry type: 1 = Point
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(5, 8), x);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(13, 8), y);
        return bytes;
    }

    private static byte[] LineString(params (double X, double Y)[] points)
    {
        byte[] bytes = new byte[1 + 4 + 4 + 16 * points.Length];
        bytes[0] = 0x01;                                                    // little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), 2);    // type: 2 = LineString
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5, 4), (uint)points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            int off = 9 + i * 16;
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(off, 8), points[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(off + 8, 8), points[i].Y);
        }
        return bytes;
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

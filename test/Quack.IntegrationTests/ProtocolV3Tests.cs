// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Quack;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;

namespace Quack.IntegrationTests;

// What quack version 3 (DuckDB 2.0) added or changed on the wire.
public class ProtocolV3Tests : IClassFixture<QuackServerFixture>
{
    private static readonly LogicalType IntType = new(LogicalTypeId.Integer);
    private static readonly LogicalType VarcharType = new(LogicalTypeId.Varchar);

    private readonly QuackServerFixture _server;

    public ProtocolV3Tests(QuackServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Append_MoreRowsThanOneVector_ArrivesComplete()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "t_v3_big_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, name VARCHAR)"));

        // 5000 rows in ONE chunk. The client cuts it at 2048 rows (DuckDB's
        // vector size; the server's client-data scan hands each chunk to the
        // pipeline as it is). NOTE: this test passes without the cut too
        // (checked against DuckDB v2.0.1), so it pins the result, not the cut.
        const int rows = 5000;
        long inserted = await conn.AppendAsync(table, BuildChunk(0, rows, nullEvery: 7));
        Assert.Equal(rows, inserted);

        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync(
            $"SELECT count(*)::BIGINT, sum(id)::BIGINT, count(name)::BIGINT, arg_max(name, id) FROM {table}")).ToListAsync();
        DuckDbChunk result = Assert.Single(back);
        Assert.Equal(rows, ReadInt64(result, 0));
        Assert.Equal((long)rows * (rows - 1) / 2, ReadInt64(result, 1));
        Assert.Equal(rows - (rows + 6) / 7, ReadInt64(result, 2));
        Assert.Equal("name-4999", ReadString(result, 3));
    }

    [Fact]
    public async Task Append_SeveralChunks_IsOneStatement()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "t_v3_multi_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, name VARCHAR)"));

        long inserted = await conn.AppendAsync(string.Empty, table, [BuildChunk(0, 10), BuildChunk(10, 3000), BuildChunk(3010, 1)]);
        Assert.Equal(3011, inserted);

        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync(
            $"SELECT count(DISTINCT id)::BIGINT, min(id)::BIGINT, max(id)::BIGINT FROM {table}")).ToListAsync();
        DuckDbChunk result = Assert.Single(back);
        Assert.Equal(3011, ReadInt64(result, 0));
        Assert.Equal(0, ReadInt64(result, 1));
        Assert.Equal(3010, ReadInt64(result, 2));
    }

    [Fact]
    public async Task Append_TypeMismatch_FailsAndConnectionStaysUsable()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string table = "t_v3_bad_" + Guid.NewGuid().ToString("N");
        await Drain(await conn.ExecuteAsync($"CREATE TABLE {table} (id INTEGER, d DATE)"));

        // VARCHAR 'name-0' does not cast to DATE.
        await Assert.ThrowsAnyAsync<QuackException>(() => conn.AppendAsync(table, BuildChunk(0, 3)));

        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync($"SELECT count(*)::BIGINT FROM {table}")).ToListAsync();
        Assert.Equal(0, ReadInt64(Assert.Single(back), 0));
    }

    [Fact]
    public async Task Cancel_InterruptsARunningQuery()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        // PREPARE blocks until the first batch exists, which this query never produces quickly.
        Task<QuackQueryResult> running = conn.ExecuteAsync(
            "SELECT count(*) FROM range(100000000000) a(x) WHERE x % 7 = 3");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(running.IsCompleted, "the query finished before it could be cancelled");

        await conn.CancelAsync();

        QuackException ex = await Assert.ThrowsAnyAsync<QuackException>(() => running);
        Assert.Contains("Interrupt", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The connection survives a cancel.
        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync("SELECT 42::BIGINT")).ToListAsync();
        Assert.Equal(42, ReadInt64(Assert.Single(back), 0));
    }

    [Fact]
    public async Task Heartbeat_KeepsAnIdleSessionAlive()
    {
        QuackConnectionOptions options = new() { HeartbeatTimeoutSeconds = 2 };
        await using QuackConnection conn = await QuackConnection.OpenAsync(
            QuackUri.Parse(_server.QuackUri), _server.Token, options: options);
        Assert.Equal(2UL, conn.HeartbeatTimeoutSeconds);

        await Task.Delay(TimeSpan.FromSeconds(5));

        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync("SELECT 1::BIGINT")).ToListAsync();
        Assert.Equal(1, ReadInt64(Assert.Single(back), 0));
    }

    [Fact]
    public async Task Heartbeat_Control_ASessionWithoutHeartbeatsExpires()
    {
        // The positive control for the test above: the same lease without heartbeats.
        using QuackTransport transport = new(QuackUri.Parse(_server.QuackUri));
        ConnectionResponseMessage session = await transport.SendAsync<ConnectionResponseMessage>(new ConnectionRequestMessage
        {
            AuthString = _server.Token,
            MinSupportedQuackVersion = QuackConnection.ProtocolVersion,
            MaxSupportedQuackVersion = QuackConnection.ProtocolVersion,
            HeartbeatTimeoutSeconds = 2,
        });

        await Task.Delay(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<QuackSessionExpiredException>(() => transport.SendAsync<PrepareResponseMessage>(
            new PrepareRequestMessage { ConnectionId = session.ConnectionId, SqlQuery = "SELECT 1", QueryUuid = 1 }));
    }

    [Fact]
    public async Task ServerError_CarriesItsExceptionType()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        QuackException ex = await Assert.ThrowsAnyAsync<QuackException>(
            () => conn.ExecuteAsync("SELECT * FROM t_v3_does_not_exist"));
        Assert.Equal("Catalog", ex.ServerExceptionType);
    }

    [Fact]
    public async Task NewTypes_TimestampTzNs_And_Tuple_AreRead()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        QuackQueryResult result = await conn.ExecuteAsync(
            "SELECT '2024-01-02 03:04:05.123456789+00'::TIMESTAMPTZ_NS AS ts, (7, 'x')::TUPLE(INTEGER, VARCHAR) AS tup");

        Assert.Equal(LogicalTypeId.TimestampTzNs, result.ColumnTypes[0].Id);
        Assert.Equal(LogicalTypeId.Tuple, result.ColumnTypes[1].Id);

        DuckDbChunk chunk = Assert.Single(await result.ToListAsync());
        FixedSizeColumn ts = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        DateTimeOffset value = DuckDbTimestamp.GetDateTimeOffset(ts, 0);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567), value);
        Assert.Equal(1704164645123456789L, DuckDbTimestamp.GetRaw(ts, 0));

        StructColumn tup = Assert.IsType<StructColumn>(chunk.Columns[1]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(Assert.IsType<FixedSizeColumn>(tup.Fields[0]).GetBytes(0)));
        Assert.Equal("x", Encoding.UTF8.GetString(Assert.IsType<VarBytesColumn>(tup.Fields[1]).Values[0]!.Value.Span));
    }

    [Fact]
    public async Task LargeResult_IsFetchedInDenseBatches()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        // ~48 MiB of flat UBIGINTs: more than one 32 MiB batch, so FETCH runs
        // after PREPARE. (A bare range() column is a sequence vector, which
        // serializes to a few bytes and would all come back inline.)
        const string values = "SELECT hash(i) AS h FROM range(6000000) t(i)";
        IReadOnlyList<DuckDbChunk> expected = await (await conn.ExecuteAsync(
            $"SELECT count(*)::BIGINT, bit_xor(h)::UBIGINT FROM ({values})")).ToListAsync();

        QuackQueryResult result = await conn.ExecuteAsync(values);
        long rows = 0;
        ulong xor = 0;
        await foreach (DuckDbChunk chunk in result.GetChunksAsync())
        {
            FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
            for (int i = 0; i < chunk.RowCount; i++)
            {
                xor ^= BinaryPrimitives.ReadUInt64LittleEndian(col.GetBytes(i));
            }
            rows += chunk.RowCount;
        }
        Assert.Equal(ReadInt64(expected[0], 0), rows);
        Assert.Equal((ulong)ReadInt64(expected[0], 1), xor);
    }

    [Fact]
    public async Task Token_CancelsTheQueryOnTheServer_AndKeepsTheConnection()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string marker = "tok_" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new();

        Task<QuackQueryResult> running = conn.ExecuteAsync(
            $"SELECT count(*) /* {marker} */ FROM range(100000000000) a(x) WHERE x % 7 = 3", cts.Token);
        await ServerSessions.WaitUntilActiveAsync(_server, marker);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(conn.IsUsable);
        Assert.Equal("cancelled", await ServerSessions.StateOfConnectionAsync(_server, conn.ConnectionId));
        Assert.False(await ServerSessions.IsActiveAsync(_server, marker));

        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync("SELECT 42::BIGINT")).ToListAsync();
        Assert.Equal(42, ReadInt64(Assert.Single(back), 0));
    }

    [Fact]
    public async Task Token_DuringFetch_CancelsTheQueryOnTheServer()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string marker = "fetch_" + Guid.NewGuid().ToString("N");
        // Large enough that FETCH follows PREPARE (see LargeResult_IsFetchedInDenseBatches).
        QuackQueryResult result = await conn.ExecuteAsync(
            $"SELECT hash(i) /* {marker} */ AS h FROM range(60000000) t(i)");
        Assert.True(await ServerSessions.IsActiveAsync(_server, marker));
        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DuckDbChunk _ in result.GetChunksAsync(cts.Token))
            {
                cts.Cancel();
            }
        });

        Assert.True(conn.IsUsable);
        Assert.Equal("cancelled", await ServerSessions.StateOfConnectionAsync(_server, conn.ConnectionId));
        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync("SELECT 7::BIGINT")).ToListAsync();
        Assert.Equal(7, ReadInt64(Assert.Single(back), 0));
    }

    private static async Task Drain(QuackQueryResult result) => _ = await result.ToListAsync();

    private static long ReadInt64(DuckDbChunk chunk, int column)
        => BinaryPrimitives.ReadInt64LittleEndian(Assert.IsType<FixedSizeColumn>(chunk.Columns[column]).GetBytes(0));

    private static string ReadString(DuckDbChunk chunk, int column)
        => Encoding.UTF8.GetString(Assert.IsType<VarBytesColumn>(chunk.Columns[column]).Values[0]!.Value.Span);

    // ids start..start+count-1, names "name-<id>", every nullEvery-th name NULL (from index 0).
    private static DuckDbChunk BuildChunk(int start, int count, int nullEvery = 0)
    {
        byte[] ids = new byte[count * 4];
        ReadOnlyMemory<byte>?[] names = new ReadOnlyMemory<byte>?[count];
        byte[] validity = new byte[ValidityMask.RequiredByteCount(count)];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(ids.AsSpan(i * 4), start + i);
            if (nullEvery > 0 && i % nullEvery == 0)
            {
                continue;
            }
            names[i] = Encoding.UTF8.GetBytes($"name-{start + i}");
            validity[i >> 3] |= (byte)(1 << (i & 7));
        }
        return new DuckDbChunk
        {
            Types = [IntType, VarcharType],
            Columns =
            [
                new FixedSizeColumn { Type = IntType, Count = count, ElementSize = 4, Data = ids },
                new VarBytesColumn
                {
                    Type = VarcharType,
                    Count = count,
                    Values = names,
                    Validity = nullEvery > 0 ? new ValidityMask(validity) : ValidityMask.AllValid,
                },
            ],
            RowCount = count,
        };
    }
}

// Reads the server's own view of its sessions (quack_active_connections()),
// over a separate connection, to check what happened to a statement.
internal static class ServerSessions
{
    public static async Task<List<(string ConnectionId, string Query, string State)>> ListAsync(QuackServerFixture server)
    {
        await using QuackConnection observer = await QuackConnection.OpenAsync(server.QuackUri, server.Token);
        IReadOnlyList<DuckDbChunk> chunks = await (await observer.ExecuteAsync(
            "SELECT connection_id, coalesce(query, ''), state FROM quack_active_connections()")).ToListAsync();
        List<(string, string, string)> rows = [];
        foreach (DuckDbChunk chunk in chunks)
        {
            for (int i = 0; i < chunk.RowCount; i++)
            {
                rows.Add((Text(chunk, 0, i), Text(chunk, 1, i), Text(chunk, 2, i)));
            }
        }
        return rows;
    }

    // Positive control: the statement carrying `marker` is visible as running.
    public static async Task WaitUntilActiveAsync(QuackServerFixture server, string marker)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if ((await ListAsync(server)).Any(r => r.Query.Contains(marker, StringComparison.Ordinal) && r.State == "active"))
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"The statement '{marker}' never showed up as active on the server.");
    }

    public static async Task<string> StateOfConnectionAsync(QuackServerFixture server, string connectionId)
        => Assert.Single(await ListAsync(server), r => r.ConnectionId == connectionId).State;

    public static async Task<bool> IsActiveAsync(QuackServerFixture server, string marker)
        => (await ListAsync(server)).Any(r => r.Query.Contains(marker, StringComparison.Ordinal) && r.State == "active");

    private static string Text(DuckDbChunk chunk, int column, int row)
        => System.Text.Encoding.UTF8.GetString(((VarBytesColumn)chunk.Columns[column]).Values[row]!.Value.Span);
}

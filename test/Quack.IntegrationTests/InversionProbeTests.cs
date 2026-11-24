// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Quack;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;
using Xunit.Abstractions;

namespace Quack.IntegrationTests;

// Inversion pass over the protocol version 3 port: ways it could break that
// the feature tests do not cover, each measured against a DuckDB 2.0 server.
public class InversionProbeTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;
    private readonly ITestOutputHelper _out;

    public InversionProbeTests(QuackServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _out = output;
    }

    // A: strings in DICTIONARY vectors (a checkpointed, dictionary-compressed
    // column) are decoded in the 2.0 layout, NULLs, '' and UTF-8 included.
    [Fact]
    public async Task A_DictionaryStrings_MatchTheServersCounts()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string path = Path.Combine(Path.GetTempPath(), "quack_probe_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
        string alias = "fdb_" + Guid.NewGuid().ToString("N")[..8];
        await Drain(conn, $"ATTACH '{path}' AS {alias}");
        try
        {
            await Drain(conn, $"CREATE TABLE {alias}.d AS SELECT i, CASE WHEN i % 10 = 0 THEN NULL ELSE ['alpha','beta','gamma','δέλτα',''][1 + i % 5] END AS s FROM range(200000) t(i)");
            await Drain(conn, $"CHECKPOINT {alias}");

            // Positive control: the scan really produces dictionary vectors.
            IReadOnlyList<DuckDbChunk> vt = await All(conn, $"SELECT count(*)::BIGINT FROM {alias}.d WHERE vector_type(s) = 'DICTIONARY_VECTOR'");
            Assert.True(Int64(vt[0], 0, 0) > 100000);

            Dictionary<string, long> client = new(StringComparer.Ordinal);
            await foreach (DuckDbChunk chunk in (await conn.ExecuteAsync($"SELECT s FROM {alias}.d")).GetChunksAsync())
            {
                VarBytesColumn col = Assert.IsType<VarBytesColumn>(chunk.Columns[0]);
                for (int i = 0; i < chunk.RowCount; i++)
                {
                    string key = col.IsNull(i) ? "<NULL>" : Encoding.UTF8.GetString(col.Values[i]!.Value.Span);
                    client[key] = client.GetValueOrDefault(key) + 1;
                }
            }
            Dictionary<string, long> server = new(StringComparer.Ordinal);
            foreach (DuckDbChunk chunk in await All(conn, $"SELECT coalesce(s, '<NULL>'), count(*)::BIGINT FROM {alias}.d GROUP BY 1"))
            {
                for (int i = 0; i < chunk.RowCount; i++)
                {
                    server[Text(chunk, 0, i)] = Int64(chunk, 1, i);
                }
            }
            _out.WriteLine(string.Join(", ", client.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            Assert.Equal(server.OrderBy(k => k.Key), client.OrderBy(k => k.Key));
        }
        finally
        {
            await Drain(conn, $"DETACH {alias}");
            File.Delete(path);
        }
    }

    // A: CONSTANT vectors of strings, including a NULL and an empty constant.
    [Fact]
    public async Task A_ConstantStrings_AreExpandedPerRow()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        long rows = 0;
        await foreach (DuckDbChunk chunk in (await conn.ExecuteAsync(
            "SELECT 'const' AS c, NULL::VARCHAR AS n, '' AS e, 'ünï' AS u FROM range(5000)")).GetChunksAsync())
        {
            for (int i = 0; i < chunk.RowCount; i++)
            {
                Assert.Equal("const", Text(chunk, 0, i));
                Assert.True(chunk.Columns[1].IsNull(i));
                Assert.Equal(string.Empty, Text(chunk, 2, i));
                Assert.Equal("ünï", Text(chunk, 3, i));
            }
            rows += chunk.RowCount;
        }
        Assert.Equal(5000, rows);
    }

    // C: a statement that runs several times longer than the lease. The server
    // checks the lease lazily, when the NEXT message of the session arrives, so
    // what is at risk is the request after the long one (FETCH, ACK, the next
    // query): the lease was last renewed when the long PREPARE started.
    [Fact]
    public async Task C_RequestAfterAQueryLongerThanTheLease_Succeeds()
    {
        QuackConnectionOptions options = new() { HeartbeatTimeoutSeconds = 2 };
        await using QuackConnection conn = await QuackConnection.OpenAsync(QuackUri.Parse(_server.QuackUri), _server.Token, options: options);
        Stopwatch sw = Stopwatch.StartNew();
        IReadOnlyList<DuckDbChunk> back = await (await conn.ExecuteAsync(
            "SELECT count(*)::BIGINT FROM range(700000000) a(x) WHERE hash(x) % 7 = 3")).ToListAsync();
        _out.WriteLine($"long query: {sw.Elapsed.TotalSeconds:0.0} s, result {Int64(back[0], 0, 0)}");
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(4), "the query must outlast the 2 s lease several times");

        IReadOnlyList<DuckDbChunk> next = await All(conn, "SELECT 11::BIGINT");
        Assert.Equal(11, Int64(next[0], 0, 0));
    }

    // C control: the same sequence on a session that never heartbeats.
    [Fact]
    public async Task C_Control_WithoutHeartbeatsTheNextRequestFails()
    {
        using QuackTransport transport = new(QuackUri.Parse(_server.QuackUri));
        ConnectionResponseMessage session = await transport.SendAsync<ConnectionResponseMessage>(new ConnectionRequestMessage
        {
            AuthString = _server.Token,
            MinSupportedQuackVersion = QuackConnection.ProtocolVersion,
            MaxSupportedQuackVersion = QuackConnection.ProtocolVersion,
            HeartbeatTimeoutSeconds = 2,
        });
        Stopwatch sw = Stopwatch.StartNew();
        PrepareResponseMessage first = await transport.SendAsync<PrepareResponseMessage>(new PrepareRequestMessage
        {
            ConnectionId = session.ConnectionId,
            SqlQuery = "SELECT count(*)::BIGINT FROM range(700000000) a(x) WHERE hash(x) % 7 = 3",
            QueryUuid = 99,
        });
        _out.WriteLine($"long PREPARE without heartbeats returned after {sw.Elapsed.TotalSeconds:0.0} s");
        QuackSessionExpiredException ex = await Assert.ThrowsAsync<QuackSessionExpiredException>(() => transport.SendAsync<PrepareResponseMessage>(
            new PrepareRequestMessage { ConnectionId = session.ConnectionId, SqlQuery = "SELECT 1", QueryUuid = 100 }));
        _out.WriteLine("next request: " + ex.ServerMessage);
    }

    // D: target names that the quoted INSERT could treat differently from the
    // v1 APPEND (case, schema, quotes).
    [Fact]
    public async Task D_AppendTargets_CaseSchemaAndQuotes()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string schema = "S_" + suffix;
        await Drain(conn, $"CREATE TABLE \"MixedCase_{suffix}\" (id INTEGER)");
        await Drain(conn, $"CREATE SCHEMA {schema}");
        await Drain(conn, $"CREATE TABLE {schema}.t (id INTEGER)");
        await Drain(conn, $"CREATE TABLE \"we\"\"ird {suffix}\" (id INTEGER)");

        Assert.Equal(1, await conn.AppendAsync($"mixedcase_{suffix}", IntChunk(1)));
        Assert.Equal(1, await conn.AppendAsync($"MixedCase_{suffix}", IntChunk(2)));
        Assert.Equal(1, await conn.AppendAsync(schema.ToLowerInvariant(), "T", IntChunk(3)));
        Assert.Equal(1, await conn.AppendAsync($"we\"ird {suffix}", IntChunk(4)));
        QuackException dotted = await Assert.ThrowsAnyAsync<QuackException>(() => conn.AppendAsync($"{schema}.t", IntChunk(5)));
        _out.WriteLine("dotted name: " + dotted.Message);
    }

    // F: round trips per small query, and what they cost here.
    [Fact]
    public async Task F_RoundTripsPerSmallQuery()
    {
        CountingHandler counter = new(new SocketsHttpHandler());
        using HttpClient http = new(counter) { Timeout = Timeout.InfiniteTimeSpan };
        await using QuackConnection conn = await QuackConnection.OpenAsync(QuackUri.Parse(_server.QuackUri), _server.Token, http);
        int before = counter.Count;
        _ = await (await conn.ExecuteAsync("SELECT 1")).ToListAsync();
        _out.WriteLine($"requests for one SELECT 1: {counter.Count - before} ({string.Join(", ", counter.Types.Skip(before))})");
        // Pinned on purpose: the acknowledgement is one extra round trip per query.
        Assert.Equal(["PrepareRequest", "Acknowledgement"], counter.Types.Skip(before).ToArray());

        const int n = 300;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            _ = await (await conn.ExecuteAsync("SELECT 1")).ToListAsync();
        }
        _out.WriteLine($"{n} x SELECT 1 incl. ack: {sw.Elapsed.TotalMilliseconds / n:0.000} ms per query");
        sw.Restart();
        for (int i = 0; i < n; i++)
        {
            _ = await conn.ExecuteAsync("SELECT 1");
        }
        _out.WriteLine($"{n} x SELECT 1 without reading (no ack): {sw.Elapsed.TotalMilliseconds / n:0.000} ms per query");
    }

    // G: a token that fires right at the start, before the server has
    // registered the query. Repeated, since it is a race.
    [Fact]
    public async Task G_TokenAtTheStart_LeavesNothingRunning()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        int cancelled = 0;
        int completed = 0;
        for (int i = 0; i < 20; i++)
        {
            string marker = "g_" + Guid.NewGuid().ToString("N");
            using CancellationTokenSource cts = new();
            Task<QuackQueryResult> run = conn.ExecuteAsync(
                $"SELECT count(*) /* {marker} */ FROM range(100000000000) a(x) WHERE x % 7 = 3", cts.Token);
            if (i % 2 == 0)
            {
                cts.Cancel();
            }
            else
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(i));
            }
            try
            {
                await run;
                completed++;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
            Assert.True(conn.IsUsable, $"iteration {i}: connection broken");
            Assert.False(await ServerSessions.IsActiveAsync(_server, marker), $"iteration {i}: statement still running");
        }
        _out.WriteLine($"cancelled {cancelled}, completed {completed}");
        IReadOnlyList<DuckDbChunk> back = await All(conn, "SELECT 5::BIGINT");
        Assert.Equal(5, Int64(back[0], 0, 0));
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public List<string> Types { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            lock (Types)
            {
                Types.Add(QuackMessage.Deserialize(body).MessageType.ToString());
            }
            Interlocked.Increment(ref _count);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static DuckDbChunk IntChunk(int value)
    {
        LogicalType intType = new(LogicalTypeId.Integer);
        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        return new DuckDbChunk
        {
            Types = [intType],
            Columns = [new FixedSizeColumn { Type = intType, Count = 1, ElementSize = 4, Data = data }],
            RowCount = 1,
        };
    }

    private static async Task Drain(QuackConnection conn, string sql) => _ = await (await conn.ExecuteAsync(sql)).ToListAsync();

    private static async Task<IReadOnlyList<DuckDbChunk>> All(QuackConnection conn, string sql)
        => (await (await conn.ExecuteAsync(sql)).ToListAsync()).Where(c => c.RowCount > 0).ToList();

    private static long Int64(DuckDbChunk chunk, int column, int row)
        => BinaryPrimitives.ReadInt64LittleEndian(Assert.IsType<FixedSizeColumn>(chunk.Columns[column]).GetBytes(row));

    private static string Text(DuckDbChunk chunk, int column, int row)
        => Encoding.UTF8.GetString(Assert.IsType<VarBytesColumn>(chunk.Columns[column]).Values[row]!.Value.Span);
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Protocol;
using Quack.Transport;

namespace Quack.IntegrationTests;

// A result left before its end is cancelled on the server (which would
// otherwise keep producing into its buffer); a result read to its end is not.
public class ResultCloseTests : IClassFixture<QuackServerFixture>
{
    private const string LongResult = "SELECT hash(range) AS h FROM range(20000000)";

    private readonly QuackServerFixture _server;

    public ResultCloseTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task LeftMidway_SendsACancelForThatQuery()
    {
        Recorder recorder = new();
        await using QuackConnection conn = await Open(recorder);
        QuackQueryResult result = await conn.ExecuteAsync(LongResult);
        await using (IAsyncEnumerator<DuckDbChunk> chunks = result.GetChunksAsync().GetAsyncEnumerator())
        {
            Assert.True(await chunks.MoveNextAsync());
        }
        Assert.Equal([result.QueryUuid], recorder.Cancels());
        Assert.Equal(42L, await Scalar(conn, "SELECT 42::BIGINT"));
    }

    [Fact]
    public async Task NeverRead_CloseAsyncCancels()
    {
        Recorder recorder = new();
        await using QuackConnection conn = await Open(recorder);
        QuackQueryResult result = await conn.ExecuteAsync(LongResult);
        await result.CloseAsync();
        await result.CloseAsync();
        Assert.Equal([result.QueryUuid], recorder.Cancels());
        Assert.Equal(42L, await Scalar(conn, "SELECT 42::BIGINT"));
    }

    [Fact]
    public async Task ReadToTheEnd_SendsNoCancel()
    {
        Recorder recorder = new();
        await using QuackConnection conn = await Open(recorder);
        QuackQueryResult result = await conn.ExecuteAsync("SELECT range FROM range(100000)");
        _ = await result.ToListAsync();
        await result.CloseAsync();
        Assert.Empty(recorder.Cancels());
    }

    // An append fetches its row count without acking it first, reads the
    // result to its end and acknowledges it.
    [Fact]
    public async Task Append_FetchesAcksAndAcknowledges()
    {
        Recorder recorder = new();
        await using QuackConnection conn = await Open(recorder);
        string table = "ra_" + Guid.NewGuid().ToString("N")[..8];
        _ = await (await conn.ExecuteAsync($"CREATE TABLE {table} (v BIGINT)")).ToListAsync();
        QuackQueryResult source = await conn.ExecuteAsync("SELECT range AS v FROM range(5000)");
        IReadOnlyList<DuckDbChunk> chunks = await source.ToListAsync();
        recorder.Clear();
        Assert.Equal(5000, await conn.AppendAsync("main", table, chunks));
        List<QuackMessage> sent = recorder.Sent();
        List<(ulong, ulong)> fetches = sent.OfType<FetchRequestMessage>().Select(f => (f.BatchIndex, f.AckIndex)).ToList();
        Assert.Equal([(1UL, 0UL), (2UL, 1UL)], fetches);
        Assert.IsType<AcknowledgementMessage>(sent[^1]);
        Assert.Equal(5000L, await Scalar(conn, $"SELECT count(*)::BIGINT FROM {table}"));
        _ = await (await conn.ExecuteAsync($"DROP TABLE {table}")).ToListAsync();
    }

    private async Task<QuackConnection> Open(Recorder recorder)
        => await QuackConnection.OpenAsync(QuackUri.Parse(_server.QuackUri), _server.Token, new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan });

    private static async Task<long> Scalar(QuackConnection conn, string sql)
    {
        IReadOnlyList<DuckDbChunk> chunks = await (await conn.ExecuteAsync(sql)).ToListAsync();
        return BitConverter.ToInt64(((FixedSizeColumn)chunks[0].Columns[0]).GetBytes(0));
    }

    // Records the messages the client sends.
    private sealed class Recorder : DelegatingHandler
    {
        private readonly List<QuackMessage> _sent = [];

        public Recorder() : base(new HttpClientHandler())
        {
        }

        public void Clear()
        {
            lock (_sent)
            {
                _sent.Clear();
            }
        }

        public List<QuackMessage> Sent()
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }

        public List<Int128> Cancels()
        {
            lock (_sent)
            {
                return _sent.OfType<CancelRequestMessage>().Select(c => c.QueryUuid).ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                lock (_sent)
                {
                    _sent.Add(QuackMessage.Deserialize(body));
                }
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

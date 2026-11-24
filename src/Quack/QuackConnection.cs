// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;
using Quack.Types;

namespace Quack;

// A live quack-protocol session against a DuckDB server (quack version 3, as
// served by the quack extension of DuckDB 2.0). Opening sends a
// ConnectionRequest and stashes the server-assigned session id; the id is
// echoed in every subsequent request header until DisposeAsync sends the
// DisconnectMessage.
//
// Lease: the server keeps a connection only while it hears from it at least
// once per HeartbeatTimeoutSeconds (default 60). Every request renews the
// lease; a background timer sends a HeartbeatRequest about every third of the
// timeout, so a long-running query or an idle connection is not reaped. An
// expired lease aborts the running statement and surfaces as
// QuackSessionExpiredException.
//
// Cancellation: a CancellationToken passed to ExecuteAsync, AppendAsync or
// QuackQueryResult.GetChunksAsync cancels the QUERY: the client sends a
// CancelRequest, the server interrupts the statement, the pending request ends
// and the call throws OperationCanceledException. The connection stays usable.
// Only if the server does not end the request within a grace period (10 s) is
// the HTTP request aborted, which breaks the transport (IsUsable turns false;
// subsequent calls throw and DisposeAsync skips its DisconnectMessage).
// CancelAsync sends a CancelRequest directly.
//
// Auto-reconnect (opt-in via OpenAsync's autoReconnect parameter): when
// the server reports the session is gone — an unknown connection_id after a
// server restart, or an expired lease — the next ExecuteAsync/AppendAsync
// call transparently re-handshakes (sending a fresh ConnectionRequest with the
// retained token) and retries once. Fetching the rest of a result is NOT
// auto-recovered: the result is gone and re-executing the query that produced
// it could have side effects. Auto-reconnect silently drops session-level
// state on the server (transactions, SET, ATTACH, temp tables, prepared
// statements) — the recovery is correct only for stateless queries.
// SessionGeneration is bumped every time a re-handshake succeeds.
public sealed class QuackConnection : IAsyncDisposable
{
    // The only protocol version this client speaks (duckdb-quack QUACK_VERSION).
    public const ulong ProtocolVersion = 3;
    // duckdb-quack's quack_default_heartbeat_timeout.
    public const ulong DefaultHeartbeatTimeoutSeconds = 60;
    // duckdb-quack's MAX_HEARTBEAT_TIMEOUT_SECONDS.
    public const ulong MaxHeartbeatTimeoutSeconds = (ulong)long.MaxValue / 1000;

    private readonly QuackTransport _transport;
    private readonly bool _ownsTransport;
    private readonly string _token;
    private readonly bool _autoReconnect;
    private readonly QuackConnectionOptions _options;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly Timer _heartbeat;
    private string _connectionId;
    private long _heartbeatTimeoutSeconds;
    private int _sessionGeneration;
    private int _disposed;

    public string ConnectionId => Volatile.Read(ref _connectionId)!;
    public string ServerDuckDbVersion { get; }
    public string ServerPlatform { get; }
    public ulong QuackVersion { get; }
    // The lease the server accepted for the current session.
    public ulong HeartbeatTimeoutSeconds => (ulong)Interlocked.Read(ref _heartbeatTimeoutSeconds);
    public QuackUri ServerUri => _transport.Uri;
    public int SessionGeneration => Volatile.Read(ref _sessionGeneration);
    public bool AutoReconnect => _autoReconnect;
    // False once disposed, or once a request had to be aborted locally.
    public bool IsUsable => Volatile.Read(ref _disposed) == 0 && !_transport.IsBroken;

    internal QuackTransport Transport => _transport;

    private QuackConnection(QuackTransport transport, bool ownsTransport, string token, bool autoReconnect, QuackConnectionOptions options, ConnectionResponseMessage response)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        _token = token;
        _autoReconnect = autoReconnect;
        _options = options;
        _connectionId = response.ConnectionId;
        _heartbeatTimeoutSeconds = (long)response.HeartbeatTimeoutSeconds;
        ServerDuckDbVersion = response.ServerDuckDbVersion;
        ServerPlatform = response.ServerPlatform;
        QuackVersion = response.QuackVersion;
        _heartbeat = new Timer(static state => ((QuackConnection)state!).OnHeartbeat(), this, Timeout.Infinite, Timeout.Infinite);
        ScheduleHeartbeat();
    }

    public static Task<QuackConnection> OpenAsync(string quackUri, string token, CancellationToken cancellationToken = default)
        => OpenAsync(QuackUri.Parse(quackUri), token, cancellationToken: cancellationToken);

    public static async Task<QuackConnection> OpenAsync(QuackUri uri, string token, HttpClient? httpClient = null, bool autoReconnect = false, QuackConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        QuackTransport transport = new(uri, httpClient);
        try
        {
            return await OpenInternalAsync(transport, ownsTransport: true, token, autoReconnect, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public static Task<QuackConnection> OpenAsync(QuackTransport transport, string token, bool autoReconnect = false, QuackConnectionOptions? options = null, CancellationToken cancellationToken = default)
        => OpenInternalAsync(transport, ownsTransport: false, token, autoReconnect, options, cancellationToken);

    private static async Task<QuackConnection> OpenInternalAsync(QuackTransport transport, bool ownsTransport, string token, bool autoReconnect, QuackConnectionOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(token);
        options ??= QuackConnectionOptions.Default;
        options.Validate();

        ConnectionResponseMessage response = await HandshakeAsync(transport, token, options, cancellationToken).ConfigureAwait(false);
        return new QuackConnection(transport, ownsTransport, token, autoReconnect, options, response);
    }

    private static async Task<ConnectionResponseMessage> HandshakeAsync(QuackTransport transport, string token, QuackConnectionOptions options, CancellationToken cancellationToken)
    {
        ConnectionRequestMessage request = new()
        {
            AuthString = token,
            ClientDuckDbVersion = ClientLibraryVersion(),
            ClientPlatform = ClientPlatformString(),
            MinSupportedQuackVersion = ProtocolVersion,
            MaxSupportedQuackVersion = ProtocolVersion,
            ClientId = options.ClientId,
            HeartbeatTimeoutSeconds = options.HeartbeatTimeoutSeconds,
        };

        ConnectionResponseMessage response;
        try
        {
            response = await transport
                .SendAsync<ConnectionResponseMessage>(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QuackException ex) when (ex.HttpStatusCode == 500)
        {
            // Measured: a DuckDB 1.5 server (quack protocol version 1) cannot
            // read this handshake and answers HTTP 500 without an ErrorResponse.
            throw new QuackException(
                $"{ex.Message} The server could not read the handshake; a DuckDB 1.5 server (quack protocol "
                + $"version 1) answers this way. This client speaks quack protocol version {ProtocolVersion} (DuckDB 2.0).",
                ex)
            {
                HttpStatusCode = ex.HttpStatusCode,
            };
        }

        if (response.QuackVersion != ProtocolVersion)
        {
            throw new QuackException(
                $"Server negotiated quack version {response.QuackVersion}, but this client only supports version {ProtocolVersion}.");
        }
        if (response.HeartbeatTimeoutSeconds is 0 or > MaxHeartbeatTimeoutSeconds)
        {
            throw new QuackException(
                $"Server accepted an out-of-range heartbeat timeout of {response.HeartbeatTimeoutSeconds} seconds.");
        }
        return response;
    }

    public async Task<QuackQueryResult> ExecuteAsync(string sqlQuery, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sqlQuery);

        Int128 queryUuid = NewQueryUuid();
        PrepareResponseMessage response = await SendWithReconnectAsync<PrepareResponseMessage>(
            id => new PrepareRequestMessage { ConnectionId = id, SqlQuery = sqlQuery, QueryUuid = queryUuid },
            queryUuid,
            cancellationToken).ConfigureAwait(false);

        // Snapshot ConnectionId after the (possibly retried) send so that
        // a subsequent FetchRequest carries the post-reconnect id.
        return new QuackQueryResult(
            transport: _transport,
            connectionId: ConnectionId,
            columnTypes: response.ResultTypes,
            columnNames: response.ResultNames,
            firstBatch: response.Results,
            needsMoreFetch: response.NeedsMoreFetch,
            queryUuid: response.QueryUuid);
    }

    // Interrupts a query on the server. Int128.Zero cancels whatever runs on
    // this connection; any other id must name the running query.
    public async Task CancelAsync(Int128 queryUuid = default, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _ = await _transport.SendAsync<SuccessResponse>(
            new CancelRequestMessage { ConnectionId = ConnectionId, QueryUuid = queryUuid },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<long> AppendAsync(string tableName, DuckDbChunk chunk, CancellationToken cancellationToken = default)
        => AppendAsync(schemaName: string.Empty, tableName: tableName, chunk: chunk, cancellationToken: cancellationToken);

    public Task<long> AppendAsync(string schemaName, string tableName, DuckDbChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return AppendAsync(schemaName, tableName, [chunk], cancellationToken);
    }

    // Appends the chunks' rows to (schema.)table, by position, in ONE statement:
    //   INSERT INTO t SELECT * FROM scan_data_from_quack_client(stream, NULL::STRUCT(...), ordered := true)
    // prepared with inline_rows = 0, then one SEND_DATA batch per chunk (cut
    // into pieces of at most 2048 rows, DuckDB's vector size, since the server
    // hands each piece to its pipeline as it is) and a terminal SEND_DATA with
    // the batch count (a lost batch fails the statement), then a FETCH of the
    // inserted row count. Returns that count.
    public async Task<long> AppendAsync(string schemaName, string tableName, IReadOnlyList<DuckDbChunk> chunks, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(schemaName);
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentNullException.ThrowIfNull(chunks);

        List<DuckDbChunk> batches = chunks.Where(c => c.RowCount > 0).ToList();
        if (batches.Count == 0)
        {
            return 0;
        }
        IReadOnlyList<LogicalType> types = batches[0].Types;
        foreach (DuckDbChunk chunk in batches)
        {
            if (!chunk.Types.SequenceEqual(types))
            {
                throw new ArgumentException("All chunks of one append must have the same column types.", nameof(chunks));
            }
        }

        string streamId = Guid.NewGuid().ToString();
        string sql = BuildAppendSql(schemaName, tableName, streamId, types);
        return await SendStreamAsync(sql, streamId, types, ToAsyncEnumerable(batches), cancellationToken).ConfigureAwait(false);
    }

    internal enum IngestMode
    {
        Append,
        Create,
        Replace,
        CreateAppend,
    }

    // Where an ADBC bulk ingest goes. A temporary target is DuckDB's temp.main.
    internal sealed record IngestTarget(string Table, string? Catalog, string? Schema, bool Temporary, IngestMode Mode);

    // ADBC bulk ingest: the chunks' rows into the target, one statement per mode,
    // the chunks read from the source as they are sent:
    //   append         INSERT INTO t SELECT * FROM scan_data_from_quack_client(...)
    //   create         CREATE TABLE t AS SELECT * FROM scan_data_from_quack_client(...)
    //   replace        CREATE OR REPLACE TABLE t AS SELECT * FROM scan_data_from_quack_client(...)
    //   create_append  CREATE TABLE IF NOT EXISTS t (columns), then the append
    // create and replace are one statement each; create_append's two are not atomic.
    // The column names matter for a created table only; the append is by position.
    internal async Task<long> IngestAsync(IngestTarget target, IReadOnlyList<string> names, IReadOnlyList<LogicalType> types, IAsyncEnumerable<DuckDbChunk> chunks, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(target.Table);
        if (names.Count != types.Count || types.Count == 0)
        {
            throw new ArgumentException("An ingest needs at least one column and one name per column.", nameof(names));
        }
        if (target.Mode == IngestMode.CreateAppend)
        {
            QuackQueryResult created = await ExecuteAsync(BuildCreateIfNotExistsSql(target, names, types), cancellationToken).ConfigureAwait(false);
            _ = await created.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        string streamId = Guid.NewGuid().ToString();
        string sql = BuildIngestSql(target, streamId, names, types);
        return await SendStreamAsync(sql, streamId, types, chunks, cancellationToken).ConfigureAwait(false);
    }

    // Prepares sql (which scans streamId), sends the chunks as SEND_DATA batches,
    // ends the stream with the batch count and fetches the statement's row count.
    private async Task<long> SendStreamAsync(string sql, string streamId, IReadOnlyList<LogicalType> types, IAsyncEnumerable<DuckDbChunk> chunks, CancellationToken cancellationToken)
    {
        Int128 queryUuid = NewQueryUuid();
        _ = await SendWithReconnectAsync<PrepareResponseMessage>(
            id => new PrepareRequestMessage { ConnectionId = id, SqlQuery = sql, QueryUuid = queryUuid, InlineRows = 0 },
            queryUuid,
            cancellationToken).ConfigureAwait(false);

        string connectionId = ConnectionId;
        try
        {
            ulong sent = 0;
            await foreach (IReadOnlyList<ReadOnlyMemory<byte>> batch in SendDataBatchesAsync(OfTypes(chunks, types), SendDataTargetBatchBytes, cancellationToken).ConfigureAwait(false))
            {
                sent++;
                _ = await QueryCancellation.SendAsync<SendDataResponseMessage>(
                    _transport,
                    new SendDataRequestMessage
                    {
                        ConnectionId = connectionId,
                        StreamId = streamId,
                        BatchIndex = sent,
                        SerializedChunks = batch,
                    },
                    queryUuid,
                    cancellationToken,
                    queryStarted: true).ConfigureAwait(false);
            }
            _ = await QueryCancellation.SendAsync<SendDataResponseMessage>(
                _transport,
                new SendDataRequestMessage
                {
                    ConnectionId = connectionId,
                    StreamId = streamId,
                    TotalBatches = sent,
                },
                queryUuid,
                cancellationToken,
                queryStarted: true).ConfigureAwait(false);

            // The statement's result is one row: the rows it inserted.
            FetchResponseMessage result = await QueryCancellation.SendAsync<FetchResponseMessage>(
                _transport,
                new FetchRequestMessage { ConnectionId = connectionId, QueryUuid = queryUuid, BatchIndex = 1, AckIndex = 1 },
                queryUuid,
                cancellationToken,
                queryStarted: true).ConfigureAwait(false);
            return ReadRowCount(result);
        }
        catch (Exception) when (!_transport.IsBroken)
        {
            // Otherwise the server statement keeps waiting for batches until the next query.
            try
            {
                await _transport.SendAsync<SuccessResponse>(
                    new CancelRequestMessage { ConnectionId = connectionId, QueryUuid = queryUuid },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best effort; keep the original error.
            }
            throw;
        }
    }

    private static async IAsyncEnumerable<DuckDbChunk> ToAsyncEnumerable(IReadOnlyList<DuckDbChunk> chunks)
    {
        foreach (DuckDbChunk chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // The non-empty chunks, each checked against the stream's column types.
    private static async IAsyncEnumerable<DuckDbChunk> OfTypes(IAsyncEnumerable<DuckDbChunk> chunks, IReadOnlyList<LogicalType> types)
    {
        await foreach (DuckDbChunk chunk in chunks.ConfigureAwait(false))
        {
            if (!chunk.Types.SequenceEqual(types))
            {
                throw new ArgumentException("A chunk's column types differ from the stream's.");
            }
            if (chunk.RowCount > 0)
            {
                yield return chunk;
            }
        }
    }

    // duckdb-quack's quack_target_batch_bytes default. One SEND_DATA request is
    // one HTTP body, and the server's HTTP layer refuses bodies over 100 MiB.
    internal const int SendDataTargetBatchBytes = 32 * 1024 * 1024;

    // Serialized chunks of at most 2048 rows, grouped into batches of about
    // targetBytes (across input chunks). A piece that alone exceeds the target
    // is halved until it fits or is a single row.
    internal static IEnumerable<IReadOnlyList<ReadOnlyMemory<byte>>> SendDataBatches(IEnumerable<DuckDbChunk> chunks, int targetBytes)
    {
        List<ReadOnlyMemory<byte>> current = [];
        long currentBytes = 0;
        foreach (DuckDbChunk chunk in chunks)
        {
            foreach (ReadOnlyMemory<byte> piece in Pieces(chunk, targetBytes))
            {
                if (current.Count > 0 && currentBytes + piece.Length > targetBytes)
                {
                    yield return current;
                    current = [];
                    currentBytes = 0;
                }
                current.Add(piece);
                currentBytes += piece.Length;
            }
        }
        if (current.Count > 0)
        {
            yield return current;
        }
    }

    // As SendDataBatches, from a stream. A chunk is serialized completely (into
    // new buffers) before the next one is requested, so a source may release a
    // chunk's memory once it is asked for the next.
    internal static async IAsyncEnumerable<IReadOnlyList<ReadOnlyMemory<byte>>> SendDataBatchesAsync(
        IAsyncEnumerable<DuckDbChunk> chunks, int targetBytes, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ReadOnlyMemory<byte>> current = [];
        long currentBytes = 0;
        await foreach (DuckDbChunk chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            List<ReadOnlyMemory<byte>> pieces = Pieces(chunk, targetBytes).ToList();
            foreach (ReadOnlyMemory<byte> piece in pieces)
            {
                if (current.Count > 0 && currentBytes + piece.Length > targetBytes)
                {
                    yield return current;
                    current = [];
                    currentBytes = 0;
                }
                current.Add(piece);
                currentBytes += piece.Length;
            }
        }
        if (current.Count > 0)
        {
            yield return current;
        }
    }

    // A chunk as serialized pieces of at most 2048 rows (DuckDB's vector size).
    private static IEnumerable<ReadOnlyMemory<byte>> Pieces(DuckDbChunk chunk, int targetBytes)
        => DuckDbChunkSlicer.Split(chunk).SelectMany(slice => SerializeWithin(slice, targetBytes));

    private static IEnumerable<ReadOnlyMemory<byte>> SerializeWithin(DuckDbChunk chunk, int targetBytes)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        DataChunkWriter.Write(new Serialization.BinarySerializer(buffer), chunk);
        if (buffer.WrittenCount <= targetBytes || chunk.RowCount == 1)
        {
            yield return buffer.WrittenMemory;
            yield break;
        }
        int half = chunk.RowCount / 2;
        foreach (DuckDbChunk part in DuckDbChunkSlicer.Split(chunk, half))
        {
            foreach (ReadOnlyMemory<byte> piece in SerializeWithin(part, targetBytes))
            {
                yield return piece;
            }
        }
    }

    internal static string BuildAppendSql(string schemaName, string tableName, string streamId, IReadOnlyList<LogicalType> types)
    {
        StringBuilder sb = new("INSERT INTO ");
        if (schemaName.Length > 0)
        {
            sb.Append(LogicalTypeSql.QuoteIdentifier(schemaName)).Append('.');
        }
        sb.Append(LogicalTypeSql.QuoteIdentifier(tableName));
        sb.Append(" SELECT * FROM ");
        AppendScan(sb, streamId, names: null, types);
        return sb.ToString();
    }

    internal static string BuildIngestSql(IngestTarget target, string streamId, IReadOnlyList<string> names, IReadOnlyList<LogicalType> types)
    {
        bool creates = target.Mode is IngestMode.Create or IngestMode.Replace;
        string temp = target.Temporary ? "TEMP " : "";
        StringBuilder sb = new(target.Mode switch
        {
            IngestMode.Create => $"CREATE {temp}TABLE ",
            IngestMode.Replace => $"CREATE OR REPLACE {temp}TABLE ",
            _ => "INSERT INTO ",
        });
        AppendTarget(sb, target, creating: creates);
        sb.Append(creates ? " AS SELECT * FROM " : " SELECT * FROM ");
        AppendScan(sb, streamId, creates ? names : null, types);
        return sb.ToString();
    }

    internal static string BuildCreateIfNotExistsSql(IngestTarget target, IReadOnlyList<string> names, IReadOnlyList<LogicalType> types)
    {
        StringBuilder sb = new(target.Temporary ? "CREATE TEMP TABLE IF NOT EXISTS " : "CREATE TABLE IF NOT EXISTS ");
        AppendTarget(sb, target, creating: true);
        sb.Append(" (");
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(LogicalTypeSql.QuoteIdentifier(names[i])).Append(' ').Append(LogicalTypeSql.ToSql(types[i]));
        }
        return sb.Append(')').ToString();
    }

    // A temporary table is created as CREATE TEMP TABLE t (DuckDB refuses the
    // temp catalog there) and written as temp.main.t.
    private static void AppendTarget(StringBuilder sb, IngestTarget target, bool creating)
    {
        if (target.Temporary)
        {
            if (!creating)
            {
                sb.Append("temp.main.");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(target.Catalog))
            {
                sb.Append(LogicalTypeSql.QuoteIdentifier(target.Catalog)).Append('.');
            }
            if (!string.IsNullOrEmpty(target.Schema))
            {
                sb.Append(LogicalTypeSql.QuoteIdentifier(target.Schema)).Append('.');
            }
        }
        sb.Append(LogicalTypeSql.QuoteIdentifier(target.Table));
    }

    // scan_data_from_quack_client('<stream>', NULL::STRUCT(<columns>), ordered := true);
    // the STRUCT's field names are the scan's column names (col0, col1, ... unless given).
    private static void AppendScan(StringBuilder sb, string streamId, IReadOnlyList<string>? names, IReadOnlyList<LogicalType> types)
    {
        sb.Append("scan_data_from_quack_client('").Append(streamId.Replace("'", "''", StringComparison.Ordinal));
        sb.Append("', NULL::STRUCT(");
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            if (names is null)
            {
                sb.Append("col").Append(i.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(LogicalTypeSql.QuoteIdentifier(names[i]));
            }
            sb.Append(' ').Append(LogicalTypeSql.ToSql(types[i]));
        }
        sb.Append("), ordered := true)");
    }

    private static long ReadRowCount(FetchResponseMessage response)
    {
        foreach (DuckDbChunk chunk in response.Results)
        {
            if (chunk.RowCount > 0 && chunk.Columns.Count > 0 && chunk.Columns[0] is FixedSizeColumn count && count.ElementSize == sizeof(long))
            {
                return BinaryPrimitives.ReadInt64LittleEndian(count.GetBytes(0));
            }
        }
        throw new QuackException("The append statement returned no row count.");
    }

    // A random, nonzero query id: the server reserves 0 for its catalog traffic.
    internal static Int128 NewQueryUuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        Int128 value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadInt128LittleEndian(bytes);
        }
        while (value == Int128.Zero);
        return value;
    }

    // Sends a message that carries a connection_id (Prepare). If the
    // server reports the session is gone AND auto-reconnect is enabled,
    // re-handshakes once and retries the request with the new id. The lock
    // serialises concurrent reconnect attempts and the SessionGeneration
    // check lets racing callers piggy-back on a peer's reconnect instead of
    // re-doing it.
    private async Task<TResponse> SendWithReconnectAsync<TResponse>(
        Func<string, QuackMessage> buildRequest,
        Int128 queryUuid,
        CancellationToken cancellationToken)
        where TResponse : QuackMessage
    {
        string idAtSend = ConnectionId;
        int genAtSend = SessionGeneration;
        try
        {
            return await QueryCancellation.SendAsync<TResponse>(_transport, buildRequest(idAtSend), queryUuid, cancellationToken, queryStarted: false).ConfigureAwait(false);
        }
        catch (QuackSessionExpiredException) when (_autoReconnect)
        {
            await EnsureReconnectedAsync(genAtSend, cancellationToken).ConfigureAwait(false);
            // Second attempt uses the post-reconnect id. If it fails, throw —
            // we don't loop, to avoid masking a genuine ongoing problem.
            return await QueryCancellation.SendAsync<TResponse>(_transport, buildRequest(ConnectionId), queryUuid, cancellationToken, queryStarted: false).ConfigureAwait(false);
        }
    }

    private async Task EnsureReconnectedAsync(int generationAtFailure, CancellationToken cancellationToken)
    {
        await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller already reconnected for us.
            if (SessionGeneration != generationAtFailure)
            {
                return;
            }
            ConnectionResponseMessage response = await HandshakeAsync(_transport, _token, _options, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _connectionId, response.ConnectionId);
            Interlocked.Exchange(ref _heartbeatTimeoutSeconds, (long)response.HeartbeatTimeoutSeconds);
            Interlocked.Increment(ref _sessionGeneration);
            ScheduleHeartbeat();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    // About a third of the lease, with jitter (like DuckDB's own client).
    private void ScheduleHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        double intervalMs = HeartbeatTimeoutSeconds * 1000.0 / 3.0 * (0.8 + 0.4 * Random.Shared.NextDouble());
        long dueTime = (long)Math.Clamp(intervalMs, 1.0, int.MaxValue - 1.0);
        try
        {
            _heartbeat.Change(dueTime, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0 || _transport.IsBroken)
        {
            return;
        }
        try
        {
            await _transport.SendAsync<SuccessResponse>(
                new HeartbeatRequestMessage { ConnectionId = ConnectionId },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A lost heartbeat is retried on the next tick; a dead session
            // surfaces on the next real request.
        }
        ScheduleHeartbeat();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _heartbeat.DisposeAsync().ConfigureAwait(false);
        if (!_transport.IsBroken)
        {
            try
            {
                await _transport
                    .SendAsync<SuccessResponse>(new DisconnectMessage { ConnectionId = ConnectionId }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the server may already have gone away. Don't mask
                // the original (probably more interesting) reason for disposing.
            }
        }
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
        _reconnectLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(QuackConnection));
        }
    }

    private static string ClientLibraryVersion()
    {
        Version? version = typeof(QuackConnection).Assembly.GetName().Version;
        return version is null ? "quack-net" : $"quack-net {version.ToString(3)}";
    }

    private static string ClientPlatformString()
    {
        string os = RuntimeInformation.OSDescription;
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $".NET {Environment.Version.ToString(3)} on {os} ({arch})";
    }
}

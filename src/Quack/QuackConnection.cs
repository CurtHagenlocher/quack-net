// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Runtime.InteropServices;
using Quack.Data;
using Quack.Protocol;
using Quack.Transport;

namespace Quack;

// A live quack-protocol session against a DuckDB server. Opening sends a
// ConnectionRequest and stashes the server-assigned session id; the id is
// echoed in every subsequent request header until DisposeAsync sends the
// DisconnectMessage.
//
// Cancellation semantics (v1.5-variegata): the protocol has no
// cancel/interrupt message, so cancellation is best-effort and terminal
// for the connection. When a CancellationToken passed to any *Async method
// fires, the local HTTP request is aborted and the underlying transport is
// marked broken — subsequent *Async calls throw, and DisposeAsync skips
// its DisconnectMessage attempt (which would race with the mid-flight
// request) and just releases the socket. The server-side query keeps
// running until it naturally completes; we cannot reclaim the session
// before then. Callers who hit cancellation must dispose and reopen.
//
// Auto-reconnect (opt-in via OpenAsync's autoReconnect parameter): when
// the server reports the connection_id is unknown — typically because the
// server was restarted and its in-memory session map is gone — the next
// ExecuteAsync/AppendAsync call will transparently re-handshake (sending
// a fresh ConnectionRequest with the retained token) and retry once.
// FetchAsync deliberately is NOT auto-recovered: the server's result_uuid
// is gone and re-executing the query that produced it could have side
// effects. Auto-reconnect silently drops session-level state on the
// server (transactions, SET, ATTACH, temp tables, prepared statements)
// — the user-visible recovery is correct only for stateless queries.
// SessionGeneration is bumped every time a re-handshake succeeds.
public sealed class QuackConnection : IAsyncDisposable
{
    private readonly QuackTransport _transport;
    private readonly bool _ownsTransport;
    private readonly string _token;
    private readonly bool _autoReconnect;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private string _connectionId;
    private int _sessionGeneration;
    private int _disposed;

    public string ConnectionId => Volatile.Read(ref _connectionId)!;
    public string ServerDuckDbVersion { get; }
    public string ServerPlatform { get; }
    public ulong QuackVersion { get; }
    public QuackUri ServerUri => _transport.Uri;
    public int SessionGeneration => Volatile.Read(ref _sessionGeneration);
    public bool AutoReconnect => _autoReconnect;

    internal QuackTransport Transport => _transport;

    private QuackConnection(QuackTransport transport, bool ownsTransport, string token, bool autoReconnect, ConnectionResponseMessage response)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        _token = token;
        _autoReconnect = autoReconnect;
        _connectionId = response.ConnectionId;
        ServerDuckDbVersion = response.ServerDuckDbVersion;
        ServerPlatform = response.ServerPlatform;
        QuackVersion = response.QuackVersion;
    }

    public static Task<QuackConnection> OpenAsync(string quackUri, string token, CancellationToken cancellationToken = default)
        => OpenAsync(QuackUri.Parse(quackUri), token, cancellationToken: cancellationToken);

    public static async Task<QuackConnection> OpenAsync(QuackUri uri, string token, HttpClient? httpClient = null, bool autoReconnect = false, CancellationToken cancellationToken = default)
    {
        QuackTransport transport = new(uri, httpClient);
        try
        {
            return await OpenInternalAsync(transport, ownsTransport: true, token, autoReconnect, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public static Task<QuackConnection> OpenAsync(QuackTransport transport, string token, bool autoReconnect = false, CancellationToken cancellationToken = default)
        => OpenInternalAsync(transport, ownsTransport: false, token, autoReconnect, cancellationToken);

    private static async Task<QuackConnection> OpenInternalAsync(QuackTransport transport, bool ownsTransport, string token, bool autoReconnect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(token);

        ConnectionResponseMessage response = await HandshakeAsync(transport, token, cancellationToken).ConfigureAwait(false);
        return new QuackConnection(transport, ownsTransport, token, autoReconnect, response);
    }

    private static async Task<ConnectionResponseMessage> HandshakeAsync(QuackTransport transport, string token, CancellationToken cancellationToken)
    {
        ConnectionRequestMessage request = new()
        {
            AuthString = token,
            ClientDuckDbVersion = ClientLibraryVersion(),
            ClientPlatform = ClientPlatformString(),
            MinSupportedQuackVersion = 1,
            MaxSupportedQuackVersion = 1,
        };

        ConnectionResponseMessage response = await transport
            .SendAsync<ConnectionResponseMessage>(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.QuackVersion != 1)
        {
            throw new QuackException(
                $"Server negotiated quack version {response.QuackVersion}, but this client only supports version 1.");
        }
        return response;
    }

    public async Task<QuackQueryResult> ExecuteAsync(string sqlQuery, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sqlQuery);

        PrepareResponseMessage response = await SendWithReconnectAsync<PrepareResponseMessage>(
            id => new PrepareRequestMessage { ConnectionId = id, SqlQuery = sqlQuery },
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
            resultUuid: response.ResultUuid);
    }

    public Task AppendAsync(string tableName, DuckDbChunk chunk, CancellationToken cancellationToken = default)
        => AppendAsync(schemaName: string.Empty, tableName: tableName, chunk: chunk, cancellationToken: cancellationToken);

    public async Task AppendAsync(string schemaName, string tableName, DuckDbChunk chunk, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(schemaName);
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentNullException.ThrowIfNull(chunk);

        _ = await SendWithReconnectAsync<SuccessResponse>(
            id => new AppendRequestMessage
            {
                ConnectionId = id,
                SchemaName = schemaName,
                TableName = tableName,
                AppendChunk = chunk,
            },
            cancellationToken).ConfigureAwait(false);
    }

    // Sends a message that carries a connection_id (Prepare/Append). If the
    // server reports the session is gone AND auto-reconnect is enabled,
    // re-handshakes once and retries the request with the new id. The lock
    // serialises concurrent reconnect attempts and the SessionGeneration
    // check lets racing callers piggy-back on a peer's reconnect instead of
    // re-doing it.
    private async Task<TResponse> SendWithReconnectAsync<TResponse>(
        Func<string, QuackMessage> buildRequest,
        CancellationToken cancellationToken)
        where TResponse : QuackMessage
    {
        string idAtSend = ConnectionId;
        int genAtSend = SessionGeneration;
        try
        {
            return await _transport.SendAsync<TResponse>(buildRequest(idAtSend), cancellationToken).ConfigureAwait(false);
        }
        catch (QuackSessionExpiredException) when (_autoReconnect)
        {
            await EnsureReconnectedAsync(genAtSend, cancellationToken).ConfigureAwait(false);
            // Second attempt uses the post-reconnect id. If it fails, throw —
            // we don't loop, to avoid masking a genuine ongoing problem.
            return await _transport.SendAsync<TResponse>(buildRequest(ConnectionId), cancellationToken).ConfigureAwait(false);
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
            ConnectionResponseMessage response = await HandshakeAsync(_transport, _token, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _connectionId, response.ConnectionId);
            Interlocked.Increment(ref _sessionGeneration);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
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

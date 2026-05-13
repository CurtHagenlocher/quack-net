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
// Cancellation caveat: cancelling a CancellationToken passed to any *Async
// method aborts the local HTTP request immediately, but the v1.5-variegata
// server has no equivalent of Connection::Interrupt() wired into its HTTP
// handler. So a long-running query keeps executing server-side until it
// completes on its own — the cancel saves client CPU/memory, not server
// work. DisposeAsync (which sends DisconnectMessage) is still the only way
// to make the server release the session.
public sealed class QuackConnection : IAsyncDisposable
{
    private readonly QuackTransport _transport;
    private readonly bool _ownsTransport;
    private int _disposed;

    public string ConnectionId { get; }
    public string ServerDuckDbVersion { get; }
    public string ServerPlatform { get; }
    public ulong QuackVersion { get; }
    public QuackUri ServerUri => _transport.Uri;

    private QuackConnection(QuackTransport transport, bool ownsTransport, ConnectionResponseMessage response)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        ConnectionId = response.ConnectionId;
        ServerDuckDbVersion = response.ServerDuckDbVersion;
        ServerPlatform = response.ServerPlatform;
        QuackVersion = response.QuackVersion;
    }

    public static Task<QuackConnection> OpenAsync(string quackUri, string token, CancellationToken cancellationToken = default)
        => OpenAsync(QuackUri.Parse(quackUri), token, cancellationToken: cancellationToken);

    public static async Task<QuackConnection> OpenAsync(QuackUri uri, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        QuackTransport transport = new(uri, httpClient);
        try
        {
            return await OpenInternalAsync(transport, ownsTransport: true, token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public static Task<QuackConnection> OpenAsync(QuackTransport transport, string token, CancellationToken cancellationToken = default)
        => OpenInternalAsync(transport, ownsTransport: false, token, cancellationToken);

    private static async Task<QuackConnection> OpenInternalAsync(QuackTransport transport, bool ownsTransport, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(token);

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

        return new QuackConnection(transport, ownsTransport, response);
    }

    public async Task<QuackQueryResult> ExecuteAsync(string sqlQuery, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sqlQuery);

        PrepareRequestMessage request = new()
        {
            ConnectionId = ConnectionId,
            SqlQuery = sqlQuery,
        };

        PrepareResponseMessage response = await _transport
            .SendAsync<PrepareResponseMessage>(request, cancellationToken)
            .ConfigureAwait(false);

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

        AppendRequestMessage request = new()
        {
            ConnectionId = ConnectionId,
            SchemaName = schemaName,
            TableName = tableName,
            AppendChunk = chunk,
        };

        // Server replies with SuccessResponse on success. ErrorResponse is
        // turned into a QuackException by the transport.
        _ = await _transport
            .SendAsync<SuccessResponse>(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
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
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
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

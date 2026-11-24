// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using Quack.Protocol;
using Quack.Serialization;

namespace Quack.Transport;

// HTTP transport for the quack protocol. Sends each message as a single
// `POST /quack` with Content-Type `application/vnd.duckdb`. Mirrors the server in
// duckdb_quack::QuackHttpServer.
//
// Default HttpClient has no timeout. An HTTP-level timeout can only abort
// the request locally while leaving the server-side query running on a
// session the client can no longer safely reuse (QuackConnection.CancelAsync
// is the in-protocol way to stop a query). Callers who want to bound a
// request pass a CancellationToken; cancellation marks the transport broken
// (IsBroken == true) so QuackConnection.DisposeAsync skips its
// DisconnectMessage attempt and just releases the socket.
public sealed class QuackTransport : IDisposable
{
    // What DuckDB's own quack client and server send.
    private static readonly MediaTypeHeaderValue QuackMediaType = new("application/vnd.duckdb");

    // Exact server-side wording from QuackServer::DispatchMessage in
    // duckdb-quack v2.0-cyanoptera: an unknown connection id, and a session
    // whose heartbeat lease ran out. Match is case-insensitive for forward
    // robustness against minor server rewording (an upstream issue asks
    // for a typed error code; until then string-matching is the contract).
    private static readonly string[] SessionExpiredServerMessages =
    [
        "Invalid connection id",
        "Connection heartbeat lease expired",
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;
    private int _broken;

    public QuackUri Uri { get; }

    public bool IsBroken => Volatile.Read(ref _broken) != 0;

    public QuackTransport(QuackUri uri, HttpClient? httpClient = null)
    {
        Uri = uri;
        _endpoint = uri.HttpUrl;
        _httpClient = httpClient ?? CreateDefaultClient(uri.SkipCertificateVerification);
        _ownsHttpClient = httpClient is null;
    }

    public QuackTransport(string quackUri, bool? useSsl = null, HttpClient? httpClient = null)
        : this(QuackUri.Parse(quackUri, useSsl), httpClient)
    {
    }

    public Task<QuackMessage> SendAsync(QuackMessage message, CancellationToken cancellationToken = default)
        => SendCoreAsync(message, cancellationToken, markBrokenOnCancel: true);

    // For a side request (CANCEL) that may be abandoned: it runs on its own
    // pooled HTTP connection, so giving up on it leaves the transport intact.
    internal async Task<TResponse> SendSideRequestAsync<TResponse>(QuackMessage message, CancellationToken cancellationToken)
        where TResponse : QuackMessage
    {
        QuackMessage response = await SendCoreAsync(message, cancellationToken, markBrokenOnCancel: false).ConfigureAwait(false);
        return response as TResponse ?? throw new QuackException(
            $"Expected response of type '{typeof(TResponse).Name}' but server returned '{response.GetType().Name}' (MessageType={response.MessageType}).");
    }

    private async Task<QuackMessage> SendCoreAsync(QuackMessage message, CancellationToken cancellationToken, bool markBrokenOnCancel)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsBroken)
        {
            throw new QuackException("Transport is broken (a prior request was cancelled); open a new connection.");
        }

        byte[] requestBytes = message.ToBytes();

        using ByteArrayContent content = new(requestBytes);
        content.Headers.ContentType = QuackMediaType;

        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (markBrokenOnCancel) MarkBroken();
            throw;
        }
        catch (HttpRequestException ex)
        {
            MarkBroken();
            throw new QuackException($"HTTP request to {_endpoint} failed: {ex.Message}", ex);
        }

        using (response)
        {
            byte[] responseBytes;
            try
            {
                responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (markBrokenOnCancel) MarkBroken();
                throw;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Try to surface a server-side ErrorResponse if the body parses
                // as one; otherwise just report the status.
                if (responseBytes.Length > 0 && TryParseError(responseBytes, out ErrorResponse? errorResponse))
                {
                    if (errorResponse.MustInvalidate)
                    {
                        Volatile.Write(ref _broken, 1);
                    }
                    throw ToTypedError(message.ConnectionId, errorResponse);
                }
                throw new QuackException($"HTTP {(int)response.StatusCode} ({response.StatusCode}) from quack server at {_endpoint}.")
                {
                    HttpStatusCode = (int)response.StatusCode,
                };
            }

            QuackMessage parsed;
            try
            {
                parsed = QuackMessage.Deserialize(responseBytes);
            }
            catch (SerializationException ex)
            {
                throw new QuackException($"Failed to deserialize quack response: {ex.Message}", ex);
            }

            if (parsed is ErrorResponse error)
            {
                // The server's own database died with this error: nothing on
                // this connection can succeed any more.
                if (error.MustInvalidate)
                {
                    Volatile.Write(ref _broken, 1);
                }
                throw ToTypedError(message.ConnectionId, error);
            }
            return parsed;
        }
    }

    // ErrorResponse on the wire could be a generic failure or a
    // session-loss signal. The latter leaves the transport itself
    // healthy — only the server-side session is gone — so we keep
    // IsBroken untouched and let QuackConnection decide whether to
    // re-handshake.
    private static QuackException ToTypedError(string requestConnectionId, ErrorResponse error)
    {
        string serverMessage = error.Message;
        bool expired = SessionExpiredServerMessages.Any(m => serverMessage.Contains(m, StringComparison.OrdinalIgnoreCase));
        return expired
            ? new QuackSessionExpiredException(requestConnectionId, serverMessage)
            {
                ServerExceptionType = NullIfEmpty(error.ExceptionType),
                ServerExtraInfo = error.ExtraInfo,
                ServerMustInvalidate = error.MustInvalidate,
            }
            : new QuackException(serverMessage)
            {
                ServerExceptionType = NullIfEmpty(error.ExceptionType),
                ServerExtraInfo = error.ExtraInfo,
                ServerMustInvalidate = error.MustInvalidate,
            };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public async Task<TResponse> SendAsync<TResponse>(QuackMessage message, CancellationToken cancellationToken = default)
        where TResponse : QuackMessage
    {
        QuackMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response is TResponse typed)
        {
            return typed;
        }
        throw new QuackException(
            $"Expected response of type '{typeof(TResponse).Name}' but server returned '{response.GetType().Name}' (MessageType={response.MessageType}).");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void MarkBroken() => Interlocked.Exchange(ref _broken, 1);

    private static bool TryParseError(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out ErrorResponse? error)
    {
        try
        {
            if (QuackMessage.Deserialize(bytes) is ErrorResponse parsed)
            {
                error = parsed;
                return true;
            }
        }
        catch
        {
            // Fall through; we'll return a status-based message instead.
        }
        error = null;
        return false;
    }

    private static HttpClient CreateDefaultClient(bool skipCertificateVerification)
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        if (skipCertificateVerification)
        {
            // tls=skip_verify: encrypted, but any certificate is accepted.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        // No HttpClient.Timeout: a fired HttpClient.Timeout aborts the
        // request locally and leaves the server-side query running on a
        // session the client can't safely reuse. Callers wanting a query
        // timeout pass a CancellationToken (terminal for the connection) or
        // call QuackConnection.CancelAsync.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

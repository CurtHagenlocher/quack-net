using System.Net.Http.Headers;
using Quack.Protocol;
using Quack.Serialization;

namespace Quack.Transport;

// HTTP transport for the quack protocol. Sends each message as a single
// `POST /quack` with Content-Type `application/duckdb`. Mirrors the server in
// duckdb_quack::QuackHttpServer.
//
// Default HttpClient has no timeout. The quack server has no in-protocol
// cancel/interrupt (see QuackConnection comment), so an HTTP-level timeout
// can only abort the request locally while leaving the server-side query
// running on a session the client can no longer safely reuse. Callers who
// want to bound a query pass a CancellationToken; cancellation marks the
// transport broken (IsBroken == true) so QuackConnection.DisposeAsync skips
// its DisconnectMessage attempt and just releases the socket.
public sealed class QuackTransport : IDisposable
{
    private static readonly MediaTypeHeaderValue QuackMediaType = new("application/duckdb");

    // Exact server-side wording from QuackServer::HandleMessage in
    // duckdb-quack v1.5-variegata. Match is case-insensitive for forward
    // robustness against minor server rewording (an upstream issue asks
    // for a typed error code; until then string-matching is the contract).
    private const string SessionExpiredServerMessage = "Invalid connection id";

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
        _httpClient = httpClient ?? CreateDefaultClient();
        _ownsHttpClient = httpClient is null;
    }

    public QuackTransport(string quackUri, bool? useSsl = null, HttpClient? httpClient = null)
        : this(QuackUri.Parse(quackUri, useSsl), httpClient)
    {
    }

    public async Task<QuackMessage> SendAsync(QuackMessage message, CancellationToken cancellationToken = default)
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
            MarkBroken();
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
                MarkBroken();
                throw;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Try to surface a server-side ErrorResponse if the body parses
                // as one; otherwise just report the status.
                if (responseBytes.Length > 0 && TryParseErrorMessage(responseBytes, out string? errorMessage))
                {
                    throw ToTypedError(message.ConnectionId, errorMessage);
                }
                throw new QuackException($"HTTP {(int)response.StatusCode} ({response.StatusCode}) from quack server at {_endpoint}.");
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
                throw ToTypedError(message.ConnectionId, error.Message);
            }
            return parsed;
        }
    }

    // ErrorResponse on the wire could be a generic failure or a
    // session-loss signal. The latter leaves the transport itself
    // healthy — only the server-side session is gone — so we keep
    // IsBroken untouched and let QuackConnection decide whether to
    // re-handshake.
    private static QuackException ToTypedError(string requestConnectionId, string serverMessage)
        => serverMessage.Contains(SessionExpiredServerMessage, StringComparison.OrdinalIgnoreCase)
            ? new QuackSessionExpiredException(requestConnectionId, serverMessage)
            : new QuackException(serverMessage);

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

    private static bool TryParseErrorMessage(ReadOnlyMemory<byte> bytes, out string message)
    {
        try
        {
            QuackMessage parsed = QuackMessage.Deserialize(bytes);
            if (parsed is ErrorResponse error)
            {
                message = error.Message;
                return true;
            }
        }
        catch
        {
            // Fall through; we'll return a status-based message instead.
        }
        message = string.Empty;
        return false;
    }

    private static HttpClient CreateDefaultClient()
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        // No HttpClient.Timeout: the quack protocol has no cancel/interrupt
        // message, so a fired HttpClient.Timeout aborts the request locally
        // and leaves the server-side query running on a session the client
        // can't safely reuse. Callers wanting a query timeout pass a
        // CancellationToken instead; QuackConnection treats cancellation as
        // a terminal event for the connection.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

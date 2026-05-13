using System.Net.Http.Headers;
using Quack.Protocol;
using Quack.Serialization;

namespace Quack.Transport;

// HTTP transport for the quack protocol. Sends each message as a single
// `POST /quack` with Content-Type `application/duckdb`. Mirrors the server in
// duckdb_quack::QuackHttpServer.
public sealed class QuackTransport : IDisposable
{
    private static readonly MediaTypeHeaderValue QuackMediaType = new("application/duckdb");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;

    public QuackUri Uri { get; }

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

        byte[] requestBytes = message.ToBytes();

        using ByteArrayContent content = new(requestBytes);
        content.Headers.ContentType = QuackMediaType;

        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new QuackException($"HTTP request to {_endpoint} failed: {ex.Message}", ex);
        }

        using (response)
        {
            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Try to surface a server-side ErrorResponse if the body parses
                // as one; otherwise just report the status.
                if (responseBytes.Length > 0 && TryParseErrorMessage(responseBytes, out string? errorMessage))
                {
                    throw new QuackException(errorMessage);
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
                throw new QuackException(error.Message);
            }
            return parsed;
        }
    }

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
        return new HttpClient(handler);
    }
}

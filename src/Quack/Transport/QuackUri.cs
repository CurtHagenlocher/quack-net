namespace Quack.Transport;

// Parser/builder for `quack:host[:port]` URIs. Mirrors duckdb_quack::QuackUri.
//   - Accepts `quack:host`, `quack:host:port`, `quack:[::1]:port`, and `quack://...`.
//   - Default port is 9494.
//   - Local hosts (`localhost`, `127.0.0.1`, `::1`) default to plain HTTP;
//     everything else defaults to HTTPS. The caller can override.
public sealed class QuackUri
{
    public const int DefaultPort = 9494;

    public string Host { get; }
    public int Port { get; }
    public bool UseSsl { get; }

    public bool IsLocal => Host is "localhost" or "127.0.0.1" or "::1";

    public string CanonicalUri => Host.Contains(':')
        ? $"quack:[{Host}]:{Port}"
        : $"quack:{Host}:{Port}";

    public Uri HttpUrl
    {
        get
        {
            string scheme = UseSsl ? "https" : "http";
            string formattedHost = Host.Contains(':') ? $"[{Host}]" : Host;
            return new Uri($"{scheme}://{formattedHost}:{Port}/quack");
        }
    }

    public QuackUri(string host, int port, bool useSsl)
    {
        if (string.IsNullOrEmpty(host))
        {
            throw new ArgumentException("Host must not be empty.", nameof(host));
        }
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be in [1, 65535].");
        }
        Host = host;
        Port = port;
        UseSsl = useSsl;
    }

    public static QuackUri Parse(string uri, bool? useSsl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);

        string remainder;
        if (uri.StartsWith("quack://", StringComparison.OrdinalIgnoreCase))
        {
            remainder = uri[8..];
        }
        else if (uri.StartsWith("quack:", StringComparison.OrdinalIgnoreCase))
        {
            remainder = uri[6..];
        }
        else
        {
            throw new ArgumentException(
                $"Quack URI must start with 'quack:' or 'quack://': '{uri}'.", nameof(uri));
        }

        string host;
        int port;

        if (remainder.StartsWith('['))
        {
            int closingBracket = remainder.IndexOf(']');
            if (closingBracket < 0)
            {
                throw new ArgumentException($"Missing closing bracket in URI: '{uri}'.", nameof(uri));
            }
            host = remainder.Substring(1, closingBracket - 1);
            string rest = remainder[(closingBracket + 1)..];
            if (rest.Length == 0)
            {
                port = DefaultPort;
            }
            else if (rest.StartsWith(':') && int.TryParse(rest.AsSpan(1), out int parsedPort))
            {
                port = parsedPort;
            }
            else
            {
                throw new ArgumentException($"Invalid IPv6-form authority in URI: '{uri}'.", nameof(uri));
            }
        }
        else
        {
            int colon = remainder.LastIndexOf(':');
            if (colon < 0)
            {
                host = remainder;
                port = DefaultPort;
            }
            else
            {
                host = remainder[..colon];
                if (!int.TryParse(remainder.AsSpan(colon + 1), out port))
                {
                    throw new ArgumentException($"Invalid port in URI: '{uri}'.", nameof(uri));
                }
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            throw new ArgumentException($"Empty host in URI: '{uri}'.", nameof(uri));
        }
        if (port < 1 || port > 65535)
        {
            throw new ArgumentException($"Port {port} out of range [1, 65535] in URI: '{uri}'.", nameof(uri));
        }

        bool resolvedUseSsl = useSsl ?? !(host is "localhost" or "127.0.0.1" or "::1");
        return new QuackUri(host, port, resolvedUseSsl);
    }

    public override string ToString() => CanonicalUri;
}

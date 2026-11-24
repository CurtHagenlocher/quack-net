// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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

    // tls=skip_verify: TLS without certificate validation.
    public bool SkipCertificateVerification { get; }

    // The token from a quack://host:port/?token=... URI, if it carried one.
    // Not part of CanonicalUri or ToString, so it stays out of logs.
    public string? Token { get; }

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
        : this(host, port, useSsl, skipCertificateVerification: false, token: null)
    {
    }

    public QuackUri(string host, int port, bool useSsl, bool skipCertificateVerification, string? token)
    {
        if (skipCertificateVerification && !useSsl)
        {
            throw new ArgumentException("Skipping certificate verification needs TLS.", nameof(skipCertificateVerification));
        }
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
        SkipCertificateVerification = skipCertificateVerification;
        Token = token;
    }

    public static QuackUri Parse(string uri, bool? useSsl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);

        string remainder;
        string? query = null;
        if (uri.StartsWith("quack://", StringComparison.OrdinalIgnoreCase))
        {
            // The URL form, as the other quack ADBC drivers take it:
            // quack://host[:port][/][?token=...&tls=true|false|skip_verify]
            remainder = uri[8..];
            int questionMark = remainder.IndexOf('?');
            if (questionMark >= 0)
            {
                query = remainder[(questionMark + 1)..];
                remainder = remainder[..questionMark];
            }
            int slash = remainder.IndexOf('/');
            if (slash >= 0)
            {
                if (slash != remainder.Length - 1)
                {
                    throw new ArgumentException($"A quack URI has no path: '{uri}'.", nameof(uri));
                }
                remainder = remainder[..slash];
            }
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

        (string? token, bool? tls, bool skipVerify) = ParseQuery(uri, query);
        if (tls is not null && useSsl is not null && tls != useSsl)
        {
            throw new ArgumentException($"The URI's tls parameter contradicts useSsl={useSsl}: '{uri}'.", nameof(uri));
        }
        bool resolvedUseSsl = tls ?? useSsl ?? !(host is "localhost" or "127.0.0.1" or "::1");
        return new QuackUri(host, port, resolvedUseSsl, skipVerify, token);
    }

    // token (the first one counts) and tls; other parameters are ignored.
    private static (string? Token, bool? Tls, bool SkipVerify) ParseQuery(string uri, string? query)
    {
        string? token = null;
        bool? tls = null;
        bool skipVerify = false;
        if (string.IsNullOrEmpty(query))
        {
            return (token, tls, skipVerify);
        }
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            switch (key)
            {
                case "token":
                    token ??= value;
                    break;
                case "tls":
                    if (tls is not null)
                    {
                        throw new ArgumentException($"Duplicate tls parameter in URI: '{uri}'.", nameof(uri));
                    }
                    (tls, skipVerify) = ParseTls(value) ?? throw new ArgumentException(
                        $"Invalid tls parameter '{value}' in URI (expected true, false or skip_verify): '{uri}'.", nameof(uri));
                    break;
            }
        }
        return (token, tls, skipVerify);
    }

    // true (verified), false (plain HTTP) or skip_verify (TLS, any certificate).
    public static (bool UseSsl, bool SkipVerify)? ParseTls(string value) => value switch
    {
        "true" => (true, false),
        "false" => (false, false),
        "skip_verify" or "skip-verify" => (true, true),
        _ => null,
    };

    public override string ToString() => CanonicalUri;
}

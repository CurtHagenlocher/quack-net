// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using Apache.Arrow.Adbc;
using Quack.Transport;

namespace Quack.Adbc;

// Configuration container for an ADBC database handle. Stores the URI and
// token supplied via QuackAdbcDriver.Open and opens fresh QuackConnections
// on each Connect(...) call.
internal sealed class QuackAdbcDatabase : AdbcDatabase
{
    private readonly Dictionary<string, string> _options;

    public QuackAdbcDatabase(IReadOnlyDictionary<string, string> parameters)
    {
        _options = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
    }

    public override AdbcConnection Connect(IReadOnlyDictionary<string, string>? options)
    {
        if (options is not null)
        {
            foreach (KeyValuePair<string, string> kvp in options)
            {
                _options[kvp.Key] = kvp.Value;
            }
        }
        if (!_options.TryGetValue(QuackAdbcDriver.UriParameter, out string? uri) ||
            string.IsNullOrEmpty(uri))
        {
            throw AdbcException.NotImplemented(
                $"Missing required '{QuackAdbcDriver.UriParameter}' parameter.");
        }
        // quack://host:port/?token=...&tls=... (the other quack ADBC drivers'
        // form) carries the token and TLS mode; explicit options win.
        QuackUri parsedUri;
        try
        {
            parsedUri = QuackUri.Parse(uri);
        }
        catch (ArgumentException e)
        {
            throw new AdbcException(e.Message, AdbcStatusCode.InvalidArgument);
        }
        if (!_options.TryGetValue(QuackAdbcDriver.TokenParameter, out string? token))
        {
            token = parsedUri.Token ?? throw new AdbcException(
                $"Missing required '{QuackAdbcDriver.TokenParameter}' parameter (or a token in the URI).", AdbcStatusCode.InvalidArgument);
        }

        TimeSpan? defaultCommandTimeout = null;
        if (_options.TryGetValue(QuackAdbcDriver.CommandTimeoutSecondsParameter, out string? timeoutText) &&
            !string.IsNullOrEmpty(timeoutText))
        {
            defaultCommandTimeout = ParseCommandTimeoutSeconds(timeoutText);
        }

        bool autoReconnect = false;
        if (_options.TryGetValue(QuackAdbcDriver.ReconnectOnSessionLossParameter, out string? reconnectText) &&
            !string.IsNullOrEmpty(reconnectText))
        {
            if (!bool.TryParse(reconnectText, out autoReconnect))
            {
                throw AdbcException.NotImplemented(
                    $"Invalid '{QuackAdbcDriver.ReconnectOnSessionLossParameter}' value '{reconnectText}'; expected 'true' or 'false'.");
            }
        }

        // Unset leaves the choice to QuackUri.Parse: plain HTTP for a loopback
        // host, TLS otherwise.
        bool? useSSL = null;
        if (_options.TryGetValue(QuackAdbcDriver.UseSSLParameter, out string? useSSLText) &&
            !string.IsNullOrEmpty(useSSLText))
        {
            if (!bool.TryParse(useSSLText, out bool parsedUseSSL))
            {
                throw AdbcException.NotImplemented(
                    $"Invalid '{QuackAdbcDriver.UseSSLParameter}' value '{useSSLText}'; expected 'true' or 'false'.");
            }
            useSSL = parsedUseSSL;
        }

        // ADBC's contract is synchronous; sync-over-async is acceptable here
        // because OpenAsync is a small handshake (no streaming) and there's
        // no SynchronizationContext to deadlock against in typical hosts.
        bool skipVerify = parsedUri.SkipCertificateVerification;
        if (_options.TryGetValue(QuackAdbcDriver.TlsParameter, out string? tlsText) && !string.IsNullOrEmpty(tlsText))
        {
            (bool useTls, skipVerify) = QuackUri.ParseTls(tlsText) ?? throw new AdbcException(
                $"Invalid '{QuackAdbcDriver.TlsParameter}' value '{tlsText}'; expected 'true', 'false' or 'skip_verify'.", AdbcStatusCode.InvalidArgument);
            if (useSSL is not null && useSSL != useTls)
            {
                throw new AdbcException($"'{QuackAdbcDriver.TlsParameter}' contradicts '{QuackAdbcDriver.UseSSLParameter}'.", AdbcStatusCode.InvalidArgument);
            }
            useSSL = useTls;
        }
        QuackUri target;
        try
        {
            QuackUri resolved = QuackUri.Parse(uri, useSSL);
            target = new QuackUri(resolved.Host, resolved.Port, resolved.UseSsl, skipVerify && resolved.UseSsl, resolved.Token);
        }
        catch (ArgumentException e)
        {
            throw new AdbcException(e.Message, AdbcStatusCode.InvalidArgument);
        }
        QuackConnection connection;
        try
        {
            connection = QuackConnection
                .OpenAsync(target, token, autoReconnect: autoReconnect)
                .GetAwaiter()
                .GetResult();
        }
        catch (QuackException e)
        {
            throw QuackAdbcException.From(e);
        }
        return new QuackAdbcConnection(connection, defaultCommandTimeout);
    }

    private static readonly HashSet<string> KnownOptions =
    [
        QuackAdbcDriver.UriParameter,
        QuackAdbcDriver.TokenParameter,
        QuackAdbcDriver.UseSSLParameter,
        QuackAdbcDriver.TlsParameter,
        QuackAdbcDriver.CommandTimeoutSecondsParameter,
        QuackAdbcDriver.ReconnectOnSessionLossParameter,
    ];

    // An option the driver does not know is NOT_IMPLEMENTED, as ADBC asks.
    public override void SetOption(string key, string value)
    {
        if (!KnownOptions.Contains(key))
        {
            throw new AdbcException($"Unknown database option '{key}'.", AdbcStatusCode.NotImplemented);
        }
        _options[key] = value;
    }

    // The options as set; the token is write-only.
    public override string GetOption(string key)
    {
        if (!string.Equals(key, QuackAdbcDriver.TokenParameter, StringComparison.Ordinal) &&
            _options.TryGetValue(key, out string? value))
        {
            return value;
        }
        throw new AdbcException($"Option '{key}' is not set or can't be read.", AdbcStatusCode.NotFound);
    }

    internal static TimeSpan ParseCommandTimeoutSeconds(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds <= 0)
        {
            throw AdbcException.NotImplemented(
                $"Invalid '{QuackAdbcDriver.CommandTimeoutSecondsParameter}' value '{text}'; expected a positive number of seconds.");
        }
        return TimeSpan.FromSeconds(seconds);
    }
}

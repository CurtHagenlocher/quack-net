using System.Globalization;
using Apache.Arrow.Adbc;

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
        if (!_options.TryGetValue(QuackAdbcDriver.TokenParameter, out string? token))
        {
            throw AdbcException.NotImplemented(
                $"Missing required '{QuackAdbcDriver.TokenParameter}' parameter.");
        }

        TimeSpan? defaultCommandTimeout = null;
        if (_options.TryGetValue(QuackAdbcDriver.CommandTimeoutSecondsParameter, out string? timeoutText) &&
            !string.IsNullOrEmpty(timeoutText))
        {
            defaultCommandTimeout = ParseCommandTimeoutSeconds(timeoutText);
        }

        // ADBC's contract is synchronous; sync-over-async is acceptable here
        // because OpenAsync is a small handshake (no streaming) and there's
        // no SynchronizationContext to deadlock against in typical hosts.
        QuackConnection connection = QuackConnection
            .OpenAsync(uri, token)
            .GetAwaiter()
            .GetResult();
        return new QuackAdbcConnection(connection, defaultCommandTimeout);
    }

    public override void SetOption(string key, string value)
    {
        _options[key] = value;
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

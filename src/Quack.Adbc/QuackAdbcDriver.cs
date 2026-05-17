using Apache.Arrow.Adbc;

namespace Quack.Adbc;

// ADBC entry point for the quack remote protocol.
//
// Required parameter keys passed to Open(...):
//   uri    — e.g. "quack:127.0.0.1:9494"
//   token  — the auth token expected by the server's quack_serve(...)
//
// Optional parameter keys:
//   command_timeout_seconds — best-effort per-query timeout (positive
//                             integer or decimal seconds). When it fires,
//                             ExecuteQuery/ExecuteUpdate throws and the
//                             ADBC connection becomes unusable — callers
//                             must dispose and reopen. The server-side
//                             query keeps running to completion because
//                             quack v1.5-variegata has no cancel message.
//                             May be overridden per-statement via
//                             AdbcStatement.SetOption.
//
// The driver itself is stateless; per-database state lives on
// QuackAdbcDatabase.
public sealed class QuackAdbcDriver : AdbcDriver
{
    public const string UriParameter = "uri";
    public const string TokenParameter = "token";
    public const string CommandTimeoutSecondsParameter = "command_timeout_seconds";

    public override AdbcDatabase Open(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new QuackAdbcDatabase(parameters);
    }
}

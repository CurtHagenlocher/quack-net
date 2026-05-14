using Apache.Arrow.Adbc;

namespace Quack.Adbc;

// ADBC entry point for the quack remote protocol.
//
// Required parameter keys passed to Open(...):
//   uri    — e.g. "quack:127.0.0.1:9494"
//   token  — the auth token expected by the server's quack_serve(...)
//
// The driver itself is stateless; per-database state lives on
// QuackAdbcDatabase.
public sealed class QuackAdbcDriver : AdbcDriver
{
    public const string UriParameter = "uri";
    public const string TokenParameter = "token";

    public override AdbcDatabase Open(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new QuackAdbcDatabase(parameters);
    }
}

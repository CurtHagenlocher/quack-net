// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Quack.Adbc;

// ADBC entry point for the quack remote protocol.
//
// Required parameter keys passed to Open(...):
//   uri    — e.g. "quack:127.0.0.1:9494"
//   token  — the auth token expected by the server's quack_serve(...)
//
// Optional parameter keys:
//   command_timeout_seconds — per-query timeout (positive integer or
//                             decimal seconds). When it fires, the driver
//                             cancels the query on the server and
//                             ExecuteQuery/ExecuteUpdate throws a
//                             TimeoutException; the ADBC connection stays
//                             usable. Only if the server does not stop the
//                             query within 10 s is the request aborted
//                             locally, and then the connection is unusable
//                             (the exception says which).
//                             May be overridden per-statement via
//                             AdbcStatement.SetOption.
//   reconnect_on_session_loss — "true"/"false" (default "false"). When
//                             true, ExecuteQuery/ExecuteUpdate transparently
//                             re-handshakes with the server if it reports
//                             the connection_id is unknown (typical after
//                             a server restart). Silently drops any
//                             server-side session state (open transactions,
//                             SET variables, ATTACH, temp tables); safe
//                             only for stateless query workloads. Fetches
//                             on an existing result set are never
//                             auto-recovered.
//
// The driver itself is stateless; per-database state lives on
// QuackAdbcDatabase.
public sealed class QuackAdbcDriver : AdbcDriver
{
    public const string UriParameter = "uri";
    public const string TokenParameter = "token";
    public const string CommandTimeoutSecondsParameter = "command_timeout_seconds";

    // TLS mode, as the other quack ADBC drivers name it: true (verified),
    // false (plain HTTP) or skip_verify (TLS, any certificate). Also accepted
    // as tls=... in a quack://host:port/?... URI.
    public const string TlsParameter = "quack.tls";
    public const string ReconnectOnSessionLossParameter = "reconnect_on_session_loss";
    public const string UseSSLParameter = "use_ssl";

    // Result types as DuckDB's arrow_lossless_conversion exports them:
    // HUGEINT, UHUGEINT, TIME_TZ and BIT as arrow.opaque, UUID as arrow.uuid.
    // Off (the default): DuckDB's default Arrow types. A database or
    // connection option.
    public const string ArrowLosslessConversionParameter = "quack.arrow_lossless_conversion";

    public override AdbcDatabase Open(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new QuackAdbcDatabase(parameters);
    }
}

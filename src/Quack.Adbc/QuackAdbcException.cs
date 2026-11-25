// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow.Adbc;

namespace Quack.Adbc;

// A quack error as an ADBC error: the status from DuckDB's exception type,
// and the server's exception type, must_invalidate and extra_info as ADBC
// error details (quack.exception_type, quack.must_invalidate,
// quack.extra_info.<key>). The QuackException is the inner exception.
internal sealed class QuackAdbcException : AdbcException
{
    private readonly IReadOnlyList<KeyValuePair<string, byte[]>> _details;

    private QuackAdbcException(QuackException error, AdbcStatusCode status)
        : base(error.Message, status, error)
    {
        List<KeyValuePair<string, byte[]>> details = [];
        if (error.ServerExceptionType is { } type)
        {
            details.Add(new("quack.exception_type", Encoding.UTF8.GetBytes(type)));
        }
        if (error.ServerMustInvalidate)
        {
            details.Add(new("quack.must_invalidate", Encoding.UTF8.GetBytes("true")));
        }
        if (error.ServerExtraInfo is { } extra)
        {
            foreach (KeyValuePair<string, string> entry in extra)
            {
                details.Add(new("quack.extra_info." + entry.Key, Encoding.UTF8.GetBytes(entry.Value)));
            }
        }
        _details = details;
    }

    public override IReadOnlyList<KeyValuePair<string, byte[]>> Details => _details;

    public static QuackAdbcException From(QuackException error) => new(error, StatusOf(error));

    // For an append (append / create_append): a table whose columns do not
    // take the data is ADBC's ALREADY_EXISTS, as the spec defines it.
    public static QuackAdbcException FromAppend(QuackException error)
        => error.ServerExceptionType is "Binder" or "Conversion" or "Mismatch Type"
            ? new(error, AdbcStatusCode.AlreadyExists)
            : From(error);

    private static AdbcStatusCode StatusOf(QuackException error)
    {
        if (error is QuackSessionExpiredException)
        {
            return AdbcStatusCode.InvalidState;
        }
        string message = error.Message;
        return error.ServerExceptionType switch
        {
            "Catalog" when message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => AdbcStatusCode.AlreadyExists,
            "Catalog" => AdbcStatusCode.NotFound,
            "Constraint" => AdbcStatusCode.IntegrityError,
            "INTERRUPT" => AdbcStatusCode.Cancelled,
            "Parser" or "Syntax" or "Binder" or "Parameter Not Resolved" or "Parameter Not Allowed" => AdbcStatusCode.InvalidArgument,
            "Conversion" or "Out of Range" or "Invalid Input" or "Mismatch Type" or "Divide by Zero" or "Decimal" => AdbcStatusCode.InvalidData,
            "TransactionContext" => AdbcStatusCode.InvalidState,
            "Permission" => AdbcStatusCode.Unauthorized,
            "IO" or "HTTP" or "Connection" => AdbcStatusCode.IOError,
            "Not implemented" => AdbcStatusCode.NotImplemented,
            "INTERNAL" or "FATAL" or "Data Corruption" => AdbcStatusCode.InternalError,
            // No server answer: the HTTP request failed (connection, TLS handshake) or returned an HTTP error.
            null when error.HttpStatusCode is not null || error.InnerException is HttpRequestException or IOException => AdbcStatusCode.IOError,
            _ => AdbcStatusCode.UnknownError,
        };
    }
}

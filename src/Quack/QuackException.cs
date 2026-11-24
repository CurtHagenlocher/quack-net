// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack;

public class QuackException : Exception
{
    public QuackException(string message)
        : base(message)
    {
    }

    public QuackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    // The DuckDB exception type the server raised ("Catalog", "Binder", ...),
    // when the error came from an ErrorResponse that carried one.
    public string? ServerExceptionType { get; init; }

    // ErrorData extra info sent with the server's error.
    public IReadOnlyDictionary<string, string>? ServerExtraInfo { get; init; }

    // The server's own database was invalidated by this error.
    public bool ServerMustInvalidate { get; init; }
}

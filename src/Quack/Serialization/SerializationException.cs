// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Serialization;

internal sealed class SerializationException : Exception
{
    public SerializationException(string message)
        : base(message)
    {
    }

    public SerializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

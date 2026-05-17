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
}

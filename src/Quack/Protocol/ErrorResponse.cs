// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record ErrorResponse : QuackMessage
{
    public override MessageType MessageType => MessageType.ErrorResponse;

    public string Message { get; init; } = string.Empty;

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, Message);
    }

    internal static ErrorResponse DeserializeBody(BinaryDeserializer d)
    {
        string message = string.Empty;
        if (d.TryBeginProperty(fieldId: 1)) message = d.ReadString();
        return new ErrorResponse { Message = message };
    }
}

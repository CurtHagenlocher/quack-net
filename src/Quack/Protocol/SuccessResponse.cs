// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record SuccessResponse : QuackMessage
{
    public override MessageType MessageType => MessageType.SuccessResponse;

    internal override void SerializeBody(BinarySerializer s)
    {
        // Empty body.
    }

    internal static SuccessResponse DeserializeBody(BinaryDeserializer d) => new();
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record DisconnectMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.DisconnectMessage;

    internal override void SerializeBody(BinarySerializer s)
    {
        // Empty body.
    }

    internal static DisconnectMessage DeserializeBody(BinaryDeserializer d) => new();
}

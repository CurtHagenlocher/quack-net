// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Renews the connection's lease. Every request renews it; this one exists for
// idle connections. Success is a SuccessResponse.
public sealed record HeartbeatRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.HeartbeatRequest;

    internal override void SerializeBody(BinarySerializer s)
    {
        // Empty body.
    }

    internal static HeartbeatRequestMessage DeserializeBody(BinaryDeserializer d) => new();
}

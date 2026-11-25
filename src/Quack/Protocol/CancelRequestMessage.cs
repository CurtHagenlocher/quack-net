// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Interrupts the query running on a connection. QueryUuid 0 is a wildcard
// (whatever runs now); any other id must name the running query, or the server
// answers with an ErrorResponse. Success is a SuccessResponse.
public sealed record CancelRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.CancelRequest;

    public Int128 QueryUuid { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WriteProperty(fieldId: 1, QueryUuid);
    }

    internal static CancelRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        d.BeginProperty(fieldId: 1);
        return new CancelRequestMessage { QueryUuid = d.ReadHugeInt() };
    }
}

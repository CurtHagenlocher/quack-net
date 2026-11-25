// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Tells the server the client received the full result of QueryUuid, so it can
// drop the result it retained for replay (only kept when server-side result
// caching is on; otherwise a no-op). Success is a SuccessResponse.
public sealed record AcknowledgementMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.Acknowledgement;

    public Int128 QueryUuid { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        // WritePropertyWithDefault<hugeint_t>(..., hugeint_t(0, 0))
        if (QueryUuid != Int128.Zero)
        {
            s.WriteProperty(fieldId: 1, QueryUuid);
        }
    }

    internal static AcknowledgementMessage DeserializeBody(BinaryDeserializer d)
    {
        Int128 queryUuid = Int128.Zero;
        if (d.TryBeginProperty(fieldId: 1)) queryUuid = d.ReadHugeInt();
        return new AcknowledgementMessage { QueryUuid = queryUuid };
    }
}

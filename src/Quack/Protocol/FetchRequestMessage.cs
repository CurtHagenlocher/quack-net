// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Asks for one dense batch of a result. Batches are numbered from 1, after the
// ones the PREPARE response carried inline. Because every request names its
// batch, a retried request gets the same batch back.
public sealed record FetchRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.FetchRequest;

    public Int128 QueryUuid { get; init; }
    public ulong BatchIndex { get; init; }
    // Batches 1..AckIndex all arrived; the server may drop them.
    public ulong AckIndex { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WriteProperty(fieldId: 1, QueryUuid);
        s.WritePropertyWithDefault(fieldId: 2, BatchIndex);
        s.WritePropertyWithDefault(fieldId: 3, AckIndex);
    }

    internal static FetchRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        d.BeginProperty(fieldId: 1);
        Int128 queryUuid = d.ReadHugeInt();
        ulong batchIndex = 0;
        ulong ackIndex = 0;
        if (d.TryBeginProperty(fieldId: 2)) batchIndex = d.ReadUInt64();
        if (d.TryBeginProperty(fieldId: 3)) ackIndex = d.ReadUInt64();
        return new FetchRequestMessage { QueryUuid = queryUuid, BatchIndex = batchIndex, AckIndex = ackIndex };
    }
}

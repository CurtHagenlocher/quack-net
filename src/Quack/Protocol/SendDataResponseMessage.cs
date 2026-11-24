// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Success reply to a SendDataRequestMessage. AcceptBudget is a placeholder for
// future flow control (null = unbounded); clients ignore it today.
public sealed record SendDataResponseMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.SendDataResponse;

    public ulong? AcceptBudget { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        OptionalIdx.WriteProperty(s, fieldId: 1, AcceptBudget);
    }

    internal static SendDataResponseMessage DeserializeBody(BinaryDeserializer d)
    {
        d.BeginProperty(fieldId: 1);
        return new SendDataResponseMessage { AcceptBudget = OptionalIdx.Read(d) };
    }
}

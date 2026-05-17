// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using Quack.Serialization;

namespace Quack.Protocol;

// Base class for every quack protocol message. On the wire each message is two
// top-level objects:
//   1) MessageHeader  (object, 0xFFFF terminator)
//   2) body fields    (object, 0xFFFF terminator)
public abstract record QuackMessage
{
    public abstract MessageType MessageType { get; }

    public string ConnectionId { get; init; } = string.Empty;
    public ulong? ClientQueryId { get; init; }

    internal MessageHeader BuildHeader() => new()
    {
        Type = MessageType,
        ConnectionId = ConnectionId,
        ClientQueryId = ClientQueryId,
    };

    public void Serialize(IBufferWriter<byte> output)
    {
        BinarySerializer s = new(output);
        s.BeginObject();
        BuildHeader().Serialize(s);
        s.EndObject();
        s.BeginObject();
        SerializeBody(s);
        s.EndObject();
    }

    public byte[] ToBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        Serialize(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    internal abstract void SerializeBody(BinarySerializer s);

    public static QuackMessage Deserialize(ReadOnlyMemory<byte> bytes)
    {
        BinaryDeserializer d = new(bytes);
        d.BeginObject();
        MessageHeader header = MessageHeader.Deserialize(d);
        d.EndObject();
        d.BeginObject();
        QuackMessage body = DeserializeBody(header.Type, d);
        d.EndObject();

        // Propagate header fields onto the body via with-style copies.
        return body switch
        {
            ConnectionResponseMessage cr => cr with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            PrepareResponseMessage pr => pr with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            FetchResponseMessage fr => fr with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            SuccessResponse sr => sr with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            ErrorResponse er => er with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            ConnectionRequestMessage cq => cq with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            PrepareRequestMessage pq => pq with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            FetchRequestMessage fq => fq with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            AppendRequestMessage aq => aq with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            DisconnectMessage dm => dm with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId },
            _ => body,
        };
    }

    private static QuackMessage DeserializeBody(MessageType type, BinaryDeserializer d) => type switch
    {
        MessageType.ConnectionRequest => ConnectionRequestMessage.DeserializeBody(d),
        MessageType.ConnectionResponse => ConnectionResponseMessage.DeserializeBody(d),
        MessageType.PrepareRequest => PrepareRequestMessage.DeserializeBody(d),
        MessageType.PrepareResponse => PrepareResponseMessage.DeserializeBody(d),
        MessageType.FetchRequest => FetchRequestMessage.DeserializeBody(d),
        MessageType.FetchResponse => FetchResponseMessage.DeserializeBody(d),
        MessageType.AppendRequest => AppendRequestMessage.DeserializeBody(d),
        MessageType.SuccessResponse => SuccessResponse.DeserializeBody(d),
        MessageType.DisconnectMessage => DisconnectMessage.DeserializeBody(d),
        MessageType.ErrorResponse => ErrorResponse.DeserializeBody(d),
        _ => throw new SerializationException($"Unsupported message type '{type}' ({(int)type})."),
    };
}

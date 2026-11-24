// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

// Base class for every quack protocol message. On the wire each message is two
// top-level objects:
//   1) MessageHeader  (object, 0xFFFF terminator)
//   2) body fields    (object, 0xFFFF terminator)
// A chunk-carrying message (FETCH_RESPONSE, SEND_DATA_REQUEST) continues with
// its raw blob: chunk_count DataChunk objects, each with its own terminator.
//
// The wire serialization matches StorageVersion V2_0_0 (quack version 3).
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
        if (this is IChunkBlobMessage blob)
        {
            foreach (DuckDbChunk chunk in blob.Chunks)
            {
                if (chunk.RowCount == 0)
                {
                    throw new SerializationException("A quack chunk blob must not contain an empty chunk.");
                }
                DataChunkWriter.Write(s, chunk);
            }
        }
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
        QuackMessage body = DeserializeBody(header.Type, d, out ulong chunkCount);
        d.EndObject();
        body = AttachBlob(body, ReadChunkBlob(d, chunkCount));

        // Propagate header fields onto the body.
        return body with { ConnectionId = header.ConnectionId, ClientQueryId = header.ClientQueryId };
    }

    private static List<DuckDbChunk> ReadChunkBlob(BinaryDeserializer d, ulong chunkCount)
    {
        // The count comes from the wire: cap the reservation, a false count fails on the read.
        List<DuckDbChunk> chunks = new((int)Math.Min(chunkCount, 1024));
        for (ulong i = 0; i < chunkCount; i++)
        {
            chunks.Add(DataChunkReader.Read(d));
        }
        return chunks;
    }

    private static QuackMessage AttachBlob(QuackMessage body, List<DuckDbChunk> chunks) => body switch
    {
        FetchResponseMessage fr => fr with { Results = chunks },
        SendDataRequestMessage sd => sd with { Chunks = chunks },
        _ => body,
    };

    private static QuackMessage DeserializeBody(MessageType type, BinaryDeserializer d, out ulong chunkCount)
    {
        chunkCount = 0;
        switch (type)
        {
            case MessageType.FetchResponse:
                return FetchResponseMessage.DeserializeBody(d, out chunkCount);
            case MessageType.SendDataRequest:
                return SendDataRequestMessage.DeserializeBody(d, out chunkCount);
            default:
                return type switch
                {
                    MessageType.ConnectionRequest => ConnectionRequestMessage.DeserializeBody(d),
                    MessageType.ConnectionResponse => ConnectionResponseMessage.DeserializeBody(d),
                    MessageType.PrepareRequest => PrepareRequestMessage.DeserializeBody(d),
                    MessageType.PrepareResponse => PrepareResponseMessage.DeserializeBody(d),
                    MessageType.FetchRequest => FetchRequestMessage.DeserializeBody(d),
                    MessageType.SendDataResponse => SendDataResponseMessage.DeserializeBody(d),
                    MessageType.SuccessResponse => SuccessResponse.DeserializeBody(d),
                    MessageType.DisconnectMessage => DisconnectMessage.DeserializeBody(d),
                    MessageType.CancelRequest => CancelRequestMessage.DeserializeBody(d),
                    MessageType.Acknowledgement => AcknowledgementMessage.DeserializeBody(d),
                    MessageType.HeartbeatRequest => HeartbeatRequestMessage.DeserializeBody(d),
                    MessageType.ErrorResponse => ErrorResponse.DeserializeBody(d),
                    _ => throw new SerializationException($"Unsupported message type '{type}' ({(int)type})."),
                };
        }
    }
}

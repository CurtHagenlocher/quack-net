// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// Mirror of duckdb_quack::MessageHeader. Wire layout (one top-level object):
//   field 1 "type"            -- MessageType, uint8 -> LEB128 (required)
//   field 2 "connection_id"   -- string (default-omit; empty for ConnectionRequest)
//   field 3 "client_query_id" -- optional_idx: a single uint64 LEB128. The
//                                sentinel value `(idx_t)-1` = ulong.MaxValue
//                                stands for "not valid".
public sealed class MessageHeader
{
    internal const ulong OptionalIdxInvalid = ulong.MaxValue;

    public MessageType Type { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public ulong? ClientQueryId { get; init; }

    internal void Serialize(BinarySerializer s)
    {
        s.WriteProperty(fieldId: 1, (byte)Type);
        s.WritePropertyWithDefault(fieldId: 2, ConnectionId, string.Empty);
        s.WriteProperty(fieldId: 3, ClientQueryId ?? OptionalIdxInvalid);
    }

    internal static MessageHeader Deserialize(BinaryDeserializer d)
    {
        d.BeginProperty(fieldId: 1);
        MessageType type = (MessageType)d.ReadByte();

        string connectionId = string.Empty;
        if (d.TryBeginProperty(fieldId: 2))
        {
            connectionId = d.ReadString();
        }

        d.BeginProperty(fieldId: 3);
        ulong raw = d.ReadUInt64();
        ulong? clientQueryId = raw == OptionalIdxInvalid ? null : raw;

        return new MessageHeader
        {
            Type = type,
            ConnectionId = connectionId,
            ClientQueryId = clientQueryId,
        };
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record ConnectionRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.ConnectionRequest;

    public string AuthString { get; init; } = string.Empty;
    public string ClientDuckDbVersion { get; init; } = string.Empty;
    public string ClientPlatform { get; init; } = string.Empty;
    public ulong MinSupportedQuackVersion { get; init; }
    public ulong MaxSupportedQuackVersion { get; init; }
    // Optional. Empty means none; otherwise at least 4 characters. The server
    // records only a hash of it, in its request log.
    public string ClientId { get; init; } = string.Empty;
    // The lease the server grants this connection; any request renews it.
    // Must be in [1, QuackConnection.MaxHeartbeatTimeoutSeconds].
    public ulong HeartbeatTimeoutSeconds { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, AuthString);
        s.WritePropertyWithDefault(fieldId: 2, ClientDuckDbVersion);
        s.WritePropertyWithDefault(fieldId: 3, ClientPlatform);
        s.WritePropertyWithDefault(fieldId: 4, MinSupportedQuackVersion);
        s.WritePropertyWithDefault(fieldId: 5, MaxSupportedQuackVersion);
        s.WritePropertyWithDefault(fieldId: 6, ClientId);
        s.WritePropertyWithDefault(fieldId: 7, HeartbeatTimeoutSeconds);
    }

    internal static ConnectionRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        string authString = string.Empty;
        string clientDuckDbVersion = string.Empty;
        string clientPlatform = string.Empty;
        ulong minVersion = 0;
        ulong maxVersion = 0;
        string clientId = string.Empty;
        ulong heartbeatTimeoutSeconds = 0;

        if (d.TryBeginProperty(fieldId: 1)) authString = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) clientDuckDbVersion = d.ReadString();
        if (d.TryBeginProperty(fieldId: 3)) clientPlatform = d.ReadString();
        if (d.TryBeginProperty(fieldId: 4)) minVersion = d.ReadUInt64();
        if (d.TryBeginProperty(fieldId: 5)) maxVersion = d.ReadUInt64();
        if (d.TryBeginProperty(fieldId: 6)) clientId = d.ReadString();
        if (d.TryBeginProperty(fieldId: 7)) heartbeatTimeoutSeconds = d.ReadUInt64();

        return new ConnectionRequestMessage
        {
            AuthString = authString,
            ClientDuckDbVersion = clientDuckDbVersion,
            ClientPlatform = clientPlatform,
            MinSupportedQuackVersion = minVersion,
            MaxSupportedQuackVersion = maxVersion,
            ClientId = clientId,
            HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds,
        };
    }
}

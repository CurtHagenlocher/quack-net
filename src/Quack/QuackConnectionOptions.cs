// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack;

// Connection-level settings sent in the ConnectionRequest.
public sealed record QuackConnectionOptions
{
    public static QuackConnectionOptions Default { get; } = new();

    // The lease requested from the server. The client heartbeats about every
    // third of it; a session not heard from for this long is reaped.
    public ulong HeartbeatTimeoutSeconds { get; init; } = QuackConnection.DefaultHeartbeatTimeoutSeconds;

    // Optional client identity for the server's request log (it stores a hash).
    // Empty means none; otherwise at least 4 characters.
    public string ClientId { get; init; } = string.Empty;

    internal void Validate()
    {
        if (HeartbeatTimeoutSeconds is 0 or > QuackConnection.MaxHeartbeatTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatTimeoutSeconds),
                $"HeartbeatTimeoutSeconds must be between 1 and {QuackConnection.MaxHeartbeatTimeoutSeconds}.");
        }
        ArgumentNullException.ThrowIfNull(ClientId);
        if (ClientId.Length is > 0 and < 4)
        {
            throw new ArgumentException("ClientId must be empty or at least 4 characters long.", nameof(ClientId));
        }
    }
}

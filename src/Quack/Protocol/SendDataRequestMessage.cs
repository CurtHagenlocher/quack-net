// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

// Client -> server: one dense batch of a client data stream that a statement
// reads with scan_data_from_quack_client(stream_id, ...). Batches are numbered
// from 1 and may arrive in any order; the server drops a repeated index, so a
// retry is safe. The client ends the stream with a chunk-less message that
// carries TotalBatches, so a lost batch fails the statement.
//
// Replaces quack version 1's APPEND_REQUEST (schema, table, chunk).
public sealed record SendDataRequestMessage : QuackMessage, IChunkBlobMessage
{
    public override MessageType MessageType => MessageType.SendDataRequest;

    public string StreamId { get; init; } = string.Empty;
    public IReadOnlyList<DuckDbChunk> Chunks { get; init; } = [];
    // Set on every batch, unset on the terminal message.
    public ulong? BatchIndex { get; init; }
    // Set on the terminal message only.
    public ulong? TotalBatches { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, StreamId);
        s.WritePropertyWithDefault(fieldId: 2, (ulong)Chunks.Count);
        OptionalIdx.WritePropertyWithDefault(s, fieldId: 3, TotalBatches);
        OptionalIdx.WritePropertyWithDefault(s, fieldId: 4, BatchIndex);
    }

    internal static SendDataRequestMessage DeserializeBody(BinaryDeserializer d, out ulong chunkCount)
    {
        string streamId = string.Empty;
        chunkCount = 0;
        if (d.TryBeginProperty(fieldId: 1)) streamId = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) chunkCount = d.ReadUInt64();
        ulong? totalBatches = OptionalIdx.ReadPropertyWithDefault(d, fieldId: 3);
        ulong? batchIndex = OptionalIdx.ReadPropertyWithDefault(d, fieldId: 4);
        return new SendDataRequestMessage { StreamId = streamId, TotalBatches = totalBatches, BatchIndex = batchIndex };
    }
}

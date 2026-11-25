// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

// One dense batch of a result. The body holds only the counts; the chunks
// follow the body as a raw blob of ChunkCount self-describing DataChunk
// objects. A response with no chunks is terminal and carries TotalBatches, so
// the client can check that it received every batch.
public sealed record FetchResponseMessage : QuackMessage, IChunkBlobMessage
{
    public override MessageType MessageType => MessageType.FetchResponse;

    public IReadOnlyList<DuckDbChunk> Results { get; init; } = [];
    public ulong? BatchIndex { get; init; }
    public ulong? TotalBatches { get; init; }

    public bool IsEndOfStream => Results.Count == 0;

    IReadOnlyList<DuckDbChunk> IChunkBlobMessage.Chunks => Results;

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, (ulong)Results.Count);
        OptionalIdx.WritePropertyWithDefault(s, fieldId: 2, TotalBatches);
        OptionalIdx.WritePropertyWithDefault(s, fieldId: 3, BatchIndex);
    }

    internal static FetchResponseMessage DeserializeBody(BinaryDeserializer d, out ulong chunkCount)
    {
        chunkCount = 0;
        if (d.TryBeginProperty(fieldId: 1)) chunkCount = d.ReadUInt64();
        ulong? totalBatches = OptionalIdx.ReadPropertyWithDefault(d, fieldId: 2);
        ulong? batchIndex = OptionalIdx.ReadPropertyWithDefault(d, fieldId: 3);
        return new FetchResponseMessage { TotalBatches = totalBatches, BatchIndex = batchIndex };
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;

namespace Quack.Protocol;

// A message whose chunks travel as a raw blob after the body (FETCH_RESPONSE,
// SEND_DATA_REQUEST): the body carries chunk_count, then each chunk follows as
// one top-level DataChunk object (fields 100-102, 0xFFFF terminator).
internal interface IChunkBlobMessage
{
    IReadOnlyList<DuckDbChunk> Chunks { get; }
}

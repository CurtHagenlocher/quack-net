// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Write path for one DataChunk. Mirrors duckdb::DataChunk::Serialize.
internal static class DataChunkWriter
{
    public static void Write(BinarySerializer s, DuckDbChunk chunk)
    {
        if (chunk.Columns.Count != chunk.Types.Count)
        {
            throw new ArgumentException(
                $"Chunk has {chunk.Columns.Count} columns but {chunk.Types.Count} types.", nameof(chunk));
        }

        s.BeginObject();
        s.WriteProperty(fieldId: 100, (uint)chunk.RowCount);

        s.WriteFieldId(101);
        s.BeginList((ulong)chunk.Types.Count);
        foreach (LogicalType type in chunk.Types)
        {
            // list.WriteElement(LogicalType) uses WriteValue<T-has-Serialize>
            // which wraps the type in its own object.
            s.BeginObject();
            type.Serialize(s);
            s.EndObject();
        }
        s.EndList();

        s.WriteFieldId(102);
        s.BeginList((ulong)chunk.Columns.Count);
        for (int i = 0; i < chunk.Columns.Count; i++)
        {
            // list.WriteObject(lambda) wraps each column in its own object.
            s.BeginObject();
            VectorWriter.Write(s, chunk.Columns[i], chunk.RowCount);
            s.EndObject();
        }
        s.EndList();

        s.EndObject();
    }
}

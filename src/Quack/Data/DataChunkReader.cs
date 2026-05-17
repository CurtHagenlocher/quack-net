// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Port of duckdb::DataChunk::Deserialize. The chunk is one object containing:
//   field 100 "rows"    -- uint32 (sel_t) LEB128
//   field 101 "types"   -- list<LogicalType>
//   field 102 "columns" -- list of objects, each containing Vector::Deserialize
internal static class DataChunkReader
{
    public static DuckDbChunk Read(BinaryDeserializer reader)
    {
        reader.BeginObject();

        reader.BeginProperty(fieldId: 100);
        int rowCount = checked((int)reader.ReadUInt32());

        reader.BeginProperty(fieldId: 101);
        ulong typesCount = reader.BeginList();
        LogicalType[] types = new LogicalType[checked((int)typesCount)];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = LogicalType.Deserialize(reader);
        }
        reader.EndList();

        DuckDbColumn[] columns;
        if (reader.TryBeginProperty(fieldId: 102))
        {
            ulong columnsCount = reader.BeginList();
            if (columnsCount != typesCount)
            {
                throw new SerializationException(
                    $"DataChunk has {typesCount} types but {columnsCount} columns.");
            }
            columns = new DuckDbColumn[(int)columnsCount];
            for (int i = 0; i < columns.Length; i++)
            {
                reader.BeginObject();
                columns[i] = VectorReader.Read(reader, types[i], rowCount);
                reader.EndObject();
            }
            reader.EndList();
        }
        else
        {
            // DuckDB asserts non-empty columns, but defensively handle the
            // empty case for forward compatibility.
            columns = [];
        }

        reader.EndObject();

        return new DuckDbChunk
        {
            Types = types,
            Columns = columns,
            RowCount = rowCount,
        };
    }
}

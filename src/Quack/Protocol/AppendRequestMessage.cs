// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

// Client -> server: append a DataChunk's rows to (schema.)table. Wire format
// per duckdb-quack's quack_message.json (field ids 1/2/3).
//
// Server responds with SuccessResponse on success or ErrorResponse on
// authorization/parse/type errors.
public sealed record AppendRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.AppendRequest;

    public string SchemaName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public required DuckDbChunk AppendChunk { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, SchemaName);
        s.WritePropertyWithDefault(fieldId: 2, TableName);
        // field 3 append_chunk: WritePropertyWithDefault<unique_ptr<DataChunkWrapper>>
        // emits the field id followed by the unique_ptr value (a nullable
        // wrapper around a has-Serialize T). DataChunkWrapper.WriteChunk
        // handles the nullable + outer-object framing.
        s.WriteFieldId(3);
        DataChunkWrapper.WriteChunk(s, AppendChunk);
    }

    internal static AppendRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        string schema = string.Empty;
        string table = string.Empty;
        if (d.TryBeginProperty(fieldId: 1)) schema = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) table = d.ReadString();

        DuckDbChunk? chunk = null;
        if (d.TryBeginProperty(fieldId: 3))
        {
            chunk = DataChunkWrapper.ReadChunk(d);
        }
        if (chunk is null)
        {
            throw new SerializationException(
                "AppendRequestMessage missing required append_chunk (field 3).");
        }

        return new AppendRequestMessage
        {
            SchemaName = schema,
            TableName = table,
            AppendChunk = chunk,
        };
    }
}

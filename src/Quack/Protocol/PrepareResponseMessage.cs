// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Protocol;

public sealed record PrepareResponseMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.PrepareResponse;

    public IReadOnlyList<LogicalType> ResultTypes { get; init; } = [];
    public IReadOnlyList<string> ResultNames { get; init; } = [];
    public bool NeedsMoreFetch { get; init; }
    public IReadOnlyList<DuckDbChunk> Results { get; init; } = [];
    public Int128 ResultUuid { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        throw new NotSupportedException("PrepareResponseMessage serialization is server-side only.");
    }

    internal static PrepareResponseMessage DeserializeBody(BinaryDeserializer d)
    {
        List<LogicalType> resultTypes = [];
        if (d.TryBeginProperty(fieldId: 1))
        {
            ulong count = d.BeginList();
            for (ulong i = 0; i < count; i++) resultTypes.Add(LogicalType.Deserialize(d));
            d.EndList();
        }

        List<string> resultNames = [];
        if (d.TryBeginProperty(fieldId: 2))
        {
            ulong count = d.BeginList();
            for (ulong i = 0; i < count; i++) resultNames.Add(d.ReadString());
            d.EndList();
        }

        bool needsMoreFetch = false;
        if (d.TryBeginProperty(fieldId: 3)) needsMoreFetch = d.ReadBool();

        List<DuckDbChunk> results = [];
        if (d.TryBeginProperty(fieldId: 4))
        {
            ulong count = d.BeginList();
            for (ulong i = 0; i < count; i++)
            {
                DuckDbChunk? chunk = DataChunkWrapper.ReadChunk(d);
                if (chunk is not null) results.Add(chunk);
            }
            d.EndList();
        }

        d.BeginProperty(fieldId: 5);
        Int128 resultUuid = d.ReadHugeInt();

        return new PrepareResponseMessage
        {
            ResultTypes = resultTypes,
            ResultNames = resultNames,
            NeedsMoreFetch = needsMoreFetch,
            Results = results,
            ResultUuid = resultUuid,
        };
    }
}

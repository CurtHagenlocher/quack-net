// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record PrepareRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.PrepareRequest;

    public string SqlQuery { get; init; } = string.Empty;
    // Names the query for FETCH, CANCEL and ACKNOWLEDGEMENT. A client query
    // should use a random nonzero id: the server reserves 0 for internal
    // catalog traffic, which never displaces a retained result.
    public Int128 QueryUuid { get; init; }
    // Rows the PREPARE response may carry inline. Null uses the server's
    // quack_prepare_inline_rows setting; 0 makes PREPARE answer before the
    // first row, which a statement that waits for SEND_DATA needs.
    public ulong? InlineRows { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, SqlQuery);
        s.WriteProperty(fieldId: 2, QueryUuid);
        OptionalIdx.WritePropertyWithDefault(s, fieldId: 3, InlineRows);
    }

    internal static PrepareRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        string sql = string.Empty;
        if (d.TryBeginProperty(fieldId: 1)) sql = d.ReadString();
        d.BeginProperty(fieldId: 2);
        Int128 queryUuid = d.ReadHugeInt();
        ulong? inlineRows = OptionalIdx.ReadPropertyWithDefault(d, fieldId: 3);
        return new PrepareRequestMessage { SqlQuery = sql, QueryUuid = queryUuid, InlineRows = inlineRows };
    }
}

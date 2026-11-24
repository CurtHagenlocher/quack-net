// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

public sealed record ErrorResponse : QuackMessage
{
    public override MessageType MessageType => MessageType.ErrorResponse;

    public string Message { get; init; } = string.Empty;
    // DuckDB's exception type in its wire form ("Catalog", "Binder", ...);
    // empty when the server sent none.
    public string ExceptionType { get; init; } = string.Empty;
    // ErrorData extra info, minus the entries that only mean something on the server.
    public IReadOnlyDictionary<string, string> ExtraInfo { get; init; } = new Dictionary<string, string>();
    // The server's own database was invalidated by this error: stop using the connection.
    public bool MustInvalidate { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, Message);
        s.WritePropertyWithDefault(fieldId: 2, ExceptionType);
        if (ExtraInfo.Count > 0)
        {
            // unordered_map<string, string>: a list of {0: key, 1: value} objects.
            s.WriteFieldId(3);
            s.BeginList((ulong)ExtraInfo.Count);
            foreach (KeyValuePair<string, string> entry in ExtraInfo)
            {
                s.BeginObject();
                s.WriteProperty(fieldId: 0, entry.Key);
                s.WriteProperty(fieldId: 1, entry.Value);
                s.EndObject();
            }
            s.EndList();
        }
        s.WritePropertyWithDefault(fieldId: 4, MustInvalidate);
    }

    internal static ErrorResponse DeserializeBody(BinaryDeserializer d)
    {
        string message = string.Empty;
        string exceptionType = string.Empty;
        Dictionary<string, string> extraInfo = new(StringComparer.Ordinal);
        bool mustInvalidate = false;

        if (d.TryBeginProperty(fieldId: 1)) message = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) exceptionType = d.ReadString();
        if (d.TryBeginProperty(fieldId: 3))
        {
            ulong count = d.BeginList();
            for (ulong i = 0; i < count; i++)
            {
                d.BeginObject();
                d.BeginProperty(fieldId: 0);
                string key = d.ReadString();
                d.BeginProperty(fieldId: 1);
                string value = d.ReadString();
                d.EndObject();
                extraInfo[key] = value;
            }
            d.EndList();
        }
        if (d.TryBeginProperty(fieldId: 4)) mustInvalidate = d.ReadBool();

        return new ErrorResponse
        {
            Message = message,
            ExceptionType = exceptionType,
            ExtraInfo = extraInfo,
            MustInvalidate = mustInvalidate,
        };
    }
}

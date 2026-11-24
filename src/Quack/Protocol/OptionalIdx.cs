// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Protocol;

// duckdb::optional_idx on the wire: one uint64 LEB128, with (idx_t)-1 meaning "not set".
internal static class OptionalIdx
{
    public const ulong Invalid = ulong.MaxValue;

    public static ulong? Read(BinaryDeserializer d)
    {
        ulong raw = d.ReadUInt64();
        return raw == Invalid ? null : raw;
    }

    // WriteProperty<optional_idx>: always written, Invalid when unset.
    public static void WriteProperty(BinarySerializer s, ushort fieldId, ulong? value)
    {
        s.WriteProperty(fieldId, value ?? Invalid);
    }

    // WritePropertyWithDefault<optional_idx>(..., optional_idx()): omitted when unset.
    public static void WritePropertyWithDefault(BinarySerializer s, ushort fieldId, ulong? value)
    {
        if (value is ulong v)
        {
            s.WriteProperty(fieldId, v);
        }
    }

    public static ulong? ReadPropertyWithDefault(BinaryDeserializer d, ushort fieldId)
        => d.TryBeginProperty(fieldId) ? Read(d) : null;
}

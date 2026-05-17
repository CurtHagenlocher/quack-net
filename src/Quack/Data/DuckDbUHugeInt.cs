// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing UHUGEINT columns. Same 16-byte
// hugeint_t struct as HUGEINT, just interpreted as an unsigned 128-bit
// value.
public static class DuckDbUHugeInt
{
    public static UInt128 Get(FixedSizeColumn column, int rowIndex)
    {
        EnsureUHugeIntColumn(column);
        return HugeintLayout.ReadUnsigned(column.GetBytes(rowIndex));
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<UInt128> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * HugeintLayout.ByteSize];
        for (int i = 0; i < values.Length; i++)
        {
            HugeintLayout.WriteUnsigned(
                data.AsSpan(i * HugeintLayout.ByteSize, HugeintLayout.ByteSize),
                values[i]);
        }
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.UHugeInt),
            Count = values.Length,
            ElementSize = HugeintLayout.ByteSize,
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureUHugeIntColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.UHugeInt)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbUHugeInt helpers require UHUGEINT.");
        }
    }
}

// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing TIME_NS columns. DuckDB stores
// TIME_NS as a signed int64 nanosecond count since 00:00:00. The valid
// range is [0, 86_400_000_000_000]; the upper bound represents
// 24:00:00.000000000 exactly, which is legal in DuckDB but exceeds
// TimeOnly.MaxValue.
public static class DuckDbTimeNs
{
    private const long MaxNanos = 86_400_000_000_000L;

    public static long GetNanoseconds(FixedSizeColumn column, int rowIndex)
    {
        EnsureTimeNsColumn(column);
        return BinaryPrimitives.ReadInt64LittleEndian(column.GetBytes(rowIndex));
    }

    public static TimeOnly GetTime(FixedSizeColumn column, int rowIndex)
    {
        return NanosecondsToTimeOnly(GetNanoseconds(column, rowIndex));
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<TimeOnly> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            // TimeOnly.Ticks is in 100ns units; TIME_NS is in nanoseconds.
            long nanos = values[i].Ticks * 100L;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), nanos);
        }
        return BuildColumn(data, values.Length, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<long> nanos, ValidityMask validity = default)
    {
        byte[] data = new byte[nanos.Length * sizeof(long)];
        for (int i = 0; i < nanos.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), nanos[i]);
        }
        return BuildColumn(data, nanos.Length, validity);
    }

    internal static TimeOnly NanosecondsToTimeOnly(long nanos)
    {
        if (nanos < 0 || nanos > MaxNanos)
        {
            throw new OverflowException(
                $"TIME_NS value {nanos} ns is outside DuckDB's valid range [0, {MaxNanos}].");
        }
        if (nanos == MaxNanos)
        {
            throw new OverflowException(
                "TIME_NS value 24:00:00 is representable in DuckDB but exceeds TimeOnly.MaxValue; use GetNanoseconds for raw access.");
        }
        // TimeOnly.Ticks is 100ns units; nanos with sub-100ns precision is rounded toward zero.
        return new TimeOnly(nanos / 100L);
    }

    private static FixedSizeColumn BuildColumn(byte[] data, int count, ValidityMask validity)
    {
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.TimeNs),
            Count = count,
            ElementSize = sizeof(long),
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureTimeNsColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.TimeNs)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbTimeNs helpers require TIME_NS.");
        }
    }
}

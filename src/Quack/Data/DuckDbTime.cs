// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing TIME columns. DuckDB stores TIME as a
// signed int64 microsecond count since 00:00:00. The valid range is
// [0, 86_400_000_000]; the upper bound represents 24:00:00.000000 exactly,
// which is legal in DuckDB but exceeds TimeOnly.MaxValue (23:59:59.9999999).
public static class DuckDbTime
{
    private const long MaxMicros = 86_400_000_000L;

    public static long GetMicroseconds(FixedSizeColumn column, int rowIndex)
    {
        EnsureTimeColumn(column);
        return BinaryPrimitives.ReadInt64LittleEndian(column.GetBytes(rowIndex));
    }

    public static TimeOnly GetTime(FixedSizeColumn column, int rowIndex)
    {
        return MicrosecondsToTimeOnly(GetMicroseconds(column, rowIndex));
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<TimeOnly> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            // TimeOnly.Ticks is in 100ns units; DuckDB stores microseconds.
            long micros = values[i].Ticks / 10L;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), micros);
        }
        return BuildColumn(data, values.Length, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<long> micros, ValidityMask validity = default)
    {
        byte[] data = new byte[micros.Length * sizeof(long)];
        for (int i = 0; i < micros.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), micros[i]);
        }
        return BuildColumn(data, micros.Length, validity);
    }

    internal static TimeOnly MicrosecondsToTimeOnly(long micros)
    {
        if (micros < 0 || micros > MaxMicros)
        {
            throw new OverflowException(
                $"TIME value {micros} micros is outside DuckDB's valid range [0, {MaxMicros}].");
        }
        if (micros == MaxMicros)
        {
            throw new OverflowException(
                "TIME value 24:00:00.000000 is representable in DuckDB but exceeds TimeOnly.MaxValue; use GetMicroseconds for raw access.");
        }
        return new TimeOnly(micros * 10L);
    }

    private static FixedSizeColumn BuildColumn(byte[] data, int count, ValidityMask validity)
    {
        return new FixedSizeColumn
        {
            Type = new LogicalType(LogicalTypeId.Time),
            Count = count,
            ElementSize = sizeof(long),
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureTimeColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.Time)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbTime helpers require TIME.");
        }
    }
}

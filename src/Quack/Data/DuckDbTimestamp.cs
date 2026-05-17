// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing TIMESTAMP columns. All five DuckDB
// timestamp flavors share an int64 epoch-relative payload; only the unit and
// whether the value is logically UTC differs:
//   TIMESTAMP, TIMESTAMP_TZ -> microseconds since 1970-01-01 00:00:00
//   TIMESTAMP_S             -> seconds
//   TIMESTAMP_MS            -> milliseconds
//   TIMESTAMP_NS            -> nanoseconds
// Sentinels are +/-INT64_MAX (NOT INT64_MIN) representing infinity.
// TIMESTAMP_TZ is bitwise identical to TIMESTAMP — the TZ distinction is
// logical (DuckDB always stores UTC microseconds).
public static class DuckDbTimestamp
{
    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long UnixEpochTicks = UnixEpochUtc.Ticks;

    public static long GetRaw(FixedSizeColumn column, int rowIndex)
    {
        EnsureTimestampColumn(column);
        return BinaryPrimitives.ReadInt64LittleEndian(column.GetBytes(rowIndex));
    }

    public static DateTime GetDateTime(FixedSizeColumn column, int rowIndex)
    {
        return RawToDateTime(GetRaw(column, rowIndex), column.Type.Id);
    }

    public static DateTimeOffset GetDateTimeOffset(FixedSizeColumn column, int rowIndex)
    {
        if (column.Type.Id != LogicalTypeId.TimestampTz)
        {
            throw new InvalidOperationException(
                $"GetDateTimeOffset requires TIMESTAMP_TZ; got {column.Type.Id}. Use GetDateTime for naive timestamps.");
        }
        return new DateTimeOffset(GetDateTime(column, rowIndex), TimeSpan.Zero);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<DateTime> values, LogicalTypeId kind = LogicalTypeId.Timestamp, ValidityMask validity = default)
    {
        ValidateKind(kind);
        byte[] data = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            long raw = DateTimeToRaw(NormalizeForKind(values[i], kind), kind);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), raw);
        }
        return BuildColumn(data, values.Length, kind, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<DateTimeOffset> values, ValidityMask validity = default)
    {
        byte[] data = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            long raw = DateTimeToRaw(values[i].UtcDateTime, LogicalTypeId.TimestampTz);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), raw);
        }
        return BuildColumn(data, values.Length, LogicalTypeId.TimestampTz, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<long> raw, LogicalTypeId kind = LogicalTypeId.Timestamp, ValidityMask validity = default)
    {
        ValidateKind(kind);
        byte[] data = new byte[raw.Length * sizeof(long)];
        for (int i = 0; i < raw.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * sizeof(long), sizeof(long)), raw[i]);
        }
        return BuildColumn(data, raw.Length, kind, validity);
    }

    internal static DateTime RawToDateTime(long raw, LogicalTypeId kind)
    {
        if (raw == long.MaxValue || raw == -long.MaxValue)
        {
            throw new OverflowException(
                $"TIMESTAMP value {raw} is the {(raw > 0 ? "positive" : "negative")} infinity sentinel; use GetRaw for raw access.");
        }
        DateTimeKind dtKind = kind == LogicalTypeId.TimestampTz ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        try
        {
            DateTime result = kind switch
            {
                LogicalTypeId.Timestamp or LogicalTypeId.TimestampTz => UnixEpochUtc.AddTicks(raw * 10L),
                LogicalTypeId.TimestampSec => UnixEpochUtc.AddSeconds(raw),
                LogicalTypeId.TimestampMs => UnixEpochUtc.AddMilliseconds(raw),
                LogicalTypeId.TimestampNs => UnixEpochUtc.AddTicks(raw / 100L),
                _ => throw new InvalidOperationException(
                    $"LogicalTypeId {kind} is not a TIMESTAMP variant."),
            };
            return DateTime.SpecifyKind(result, dtKind);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new OverflowException(
                $"TIMESTAMP value {raw} ({kind}) is outside DateTime's [0001-01-01, 9999-12-31] range.", ex);
        }
    }

    internal static long DateTimeToRaw(DateTime utc, LogicalTypeId kind)
    {
        long ticksSinceEpoch = utc.Ticks - UnixEpochTicks;
        return kind switch
        {
            LogicalTypeId.Timestamp or LogicalTypeId.TimestampTz => ticksSinceEpoch / 10L,
            LogicalTypeId.TimestampSec => ticksSinceEpoch / TimeSpan.TicksPerSecond,
            LogicalTypeId.TimestampMs => ticksSinceEpoch / TimeSpan.TicksPerMillisecond,
            LogicalTypeId.TimestampNs => checked(ticksSinceEpoch * 100L),
            _ => throw new InvalidOperationException(
                $"LogicalTypeId {kind} is not a TIMESTAMP variant."),
        };
    }

    private static DateTime NormalizeForKind(DateTime value, LogicalTypeId kind)
    {
        if (kind == LogicalTypeId.TimestampTz)
        {
            // TIMESTAMP_TZ is stored as UTC. Local -> convert; Unspecified ->
            // assume UTC (no info to do otherwise).
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }
        // Naive timestamps: store the literal Y/M/D/H/M/S, ignoring Kind.
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static void ValidateKind(LogicalTypeId kind)
    {
        switch (kind)
        {
            case LogicalTypeId.Timestamp:
            case LogicalTypeId.TimestampSec:
            case LogicalTypeId.TimestampMs:
            case LogicalTypeId.TimestampNs:
            case LogicalTypeId.TimestampTz:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind),
                    $"LogicalTypeId {kind} is not a TIMESTAMP variant.");
        }
    }

    private static FixedSizeColumn BuildColumn(byte[] data, int count, LogicalTypeId kind, ValidityMask validity)
    {
        return new FixedSizeColumn
        {
            Type = new LogicalType(kind),
            Count = count,
            ElementSize = sizeof(long),
            Data = data,
            Validity = validity,
        };
    }

    private static void EnsureTimestampColumn(FixedSizeColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        switch (column.Type.Id)
        {
            case LogicalTypeId.Timestamp:
            case LogicalTypeId.TimestampSec:
            case LogicalTypeId.TimestampMs:
            case LogicalTypeId.TimestampNs:
            case LogicalTypeId.TimestampTz:
                return;
            default:
                throw new InvalidOperationException(
                    $"Column type is {column.Type.Id}; DuckDbTimestamp helpers require a TIMESTAMP variant.");
        }
    }
}

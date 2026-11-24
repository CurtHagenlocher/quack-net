// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Scalars.Variant;
using Quack.Data;

namespace Quack.Adbc;

// VARIANT goes to Arrow as the canonical `arrow.parquet.variant` extension
// (struct<metadata: binary, value: binary>, Parquet Variant binary encoding),
// which is what DuckDB 2.0's own Arrow export produces. The type mapping
// follows duckdb's ParquetVariantConversion: unsigned integers widen to the
// next signed type (UINT32/UINT64/UINT128/INT128 to INT64 when they fit), and
// the types the Parquet Variant spec cannot express (INTERVAL, BIGNUM,
// BITSTRING, TIMESTAMP_S/_MS, TIME_TZ, TIME_NS, TIMESTAMP_NS WITH TIME ZONE,
// GEOMETRY) are refused, as duckdb refuses them.
internal static class VariantArrowConverter
{
    public static IArrowArray Build(VariantColumn column, int rowCount)
    {
        VariantArray.Builder builder = new();
        for (int i = 0; i < rowCount; i++)
        {
            DuckDbVariant? value = column.Values[i];
            if (value is null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(ToVariantValue(value));
            }
        }
        return builder.Build();
    }

    internal static VariantValue ToVariantValue(DuckDbVariant v) => v.Type switch
    {
        DuckDbVariantType.Null => VariantValue.Null,
        DuckDbVariantType.BoolTrue => VariantValue.True,
        DuckDbVariantType.BoolFalse => VariantValue.False,
        DuckDbVariantType.Int8 => VariantValue.FromInt8((sbyte)v.Value!),
        DuckDbVariantType.Int16 => VariantValue.FromInt16((short)v.Value!),
        DuckDbVariantType.Int32 => VariantValue.FromInt32((int)v.Value!),
        DuckDbVariantType.Int64 => VariantValue.FromInt64((long)v.Value!),
        DuckDbVariantType.UInt8 => VariantValue.FromInt16((byte)v.Value!),
        DuckDbVariantType.UInt16 => VariantValue.FromInt32((ushort)v.Value!),
        DuckDbVariantType.UInt32 => VariantValue.FromInt64((uint)v.Value!),
        DuckDbVariantType.UInt64 => VariantValue.FromInt64(ToInt64((UInt128)(ulong)v.Value!, v.Type)),
        DuckDbVariantType.UInt128 => VariantValue.FromInt64(ToInt64((UInt128)v.Value!, v.Type)),
        DuckDbVariantType.Int128 => VariantValue.FromInt64(ToInt64((Int128)v.Value!, v.Type)),
        DuckDbVariantType.Float => VariantValue.FromFloat((float)v.Value!),
        DuckDbVariantType.Double => VariantValue.FromDouble((double)v.Value!),
        DuckDbVariantType.Decimal => Decimal((DuckDbVariantDecimal)v.Value!),
        DuckDbVariantType.Varchar => VariantValue.FromString((string)v.Value!),
        DuckDbVariantType.Blob => VariantValue.FromBinary((byte[])v.Value!),
        DuckDbVariantType.Uuid => VariantValue.FromUuid((Guid)v.Value!),
        DuckDbVariantType.Date => VariantValue.FromDate((int)v.Value!),
        DuckDbVariantType.TimeMicros => VariantValue.FromTimeNtz((long)v.Value!),
        DuckDbVariantType.TimestampMicros => VariantValue.FromTimestampNtz((long)v.Value!),
        DuckDbVariantType.TimestampNanos => VariantValue.FromTimestampNtzNanos((long)v.Value!),
        DuckDbVariantType.TimestampMicrosTz => VariantValue.FromTimestamp((long)v.Value!),
        DuckDbVariantType.Object => VariantValue.FromObject(
            v.Fields.ToDictionary(f => f.Key, f => ToVariantValue(f.Value), StringComparer.Ordinal)),
        DuckDbVariantType.Array => VariantValue.FromArray(v.Elements.Select(ToVariantValue).ToList()),
        _ => throw new NotSupportedException($"Can't convert VARIANT of type '{v.Type}' to Parquet VARIANT."),
    };

    private static long ToInt64(Int128 value, DuckDbVariantType type)
        => value >= long.MinValue && value <= long.MaxValue
            ? (long)value
            : throw new OverflowException($"VARIANT {type} value {value} does not fit the Parquet VARIANT INT64.");

    private static long ToInt64(UInt128 value, DuckDbVariantType type)
        => value <= long.MaxValue
            ? (long)value
            : throw new OverflowException($"VARIANT {type} value {value} does not fit the Parquet VARIANT INT64.");

    // duckdb: width <= 9 DECIMAL4, <= 18 DECIMAL8, <= 38 DECIMAL16.
    private static VariantValue Decimal(DuckDbVariantDecimal d)
    {
        if (d.Width > 38)
        {
            throw new NotSupportedException($"Can't convert VARIANT DECIMAL({d.Width}, {d.Scale}) to Parquet VARIANT.");
        }
        if (d.Width <= 18)
        {
            long unscaled = (long)d.Unscaled;
            ulong magnitude = unscaled < 0 ? (ulong)(-(unscaled + 1)) + 1 : (ulong)unscaled;
            decimal value = new((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, unscaled < 0, d.Scale);
            return d.Width <= 9 ? VariantValue.FromDecimal4(value) : VariantValue.FromDecimal8(value);
        }
        bool positive = d.Unscaled >= 0;
        UInt128 abs = positive ? (UInt128)d.Unscaled : (UInt128)(-(d.Unscaled + 1)) + 1;
        int[] data =
        [
            (int)(uint)abs,
            (int)(uint)(abs >> 32),
            (int)(uint)(abs >> 64),
            (int)(uint)(abs >> 96),
        ];
        return VariantValue.FromDecimal16(new SqlDecimal(d.Width, d.Scale, positive, data));
    }
}

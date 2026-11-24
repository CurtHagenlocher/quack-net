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

    // Ingest: arrow.parquet.variant to a VARIANT column, with duckdb's mapping
    // of Parquet variant types (ParquetVariantIterator): TIMESTAMP(_NANOS)
    // with a time zone to TIMESTAMP_TZ(_NS), DECIMAL4/8 to the width of the
    // value's digits (at least the scale), DECIMAL16 to width 38.
    public static VariantColumn ToColumn(VariantArray array, Quack.Types.LogicalType type, int rowCount, ValidityMask validity)
    {
        DuckDbVariant?[] values = new DuckDbVariant?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            values[i] = array.IsNull(i) ? null : FromVariantValue(array.GetVariantValue(i));
        }
        return new VariantColumn { Type = type, Count = rowCount, Validity = validity, Values = values };
    }

    internal static DuckDbVariant FromVariantValue(VariantValue v)
    {
        if (v.IsObject)
        {
            return new DuckDbVariant(DuckDbVariantType.Object,
                v.AsObject().Select(f => new KeyValuePair<string, DuckDbVariant>(f.Key, FromVariantValue(f.Value))).ToList());
        }
        if (v.IsArray)
        {
            return new DuckDbVariant(DuckDbVariantType.Array, v.AsArray().Select(FromVariantValue).ToList());
        }
        return v.PrimitiveType switch
        {
            VariantPrimitiveType.NullType => DuckDbVariant.VariantNull,
            VariantPrimitiveType.BooleanTrue => new(DuckDbVariantType.BoolTrue, true),
            VariantPrimitiveType.BooleanFalse => new(DuckDbVariantType.BoolFalse, false),
            VariantPrimitiveType.Int8 => new(DuckDbVariantType.Int8, v.AsInt8()),
            VariantPrimitiveType.Int16 => new(DuckDbVariantType.Int16, v.AsInt16()),
            VariantPrimitiveType.Int32 => new(DuckDbVariantType.Int32, v.AsInt32()),
            VariantPrimitiveType.Int64 => new(DuckDbVariantType.Int64, v.AsInt64()),
            VariantPrimitiveType.Float => new(DuckDbVariantType.Float, v.AsFloat()),
            VariantPrimitiveType.Double => new(DuckDbVariantType.Double, v.AsDouble()),
            VariantPrimitiveType.Decimal4 or VariantPrimitiveType.Decimal8 => SmallDecimal(v.AsDecimal()),
            VariantPrimitiveType.Decimal16 => LargeDecimal(v.AsSqlDecimal()),
            VariantPrimitiveType.Date => new(DuckDbVariantType.Date, v.AsDateDays()),
            VariantPrimitiveType.Timestamp => new(DuckDbVariantType.TimestampMicrosTz, v.AsTimestampMicros()),
            VariantPrimitiveType.TimestampNtz => new(DuckDbVariantType.TimestampMicros, v.AsTimestampNtzMicros()),
            VariantPrimitiveType.TimeNtz => new(DuckDbVariantType.TimeMicros, v.AsTimeNtzMicros()),
            VariantPrimitiveType.TimestampTzNanos => new(DuckDbVariantType.TimestampNanosTz, v.AsTimestampTzNanos()),
            VariantPrimitiveType.TimestampNtzNanos => new(DuckDbVariantType.TimestampNanos, v.AsTimestampNtzNanos()),
            VariantPrimitiveType.String => new(DuckDbVariantType.Varchar, v.AsString()),
            VariantPrimitiveType.Binary => new(DuckDbVariantType.Blob, v.AsBinary()),
            VariantPrimitiveType.Uuid => new(DuckDbVariantType.Uuid, v.AsUuid()),
            _ => throw new NotSupportedException($"Parquet VARIANT type '{v.PrimitiveType}' is not supported."),
        };
    }

    private static DuckDbVariant SmallDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        byte scale = (byte)((bits[3] >> 16) & 0xFF);
        Int128 unscaled = ((Int128)(uint)bits[2] << 64) | ((Int128)(uint)bits[1] << 32) | (uint)bits[0];
        if (bits[3] < 0) unscaled = -unscaled;
        return new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(unscaled, (byte)Math.Max(Digits(unscaled), scale), scale));
    }

    private static DuckDbVariant LargeDecimal(SqlDecimal value)
    {
        int[] data = value.Data;
        Int128 unscaled = ((Int128)(uint)data[3] << 96) | ((Int128)(uint)data[2] << 64) | ((Int128)(uint)data[1] << 32) | (uint)data[0];
        if (!value.IsPositive) unscaled = -unscaled;
        return new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(unscaled, 38, value.Scale));
    }

    private static int Digits(Int128 value)
    {
        UInt128 abs = value < 0 ? (UInt128)(-(value + 1)) + 1 : (UInt128)value;
        int digits = 1;
        while (abs >= 10)
        {
            abs /= 10;
            digits++;
        }
        return digits;
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

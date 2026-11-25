// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Scalars.Variant;
using Quack.Data;

namespace Quack.Adbc;

// VARIANT goes to Arrow as the canonical `arrow.parquet.variant` extension
// (struct<metadata: binary, value: binary>, Parquet Variant binary encoding),
// byte for byte what DuckDB 2.0's own Arrow export produces (see
// ParquetVariantWriter for the type mapping).
//
// The Parquet Variant bytes are written and read here rather than through
// Apache.Arrow.Scalars: its 23.0.0 VariantBuilder/VariantReader swap the
// field-id and field-offset sizes of an object header (fixed upstream after
// 23.0.0, apache/arrow-dotnet#421), so an object whose fields need more than
// 255 bytes would not be readable by DuckDB or Spark, and theirs not by us.
internal static class VariantArrowConverter
{
    public static IArrowArray Build(VariantColumn column, int rowCount) => ParquetVariantWriter.Build(column, rowCount);

    // Ingest: arrow.parquet.variant to a VARIANT column, with duckdb's mapping
    // of Parquet variant types (ParquetVariantIterator): TIMESTAMP(_NANOS)
    // with a time zone to TIMESTAMP_TZ(_NS), DECIMAL4/8 to the width of the
    // value's digits (at least the scale), DECIMAL16 to width 38.
    public static VariantColumn ToColumn(VariantArray array, Quack.Types.LogicalType type, int rowCount, ValidityMask validity)
    {
        DuckDbVariant?[] values = new DuckDbVariant?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (array.IsNull(i))
            {
                continue;
            }
            // A shredded input (typed_value) is merged by Apache.Arrow, which reads
            // its residual value bytes with 23.0.0's object header layout.
            values[i] = array.IsShredded
                ? FromVariantValue(array.GetVariantValue(i))
                : ParquetVariantReader.Read(array.GetMetadataBytes(i), array.GetValueBytes(i));
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
        return new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(unscaled, (byte)Math.Max(ParquetVariantReader.Digits(unscaled), scale), scale));
    }

    private static DuckDbVariant LargeDecimal(SqlDecimal value)
    {
        int[] data = value.Data;
        Int128 unscaled = ((Int128)(uint)data[3] << 96) | ((Int128)(uint)data[2] << 64) | ((Int128)(uint)data[1] << 32) | (uint)data[0];
        if (!value.IsPositive) unscaled = -unscaled;
        return new(DuckDbVariantType.Decimal, new DuckDbVariantDecimal(unscaled, 38, value.Scale));
    }
}

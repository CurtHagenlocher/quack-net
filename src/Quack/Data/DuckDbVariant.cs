// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using Quack.Types;

namespace Quack.Data;

// Mirror of duckdb::VariantLogicalType (src/include/duckdb/common/types/variant.hpp, DuckDB 2.0).
public enum DuckDbVariantType : byte
{
    Null = 0,
    BoolTrue = 1,
    BoolFalse = 2,
    Int8 = 3,
    Int16 = 4,
    Int32 = 5,
    Int64 = 6,
    Int128 = 7,
    UInt8 = 8,
    UInt16 = 9,
    UInt32 = 10,
    UInt64 = 11,
    UInt128 = 12,
    Float = 13,
    Double = 14,
    Decimal = 15,
    Varchar = 16,
    Blob = 17,
    Uuid = 18,
    Date = 19,
    TimeMicros = 20,
    TimeNanos = 21,
    TimestampSec = 22,
    TimestampMillis = 23,
    TimestampMicros = 24,
    TimestampNanos = 25,
    TimeMicrosTz = 26,
    TimestampMicrosTz = 27,
    Interval = 28,
    Object = 29,
    Array = 30,
    BigNum = 31,
    BitString = 32,
    Geometry = 33,
    TimestampNanosTz = 34,
}

// A DECIMAL inside a VARIANT: unscaled value, width and scale.
public readonly record struct DuckDbVariantDecimal(Int128 Unscaled, byte Width, byte Scale);

// One decoded VARIANT value, lossless for every DuckDB variant type. A SQL
// NULL row is a null DuckDbVariant; a JSON-style null inside a variant is
// Type == Null.
//
// Value by Type:
//   Null: null; BoolTrue/BoolFalse: bool
//   Int8..Int64: sbyte/short/int/long; Int128: Int128
//   UInt8..UInt64: byte/ushort/uint/ulong; UInt128: UInt128
//   Float: float; Double: double; Decimal: DuckDbVariantDecimal
//   Varchar: string; Blob: byte[]; Uuid: Guid
//   Date: int (days since 1970-01-01)
//   TimeMicros / TimeNanos: long (since midnight)
//   Timestamp*: long (since the epoch, in the type's unit)
//   TimeMicrosTz: ulong (DuckDB's packed dtime_tz_t)
//   Interval: DuckDbInterval
//   BigNum / BitString / Geometry: byte[] (DuckDB's own encoding; GEOMETRY is WKB)
//   Object: IReadOnlyList<KeyValuePair<string, DuckDbVariant>> (in the variant's order)
//   Array: IReadOnlyList<DuckDbVariant>
public sealed class DuckDbVariant : IEquatable<DuckDbVariant>
{
    public static readonly DuckDbVariant VariantNull = new(DuckDbVariantType.Null, null);

    public DuckDbVariant(DuckDbVariantType type, object? value)
    {
        Type = type;
        Value = value;
    }

    public DuckDbVariantType Type { get; }
    public object? Value { get; }

    public IReadOnlyList<KeyValuePair<string, DuckDbVariant>> Fields =>
        Value as IReadOnlyList<KeyValuePair<string, DuckDbVariant>>
        ?? throw new InvalidOperationException($"VARIANT value of type {Type} is not an OBJECT.");

    public IReadOnlyList<DuckDbVariant> Elements =>
        Value as IReadOnlyList<DuckDbVariant>
        ?? throw new InvalidOperationException($"VARIANT value of type {Type} is not an ARRAY.");

    public bool Equals(DuckDbVariant? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Type != Type) return false;
        switch (Type)
        {
            case DuckDbVariantType.Object:
                IReadOnlyList<KeyValuePair<string, DuckDbVariant>> a = Fields, b = other.Fields;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                {
                    if (!string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal) || !a[i].Value.Equals(b[i].Value)) return false;
                }
                return true;
            case DuckDbVariantType.Array:
                return Elements.SequenceEqual(other.Elements);
            case DuckDbVariantType.Blob:
            case DuckDbVariantType.BigNum:
            case DuckDbVariantType.BitString:
            case DuckDbVariantType.Geometry:
                return ((byte[])Value!).AsSpan().SequenceEqual((byte[])other.Value!);
            default:
                return Equals(Value, other.Value);
        }
    }

    public override bool Equals(object? obj) => obj is DuckDbVariant v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Type, Type is DuckDbVariantType.Object or DuckDbVariantType.Array ? 0 : Value?.GetHashCode() ?? 0);

    public override string ToString() => Type switch
    {
        DuckDbVariantType.Null => "null",
        DuckDbVariantType.Object => "{" + string.Join(", ", Fields.Select(f => $"{f.Key}: {f.Value}")) + "}",
        DuckDbVariantType.Array => "[" + string.Join(", ", Elements) + "]",
        DuckDbVariantType.Blob or DuckDbVariantType.BigNum or DuckDbVariantType.BitString or DuckDbVariantType.Geometry
            => $"{Type}({Convert.ToHexString((byte[])Value!)})",
        _ => Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

// A VARIANT column: one decoded value per row (null = SQL NULL).
//
// A column read from the wire keeps the DuckDB vector it came from (Source)
// and decodes Values on first access, so a consumer that converts the vector
// directly (the ADBC driver) never builds the value tree. The vector aliases
// the response buffer, which therefore lives as long as the column.
public sealed class VariantColumn : DuckDbColumn
{
    private DuckDbVariant?[]? _values;

    public VariantColumn()
    {
    }

    // Leaves Values to the lazy decode in its getter.
#pragma warning disable CS8618
    [SetsRequiredMembers]
    internal VariantColumn(LogicalType type, int count, ValidityMask validity, StructColumn source, bool sourceIsShredded)
    {
        Type = type;
        Count = count;
        Validity = validity;
        Source = source;
        SourceIsShredded = sourceIsShredded;
    }
#pragma warning restore CS8618

    public required DuckDbVariant?[] Values
    {
        get => _values ?? LazyInitializer.EnsureInitialized(ref _values, () => VariantDecoder.DecodeValues(this));
        init => _values = value;
    }

    // The vector as read: the canonical STRUCT(keys, children, values, data),
    // or for VectorType SHREDDED the STRUCT(unshredded, shredded). Null when
    // the column was built from Values.
    internal StructColumn? Source { get; }

    internal bool SourceIsShredded { get; }
}

using System.Buffers.Binary;
using Quack.Types;

namespace Quack.Data;

// Helpers for reading and constructing DECIMAL columns. DuckDB stores a
// DECIMAL(p, s) value as a signed integer mantissa M such that the represented
// value is M / 10^s; the physical type is picked by p:
//   p <= 4  -> INT16
//   p <= 9  -> INT32
//   p <= 18 -> INT64
//   p <= 38 -> INT128 (hugeint_t struct layout: upper int64 at bytes 0..7,
//                     lower uint64 at bytes 8..15)
public static class DuckDbDecimal
{
    private const byte MaxWidthInt16 = 4;
    private const byte MaxWidthInt32 = 9;
    private const byte MaxWidthInt64 = 18;
    private const byte MaxWidthInt128 = 38;

    public static int ElementSizeForWidth(byte width)
    {
        ValidateWidth(width);
        if (width <= MaxWidthInt16) return sizeof(short);
        if (width <= MaxWidthInt32) return sizeof(int);
        if (width <= MaxWidthInt64) return sizeof(long);
        return 16;
    }

    public static Int128 GetMantissa(FixedSizeColumn column, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type.Id != LogicalTypeId.Decimal)
        {
            throw new InvalidOperationException(
                $"Column type is {column.Type.Id}; DuckDbDecimal helpers require DECIMAL.");
        }
        ReadOnlySpan<byte> bytes = column.GetBytes(rowIndex);
        return column.ElementSize switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            8 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            16 => HugeintLayout.ReadSigned(bytes),
            _ => throw new InvalidOperationException(
                $"Unexpected DECIMAL element size {column.ElementSize}."),
        };
    }

    public static decimal GetDecimal(FixedSizeColumn column, int rowIndex)
    {
        Int128 mantissa = GetMantissa(column, rowIndex);
        if (column.Type.TypeInfo is not DecimalTypeInfo info)
        {
            throw new InvalidOperationException(
                "Column LogicalType lacks DecimalTypeInfo (width and scale).");
        }
        return Int128ToDecimal(mantissa, info.Scale);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<decimal> values, byte width, byte scale, ValidityMask validity = default)
    {
        ValidateWidthScale(width, scale);
        Int128[] mantissas = new Int128[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            mantissas[i] = DecimalToInt128(values[i], scale);
        }
        return CreateColumn((ReadOnlySpan<Int128>)mantissas, width, scale, validity);
    }

    public static FixedSizeColumn CreateColumn(ReadOnlySpan<Int128> mantissas, byte width, byte scale, ValidityMask validity = default)
    {
        ValidateWidthScale(width, scale);
        int elementSize = ElementSizeForWidth(width);
        byte[] data = new byte[mantissas.Length * elementSize];
        for (int i = 0; i < mantissas.Length; i++)
        {
            Span<byte> slot = data.AsSpan(i * elementSize, elementSize);
            WriteMantissa(slot, mantissas[i], elementSize);
        }
        LogicalType type = new(LogicalTypeId.Decimal,
            new DecimalTypeInfo { Width = width, Scale = scale });
        return new FixedSizeColumn
        {
            Type = type,
            Count = mantissas.Length,
            ElementSize = elementSize,
            Data = data,
            Validity = validity,
        };
    }

    internal static decimal Int128ToDecimal(Int128 mantissa, byte scale)
    {
        if (scale > 28)
        {
            throw new OverflowException(
                $"DECIMAL scale {scale} exceeds System.Decimal's max scale of 28.");
        }
        bool negative = mantissa < 0;
        Int128 abs = negative ? checked(-mantissa) : mantissa;
        // System.Decimal holds a 96-bit unsigned mantissa.
        if ((abs >> 96) != 0)
        {
            throw new OverflowException(
                $"DECIMAL mantissa {mantissa} exceeds System.Decimal's 96-bit range; use GetMantissa instead.");
        }
        UInt128 unsigned = (UInt128)abs;
        int lo = (int)(uint)unsigned;
        int mid = (int)(uint)(unsigned >> 32);
        int hi = (int)(uint)(unsigned >> 64);
        return new decimal(lo, mid, hi, negative, scale);
    }

    internal static Int128 DecimalToInt128(decimal value, byte targetScale)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        uint lo = (uint)bits[0];
        uint mid = (uint)bits[1];
        uint hi = (uint)bits[2];
        int flags = bits[3];
        int currentScale = (flags >> 16) & 0xFF;
        bool negative = (flags >> 31) != 0;

        UInt128 mantissa = ((UInt128)hi << 64) | ((UInt128)mid << 32) | lo;
        int scaleDiff = targetScale - currentScale;
        if (scaleDiff > 0)
        {
            // Scale up — multiply by 10 scaleDiff times; checked so users see
            // an exception rather than silent wrap on overflow.
            for (int i = 0; i < scaleDiff; i++)
            {
                mantissa = checked(mantissa * 10);
            }
        }
        else if (scaleDiff < 0)
        {
            // Scale down — truncate toward zero (matches the C++ CAST(... AS
            // DECIMAL(p, s)) default for over-precise input).
            for (int i = 0; i < -scaleDiff; i++)
            {
                mantissa /= 10;
            }
        }

        if (mantissa > (UInt128)Int128.MaxValue)
        {
            throw new OverflowException($"DECIMAL value {value} (scaled to {targetScale}) overflows Int128.");
        }
        Int128 result = (Int128)mantissa;
        return negative ? -result : result;
    }

    private static void WriteMantissa(Span<byte> destination, Int128 value, int elementSize)
    {
        switch (elementSize)
        {
            case 2:
                if (value < short.MinValue || value > short.MaxValue)
                {
                    throw new OverflowException(
                        $"DECIMAL mantissa {value} does not fit in INT16 (width <= 4).");
                }
                BinaryPrimitives.WriteInt16LittleEndian(destination, (short)value);
                break;
            case 4:
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new OverflowException(
                        $"DECIMAL mantissa {value} does not fit in INT32 (width <= 9).");
                }
                BinaryPrimitives.WriteInt32LittleEndian(destination, (int)value);
                break;
            case 8:
                if (value < long.MinValue || value > long.MaxValue)
                {
                    throw new OverflowException(
                        $"DECIMAL mantissa {value} does not fit in INT64 (width <= 18).");
                }
                BinaryPrimitives.WriteInt64LittleEndian(destination, (long)value);
                break;
            case 16:
                HugeintLayout.WriteSigned(destination, value);
                break;
            default:
                throw new InvalidOperationException($"Unexpected DECIMAL element size {elementSize}.");
        }
    }

    private static void ValidateWidth(byte width)
    {
        if (width < 1 || width > MaxWidthInt128)
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                $"DECIMAL width must be in [1, {MaxWidthInt128}]; got {width}.");
        }
    }

    private static void ValidateWidthScale(byte width, byte scale)
    {
        ValidateWidth(width);
        if (scale > width)
        {
            throw new ArgumentOutOfRangeException(nameof(scale),
                $"DECIMAL scale ({scale}) must not exceed width ({width}).");
        }
    }
}

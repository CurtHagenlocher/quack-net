using Quack.Serialization;

namespace Quack.Types;

// Mirror of duckdb::LogicalType (id + optional ExtraTypeInfo).
// Wire layout (LogicalType::Serialize in types.cpp):
//   field 100: id  (LogicalTypeId, uint8 -> LEB128)
//   field 101: type_info  (shared_ptr<ExtraTypeInfo>, default-omit when null;
//                          when present, written as a nested object)
public sealed class LogicalType : IEquatable<LogicalType>
{
    public LogicalTypeId Id { get; }
    public ExtraTypeInfo? TypeInfo { get; }

    public string Alias => TypeInfo?.Alias ?? string.Empty;

    public LogicalType(LogicalTypeId id, ExtraTypeInfo? typeInfo = null)
    {
        Id = id;
        TypeInfo = typeInfo;
    }

    public PhysicalType GetPhysicalType()
    {
        switch (Id)
        {
            case LogicalTypeId.Boolean: return PhysicalType.Bool;
            case LogicalTypeId.TinyInt: return PhysicalType.Int8;
            case LogicalTypeId.SmallInt: return PhysicalType.Int16;
            case LogicalTypeId.Integer: return PhysicalType.Int32;
            case LogicalTypeId.BigInt: return PhysicalType.Int64;
            case LogicalTypeId.HugeInt: return PhysicalType.Int128;
            case LogicalTypeId.UTinyInt: return PhysicalType.UInt8;
            case LogicalTypeId.USmallInt: return PhysicalType.UInt16;
            case LogicalTypeId.UInteger: return PhysicalType.UInt32;
            case LogicalTypeId.UBigInt: return PhysicalType.UInt64;
            case LogicalTypeId.UHugeInt: return PhysicalType.UInt128;
            case LogicalTypeId.Float: return PhysicalType.Float;
            case LogicalTypeId.Double: return PhysicalType.Double;
            case LogicalTypeId.Date: return PhysicalType.Int32;
            case LogicalTypeId.Time:
            case LogicalTypeId.TimeTz:
            case LogicalTypeId.TimeNs:
            case LogicalTypeId.Timestamp:
            case LogicalTypeId.TimestampSec:
            case LogicalTypeId.TimestampMs:
            case LogicalTypeId.TimestampNs:
            case LogicalTypeId.TimestampTz:
                return PhysicalType.Int64;
            case LogicalTypeId.Interval: return PhysicalType.Interval;
            case LogicalTypeId.Uuid: return PhysicalType.Int128;
            case LogicalTypeId.Varchar:
            case LogicalTypeId.Char:
            case LogicalTypeId.StringLiteral:
                return PhysicalType.Varchar;
            case LogicalTypeId.Blob:
            case LogicalTypeId.Bit:
            case LogicalTypeId.Geometry:
                return PhysicalType.Varchar;
            case LogicalTypeId.List:
            case LogicalTypeId.Map:
                return PhysicalType.List;
            case LogicalTypeId.Struct:
            case LogicalTypeId.Union:
                return PhysicalType.Struct;
            case LogicalTypeId.Array:
                return PhysicalType.Array;
            case LogicalTypeId.Enum:
                return (TypeInfo as EnumTypeInfo)?.PhysicalType ?? PhysicalType.Invalid;
            case LogicalTypeId.Decimal:
                return DecimalPhysicalType((TypeInfo as DecimalTypeInfo)?.Width ?? 0);
            case LogicalTypeId.SqlNull: return PhysicalType.Bool;
            case LogicalTypeId.Pointer: return PhysicalType.UInt64;
            case LogicalTypeId.Validity: return PhysicalType.Bit;
            default:
                return PhysicalType.Invalid;
        }
    }

    private static PhysicalType DecimalPhysicalType(byte width)
    {
        // Mirrors duckdb::Decimal::MAX_WIDTH_INT{16,32,64,128} boundaries.
        if (width == 0) return PhysicalType.Invalid;
        if (width <= 4) return PhysicalType.Int16;
        if (width <= 9) return PhysicalType.Int32;
        if (width <= 18) return PhysicalType.Int64;
        if (width <= 38) return PhysicalType.Int128;
        return PhysicalType.Invalid;
    }

    internal static LogicalType Deserialize(BinaryDeserializer reader)
    {
        reader.BeginObject();
        reader.BeginProperty(fieldId: 100);
        LogicalTypeId id = (LogicalTypeId)reader.ReadByte();

        ExtraTypeInfo? typeInfo = null;
        if (reader.TryBeginProperty(fieldId: 101))
        {
            reader.BeginObject();
            typeInfo = ExtraTypeInfo.Deserialize(reader);
            reader.EndObject();
        }
        reader.EndObject();
        return new LogicalType(id, typeInfo);
    }

    public override string ToString()
    {
        if (TypeInfo is null) return Id.ToString();
        return $"{Id}({TypeInfo})";
    }

    public bool Equals(LogicalType? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (Id != other.Id) return false;
        if (TypeInfo is null) return other.TypeInfo is null;
        return TypeInfo.Equals(other.TypeInfo);
    }

    public override bool Equals(object? obj) => obj is LogicalType lt && Equals(lt);

    public override int GetHashCode() => HashCode.Combine(Id, TypeInfo);
}

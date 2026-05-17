// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Serialization;

namespace Quack.Types;

// Mirror of duckdb::ExtraTypeInfo. Wire layout
// (src/storage/serialization/serialize_types.cpp in duckdb/duckdb v1.5.x):
//   field 100: type  (ExtraTypeInfoKind, uint8 -> LEB128)
//   field 101: alias (string, default-omit)
//   field 102: [deleted — was vector<Value> "modifiers"; default-omitted in
//              practice, so absent from the wire]
//   field 103: extension_info (unique_ptr<ExtensionTypeInfo>, default-omit;
//              not yet supported by this client)
//   fields 200+: subtype-specific (see concrete subclasses)
//
// All of the above live in the same object as the subtype's fields — the
// caller is expected to supply BeginObject/EndObject.
public abstract class ExtraTypeInfo : IEquatable<ExtraTypeInfo>
{
    public string Alias { get; internal set; } = string.Empty;

    internal abstract ExtraTypeInfoKind Kind { get; }

    internal void Serialize(BinarySerializer s)
    {
        s.WriteProperty(fieldId: 100, (byte)Kind);
        s.WritePropertyWithDefault(fieldId: 101, Alias);
        // field 102 was deleted from the schema; never emit
        // field 103 (extension_info) is not yet supported as a write path,
        // so we always omit it (treated as null)
        SerializeFields(s);
    }

    internal abstract void SerializeFields(BinarySerializer s);

    internal static ExtraTypeInfo? Deserialize(BinaryDeserializer reader)
    {
        reader.BeginProperty(fieldId: 100);
        ExtraTypeInfoKind kind = (ExtraTypeInfoKind)reader.ReadByte();

        string alias = string.Empty;
        if (reader.TryBeginProperty(fieldId: 101))
        {
            alias = reader.ReadString();
        }
        if (reader.TryBeginProperty(fieldId: 102))
        {
            throw new SerializationException(
                "Encountered deleted field 102 ('modifiers') in ExtraTypeInfo; skipping requires Value support which the MVP client does not implement.");
        }
        if (reader.TryBeginProperty(fieldId: 103))
        {
            throw new SerializationException(
                "ExtraTypeInfo carried an ExtensionTypeInfo (field 103); the MVP client does not yet support extension types.");
        }

        ExtraTypeInfo? result = kind switch
        {
            ExtraTypeInfoKind.Invalid => null,
            ExtraTypeInfoKind.Generic => new GenericTypeInfo(),
            ExtraTypeInfoKind.Decimal => DecimalTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.String => StringTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.List => ListTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.Struct => StructTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.Enum => EnumTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.Array => ArrayTypeInfo.ReadFields(reader),
            ExtraTypeInfoKind.Template => TemplateTypeInfo.ReadFields(reader),
            _ => throw new SerializationException(
                $"Unsupported ExtraTypeInfo kind '{kind}'. MVP client supports Generic, Decimal, String, List, Struct, Enum, Array, Template."),
        };

        if (result is not null)
        {
            result.Alias = alias;
        }
        return result;
    }

    public virtual bool Equals(ExtraTypeInfo? other)
    {
        if (other is null) return false;
        if (Kind != other.Kind) return false;
        if (!string.Equals(Alias, other.Alias, StringComparison.Ordinal)) return false;
        return EqualsCore(other);
    }

    public override bool Equals(object? obj) => obj is ExtraTypeInfo info && Equals(info);

    public override int GetHashCode() => HashCode.Combine(Kind, Alias);

    protected abstract bool EqualsCore(ExtraTypeInfo other);
}

public sealed class GenericTypeInfo : ExtraTypeInfo
{
    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Generic;
    internal override void SerializeFields(BinarySerializer s) { }
    protected override bool EqualsCore(ExtraTypeInfo other) => true;
    public override string ToString() => "Generic";
}

public sealed class DecimalTypeInfo : ExtraTypeInfo
{
    public byte Width { get; init; }
    public byte Scale { get; init; }

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Decimal;

    internal override void SerializeFields(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 200, Width);
        s.WritePropertyWithDefault(fieldId: 201, Scale);
    }

    internal static DecimalTypeInfo ReadFields(BinaryDeserializer reader)
    {
        byte width = 0;
        byte scale = 0;
        if (reader.TryBeginProperty(fieldId: 200)) width = reader.ReadByte();
        if (reader.TryBeginProperty(fieldId: 201)) scale = reader.ReadByte();
        return new DecimalTypeInfo { Width = width, Scale = scale };
    }

    protected override bool EqualsCore(ExtraTypeInfo other) =>
        other is DecimalTypeInfo d && d.Width == Width && d.Scale == Scale;

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Width, Scale);
    public override string ToString() => $"Decimal({Width}, {Scale})";
}

public sealed class StringTypeInfo : ExtraTypeInfo
{
    public string Collation { get; init; } = string.Empty;

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.String;

    internal override void SerializeFields(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 200, Collation);
    }

    internal static StringTypeInfo ReadFields(BinaryDeserializer reader)
    {
        string collation = string.Empty;
        if (reader.TryBeginProperty(fieldId: 200)) collation = reader.ReadString();
        return new StringTypeInfo { Collation = collation };
    }

    protected override bool EqualsCore(ExtraTypeInfo other) =>
        other is StringTypeInfo s && string.Equals(s.Collation, Collation, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Collation);
    public override string ToString() => Collation.Length == 0 ? "String" : $"String(collation={Collation})";
}

public sealed class ListTypeInfo : ExtraTypeInfo
{
    public required LogicalType ChildType { get; init; }

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.List;

    internal override void SerializeFields(BinarySerializer s)
    {
        // field 200 child_type is WriteProperty<LogicalType>(...) which wraps
        // the value in an object via WriteValue<T-has-Serialize>.
        s.WriteFieldId(200);
        s.BeginObject();
        ChildType.Serialize(s);
        s.EndObject();
    }

    internal static ListTypeInfo ReadFields(BinaryDeserializer reader)
    {
        reader.BeginProperty(fieldId: 200);
        LogicalType childType = LogicalType.Deserialize(reader);
        return new ListTypeInfo { ChildType = childType };
    }

    protected override bool EqualsCore(ExtraTypeInfo other) =>
        other is ListTypeInfo l && l.ChildType.Equals(ChildType);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), ChildType);
    public override string ToString() => $"List({ChildType})";
}

public sealed class ArrayTypeInfo : ExtraTypeInfo
{
    public required LogicalType ChildType { get; init; }
    public uint Size { get; init; }

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Array;

    internal override void SerializeFields(BinarySerializer s)
    {
        s.WriteFieldId(200);
        s.BeginObject();
        ChildType.Serialize(s);
        s.EndObject();
        s.WritePropertyWithDefault(fieldId: 201, Size);
    }

    internal static ArrayTypeInfo ReadFields(BinaryDeserializer reader)
    {
        reader.BeginProperty(fieldId: 200);
        LogicalType childType = LogicalType.Deserialize(reader);
        uint size = 0;
        if (reader.TryBeginProperty(fieldId: 201)) size = reader.ReadUInt32();
        return new ArrayTypeInfo { ChildType = childType, Size = size };
    }

    protected override bool EqualsCore(ExtraTypeInfo other) =>
        other is ArrayTypeInfo a && a.ChildType.Equals(ChildType) && a.Size == Size;

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), ChildType, Size);
    public override string ToString() => $"Array({ChildType}, {Size})";
}

public sealed class StructTypeInfo : ExtraTypeInfo
{
    public required IReadOnlyList<KeyValuePair<string, LogicalType>> ChildTypes { get; init; }

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Struct;

    internal override void SerializeFields(BinarySerializer s)
    {
        if (ChildTypes.Count == 0) return; // default-omit empty list
        s.WriteFieldId(200);
        s.BeginList((ulong)ChildTypes.Count);
        foreach (KeyValuePair<string, LogicalType> child in ChildTypes)
        {
            // Each element is a std::pair<string, LogicalType> serialized by
            // the pair template: field 0 = "first", field 1 = "second".
            s.BeginObject();
            s.WriteProperty(fieldId: 0, child.Key);
            s.WriteFieldId(1);
            s.BeginObject();
            child.Value.Serialize(s);
            s.EndObject();
            s.EndObject();
        }
        s.EndList();
    }

    internal static StructTypeInfo ReadFields(BinaryDeserializer reader)
    {
        List<KeyValuePair<string, LogicalType>> children = [];
        if (reader.TryBeginProperty(fieldId: 200))
        {
            ulong count = reader.BeginList();
            for (ulong i = 0; i < count; i++)
            {
                // Each element is a std::pair<string, LogicalType>: field 0 +
                // field 1 (per the WriteValue<std::pair> template).
                reader.BeginObject();
                reader.BeginProperty(fieldId: 0);
                string name = reader.ReadString();
                reader.BeginProperty(fieldId: 1);
                LogicalType childType = LogicalType.Deserialize(reader);
                reader.EndObject();
                children.Add(new KeyValuePair<string, LogicalType>(name, childType));
            }
            reader.EndList();
        }
        return new StructTypeInfo { ChildTypes = children };
    }

    protected override bool EqualsCore(ExtraTypeInfo other)
    {
        if (other is not StructTypeInfo s) return false;
        if (s.ChildTypes.Count != ChildTypes.Count) return false;
        for (int i = 0; i < ChildTypes.Count; i++)
        {
            if (!string.Equals(ChildTypes[i].Key, s.ChildTypes[i].Key, StringComparison.Ordinal)) return false;
            if (!ChildTypes[i].Value.Equals(s.ChildTypes[i].Value)) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(base.GetHashCode());
        foreach (KeyValuePair<string, LogicalType> child in ChildTypes)
        {
            hc.Add(child.Key);
            hc.Add(child.Value);
        }
        return hc.ToHashCode();
    }

    public override string ToString()
    {
        return "Struct(" + string.Join(", ", ChildTypes.Select(c => $"{c.Key}: {c.Value}")) + ")";
    }
}

public sealed class EnumTypeInfo : ExtraTypeInfo
{
    public required IReadOnlyList<string> Values { get; init; }
    public PhysicalType PhysicalType { get; init; }

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Enum;

    internal override void SerializeFields(BinarySerializer s)
    {
        // EnumTypeInfo::Serialize writes field 200 (idx_t values_count) and
        // field 201 (list<string> values).
        s.WriteProperty(fieldId: 200, (ulong)Values.Count);
        s.WriteFieldId(201);
        s.BeginList((ulong)Values.Count);
        foreach (string v in Values)
        {
            s.WriteString(v);
        }
        s.EndList();
    }

    internal static EnumTypeInfo ReadFields(BinaryDeserializer reader)
    {
        reader.BeginProperty(fieldId: 200);
        ulong valuesCount = reader.ReadUInt64();

        reader.BeginProperty(fieldId: 201);
        ulong listCount = reader.BeginList();
        if (listCount != valuesCount)
        {
            throw new SerializationException(
                $"EnumTypeInfo values_count ({valuesCount}) does not match the list length ({listCount}).");
        }
        List<string> values = new((int)Math.Min(listCount, int.MaxValue));
        for (ulong i = 0; i < listCount; i++)
        {
            values.Add(reader.ReadString());
        }
        reader.EndList();

        return new EnumTypeInfo
        {
            Values = values,
            PhysicalType = DictPhysicalType(valuesCount),
        };
    }

    // Mirrors duckdb::EnumTypeInfo::DictType.
    private static PhysicalType DictPhysicalType(ulong size)
    {
        if (size <= byte.MaxValue) return PhysicalType.UInt8;
        if (size <= ushort.MaxValue) return PhysicalType.UInt16;
        if (size <= uint.MaxValue) return PhysicalType.UInt32;
        throw new SerializationException($"Enum dictionary size {size} exceeds uint32 maximum.");
    }

    protected override bool EqualsCore(ExtraTypeInfo other)
    {
        if (other is not EnumTypeInfo e) return false;
        if (e.PhysicalType != PhysicalType) return false;
        if (e.Values.Count != Values.Count) return false;
        for (int i = 0; i < Values.Count; i++)
        {
            if (!string.Equals(Values[i], e.Values[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(base.GetHashCode());
        hc.Add(PhysicalType);
        hc.Add(Values.Count);
        return hc.ToHashCode();
    }

    public override string ToString() => $"Enum[{Values.Count}]";
}

public sealed class TemplateTypeInfo : ExtraTypeInfo
{
    public string Name { get; init; } = string.Empty;

    internal override ExtraTypeInfoKind Kind => ExtraTypeInfoKind.Template;

    internal override void SerializeFields(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 200, Name);
    }

    internal static TemplateTypeInfo ReadFields(BinaryDeserializer reader)
    {
        string name = string.Empty;
        if (reader.TryBeginProperty(fieldId: 200)) name = reader.ReadString();
        return new TemplateTypeInfo { Name = name };
    }

    protected override bool EqualsCore(ExtraTypeInfo other) =>
        other is TemplateTypeInfo t && string.Equals(t.Name, Name, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Name);
    public override string ToString() => $"Template({Name})";
}

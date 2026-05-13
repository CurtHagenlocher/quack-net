using System.Buffers;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Types;

public class LogicalTypeTests
{
    [Fact]
    public void Deserialize_PrimitiveInteger_HasNoTypeInfo()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Integer);

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        Assert.Equal(LogicalTypeId.Integer, type.Id);
        Assert.Null(type.TypeInfo);
        Assert.Equal(PhysicalType.Int32, type.GetPhysicalType());
    }

    [Fact]
    public void Deserialize_Boolean_HasBoolPhysicalType()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Boolean);
        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));
        Assert.Equal(PhysicalType.Bool, type.GetPhysicalType());
    }

    [Fact]
    public void Deserialize_Decimal_ReadsWidthAndScale()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Decimal, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Decimal);
            info.WriteProperty(fieldId: 200, (byte)18);
            info.WriteProperty(fieldId: 201, (byte)4);
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        Assert.Equal(LogicalTypeId.Decimal, type.Id);
        DecimalTypeInfo info = Assert.IsType<DecimalTypeInfo>(type.TypeInfo);
        Assert.Equal((byte)18, info.Width);
        Assert.Equal((byte)4, info.Scale);
        Assert.Equal(PhysicalType.Int64, type.GetPhysicalType());
    }

    [Theory]
    [InlineData(4, PhysicalType.Int16)]
    [InlineData(9, PhysicalType.Int32)]
    [InlineData(18, PhysicalType.Int64)]
    [InlineData(38, PhysicalType.Int128)]
    public void Decimal_PhysicalTypeMatchesWidthBucket(byte width, PhysicalType expected)
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Decimal, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Decimal);
            info.WriteProperty(fieldId: 200, width);
            info.WriteProperty(fieldId: 201, (byte)0);
        });
        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));
        Assert.Equal(expected, type.GetPhysicalType());
    }

    [Fact]
    public void Deserialize_ListOfInteger()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.List, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.List);
            // field 200 = child_type (a nested LogicalType object)
            info.WriteFieldId(200);
            WriteInnerLogicalType(info, LogicalTypeId.Integer);
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        Assert.Equal(LogicalTypeId.List, type.Id);
        ListTypeInfo info = Assert.IsType<ListTypeInfo>(type.TypeInfo);
        Assert.Equal(LogicalTypeId.Integer, info.ChildType.Id);
    }

    [Fact]
    public void Deserialize_Struct_WithTwoNamedFields()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Struct, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Struct);
            info.WriteFieldId(200);
            info.BeginList(2);
            // Pair 1: ("a", INTEGER)
            info.BeginObject();
            info.WriteProperty(fieldId: 0, "a");
            info.WriteFieldId(1);
            WriteInnerLogicalType(info, LogicalTypeId.Integer);
            info.EndObject();
            // Pair 2: ("b", VARCHAR)
            info.BeginObject();
            info.WriteProperty(fieldId: 0, "b");
            info.WriteFieldId(1);
            WriteInnerLogicalType(info, LogicalTypeId.Varchar);
            info.EndObject();
            info.EndList();
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        Assert.Equal(LogicalTypeId.Struct, type.Id);
        StructTypeInfo info = Assert.IsType<StructTypeInfo>(type.TypeInfo);
        Assert.Equal(2, info.ChildTypes.Count);
        Assert.Equal("a", info.ChildTypes[0].Key);
        Assert.Equal(LogicalTypeId.Integer, info.ChildTypes[0].Value.Id);
        Assert.Equal("b", info.ChildTypes[1].Key);
        Assert.Equal(LogicalTypeId.Varchar, info.ChildTypes[1].Value.Id);
    }

    [Fact]
    public void Deserialize_Array_OfDoubleSize3()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Array, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Array);
            info.WriteFieldId(200);
            WriteInnerLogicalType(info, LogicalTypeId.Double);
            info.WriteProperty(fieldId: 201, 3U);
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        ArrayTypeInfo info = Assert.IsType<ArrayTypeInfo>(type.TypeInfo);
        Assert.Equal(LogicalTypeId.Double, info.ChildType.Id);
        Assert.Equal(3U, info.Size);
    }

    [Fact]
    public void Deserialize_Enum_PicksUInt8PhysicalForSmallDict()
    {
        string[] members = ["alpha", "beta", "gamma"];

        byte[] bytes = EncodeLogicalType(LogicalTypeId.Enum, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Enum);
            info.WriteProperty(fieldId: 200, (ulong)members.Length);
            info.WriteFieldId(201);
            info.BeginList((ulong)members.Length);
            foreach (string m in members) info.WriteString(m);
            info.EndList();
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        EnumTypeInfo info = Assert.IsType<EnumTypeInfo>(type.TypeInfo);
        Assert.Equal(members, info.Values);
        Assert.Equal(PhysicalType.UInt8, info.PhysicalType);
        Assert.Equal(PhysicalType.UInt8, type.GetPhysicalType());
    }

    [Fact]
    public void Deserialize_AliasIsPropagated()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Integer, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Generic);
            info.WriteProperty(fieldId: 101, "MyInt");
        });

        LogicalType type = LogicalType.Deserialize(new BinaryDeserializer(bytes));

        Assert.IsType<GenericTypeInfo>(type.TypeInfo);
        Assert.Equal("MyInt", type.Alias);
    }

    [Fact]
    public void Deserialize_RejectsExtensionInfo_AsUnsupported()
    {
        byte[] bytes = EncodeLogicalType(LogicalTypeId.Integer, info =>
        {
            info.WriteProperty(fieldId: 100, (byte)ExtraTypeInfoKind.Generic);
            // Field 103 present but empty -> still rejected as not-yet-supported.
            info.WriteFieldId(103);
            info.BeginObject();
            info.EndObject();
        });

        Assert.Throws<SerializationException>(() => LogicalType.Deserialize(new BinaryDeserializer(bytes)));
    }

    private static byte[] EncodeLogicalType(LogicalTypeId id, Action<BinarySerializer>? writeTypeInfo = null)
    {
        ArrayBufferWriter<byte> buffer = new();
        BinarySerializer s = new(buffer);
        WriteInnerLogicalType(s, id, writeTypeInfo);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteInnerLogicalType(BinarySerializer s, LogicalTypeId id, Action<BinarySerializer>? writeTypeInfo = null)
    {
        s.BeginObject();
        s.WriteProperty(fieldId: 100, (byte)id);
        if (writeTypeInfo is not null)
        {
            s.WriteFieldId(101);
            s.BeginObject();
            writeTypeInfo(s);
            s.EndObject();
        }
        s.EndObject();
    }
}

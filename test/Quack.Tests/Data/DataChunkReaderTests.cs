using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DataChunkReaderTests
{
    [Fact]
    public void Read_IntegerColumn_FlatVector_NoValidityMask()
    {
        int[] expected = [100, 200, 300];
        byte[] bytes = EncodeChunk(rowCount: 3,
            (new LogicalType(LogicalTypeId.Integer), s =>
            {
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                byte[] data = new byte[expected.Length * 4];
                for (int i = 0; i < expected.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4, 4), expected[i]);
                }
                s.WriteBlob(data);
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        Assert.Equal(3, chunk.RowCount);
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        Assert.Equal(LogicalTypeId.Integer, col.Type.Id);
        Assert.Equal(4, col.ElementSize);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.False(col.IsNull(i));
            Assert.Equal(expected[i], BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(i)));
        }
    }

    [Fact]
    public void Read_IntegerColumn_WithValidityMask_NullsRespected()
    {
        // Row 1 is null. Validity bitmap: bit0=1, bit1=0, bit2=1 -> 0b00000101 = 0x05.
        byte[] bytes = EncodeChunk(rowCount: 3,
            (new LogicalType(LogicalTypeId.Integer), s =>
            {
                WriteFlatHeader(s, hasValidity: true);
                // 3 rows -> validity buffer is ceil(3/64)*8 = 8 bytes, only bit 0,1,2 used.
                byte[] mask = new byte[8];
                mask[0] = 0b0000_0101;  // rows 0 and 2 valid, row 1 null
                s.WriteBlob(mask);
                s.WriteFieldId(102);
                byte[] data = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 11);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 33);
                s.WriteBlob(data);
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
        Assert.Equal(33, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
    }

    [Fact]
    public void Read_IntegerColumn_ConstantVector_BroadcastsToRowCount()
    {
        byte[] bytes = EncodeChunk(rowCount: 4,
            (new LogicalType(LogicalTypeId.Integer), s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Constant);
                // Inner is a 1-element FLAT vector.
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                byte[] one = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(one, 42);
                s.WriteBlob(one);
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        Assert.Equal(4, col.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(i)));
        }
    }

    [Fact]
    public void Read_IntegerColumn_SequenceVector_GeneratesValues()
    {
        byte[] bytes = EncodeChunk(rowCount: 5,
            (new LogicalType(LogicalTypeId.Integer), s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Sequence);
                s.WriteProperty(fieldId: 91, 10L);   // seq_start
                s.WriteProperty(fieldId: 92, 2L);    // seq_increment
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        int[] expected = [10, 12, 14, 16, 18];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(i)));
        }
    }

    [Fact]
    public void Read_VarcharColumn_FlatVector()
    {
        string[] values = ["alpha", "beta", "gamma"];
        byte[] bytes = EncodeChunk(rowCount: 3,
            (new LogicalType(LogicalTypeId.Varchar), s =>
            {
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                s.BeginList((ulong)values.Length);
                foreach (string v in values) s.WriteString(v);
                s.EndList();
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        StringColumn col = Assert.IsType<StringColumn>(chunk.Columns[0]);
        Assert.Equal(values, col.Values);
    }

    [Fact]
    public void Read_ListOfInteger()
    {
        // Row 0 -> [10, 20], Row 1 -> [30]
        // Child flat int[] = {10, 20, 30}. list_size = 3.
        // entries: (0, 2), (2, 1)
        LogicalType listType = new(LogicalTypeId.List,
            new ListTypeInfo { ChildType = new LogicalType(LogicalTypeId.Integer) });

        byte[] bytes = EncodeChunk(rowCount: 2,
            (listType, s =>
            {
                WriteFlatHeader(s, hasValidity: false);

                // field 104: list_size (uint64)
                s.WriteProperty(fieldId: 104, 3UL);

                // field 105: entries
                s.WriteFieldId(105);
                s.BeginList(2);
                WriteListEntry(s, offset: 0, length: 2);
                WriteListEntry(s, offset: 2, length: 1);
                s.EndList();

                // field 106: child (a flat int vector with 3 elements)
                s.WriteFieldId(106);
                s.BeginObject();
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                byte[] childData = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(childData.AsSpan(0, 4), 10);
                BinaryPrimitives.WriteInt32LittleEndian(childData.AsSpan(4, 4), 20);
                BinaryPrimitives.WriteInt32LittleEndian(childData.AsSpan(8, 4), 30);
                s.WriteBlob(childData);
                s.EndObject();
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        ListColumn col = Assert.IsType<ListColumn>(chunk.Columns[0]);
        Assert.Equal((0UL, 2UL), col.Entries[0]);
        Assert.Equal((2UL, 1UL), col.Entries[1]);
        FixedSizeColumn child = Assert.IsType<FixedSizeColumn>(col.Child);
        Assert.Equal(3, child.Count);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(0)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(1)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(child.GetBytes(2)));
    }

    [Fact]
    public void Read_StructOfIntegerAndVarchar()
    {
        LogicalType structType = new(LogicalTypeId.Struct, new StructTypeInfo
        {
            ChildTypes =
            [
                new KeyValuePair<string, LogicalType>("a", new LogicalType(LogicalTypeId.Integer)),
                new KeyValuePair<string, LogicalType>("b", new LogicalType(LogicalTypeId.Varchar)),
            ],
        });

        byte[] bytes = EncodeChunk(rowCount: 2,
            (structType, s =>
            {
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(103);
                s.BeginList(2);
                // child 0: INTEGER {7, 8}
                s.BeginObject();
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                byte[] intData = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(intData.AsSpan(0, 4), 7);
                BinaryPrimitives.WriteInt32LittleEndian(intData.AsSpan(4, 4), 8);
                s.WriteBlob(intData);
                s.EndObject();
                // child 1: VARCHAR {"x", "y"}
                s.BeginObject();
                WriteFlatHeader(s, hasValidity: false);
                s.WriteFieldId(102);
                s.BeginList(2);
                s.WriteString("x");
                s.WriteString("y");
                s.EndList();
                s.EndObject();
                s.EndList();
            }));

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));

        StructColumn col = Assert.IsType<StructColumn>(chunk.Columns[0]);
        Assert.Equal(2, col.Fields.Length);
        FixedSizeColumn ints = Assert.IsType<FixedSizeColumn>(col.Fields[0]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(ints.GetBytes(0)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(ints.GetBytes(1)));
        StringColumn strs = Assert.IsType<StringColumn>(col.Fields[1]);
        Assert.Equal(new[] { "x", "y" }, strs.Values);
    }

    private enum VectorTypeOnWire : byte
    {
        Flat = 0,
        Fsst = 1,
        Constant = 2,
        Dictionary = 3,
        Sequence = 4,
    }

    private static byte[] EncodeChunk(int rowCount, params (LogicalType Type, Action<BinarySerializer> WriteVector)[] columns)
    {
        ArrayBufferWriter<byte> buffer = new();
        BinarySerializer s = new(buffer);
        s.BeginObject();
        s.WriteProperty(fieldId: 100, (uint)rowCount);

        s.WriteFieldId(101);
        s.BeginList((ulong)columns.Length);
        foreach ((LogicalType type, _) in columns)
        {
            WriteLogicalType(s, type);
        }
        s.EndList();

        s.WriteFieldId(102);
        s.BeginList((ulong)columns.Length);
        foreach ((_, Action<BinarySerializer> writer) in columns)
        {
            s.BeginObject();
            writer(s);
            s.EndObject();
        }
        s.EndList();

        s.EndObject();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteLogicalType(BinarySerializer s, LogicalType type)
    {
        s.BeginObject();
        s.WriteProperty(fieldId: 100, (byte)type.Id);
        if (type.TypeInfo is not null)
        {
            // shared_ptr<T> wraps the value in a Nullable + has-Serialize object.
            s.WriteFieldId(101);
            s.BeginNullable(true);
            s.BeginObject();
            WriteTypeInfo(s, type.TypeInfo);
            s.EndObject();
            s.EndNullable();
        }
        s.EndObject();
    }

    private static void WriteTypeInfo(BinarySerializer s, ExtraTypeInfo info)
    {
        switch (info)
        {
            case ListTypeInfo list:
                s.WriteProperty(fieldId: 100, (byte)4 /* ExtraTypeInfoKind.List */);
                s.WriteFieldId(200);
                WriteLogicalType(s, list.ChildType);
                break;
            case StructTypeInfo strct:
                s.WriteProperty(fieldId: 100, (byte)5 /* ExtraTypeInfoKind.Struct */);
                s.WriteFieldId(200);
                s.BeginList((ulong)strct.ChildTypes.Count);
                foreach (KeyValuePair<string, LogicalType> child in strct.ChildTypes)
                {
                    // pair<string, LogicalType> uses field 0 and 1 per
                    // WriteValue<std::pair> in the C++ Serializer.
                    s.BeginObject();
                    s.WriteProperty(fieldId: 0, child.Key);
                    s.WriteFieldId(1);
                    WriteLogicalType(s, child.Value);
                    s.EndObject();
                }
                s.EndList();
                break;
            default:
                throw new InvalidOperationException(
                    $"Test helper WriteTypeInfo does not support {info.GetType().Name}.");
        }
    }

    private static void WriteListEntry(BinarySerializer s, ulong offset, ulong length)
    {
        s.BeginObject();
        s.WriteProperty(fieldId: 100, offset);
        s.WriteProperty(fieldId: 101, length);
        s.EndObject();
    }

    private static void WriteFlatHeader(BinarySerializer s, bool hasValidity)
    {
        s.WriteProperty(fieldId: 100, hasValidity);
        if (hasValidity)
        {
            s.WriteFieldId(101);
        }
    }
}

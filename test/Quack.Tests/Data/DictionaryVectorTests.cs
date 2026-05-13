using System.Buffers;
using System.Buffers.Binary;
using Quack.Data;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Tests.Data;

public class DictionaryVectorTests
{
    [Fact]
    public void Read_DictionaryOfInteger_ExpandsToFlat()
    {
        // Dictionary [10, 20, 30] selected by [2, 0, 0, 1] -> [30, 10, 10, 20].
        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Integer),
            rowCount: 4,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                s.WriteProperty(fieldId: 91, SelBytes(2, 0, 0, 1));
                s.WriteProperty(fieldId: 92, 3UL); // dict_count
                // Inner FLAT INTEGER vector with 3 elements.
                WriteFlatFixed(s, hasValidity: false, Int32Bytes(10, 20, 30));
            });

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);

        Assert.Equal(4, col.Count);
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(1)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(3)));
        Assert.True(col.Validity.IsAllValid);
    }

    [Fact]
    public void Read_DictionaryOfVarchar_ExpandsToFlat()
    {
        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Varchar),
            rowCount: 5,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                s.WriteProperty(fieldId: 91, SelBytes(0, 1, 0, 1, 0));
                s.WriteProperty(fieldId: 92, 2UL);
                // Inner FLAT VARCHAR ["alpha", "beta"]
                s.WriteProperty(fieldId: 100, false);
                s.WriteFieldId(102);
                s.BeginList(2);
                s.WriteString("alpha");
                s.WriteString("beta");
                s.EndList();
            });

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));
        VarBytesColumn col = Assert.IsType<VarBytesColumn>(chunk.Columns[0]);

        string[] expected = ["alpha", "beta", "alpha", "beta", "alpha"];
        Assert.Equal(expected.Length, col.Values.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected[i]), col.Values[i]!.Value.ToArray());
        }
    }

    [Fact]
    public void Read_DictionaryWithNullDictEntry_PropagatesNullForSelectedRow()
    {
        // Dictionary [10, NULL, 30]. Selected by [0, 1, 2] -> [10, NULL, 30].
        byte[] dictMask = new byte[ValidityMask.RequiredByteCount(3)];
        dictMask[0] = 0b0000_0101; // rows 0 and 2 valid; row 1 null

        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Integer),
            rowCount: 3,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                s.WriteProperty(fieldId: 91, SelBytes(0, 1, 2));
                s.WriteProperty(fieldId: 92, 3UL);
                // Inner FLAT with validity mask
                s.WriteProperty(fieldId: 100, true);
                s.WriteProperty(fieldId: 101, dictMask);
                s.WriteProperty(fieldId: 102, Int32Bytes(10, 999, 30));
            });

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);

        Assert.False(col.IsNull(0));
        Assert.True(col.IsNull(1));
        Assert.False(col.IsNull(2));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(2)));
    }

    [Fact]
    public void Read_DictionaryWithRepeatedNullSelection_FlagsAllSelectedRowsNull()
    {
        // Dictionary [10, NULL]. Sel [1, 1, 1] -> all three rows null.
        byte[] dictMask = new byte[ValidityMask.RequiredByteCount(2)];
        dictMask[0] = 0b0000_0001; // only row 0 valid

        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Integer),
            rowCount: 3,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                s.WriteProperty(fieldId: 91, SelBytes(1, 1, 1));
                s.WriteProperty(fieldId: 92, 2UL);
                s.WriteProperty(fieldId: 100, true);
                s.WriteProperty(fieldId: 101, dictMask);
                s.WriteProperty(fieldId: 102, Int32Bytes(10, 0));
            });

        DuckDbChunk chunk = DataChunkReader.Read(new BinaryDeserializer(bytes));
        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(col.IsNull(i));
        }
    }

    [Fact]
    public void Read_DictionarySelLengthMismatch_Throws()
    {
        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Integer),
            rowCount: 3,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                // Sel vector with only 2 entries but row count says 3.
                s.WriteProperty(fieldId: 91, SelBytes(0, 1));
                s.WriteProperty(fieldId: 92, 2UL);
                WriteFlatFixed(s, hasValidity: false, Int32Bytes(10, 20));
            });

        Assert.Throws<SerializationException>(() => DataChunkReader.Read(new BinaryDeserializer(bytes)));
    }

    [Fact]
    public void Read_DictionarySelIndexOutOfRange_Throws()
    {
        byte[] bytes = EncodeChunk(
            type: new LogicalType(LogicalTypeId.Integer),
            rowCount: 2,
            writeVector: s =>
            {
                s.WriteProperty(fieldId: 90, (byte)VectorTypeOnWire.Dictionary);
                // dict size 2, but sel points to index 5
                s.WriteProperty(fieldId: 91, SelBytes(0, 5));
                s.WriteProperty(fieldId: 92, 2UL);
                WriteFlatFixed(s, hasValidity: false, Int32Bytes(10, 20));
            });

        Assert.Throws<SerializationException>(() => DataChunkReader.Read(new BinaryDeserializer(bytes)));
    }

    private enum VectorTypeOnWire : byte
    {
        Flat = 0,
        Fsst = 1,
        Constant = 2,
        Dictionary = 3,
        Sequence = 4,
    }

    private static byte[] EncodeChunk(LogicalType type, int rowCount, Action<BinarySerializer> writeVector)
    {
        ArrayBufferWriter<byte> buffer = new();
        BinarySerializer s = new(buffer);
        s.BeginObject();
        s.WriteProperty(fieldId: 100, (uint)rowCount);

        s.WriteFieldId(101);
        s.BeginList(1);
        s.BeginObject();
        s.WriteProperty(fieldId: 100, (byte)type.Id);
        s.EndObject();
        s.EndList();

        s.WriteFieldId(102);
        s.BeginList(1);
        s.BeginObject();
        writeVector(s);
        s.EndObject();
        s.EndList();

        s.EndObject();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFlatFixed(BinarySerializer s, bool hasValidity, byte[] data)
    {
        s.WriteProperty(fieldId: 100, hasValidity);
        s.WriteProperty(fieldId: 102, data);
    }

    private static byte[] SelBytes(params uint[] indices)
    {
        byte[] bytes = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), indices[i]);
        }
        return bytes;
    }

    private static byte[] Int32Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }
}

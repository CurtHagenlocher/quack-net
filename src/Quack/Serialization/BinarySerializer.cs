using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Quack.Serialization;

// Faithful port of DuckDB's BinarySerializer wire format
// (src/common/serializer/binary_serializer.cpp in duckdb/duckdb).
//
// Wire rules:
//   - Object Begin emits nothing; Object End emits raw uint16 0xFFFF (LE).
//   - Property Begin emits raw uint16 field id (LE). Optional Property Begin
//     emits the field id only when the value is present (default-omit).
//   - List Begin emits unsigned LEB128 element count.
//   - Nullable Begin emits a 1-byte bool (the "present" flag).
//   - bool and char are 1 raw byte; integral types are LEB128
//     (signed sign-extending, unsigned standard); float/double are raw
//     IEEE-754 little-endian; string is unsigned-LEB128(uint32 length) +
//     UTF-8 bytes; hugeint is signed-LEB128(upper) + unsigned-LEB128(lower).
internal sealed class BinarySerializer
{
    public const ushort MessageTerminatorFieldId = 0xFFFF;

    private readonly IBufferWriter<byte> _output;

    public BinarySerializer(IBufferWriter<byte> output)
    {
        _output = output;
    }

    public void BeginObject()
    {
    }

    public void EndObject()
    {
        WriteRawUInt16(MessageTerminatorFieldId);
    }

    public void WriteFieldId(ushort fieldId)
    {
        WriteRawUInt16(fieldId);
    }

    public void BeginList(ulong count)
    {
        WriteUnsignedLeb128(count);
    }

    public void EndList()
    {
    }

    public void BeginNullable(bool present)
    {
        WriteBool(present);
    }

    public void EndNullable()
    {
    }

    public void WriteProperty(ushort fieldId, bool value)
    {
        WriteFieldId(fieldId);
        WriteBool(value);
    }

    public void WriteProperty(ushort fieldId, byte value)
    {
        WriteFieldId(fieldId);
        WriteUnsignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, sbyte value)
    {
        WriteFieldId(fieldId);
        WriteSignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, ushort value)
    {
        WriteFieldId(fieldId);
        WriteUnsignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, short value)
    {
        WriteFieldId(fieldId);
        WriteSignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, uint value)
    {
        WriteFieldId(fieldId);
        WriteUnsignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, int value)
    {
        WriteFieldId(fieldId);
        WriteSignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, ulong value)
    {
        WriteFieldId(fieldId);
        WriteUnsignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, long value)
    {
        WriteFieldId(fieldId);
        WriteSignedLeb128(value);
    }

    public void WriteProperty(ushort fieldId, Int128 value)
    {
        WriteFieldId(fieldId);
        WriteHugeInt(value);
    }

    public void WriteProperty(ushort fieldId, UInt128 value)
    {
        WriteFieldId(fieldId);
        WriteUHugeInt(value);
    }

    public void WriteProperty(ushort fieldId, float value)
    {
        WriteFieldId(fieldId);
        WriteFloat(value);
    }

    public void WriteProperty(ushort fieldId, double value)
    {
        WriteFieldId(fieldId);
        WriteDouble(value);
    }

    public void WriteProperty(ushort fieldId, string value)
    {
        WriteFieldId(fieldId);
        WriteString(value);
    }

    public void WriteProperty(ushort fieldId, ReadOnlySpan<byte> value)
    {
        WriteFieldId(fieldId);
        WriteBlob(value);
    }

    public void WritePropertyWithDefault(ushort fieldId, bool value, bool defaultValue = false)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, byte value, byte defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, sbyte value, sbyte defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, ushort value, ushort defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, short value, short defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, uint value, uint defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, int value, int defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, ulong value, ulong defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, long value, long defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, float value, float defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, double value, double defaultValue = 0)
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WritePropertyWithDefault(ushort fieldId, string value, string defaultValue = "")
    {
        if (value == defaultValue) return;
        WriteProperty(fieldId, value);
    }

    public void WriteBool(bool value)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = value ? (byte)1 : (byte)0;
        _output.Write(b);
    }

    public void WriteUnsignedLeb128(ulong value)
    {
        Span<byte> buffer = stackalloc byte[Leb128.MaxBytes];
        int written = Leb128.WriteUnsigned(buffer, value);
        _output.Write(buffer[..written]);
    }

    public void WriteSignedLeb128(long value)
    {
        Span<byte> buffer = stackalloc byte[Leb128.MaxBytes];
        int written = Leb128.WriteSigned(buffer, value);
        _output.Write(buffer[..written]);
    }

    public void WriteFloat(float value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        _output.Write(buffer);
    }

    public void WriteDouble(double value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        _output.Write(buffer);
    }

    public void WriteHugeInt(Int128 value)
    {
        long upper = (long)(value >> 64);
        ulong lower = (ulong)value;
        WriteSignedLeb128(upper);
        WriteUnsignedLeb128(lower);
    }

    public void WriteUHugeInt(UInt128 value)
    {
        ulong upper = (ulong)(value >> 64);
        ulong lower = (ulong)value;
        WriteUnsignedLeb128(upper);
        WriteUnsignedLeb128(lower);
    }

    public void WriteString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUnsignedLeb128((ulong)(uint)byteCount);
        if (byteCount == 0) return;
        Span<byte> destination = _output.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(value, destination);
        _output.Advance(written);
    }

    public void WriteBlob(ReadOnlySpan<byte> data)
    {
        WriteUnsignedLeb128((ulong)data.Length);
        if (data.Length == 0) return;
        _output.Write(data);
    }

    public void WriteRawBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        _output.Write(data);
    }

    private void WriteRawUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _output.Write(buffer);
    }
}

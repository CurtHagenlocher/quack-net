using System.Buffers.Binary;
using System.Text;

namespace Quack.Serialization;

// Faithful port of DuckDB's BinaryDeserializer
// (src/common/serializer/binary_deserializer.cpp in duckdb/duckdb).
//
// Reads the wire format produced by BinarySerializer. Properties are
// consumed in strict ascending field-id order; an optional property is
// signalled by the next field id being greater than expected, in which
// case the reader returns false and leaves the field id buffered for the
// next call.
internal sealed class BinaryDeserializer
{
    private readonly ReadOnlyMemory<byte> _input;
    private int _position;
    private bool _hasBufferedField;
    private ushort _bufferedField;

    public BinaryDeserializer(ReadOnlyMemory<byte> input)
    {
        _input = input;
        _position = 0;
    }

    public int Position => _position;

    public int Remaining => _input.Length - _position;

    public bool IsAtEnd => _position == _input.Length && !_hasBufferedField;

    public void BeginObject()
    {
    }

    public void EndObject()
    {
        ushort next = NextField();
        if (next != BinarySerializer.MessageTerminatorFieldId)
        {
            throw new SerializationException(
                $"Expected end-of-object terminator (0x{BinarySerializer.MessageTerminatorFieldId:X4}) but found field id 0x{next:X4}.");
        }
    }

    public ulong BeginList() => ReadUnsignedLeb128();

    public void EndList()
    {
    }

    public bool BeginNullable() => ReadBool();

    public void EndNullable()
    {
    }

    public void BeginProperty(ushort fieldId)
    {
        ushort actual = NextField();
        if (actual != fieldId)
        {
            throw new SerializationException(
                $"Expected field id 0x{fieldId:X4} but found 0x{actual:X4}.");
        }
    }

    // Returns true if the next field id matches `fieldId` (and consumes it),
    // false if the next field is past it (and leaves it buffered for the next
    // call). Throws if the next field id is below `fieldId`, which indicates
    // an out-of-order or corrupted stream.
    public bool TryBeginProperty(ushort fieldId)
    {
        ushort next = PeekField();
        if (next == fieldId)
        {
            ConsumeField();
            return true;
        }
        if (next == BinarySerializer.MessageTerminatorFieldId || next > fieldId)
        {
            return false;
        }
        throw new SerializationException(
            $"Unexpected out-of-order field id 0x{next:X4} (expected >= 0x{fieldId:X4}).");
    }

    public bool ReadBool() => ReadRawByte() != 0;

    public byte ReadByte() => (byte)ReadUnsignedLeb128();

    public sbyte ReadSByte() => (sbyte)ReadSignedLeb128();

    public ushort ReadUInt16() => (ushort)ReadUnsignedLeb128();

    public short ReadInt16() => (short)ReadSignedLeb128();

    public uint ReadUInt32() => (uint)ReadUnsignedLeb128();

    public int ReadInt32() => (int)ReadSignedLeb128();

    public ulong ReadUInt64() => ReadUnsignedLeb128();

    public long ReadInt64() => ReadSignedLeb128();

    public Int128 ReadHugeInt()
    {
        long upper = ReadSignedLeb128();
        ulong lower = ReadUnsignedLeb128();
        return ((Int128)upper << 64) | lower;
    }

    public UInt128 ReadUHugeInt()
    {
        ulong upper = ReadUnsignedLeb128();
        ulong lower = ReadUnsignedLeb128();
        return ((UInt128)upper << 64) | lower;
    }

    public float ReadFloat()
    {
        ReadOnlySpan<byte> bytes = ReadRawBytes(sizeof(float));
        return BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    public double ReadDouble()
    {
        ReadOnlySpan<byte> bytes = ReadRawBytes(sizeof(double));
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    public string ReadString()
    {
        uint length = (uint)ReadUnsignedLeb128();
        if (length == 0) return string.Empty;
        ReadOnlySpan<byte> bytes = ReadRawBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    public byte[] ReadBlob()
    {
        ulong length = ReadUnsignedLeb128();
        if (length == 0) return [];
        ReadOnlySpan<byte> bytes = ReadRawBytes(checked((int)length));
        return bytes.ToArray();
    }

    // Reads a `WriteDataPtr(ptr, count)`-style value: an unsigned LEB128 byte
    // count followed by that many raw bytes. Returns a slice of the input
    // memory without copying.
    public ReadOnlyMemory<byte> ReadDataMemory()
    {
        if (_hasBufferedField)
        {
            throw new SerializationException(
                "Cannot read primitive while a field id is buffered. Consume the buffered field first.");
        }
        ulong length = ReadUnsignedLeb128();
        if (length == 0) return ReadOnlyMemory<byte>.Empty;
        int len = checked((int)length);
        if (_position + len > _input.Length)
        {
            throw new SerializationException(
                $"Unexpected end of stream while reading {len} data bytes.");
        }
        ReadOnlyMemory<byte> slice = _input.Slice(_position, len);
        _position += len;
        return slice;
    }

    public ulong ReadUnsignedLeb128()
    {
        if (_hasBufferedField)
        {
            throw new SerializationException(
                "Cannot read primitive while a field id is buffered. Consume the buffered field first.");
        }
        ReadOnlySpan<byte> source = _input.Span[_position..];
        ulong value = Leb128.ReadUnsigned(source, out int read);
        _position += read;
        return value;
    }

    public long ReadSignedLeb128()
    {
        if (_hasBufferedField)
        {
            throw new SerializationException(
                "Cannot read primitive while a field id is buffered. Consume the buffered field first.");
        }
        ReadOnlySpan<byte> source = _input.Span[_position..];
        long value = Leb128.ReadSigned(source, out int read);
        _position += read;
        return value;
    }

    public byte ReadRawByte()
    {
        if (_hasBufferedField)
        {
            throw new SerializationException(
                "Cannot read primitive while a field id is buffered. Consume the buffered field first.");
        }
        if (_position >= _input.Length)
        {
            throw new SerializationException("Unexpected end of stream while reading byte.");
        }
        return _input.Span[_position++];
    }

    public ReadOnlySpan<byte> ReadRawBytes(int count)
    {
        if (_hasBufferedField)
        {
            throw new SerializationException(
                "Cannot read primitive while a field id is buffered. Consume the buffered field first.");
        }
        if (_position + count > _input.Length)
        {
            throw new SerializationException(
                $"Unexpected end of stream while reading {count} bytes (have {_input.Length - _position}).");
        }
        ReadOnlySpan<byte> slice = _input.Span.Slice(_position, count);
        _position += count;
        return slice;
    }

    private ushort PeekField()
    {
        if (!_hasBufferedField)
        {
            _bufferedField = ReadRawUInt16();
            _hasBufferedField = true;
        }
        return _bufferedField;
    }

    private void ConsumeField()
    {
        if (_hasBufferedField)
        {
            _hasBufferedField = false;
        }
        else
        {
            _ = ReadRawUInt16();
        }
    }

    private ushort NextField()
    {
        if (_hasBufferedField)
        {
            _hasBufferedField = false;
            return _bufferedField;
        }
        return ReadRawUInt16();
    }

    private ushort ReadRawUInt16()
    {
        if (_position + 2 > _input.Length)
        {
            throw new SerializationException("Unexpected end of stream while reading uint16.");
        }
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_input.Span.Slice(_position, 2));
        _position += 2;
        return value;
    }
}

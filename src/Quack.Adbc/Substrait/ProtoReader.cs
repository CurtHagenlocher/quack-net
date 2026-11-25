// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (src/KustoAdbc/Substrait/ProtoReader.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace.

using System.Runtime.CompilerServices;
using System.Text;

namespace Quack.Adbc.Substrait;

/// <summary>
/// Forward-only reader over one protobuf message in a byte buffer.
/// Positions are absolute offsets into the underlying buffer, so a
/// length-delimited field can be re-read later as a sub-message.
/// All reads are bounds-checked; malformed input raises
/// <see cref="SubstraitTranslationException"/>.
/// </summary>
internal struct ProtoReader
{
    public const int WireVarint = 0;
    public const int WireFixed64 = 1;
    public const int WireLengthDelimited = 2;
    public const int WireFixed32 = 5;

    readonly byte[] _data;
    readonly int _end;
    int _pos;

    public ProtoReader(byte[] data, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > data.Length)
            throw SubstraitTranslationException.MalformedPlan("Protobuf field length exceeds buffer.");
        _data = data;
        _pos = start;
        _end = start + length;
    }

    public ProtoReader(byte[] data, ProtoSlice slice) : this(data, slice.Start, slice.Length) { }

    public int Position => _pos;

    /// <summary>Reads the next tag. Returns false at the end of the message.</summary>
    public bool Next(out int fieldNumber, out int wireType)
    {
        if (_pos >= _end)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }
        ulong tag = ReadRawVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        if (fieldNumber == 0)
            throw SubstraitTranslationException.MalformedPlan("Protobuf field number 0 is invalid.");
        return true;
    }

    public ulong ReadVarint(int wireType)
    {
        Expect(wireType, WireVarint);
        return ReadRawVarint();
    }

    public long ReadInt64(int wireType) => (long)ReadVarint(wireType);

    // Negative int32 values are sign-extended to 10 bytes on the wire; truncation restores them.
    public int ReadInt32(int wireType) => unchecked((int)ReadVarint(wireType));

    public uint ReadUInt32(int wireType) => unchecked((uint)ReadVarint(wireType));

    public bool ReadBool(int wireType) => ReadVarint(wireType) != 0;

    public uint ReadFixed32(int wireType)
    {
        Expect(wireType, WireFixed32);
        Require(4);
        uint v = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
        _pos += 4;
        return v;
    }

    public ulong ReadFixed64(int wireType)
    {
        Expect(wireType, WireFixed64);
        Require(8);
        ulong lo = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
        ulong hi = (uint)(_data[_pos + 4] | (_data[_pos + 5] << 8) | (_data[_pos + 6] << 16) | (_data[_pos + 7] << 24));
        _pos += 8;
        return lo | (hi << 32);
    }

    public float ReadFloat(int wireType)
    {
        uint bits = ReadFixed32(wireType);
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }

    public double ReadDouble(int wireType) => BitConverter.Int64BitsToDouble((long)ReadFixed64(wireType));

    /// <summary>Reads a length-delimited field and returns its location in the buffer.</summary>
    public ProtoSlice ReadSlice(int wireType)
    {
        Expect(wireType, WireLengthDelimited);
        ulong len = ReadRawVarint();
        if (len > (ulong)(_end - _pos))
            throw SubstraitTranslationException.MalformedPlan("Protobuf field length exceeds message.");
        var slice = new ProtoSlice(_pos, (int)len);
        _pos += (int)len;
        return slice;
    }

    public ProtoReader ReadMessage(int wireType) => new ProtoReader(_data, ReadSlice(wireType));

    public string ReadString(int wireType)
    {
        var s = ReadSlice(wireType);
        return Encoding.UTF8.GetString(_data, s.Start, s.Length);
    }

    public byte[] ReadBytes(int wireType)
    {
        var s = ReadSlice(wireType);
        var bytes = new byte[s.Length];
        Buffer.BlockCopy(_data, s.Start, bytes, 0, s.Length);
        return bytes;
    }

    /// <summary>
    /// Reads a repeated scalar varint field, accepting both packed and
    /// unpacked encodings.
    /// </summary>
    public void ReadRepeatedInt32(int wireType, List<int> values)
    {
        if (wireType == WireLengthDelimited)
        {
            var packed = ReadMessage(wireType);
            while (packed._pos < packed._end)
                values.Add(unchecked((int)packed.ReadRawVarint()));
        }
        else
        {
            values.Add(ReadInt32(wireType));
        }
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case WireVarint: ReadRawVarint(); break;
            case WireFixed64: Require(8); _pos += 8; break;
            case WireLengthDelimited: ReadSlice(wireType); break;
            case WireFixed32: Require(4); _pos += 4; break;
            default: throw SubstraitTranslationException.MalformedPlan($"Unsupported protobuf wire type {wireType}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ulong ReadRawVarint()
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (_pos >= _end)
                throw SubstraitTranslationException.MalformedPlan("Truncated protobuf varint.");
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw SubstraitTranslationException.MalformedPlan("Protobuf varint is too long.");
    }

    void Require(int count)
    {
        if (_end - _pos < count)
            throw SubstraitTranslationException.MalformedPlan("Truncated protobuf field.");
    }

    static void Expect(int actual, int expected)
    {
        if (actual != expected)
            throw SubstraitTranslationException.MalformedPlan($"Unexpected protobuf wire type {actual} (expected {expected}).");
    }
}

/// <summary>The location of a length-delimited field's payload within a buffer.</summary>
internal readonly struct ProtoSlice
{
    public readonly int Start;
    public readonly int Length;

    public ProtoSlice(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int End => Start + Length;
}

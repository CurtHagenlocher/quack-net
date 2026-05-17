// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using Quack.Serialization;

namespace Quack.Tests.Serialization;

public class BinarySerializerTests
{
    [Fact]
    public void EmptyObject_WritesTerminatorOnly()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.BeginObject();
        s.EndObject();
        Assert.Equal(new byte[] { 0xFF, 0xFF }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Bool_IsRawByte()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteBool(true);
        s.WriteBool(false);
        Assert.Equal(new byte[] { 0x01, 0x00 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void FieldId_IsRawLittleEndianUInt16()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteFieldId(0x0102);
        // Little-endian: low byte first.
        Assert.Equal(new byte[] { 0x02, 0x01 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteProperty_String_LengthPrefixedUtf8()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteProperty(fieldId: 1, "abc");
        // 0x01 0x00 -> field id 1 LE; 0x03 -> LEB128 length 3; 0x61 0x62 0x63 -> "abc"
        Assert.Equal(new byte[] { 0x01, 0x00, 0x03, 0x61, 0x62, 0x63 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WritePropertyWithDefault_OmitsDefault()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.BeginObject();
        s.WritePropertyWithDefault(fieldId: 1, value: "", defaultValue: "");
        s.WritePropertyWithDefault(fieldId: 2, value: 0UL, defaultValue: 0UL);
        s.WritePropertyWithDefault(fieldId: 3, value: 42UL, defaultValue: 0UL);
        s.EndObject();
        // Only field 3 is on the wire: 0x03 0x00 (field id) 0x2A (LEB128 42) then 0xFFFF (terminator).
        Assert.Equal(new byte[] { 0x03, 0x00, 0x2A, 0xFF, 0xFF }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void HugeInt_SignedUpperUnsignedLower()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        // -1 as hugeint_t = { upper = -1 (int64), lower = uint64::MaxValue }.
        s.WriteHugeInt(-1);
        // signed-LEB128(-1) = 0x7F; unsigned-LEB128(ulong.MaxValue) = ten bytes.
        byte[] expectedUpper = new byte[] { 0x7F };
        byte[] expectedLower = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        Assert.Equal([.. expectedUpper, .. expectedLower], writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Float_IsRawIeee754LittleEndian()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteFloat(1.0f);
        // 1.0f = 0x3F800000 -> LE bytes 00 00 80 3F
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Double_IsRawIeee754LittleEndian()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteDouble(1.0);
        // 1.0 = 0x3FF0000000000000 -> LE bytes 00 00 00 00 00 00 F0 3F
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void RoundTrip_RequiredAndOptionalProperties()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);

        s.BeginObject();
        s.WriteProperty(fieldId: 1, (byte)7);                            // required
        s.WritePropertyWithDefault(fieldId: 2, "hello", "");             // present
        s.WritePropertyWithDefault(fieldId: 3, "", "");                  // omitted
        s.WritePropertyWithDefault(fieldId: 4, 99UL, 0UL);               // present
        s.EndObject();

        BinaryDeserializer d = new(writer.WrittenMemory);
        d.BeginObject();
        d.BeginProperty(1);
        Assert.Equal((byte)7, d.ReadByte());

        Assert.True(d.TryBeginProperty(2));
        Assert.Equal("hello", d.ReadString());

        Assert.False(d.TryBeginProperty(3));   // omitted -> default

        Assert.True(d.TryBeginProperty(4));
        Assert.Equal(99UL, d.ReadUInt64());

        d.EndObject();
        Assert.True(d.IsAtEnd);
    }

    [Fact]
    public void RoundTrip_TwoTopLevelObjects()
    {
        // Quack messages are two top-level objects (header, body), each
        // followed by its own 0xFFFF terminator. This mirrors that pattern.
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);

        s.BeginObject();
        s.WriteProperty(fieldId: 1, (byte)42);
        s.EndObject();
        s.BeginObject();
        s.WriteProperty(fieldId: 1, "body");
        s.EndObject();

        BinaryDeserializer d = new(writer.WrittenMemory);
        d.BeginObject();
        d.BeginProperty(1);
        Assert.Equal((byte)42, d.ReadByte());
        d.EndObject();
        d.BeginObject();
        d.BeginProperty(1);
        Assert.Equal("body", d.ReadString());
        d.EndObject();
        Assert.True(d.IsAtEnd);
    }

    [Fact]
    public void RoundTrip_Hugeint()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);

        Int128 sample = ((Int128)0x0123456789ABCDEFL << 64) | 0xFEDCBA9876543210UL;
        s.WriteHugeInt(sample);

        BinaryDeserializer d = new(writer.WrittenMemory);
        Assert.Equal(sample, d.ReadHugeInt());
    }

    [Fact]
    public void RoundTrip_HugeintZeroAndNegative()
    {
        foreach (Int128 sample in new Int128[] { 0, 1, -1, Int128.MinValue, Int128.MaxValue })
        {
            ArrayBufferWriter<byte> writer = new();
            BinarySerializer s = new(writer);
            s.WriteHugeInt(sample);

            BinaryDeserializer d = new(writer.WrittenMemory);
            Assert.Equal(sample, d.ReadHugeInt());
        }
    }

    [Fact]
    public void RoundTrip_Nullable()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.BeginNullable(true);
        s.WriteUnsignedLeb128(42UL);
        s.EndNullable();
        s.BeginNullable(false);
        s.EndNullable();

        BinaryDeserializer d = new(writer.WrittenMemory);
        Assert.True(d.BeginNullable());
        Assert.Equal(42UL, d.ReadUnsignedLeb128());
        d.EndNullable();
        Assert.False(d.BeginNullable());
        d.EndNullable();
    }

    [Fact]
    public void RoundTrip_List()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        string[] items = ["alpha", "beta", "gamma"];
        s.BeginList((ulong)items.Length);
        foreach (string item in items)
        {
            s.WriteString(item);
        }
        s.EndList();

        BinaryDeserializer d = new(writer.WrittenMemory);
        ulong count = d.BeginList();
        Assert.Equal((ulong)items.Length, count);
        for (int i = 0; i < (int)count; i++)
        {
            Assert.Equal(items[i], d.ReadString());
        }
        d.EndList();
    }

    [Fact]
    public void EndObject_WithoutTerminator_Throws()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteProperty(fieldId: 1, (byte)1);   // no EndObject

        BinaryDeserializer d = new(writer.WrittenMemory);
        d.BeginObject();
        d.BeginProperty(1);
        Assert.Equal((byte)1, d.ReadByte());
        Assert.Throws<SerializationException>(d.EndObject);
    }

    [Fact]
    public void BeginProperty_WrongFieldId_Throws()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteProperty(fieldId: 7, (byte)1);

        BinaryDeserializer d = new(writer.WrittenMemory);
        Assert.Throws<SerializationException>(() => d.BeginProperty(5));
    }

    [Fact]
    public void TryBeginProperty_OutOfOrder_Throws()
    {
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        s.WriteProperty(fieldId: 3, (byte)1);

        BinaryDeserializer d = new(writer.WrittenMemory);
        Assert.Throws<SerializationException>(() => d.TryBeginProperty(5));
    }
}

using Quack.Data;

namespace Quack.Tests.Data;

public class DuckDbBitTests
{
    [Theory]
    [InlineData("11111111", new byte[] { 0x00, 0xFF })]
    [InlineData("00000000", new byte[] { 0x00, 0x00 })]
    [InlineData("1010", new byte[] { 0x04, 0xFA })]      // 1111_1010 (pad bits = 1)
    [InlineData("0", new byte[] { 0x07, 0xFE })]
    [InlineData("1", new byte[] { 0x07, 0xFF })]
    [InlineData("101010101", new byte[] { 0x07, 0xFF, 0x55 })]
    [InlineData("1111000011110000", new byte[] { 0x00, 0xF0, 0xF0 })]
    public void Encode_MatchesDuckDbWireFormat(string literal, byte[] expected)
    {
        Assert.Equal(expected, DuckDbBit.Encode(literal));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xFF }, "11111111")]
    [InlineData(new byte[] { 0x04, 0xFA }, "1010")]
    [InlineData(new byte[] { 0x07, 0xFE }, "0")]
    [InlineData(new byte[] { 0x07, 0xFF, 0x55 }, "101010101")]
    public void Decode_RoundTripsEncodedPayload(byte[] payload, string expected)
    {
        Assert.Equal(expected, DuckDbBit.Decode(payload));
    }

    [Fact]
    public void Empty_BitString_Encodes_To_SingleZeroByte()
    {
        // Empty bit string: len = 0, padding = 0, no data bytes.
        byte[] encoded = DuckDbBit.Encode("");
        Assert.Equal(new byte[] { 0x00 }, encoded);
        Assert.Equal("", DuckDbBit.Decode(encoded));
    }

    [Fact]
    public void Encode_RejectsNonBitCharacters()
    {
        Assert.Throws<ArgumentException>(() => DuckDbBit.Encode("10X1"));
    }

    [Fact]
    public void Decode_RejectsInvalidPaddingByte()
    {
        Assert.Throws<ArgumentException>(() => DuckDbBit.Decode(new byte[] { 0x08, 0x00 }));
    }

    [Fact]
    public void Decode_RejectsEmptyPayload()
    {
        Assert.Throws<ArgumentException>(() => DuckDbBit.Decode(ReadOnlySpan<byte>.Empty));
    }
}

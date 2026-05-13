using Quack.Serialization;

namespace Quack.Tests.Serialization;

public class Leb128Tests
{
    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(1UL, new byte[] { 0x01 })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0x80, 0x01 })]
    [InlineData(255UL, new byte[] { 0xFF, 0x01 })]
    [InlineData(300UL, new byte[] { 0xAC, 0x02 })]
    [InlineData(16383UL, new byte[] { 0xFF, 0x7F })]
    [InlineData(16384UL, new byte[] { 0x80, 0x80, 0x01 })]
    [InlineData(ulong.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public void Unsigned_RoundTrips(ulong value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[Leb128.MaxBytes];
        int written = Leb128.WriteUnsigned(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());

        ulong decoded = Leb128.ReadUnsigned(buffer[..written], out int read);
        Assert.Equal(value, decoded);
        Assert.Equal(written, read);
    }

    [Theory]
    [InlineData(0L, new byte[] { 0x00 })]
    [InlineData(1L, new byte[] { 0x01 })]
    [InlineData(-1L, new byte[] { 0x7F })]
    [InlineData(63L, new byte[] { 0x3F })]
    [InlineData(64L, new byte[] { 0xC0, 0x00 })]
    [InlineData(-64L, new byte[] { 0x40 })]
    [InlineData(-65L, new byte[] { 0xBF, 0x7F })]
    [InlineData(100L, new byte[] { 0xE4, 0x00 })]
    [InlineData(-100L, new byte[] { 0x9C, 0x7F })]
    [InlineData(long.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 })]
    [InlineData(long.MinValue, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x7F })]
    public void Signed_RoundTrips(long value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[Leb128.MaxBytes];
        int written = Leb128.WriteSigned(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());

        long decoded = Leb128.ReadSigned(buffer[..written], out int read);
        Assert.Equal(value, decoded);
        Assert.Equal(written, read);
    }

    [Fact]
    public void Unsigned_Truncated_Throws()
    {
        // 0x80 alone has the continuation bit set but no next byte.
        Assert.Throws<SerializationException>(() =>
            Leb128.ReadUnsigned(new byte[] { 0x80 }, out _));
    }

    [Fact]
    public void Signed_Truncated_Throws()
    {
        Assert.Throws<SerializationException>(() =>
            Leb128.ReadSigned(new byte[] { 0x80 }, out _));
    }

    [Fact]
    public void Unsigned_Overflow_Throws()
    {
        // 11 continuation bytes — definitely past 64 bits.
        byte[] tooLong = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        Assert.Throws<SerializationException>(() => Leb128.ReadUnsigned(tooLong, out _));
    }
}

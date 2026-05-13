using Quack.Transport;

namespace Quack.Tests.Transport;

public class QuackUriTests
{
    [Theory]
    [InlineData("quack:localhost", "localhost", 9494, false)]
    [InlineData("quack:localhost:1234", "localhost", 1234, false)]
    [InlineData("quack://localhost", "localhost", 9494, false)]
    [InlineData("quack:127.0.0.1", "127.0.0.1", 9494, false)]
    [InlineData("quack:example.com", "example.com", 9494, true)]
    [InlineData("quack:example.com:8080", "example.com", 8080, true)]
    public void Parse_StandardCases(string uri, string host, int port, bool useSsl)
    {
        QuackUri parsed = QuackUri.Parse(uri);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(useSsl, parsed.UseSsl);
    }

    [Theory]
    [InlineData("quack:[::1]", "::1", 9494, false)]
    [InlineData("quack:[::1]:1234", "::1", 1234, false)]
    [InlineData("quack:[2001:db8::1]:443", "2001:db8::1", 443, true)]
    public void Parse_IPv6Cases(string uri, string host, int port, bool useSsl)
    {
        QuackUri parsed = QuackUri.Parse(uri);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(useSsl, parsed.UseSsl);
    }

    [Fact]
    public void Parse_ExplicitSsl_OverridesDefault()
    {
        Assert.True(QuackUri.Parse("quack:localhost", useSsl: true).UseSsl);
        Assert.False(QuackUri.Parse("quack:example.com", useSsl: false).UseSsl);
    }

    [Fact]
    public void HttpUrl_BuildsExpectedEndpoint()
    {
        Assert.Equal("http://localhost:9494/quack", QuackUri.Parse("quack:localhost").HttpUrl.ToString());
        Uri https = QuackUri.Parse("quack:example.com:443").HttpUrl;
        Assert.Equal("example.com", https.Host);
        Assert.Equal(443, https.Port);
        Assert.Equal("/quack", https.AbsolutePath);
        Assert.Equal("https", https.Scheme);
        Assert.Equal("http://[::1]:9494/quack", QuackUri.Parse("quack:[::1]").HttpUrl.ToString());
    }

    [Fact]
    public void CanonicalUri_IncludesPort()
    {
        Assert.Equal("quack:localhost:9494", QuackUri.Parse("quack:localhost").CanonicalUri);
        Assert.Equal("quack:[::1]:9494", QuackUri.Parse("quack:[::1]").CanonicalUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost")]
    [InlineData("quack:")]
    [InlineData("quack:host:abc")]
    [InlineData("quack:host:0")]
    [InlineData("quack:host:70000")]
    [InlineData("quack:[")]
    [InlineData("quack:[::1")]
    public void Parse_BadInputs_Throw(string uri)
    {
        Assert.Throws<ArgumentException>(() => QuackUri.Parse(uri));
    }
}

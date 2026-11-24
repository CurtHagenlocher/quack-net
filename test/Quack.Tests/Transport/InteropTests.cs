// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using Quack.Protocol;
using Quack.Transport;

namespace Quack.Tests.Transport;

// The URL form of the other quack ADBC drivers, and the transport's handling
// of must_invalidate and its content type.
public class InteropTests
{
    [Theory]
    [InlineData("quack://db.example:9494/?token=abc&tls=true", "db.example", 9494, true, false, "abc")]
    [InlineData("quack://db.example:9494?token=a%20b%26c&tls=skip_verify", "db.example", 9494, true, true, "a b&c")]
    [InlineData("quack://localhost:9494/?token=t&tls=false", "localhost", 9494, false, false, "t")]
    [InlineData("quack://localhost:9494/", "localhost", 9494, false, false, null)]
    [InlineData("quack://db.example/?other=1", "db.example", 9494, true, false, null)]
    [InlineData("quack://[::1]:7000/?tls=true&token=x&token=y", "::1", 7000, true, false, "x")]
    public void UrlForm_TokenAndTls(string uri, string host, int port, bool useSsl, bool skipVerify, string? token)
    {
        QuackUri parsed = QuackUri.Parse(uri);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(useSsl, parsed.UseSsl);
        Assert.Equal(skipVerify, parsed.SkipCertificateVerification);
        Assert.Equal(token, parsed.Token);
        Assert.DoesNotContain("token", parsed.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("quack://db.example/?tls=maybe")]
    [InlineData("quack://db.example/?tls=true&tls=false")]
    [InlineData("quack://db.example/path")]
    public void UrlForm_Invalid(string uri) => Assert.Throws<ArgumentException>(() => QuackUri.Parse(uri));

    [Fact]
    public void UrlForm_TlsContradictingUseSsl_Throws()
        => Assert.Throws<ArgumentException>(() => QuackUri.Parse("quack://db.example/?tls=false", useSsl: true));

    // An error that says the server's database died leaves the transport broken.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MustInvalidate_BreaksTheTransport(bool mustInvalidate, bool broken)
    {
        Fake handler = new(new ErrorResponse { Message = "database died", ExceptionType = "FATAL", MustInvalidate = mustInvalidate });
        using QuackTransport transport = new(QuackUri.Parse("quack:localhost:9494"), new HttpClient(handler));
        QuackException e = await Assert.ThrowsAsync<QuackException>(
            () => transport.SendAsync(new HeartbeatRequestMessage { ConnectionId = "c" }));
        Assert.Equal(mustInvalidate, e.ServerMustInvalidate);
        Assert.Equal(broken, transport.IsBroken);
        Assert.Equal("application/vnd.duckdb", handler.ContentType);
    }

    private sealed class Fake(QuackMessage response) : HttpMessageHandler
    {
        public string? ContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response.ToBytes()) });
        }
    }
}

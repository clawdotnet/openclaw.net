using Microsoft.AspNetCore.Http;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewaySecurityTests
{
    [Fact]
    public void GetToken_PrefersAuthorizationHeader()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = "Bearer abc";
        ctx.Request.QueryString = new QueryString("?token=zzz");

        var token = GatewaySecurity.GetToken(ctx, allowQueryStringToken: true);
        Assert.Equal("abc", token);
    }

    [Fact]
    public void GetToken_RejectsQueryTokenWhenDisabled()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?token=zzz");

        var token = GatewaySecurity.GetToken(ctx, allowQueryStringToken: false);
        Assert.Null(token);
    }

    [Fact]
    public void GetBearerToken_RejectsWhitespaceBearerToken()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = "Bearer    ";

        var token = GatewaySecurity.GetBearerToken(ctx);

        Assert.Null(token);
    }

    [Fact]
    public void IsTokenValid_AcceptsExactMatch()
    {
        Assert.True(GatewaySecurity.IsTokenValid("abc", "abc"));
        Assert.False(GatewaySecurity.IsTokenValid("abc", "abcd"));
    }

    [Fact]
    public void IsHmacSha256SignatureValid_AcceptsHexWithOrWithoutPrefix()
    {
        const string secret = "test-secret";
        const string payload = "{\"ok\":true}";
        var hex = GatewaySecurity.ComputeHmacSha256Hex(secret, payload);

        Assert.True(GatewaySecurity.IsHmacSha256SignatureValid(secret, payload, hex));
        Assert.True(GatewaySecurity.IsHmacSha256SignatureValid(secret, payload, $"sha256={hex.ToUpperInvariant()}"));
    }

    [Fact]
    public void IsHmacSha256SignatureValid_RejectsInvalidSignature()
    {
        Assert.False(GatewaySecurity.IsHmacSha256SignatureValid("test-secret", "{\"ok\":true}", "sha256=deadbeef"));
    }
}

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
    public void IsTokenValid_AcceptsExactMatch()
    {
        Assert.True(GatewaySecurity.IsTokenValid("abc", "abc"));
        Assert.False(GatewaySecurity.IsTokenValid("abc", "abcd"));
    }
}


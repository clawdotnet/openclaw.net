using System.Text.Json;
using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class NacosArchiveTests
{
    [Fact]
    public void MatchesPinnedSha256_AcceptsOfficialDigest()
    {
        Assert.True(NacosServer.MatchesPinnedSha256(
            "da5eec77934140133fe93e5532079e4e99b4cae7eb50463f2f6cd2bc8f380a70"));
    }

    [Fact]
    public void MatchesPinnedSha256_RejectsDifferentDigest()
    {
        Assert.False(NacosServer.MatchesPinnedSha256(new string('0', 64)));
    }

    [Fact]
    public void HasAccessToken_AcceptsLoginResponseWithoutCodeField()
    {
        using var response = JsonDocument.Parse("""{"accessToken":"test-token"}""");

        Assert.True(NacosServer.HasAccessToken(response.RootElement));
    }

    [Fact]
    public void SelectPorts_UsesRouterDefaultConsolePort()
    {
        var ports = NacosServer.SelectPorts();

        Assert.Equal(8080, ports.ConsolePort);
        Assert.DoesNotContain(ports.ConsolePort, new[] { ports.ServerPort, ports.RouterPort, ports.FixturePort });
    }
}
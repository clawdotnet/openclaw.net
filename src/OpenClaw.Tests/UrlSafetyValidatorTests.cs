using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class UrlSafetyValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://10.0.0.2")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public async Task ValidateHttpUrlAsync_BlocksPrivateTargetsByDefault(string url)
    {
        var result = await UrlSafetyValidator.ValidateHttpUrlAsync(new Uri(url), new UrlSafetyConfig(), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Contains("blocked", result.ToToolError(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateHttpUrlAsync_HonorsConfiguredHostBlocklist()
    {
        var result = await UrlSafetyValidator.ValidateHttpUrlAsync(
            new Uri("https://docs.example.com"),
            new UrlSafetyConfig
            {
                BlockPrivateNetworkTargets = false,
                BlockedHostGlobs = ["*.example.com"]
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Contains("blocklist", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateHttpUrl_HonorsConfiguredIpv6CidrBlocklist()
    {
        var result = UrlSafetyValidator.ValidateHttpUrl(
            new Uri("https://[2001:db8::42]/"),
            new UrlSafetyConfig
            {
                BlockPrivateNetworkTargets = false,
                BlockedCidrs = ["2001:db8::/32"]
            },
            resolveDns: false);

        Assert.False(result.Allowed);
        Assert.Contains("CIDR", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebFetchTool_BlocksLoopbackBeforeHttpRequest()
    {
        using var tool = new WebFetchTool(new WebFetchConfig { Enabled = true });

        var result = await tool.ExecuteAsync("""{"url":"http://127.0.0.1:18789"}""", CancellationToken.None);

        Assert.Contains("URL blocked", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserTool_CreateSandboxRequest_BlocksLoopbackGoto()
    {
        var tool = new BrowserTool(new ToolingConfig { EnableBrowserTool = true });

        var ex = Assert.Throws<ToolSandboxException>(() =>
            ((ISandboxCapableTool)tool).CreateSandboxRequest("""{"action":"goto","url":"http://127.0.0.1:18789"}"""));

        Assert.Contains("URL blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

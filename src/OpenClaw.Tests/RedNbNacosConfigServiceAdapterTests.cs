using OpenClaw.Gateway.Mcp.Nacos;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Issue #238: the RedNb.Nacos adapter's translation surface. The real SDK
/// round-trip (live Nacos GetConfig / AddListener) is the live DoD check, not
/// a unit test — here we pin the stable defaults and the pure ConfigInfo →
/// NacosConfig mapping the adapter relies on.
/// </summary>
public sealed class RedNbNacosConfigServiceAdapterTests
{
    [Fact]
    public void NacosOptions_Defaults_AreStable()
    {
        var o = new NacosOptions();
        Assert.Equal("openclaw-mcp.json", o.DataId);
        Assert.Equal("DEFAULT_GROUP", o.Group);
        Assert.True(o.Enabled);
        Assert.Equal(10_000, o.LongPollingTimeoutMs);
    }

    [Fact]
    public void Translate_MapsSdkConfigInfoToGatewayNacosConfig()
    {
        var info = new RedNb.Nacos.Config.ConfigInfo
        {
            DataId = "d",
            Group = "g",
            Content = "{\"mcpServers\":{}}",
            Type = "json",
        };

        var translated = RedNbNacosConfigService.Translate(info);

        Assert.Equal("d", translated.DataId);
        Assert.Equal("g", translated.Group);
        Assert.Equal("{\"mcpServers\":{}}", translated.Content);
    }
}
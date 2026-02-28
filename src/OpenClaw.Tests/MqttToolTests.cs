using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MqttToolTests
{
    [Fact]
    public async Task MqttPublishTool_PolicyDenies_Topic()
    {
        var cfg = new MqttConfig
        {
            Enabled = true,
            Policy = new MqttPolicyConfig
            {
                AllowPublishTopicGlobs = ["home/*"],
                DenyPublishTopicGlobs = ["home/secret*"]
            }
        };

        var tool = new MqttPublishTool(cfg);
        var result = await tool.ExecuteAsync("""{"op":"publish","topic":"home/secret","payload":"x"}""", CancellationToken.None);
        Assert.Contains("not allowed by policy", result);
    }

    [Fact]
    public async Task MqttTool_GetLast_ReturnsCachedMessage()
    {
        MqttMessageCache.Set("home/test", "hello");

        var cfg = new MqttConfig { Enabled = true };
        var tool = new MqttTool(cfg);

        var result = await tool.ExecuteAsync("""{"op":"get_last","topic":"home/test"}""", CancellationToken.None);
        Assert.Contains("hello", result);
    }
}


using System.Text.Json;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HomeAutomationToolSchemaTests
{
    [Fact]
    public void HomeAssistantTool_ParameterSchema_IsValidJson()
    {
        var tool = new HomeAssistantTool(new HomeAssistantConfig
        {
            Enabled = true,
            TokenRef = "raw:test"
        });

        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("op", out _));
    }

    [Fact]
    public void HomeAssistantWriteTool_ParameterSchema_IsValidJson()
    {
        using var tool = new HomeAssistantWriteTool(new HomeAssistantConfig
        {
            Enabled = true,
            TokenRef = "raw:test"
        });

        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("domain", out _));
    }

    [Fact]
    public void MqttTool_ParameterSchema_IsValidJson()
    {
        var tool = new MqttTool(new MqttConfig { Enabled = true });
        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("topic", out _));
    }

    [Fact]
    public void MqttPublishTool_ParameterSchema_IsValidJson()
    {
        var tool = new MqttPublishTool(new MqttConfig { Enabled = true });
        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("payload", out _));
    }
}


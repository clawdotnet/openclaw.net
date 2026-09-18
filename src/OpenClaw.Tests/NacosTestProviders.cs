using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
namespace OpenClaw.Tests;
internal static class NacosTestProviders
{
    public static CapabilityProviderRegistry Create(McpServerToolRegistry registry) => new([NacosCapabilityProvider.FromSettings(registry, new Dictionary<string, System.Text.Json.JsonElement> { ["nacos"] = System.Text.Json.JsonDocument.Parse("""{"retrySafeTargets":["weather-mcp/get_weather"]}""").RootElement.Clone() })], "nacos");
}

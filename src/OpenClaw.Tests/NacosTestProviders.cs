using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
namespace OpenClaw.Tests;
internal static class NacosTestProviders
{
    public static CapabilityProviderRegistry Create(McpServerToolRegistry registry) => new([new NacosCapabilityProvider(registry, retrySafeTargets: new HashSet<string> { "weather-mcp/get_weather" })], "nacos");
}

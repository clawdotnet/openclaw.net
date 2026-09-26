using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class RouterConfigurationTests
{
    [Fact]
    public void BuildRouterEnvironment_ContainsRouterSettingsAndExcludesSmokeSettings()
    {
        var deployment = CreateDeployment();

        var environment = RouterConfiguration.BuildRouterEnvironment(
            deployment,
            24001,
            "C:\\models\\all-MiniLM",
            "C:\\data\\router");

        Assert.Equal("127.0.0.1:8848", environment["NACOS_ADDR"]);
        Assert.Equal("nacos", environment["NACOS_USERNAME"]);
        Assert.Equal("secret", environment["NACOS_PASSWORD"]);
        Assert.Equal(string.Empty, environment["NACOS_NAMESPACE"]);
        Assert.Equal("streamable_http", environment["TRANSPORT_TYPE"]);
        Assert.Equal("24001", environment["PORT"]);
        Assert.Equal("10", environment["UPDATE_INTERVAL"]);
        Assert.Equal("C:\\models\\all-MiniLM", environment["EMBEDDING_MODEL_DIR"]);
        Assert.Equal("C:\\data\\router", environment["SONNETDB_DATA_DIR"]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("OPENCLAW_NACOS_", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSmokeEnvironment_ContainsOnlyOpenClawNacosSettings()
    {
        var deployment = CreateDeployment();

        var environment = RouterConfiguration.BuildSmokeEnvironment(
            deployment,
            "http://127.0.0.1:24001/mcp");

        Assert.Equal("1", environment["OPENCLAW_NACOS_LIVE"]);
        Assert.Equal("http://127.0.0.1:24001/mcp", environment["OPENCLAW_NACOS_ROUTER_URL"]);
        Assert.Equal("127.0.0.1:8848", environment["OPENCLAW_NACOS_SERVER"]);
        Assert.Equal("nacos", environment["OPENCLAW_NACOS_USERNAME"]);
        Assert.Equal("secret", environment["OPENCLAW_NACOS_PASSWORD"]);
        Assert.Equal(5, environment.Count);
        Assert.DoesNotContain("NACOS_ADDR", environment.Keys);
        Assert.DoesNotContain("EMBEDDING_MODEL_DIR", environment.Keys);
    }

    private static NacosDeployment CreateDeployment() => new(
        "127.0.0.1:8848",
        8848,
        8080,
        24001,
        24002,
        "nacos",
        "secret",
        null!);
}
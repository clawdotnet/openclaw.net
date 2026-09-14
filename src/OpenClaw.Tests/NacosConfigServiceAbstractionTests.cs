using OpenClaw.Gateway.Mcp.Nacos;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FakeNacosConfigServiceTests
{
    [Fact]
    public async Task GetConfigAsync_ReturnsNullWhenUnset()
    {
        var fake = new FakeNacosConfigService();
        var result = await fake.GetConfigAsync("d", "g", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void Publish_InvokesSubscribedListener()
    {
        var fake = new FakeNacosConfigService();
        NacosConfig? captured = null;
        var handle = fake.AddListener("d", "g", cfg => captured = cfg);

        fake.Publish(new NacosConfig("d", "g", "{\"x\":1}"));
        Assert.NotNull(captured);
        Assert.Equal("{\"x\":1}", captured!.Content);

        handle.Dispose();
        fake.Publish(new NacosConfig("d", "g", "{\"x\":2}"));
        Assert.Equal("{\"x\":1}", captured.Content); // disposed handle → no more callbacks
    }

    [Fact]
    public void Publish_OnlyMatchesSubscribedDataIdAndGroup()
    {
        var fake = new FakeNacosConfigService();
        var a = 0;
        var b = 0;
        fake.AddListener("a", "g1", _ => a++);
        fake.AddListener("b", "g1", _ => b++);

        fake.Publish(new NacosConfig("a", "g1", "{}"));
        Assert.Equal(1, a);
        Assert.Equal(0, b);
    }
}
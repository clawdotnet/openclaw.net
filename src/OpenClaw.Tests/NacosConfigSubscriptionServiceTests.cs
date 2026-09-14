using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Mcp.Nacos;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Issue #238: NacosConfigSubscriptionService owns the listener lifecycle —
/// on publish, it funnels the change into McpWorkspaceWatcherService.TriggerReload;
/// missing ServerAddr degrades to a no-op (TTL/reload fallback stays active).
/// </summary>
public sealed class NacosConfigSubscriptionServiceTests
{
    private static NacosConfigSubscriptionService Build(
        FakeNacosConfigService fake, NacosOptions options, IMcpWorkspaceReloadTrigger trigger)
        => new(fake, options, trigger, NullLogger<NacosConfigSubscriptionService>.Instance);

    [Fact]
    public async Task OnChange_TriggersWatcherReload()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<IMcpWorkspaceReloadTrigger>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{\"mcpServers\":{}}"));

        trigger.Received().TriggerReload();
    }

    [Fact]
    public async Task OnChange_ForUnrelatedDataId_DoesNotTrigger()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<IMcpWorkspaceReloadTrigger>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("other", "g", "{}"));

        trigger.DidNotReceive().TriggerReload();
    }

    [Fact]
    public async Task EmptyServerAddr_StartAsync_IsNoOp()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<IMcpWorkspaceReloadTrigger>();
        var options = new NacosOptions { ServerAddr = null, DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{}"));

        trigger.DidNotReceive().TriggerReload(); // never subscribed
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesListener()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<IMcpWorkspaceReloadTrigger>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        var svc = Build(fake, options, trigger);
        await svc.StartAsync(CancellationToken.None);

        await svc.DisposeAsync();
        fake.Publish(new NacosConfig("d", "g", "{}"));

        trigger.DidNotReceive().TriggerReload();
    }
}
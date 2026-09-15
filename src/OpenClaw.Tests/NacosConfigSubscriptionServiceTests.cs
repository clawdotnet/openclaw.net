using OpenClaw.Core.Skills.Meta;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Adapters.Nacos.Events;
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
        FakeNacosConfigService fake, NacosOptions options, ICapabilityInvalidationSink trigger)
        => new(fake, options, trigger, NullLogger<NacosConfigSubscriptionService>.Instance);

    [Fact]
    public async Task OnChange_TriggersWatcherReload()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<ICapabilityInvalidationSink>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{\"mcpServers\":{}}"));

        trigger.Received().Invalidate(Arg.Any<CapabilityChange>());
    }

    [Fact]
    public async Task OnChange_ForUnrelatedDataId_DoesNotTrigger()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<ICapabilityInvalidationSink>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("other", "g", "{}"));

        trigger.DidNotReceive().Invalidate(Arg.Any<CapabilityChange>());
    }

    [Fact]
    public async Task EmptyServerAddr_StartAsync_IsNoOp()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<ICapabilityInvalidationSink>();
        var options = new NacosOptions { ServerAddr = null, DataId = "d", Group = "g" };
        await using var svc = Build(fake, options, trigger);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{}"));

        trigger.DidNotReceive().Invalidate(Arg.Any<CapabilityChange>()); // never subscribed
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesListener()
    {
        var fake = new FakeNacosConfigService();
        var trigger = Substitute.For<ICapabilityInvalidationSink>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        var svc = Build(fake, options, trigger);
        await svc.StartAsync(CancellationToken.None);

        await svc.DisposeAsync();
        fake.Publish(new NacosConfig("d", "g", "{}"));

        trigger.DidNotReceive().Invalidate(Arg.Any<CapabilityChange>());
    }

    [Fact]
    public async Task FailedRegistration_RetriesWithoutBlockingStartup_AndDisposesHandle()
    {
        var config = Substitute.For<INacosConfigService>();
        var registration = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = Substitute.For<IDisposable>();
        var calls = 0;
        config.AddListenerAsync("d", "g", Arg.Any<Action<NacosConfig>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<IDisposable>(new IOException("offline")) : registration.Task);
        var svc = new NacosConfigSubscriptionService(config,
            new() { ServerAddr = "test", DataId = "d", Group = "g", ReconnectDelayMs = 10 },
            Substitute.For<ICapabilityInvalidationSink>(), NullLogger<NacosConfigSubscriptionService>.Instance);
        await svc.StartAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("degraded", svc.Status);
        registration.SetResult(handle);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (svc.Status != "active") await Task.Delay(10, deadline.Token);
        Assert.Equal(2, calls);
        await svc.DisposeAsync();
        handle.Received(1).Dispose();
        Assert.Equal("stopped", svc.Status);
    }

    [Fact]
    public async Task Dispose_CancelsPendingStartup()
    {
        var config = Substitute.For<INacosConfigService>();
        config.GetConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call => { await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); return (NacosConfig?)null; });
        var svc = new NacosConfigSubscriptionService(config, new() { ServerAddr = "test" },
            Substitute.For<ICapabilityInvalidationSink>(), NullLogger<NacosConfigSubscriptionService>.Instance);
        await svc.StartAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("starting", svc.Status);
        await svc.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await config.DidNotReceive().AddListenerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Action<NacosConfig>>(), Arg.Any<CancellationToken>());
    }
}
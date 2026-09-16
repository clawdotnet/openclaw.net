using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefPrewarmServiceTests
{
    private static (VaultRefPrewarmService svc, ISecretResolver resolver, VaultSecurityOptions opts) Build(
        bool prewarmRequired, params string[] refs)
    {
        var opts = new VaultSecurityOptions
        {
            Enabled = true,
            Address = "https://vault.example",
            TokenRef = "env:X",
            PrewarmRefs = refs.ToList(),
            PrewarmRequired = prewarmRequired,
        };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>("value"));
        var sp = new ServiceCollection().BuildServiceProvider();
        var svc = new VaultRefPrewarmService(opts, resolver, sp, Substitute.For<ILogger<VaultRefPrewarmService>>());
        return (svc, resolver, opts);
    }

    [Fact]
    public async Task StartAsync_AllSuccess_NoThrow()
    {
        var (svc, resolver, _) = Build(prewarmRequired: true, "vault:secret/data/x#k", "vault:secret/data/y#k");
        await svc.WarmAsync(CancellationToken.None);
        await resolver.Received(2).ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ScansGatewayConfigForVaultRefs()
    {
        var config = new GatewayConfig();
        config.Channels.Telegram.BotTokenRef = "vault:secret/data/telegram#bot_token";
        var opts = new VaultSecurityOptions { Enabled = true, Address = "https://v", TokenRef = "env:X" };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>("value"));
        var sp = new ServiceCollection().AddSingleton(config).BuildServiceProvider();
        var svc = new VaultRefPrewarmService(opts, resolver, sp, Substitute.For<ILogger<VaultRefPrewarmService>>());

        await svc.WarmAsync(CancellationToken.None);

        await resolver.Received(1).ResolveAsync("vault:secret/data/telegram#bot_token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_OneFail_PrewarmRequired_Throws()
    {
        var opts = new VaultSecurityOptions { Enabled = true, Address = "https://v", TokenRef = "env:X", PrewarmRefs = ["vault:secret/data/x#k"], PrewarmRequired = true };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync("vault:secret/data/x#k", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>(null)); // miss
        var svc = new VaultRefPrewarmService(opts, resolver, new ServiceCollection().BuildServiceProvider(),
            Substitute.For<ILogger<VaultRefPrewarmService>>());

        await Assert.ThrowsAsync<HostingStartupException>(() => svc.WarmAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_OneFail_PrewarmNotRequired_LogsAndContinues()
    {
        var opts = new VaultSecurityOptions { Enabled = true, Address = "https://v", TokenRef = "env:X", PrewarmRefs = ["vault:secret/data/x#k"], PrewarmRequired = false };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync("vault:secret/data/x#k", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>(null));
        var logger = Substitute.For<ILogger<VaultRefPrewarmService>>();
        var svc = new VaultRefPrewarmService(opts, resolver, new ServiceCollection().BuildServiceProvider(), logger);

        await svc.WarmAsync(CancellationToken.None); // no throw
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task StartAsync_HostCancellation_Propagates()
    {
        var (svc, resolver, _) = Build(prewarmRequired: false, "vault:secret/data/x#k");
        using var cts = new CancellationTokenSource();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<string?>(Task.FromCanceled<string?>(cts.Token)));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.WarmAsync(cts.Token));
    }

    [Fact]
    public async Task WarmBeforeRuntime_IsIdempotent_AndScansJsonSettingsAndArrays()
    {
        var config = new GatewayConfig();
        config.Security.Vault = new() { Enabled = true, PrewarmRefs = ["vault:secret/data/array#key"] };
        config.Plugins.Entries["probe"] = new() { Config = System.Text.Json.JsonDocument.Parse(
            """{"nested":[{"credential":"vault:secret/data/plugin#key"}]}""").RootElement.Clone() };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<string?>("value"));
        using var services = new ServiceCollection().AddSingleton(config).BuildServiceProvider();
        using var service = new VaultRefPrewarmService(config.Security.Vault, resolver, services,
            Substitute.For<ILogger<VaultRefPrewarmService>>());
        await service.WarmAsync(CancellationToken.None);
        await service.WarmAsync(CancellationToken.None);
        await resolver.Received(1).ResolveAsync("vault:secret/data/plugin#key", Arg.Any<CancellationToken>());
        await resolver.Received(1).ResolveAsync("vault:secret/data/array#key", Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task HostedRefresh_KeepsWarmedReferencesActive_AndStopsCleanly()
    {
        var (service, resolver, options) = Build(true, "vault:secret/data/x#k");
        using var owned = service;
        options.CacheTtl = TimeSpan.FromMilliseconds(100);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1) refreshed.TrySetResult();
            return ValueTask.FromResult<string?>("value");
        });
        await service.StartAsync(TestContext.Current.CancellationToken);
        try { await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { await service.StopAsync(CancellationToken.None); }
        Assert.True(service.ExecuteTask!.IsCompleted);
    }
}

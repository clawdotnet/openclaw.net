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
        await svc.StartAsync(CancellationToken.None);
        await resolver.Received(2).ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        await Assert.ThrowsAsync<HostingStartupException>(() => svc.StartAsync(CancellationToken.None));
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

        await svc.StartAsync(CancellationToken.None); // no throw
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}

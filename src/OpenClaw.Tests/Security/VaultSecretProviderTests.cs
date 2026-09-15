using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultSecretProviderTests
{
    private static (VaultSecretProvider provider, IVaultClient client, VaultRefCache cache) Build(
        Action<VaultSecurityOptions>? configure = null,
        TimeSpan? ttl = null)
    {
        var opts = new VaultSecurityOptions();
        configure?.Invoke(opts);
        var memCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance,
            ttl ?? TimeSpan.FromMinutes(5));
        var client = Substitute.For<IVaultClient>();
        var provider = new VaultSecretProvider(client, cache, opts, NullLogger<VaultSecretProvider>.Instance);
        return (provider, client, cache);
    }

    [Fact]
    public void Scheme_IsVault()
        => Assert.Equal("vault", Build().provider.Scheme);

    [Fact]
    public void CanResolve_VaultPrefix_True()
        => Assert.True(Build().provider.CanResolve("vault:secret/data/x#k"));

    [Fact]
    public void CanResolve_EnvPrefix_False()
        => Assert.False(Build().provider.CanResolve("env:X"));

    [Fact]
    public async Task ResolveAsync_CacheMiss_FetchesAndCaches()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "sk-xyz" });

        var v = await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        Assert.Equal("sk-xyz", v);
        await client.Received(1).ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_CacheHit_NoHttpCall()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "sk-xyz" });

        await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        await client.Received(1).ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSync_CacheHit_ReturnsWithoutHttpCall()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "sk-xyz" });
        await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);

        Assert.Equal("sk-xyz", provider.ResolveSync("vault:secret/data/openclaw/openai#api_key"));
        await client.Received(1).ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResolveSync_CacheMiss_FailsWithoutHttpCall()
    {
        var (provider, client, _) = Build();

        Assert.Throws<SecretResolutionException>(() =>
            provider.ResolveSync("vault:secret/data/openclaw/openai#api_key"));
        client.DidNotReceiveWithAnyArgs().ReadSecretV2Async(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsNull_Throws_PathNotFound()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Dictionary<string, object>?)null);

        await Assert.ThrowsAsync<VaultPathNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsDictMissingKey_Throws_KeyNotFound()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["other"] = "x" });

        await Assert.ThrowsAsync<VaultKeyNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_VaultThrowsHttp_PropagatesAsUnavailable()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("network"));

        await Assert.ThrowsAsync<VaultUnavailableException>(() =>
            provider.ResolveAsync("vault:secret/data/x#k", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_InvalidRef_Throws_VaultRefParseException()
    {
        var (provider, _, _) = Build();
        await Assert.ThrowsAsync<VaultRefParseException>(() =>
            provider.ResolveAsync("vault:secret/data/x", CancellationToken.None).AsTask()); // missing '#'
    }

    [Fact]
    public async Task ResolveAsync_NoValueInExceptionMessage()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "SUPER-SECRET-VALUE" });

        var ex = await Assert.ThrowsAsync<VaultKeyNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/x#missing_key", CancellationToken.None).AsTask());
        Assert.DoesNotContain("SUPER-SECRET-VALUE", ex.Message);
    }
}

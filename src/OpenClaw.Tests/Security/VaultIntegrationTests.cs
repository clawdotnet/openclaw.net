using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
using Xunit;

namespace OpenClaw.Tests.Security;

[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    private const string DefaultMount = "secret";

    private static bool ShouldRun()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_ADDR")) &&
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_TOKEN"));
    }

    private static string Addr => Environment.GetEnvironmentVariable("OPENBAO_ADDR")!;
    private static string Token => Environment.GetEnvironmentVariable("OPENBAO_TOKEN")!;

    [Fact]
    public async Task Smoke_PingOpenBao()
    {
        Assert.SkipUnless(ShouldRun(), "Set OPENBAO_ADDR and OPENBAO_TOKEN for live Vault integration tests.");

        using var http = new HttpClient { BaseAddress = new Uri(Addr) };
        using var resp = await http.GetAsync("/v1/sys/health");
        Assert.True(resp.IsSuccessStatusCode);
    }

    [Fact]
    public async Task End2End_PutAndResolve_KvV2()
    {
        Assert.SkipUnless(ShouldRun(), "Set OPENBAO_ADDR and OPENBAO_TOKEN for live Vault integration tests.");

        var path = $"openclaw-e2e/put-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "sk-e2e" });
            var (resolver, _) = CreateResolver(TimeSpan.FromMinutes(5));

            var value = await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None);
            Assert.Equal("sk-e2e", value);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    [Fact]
    public async Task End2End_TtlExpiry_FetchesAgain()
    {
        Assert.SkipUnless(ShouldRun(), "Set OPENBAO_ADDR and OPENBAO_TOKEN for live Vault integration tests.");

        var path = $"openclaw-e2e/ttl-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v1" });
            var (resolver, client) = CreateResolver(TimeSpan.FromMilliseconds(500));

            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            Assert.Equal(1, client.Calls);
            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            Assert.Equal(1, client.Calls); // cache hit, no HTTP

            await Task.Delay(700); // pass TTL (500ms) but stay inside the stale window (2xTTL)
            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            // refresh-ahead kicks off in the background; poll for it instead of racing.
            for (var i = 0; i < 30 && client.Calls < 2; i++)
                await Task.Delay(100);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    [Fact]
    public async Task End2End_TokenUnauth_Throws_VaultAuthException()
    {
        Assert.SkipUnless(ShouldRun(), "Set OPENBAO_ADDR and OPENBAO_TOKEN for live Vault integration tests.");

        var client = new VaultSharpClient(Addr, "definitely-invalid-token", ns: null, new VaultTlsOptions());
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance, TimeSpan.FromMinutes(5));
        var provider = new VaultSecretProvider(client, cache, new VaultSecurityOptions(), NullLogger<VaultSecretProvider>.Instance);

        await Assert.ThrowsAsync<VaultAuthException>(() =>
            provider.ResolveAsync($"vault:{DefaultMount}/data/does/not/matter#k", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task End2End_RotatedValue_PickedUpAfterTtl()
    {
        Assert.SkipUnless(ShouldRun(), "Set OPENBAO_ADDR and OPENBAO_TOKEN for live Vault integration tests.");

        var path = $"openclaw-e2e/rotate-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v1" });
            var (resolver, _) = CreateResolver(TimeSpan.FromMilliseconds(500));
            var secretRef = $"vault:{DefaultMount}/data/{path}#api_key";

            Assert.Equal("v1", await resolver.ResolveAsync(secretRef, CancellationToken.None));

            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v2" });
            await Task.Delay(700); // pass TTL so the next resolve serves stale + refresh-ahead

            string? value = null;
            for (var i = 0; i < 30 && value != "v2"; i++)
            {
                value = await resolver.ResolveAsync(secretRef, CancellationToken.None);
                await Task.Delay(100);
            }
            Assert.Equal("v2", value);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    private static (ISecretResolver resolver, CountingClient client) CreateResolver(TimeSpan ttl)
    {
        var client = new CountingClient(new VaultSharpClient(Addr, Token, ns: null, new VaultTlsOptions()));
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance, ttl);
        var provider = new VaultSecretProvider(client, cache, new VaultSecurityOptions(), NullLogger<VaultSecretProvider>.Instance);
        return (new CompositeSecretResolver([provider], NullLogger<CompositeSecretResolver>.Instance), client);
    }

    private static async Task WriteSecretAsync(string path, IDictionary<string, object> data)
    {
        var client = new VaultSharp.VaultClient(new VaultClientSettings(Addr, new TokenAuthMethodInfo(Token)));
        await client.V1.Secrets.KeyValue.V2.WriteSecretAsync(path, data, checkAndSet: null, mountPoint: DefaultMount);
    }

    private static async Task DeleteSecretAsync(string path)
    {
        var client = new VaultSharp.VaultClient(new VaultClientSettings(Addr, new TokenAuthMethodInfo(Token)));
        await client.V1.Secrets.KeyValue.V2.DeleteSecretAsync(path, DefaultMount);
    }

    private sealed class CountingClient : OpenClaw.Security.Vault.IVaultClient
    {
        private readonly OpenClaw.Security.Vault.IVaultClient _inner;
        public int Calls { get; private set; }

        public CountingClient(OpenClaw.Security.Vault.IVaultClient inner) => _inner = inner;

        public async Task<Dictionary<string, object>?> ReadSecretV2Async(string mount, string path, CancellationToken ct)
        {
            Calls++;
            return await _inner.ReadSecretV2Async(mount, path, ct);
        }
    }
}

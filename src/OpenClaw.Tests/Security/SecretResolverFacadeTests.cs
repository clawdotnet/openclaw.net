using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

[Collection(ResolverAccessorCollection.Name)]
public sealed class SecretResolverFacadeTests : IDisposable
{
    public SecretResolverFacadeTests() => ResolverAccessor.Reset();
    public void Dispose() => ResolverAccessor.Reset();

    [Fact]
    public void Resolve_DIBootstrapped_DelegatesToRegisteredResolver()
    {
        var fake = new FakeResolver("from-fake");
        var sp = new ServiceCollection().AddSingleton<ISecretResolver>(fake).BuildServiceProvider();
        ResolverAccessor.Use(sp);

        Assert.Equal("from-fake", SecretResolver.Resolve("anything"));
        Assert.True(SecretResolver.IsRawRef("raw:x"));
    }

    [Fact]
    public void Resolve_DINotBootstrapped_UsesLegacy()
    {
        Assert.Equal("raw-literal", SecretResolver.Resolve("raw:raw-literal"));
        Assert.Null(SecretResolver.Resolve("env:DEFINITELY_NOT_SET_X_123"));
    }

    [Fact]
    public async Task ResolveAsync_DIBootstrapped_DelegatesAsync()
    {
        var fake = new FakeResolver("async-fake");
        var sp = new ServiceCollection().AddSingleton<ISecretResolver>(fake).BuildServiceProvider();
        ResolverAccessor.Use(sp);

        Assert.Equal("async-fake", await SecretResolver.ResolveAsync("anything"));
    }

    [Fact]
    public async Task ResolveAsync_DINotBootstrapped_UsesLegacy()
    {
        Assert.Equal("legacy-literal", await SecretResolver.ResolveAsync("raw:legacy-literal"));
    }

    [Fact]
    public void Resolve_VaultRefBeforeDIBootstrap_FailsClosed()
        => Assert.Throws<VaultNotConfiguredException>(() =>
            SecretResolver.Resolve("vault:secret/data/openclaw#key"));

    [Fact]
    public async Task ResolveAsync_VaultRefBeforeDIBootstrap_FailsClosed()
        => await Assert.ThrowsAsync<VaultNotConfiguredException>(() =>
            SecretResolver.ResolveAsync("vault:secret/data/openclaw#key").AsTask());

    private sealed class FakeResolver : ISecretResolver
    {
        private readonly string _value;
        public FakeResolver(string value) => _value = value;
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromResult<string?>(_value);
        public string? Resolve(string? secretRef) => _value;
        public bool IsRawRef(string? secretRef) => false;
    }
}

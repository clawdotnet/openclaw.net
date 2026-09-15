using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class CompositeSecretResolverTests
{
    [Fact]
    public async Task EmptyProviders_ResolveAsync_ReturnsNullForNullRef()
    {
        var resolver = new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance);
        Assert.Null(resolver.Resolve(null));
        Assert.Null(await resolver.ResolveAsync(null));
    }

    [Fact]
    public async Task ResolveAsync_DispatchesToFirstMatchingProvider()
    {
        var env = new EnvRawSecretProvider();
        var fakeVault = new FakeProvider("vault", "vault:secret/x#k", "vault-value");
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env, fakeVault }, NullLogger<CompositeSecretResolver>.Instance);

        Assert.Equal("vault-value", await resolver.ResolveAsync("vault:secret/x#k", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_NoMatch_FallsBackToEnvRawProvider()
    {
        var env = new EnvRawSecretProvider();
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env }, NullLogger<CompositeSecretResolver>.Instance);

        // Bare string with no env hit — composite defers to env provider which returns literal
        Assert.Equal("plain-text", await resolver.ResolveAsync("plain-text", CancellationToken.None));
    }

    [Fact]
    public void Resolve_Sync_DelegatesToProvider()
    {
        var env = new EnvRawSecretProvider();
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env }, NullLogger<CompositeSecretResolver>.Instance);

        Assert.Equal("value", resolver.Resolve("raw:value"));
    }

    [Fact]
    public void IsRawRef_True()
        => Assert.True(new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance).IsRawRef("raw:x"));

    [Fact]
    public void IsRawRef_False_Null()
        => Assert.False(new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance).IsRawRef(null));

    private sealed class FakeProvider : ISecretProvider
    {
        private readonly string _matchRef;
        private readonly string _value;
        public FakeProvider(string scheme, string matchRef, string value)
        {
            Scheme = scheme; _matchRef = matchRef; _value = value;
        }
        public string Scheme { get; }
        public bool CanResolve(string r) => r == _matchRef;
        public ValueTask<string?> ResolveAsync(string r, CancellationToken ct) => new(_value);
    }
}

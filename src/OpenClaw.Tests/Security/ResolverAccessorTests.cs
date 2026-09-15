using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

[Collection(ResolverAccessorCollection.Name)]
public sealed class ResolverAccessorTests : IDisposable
{
    public ResolverAccessorTests()
    {
        // Reset state for test isolation
        ResolverAccessor.Reset();
    }

    public void Dispose() => ResolverAccessor.Reset();

    [Fact]
    public void Current_BeforeUse_IsNull()
    {
        Assert.Null(ResolverAccessor.Current);
    }

    [Fact]
    public void Use_RegistersResolverFromServiceProvider()
    {
        var resolver = new FakeResolver();
        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver>(resolver);
        using var sp = services.BuildServiceProvider();

        ResolverAccessor.Use(sp);

        Assert.Same(resolver, ResolverAccessor.Current);
    }

    [Fact]
    public void Use_WhenNoResolverRegistered_CurrentStaysNull()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        ResolverAccessor.Use(sp);

        Assert.Null(ResolverAccessor.Current);
    }

    private sealed class FakeResolver : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromResult<string?>("fake");
        public string? Resolve(string? secretRef) => "fake";
        public bool IsRawRef(string? secretRef) => false;
    }
}

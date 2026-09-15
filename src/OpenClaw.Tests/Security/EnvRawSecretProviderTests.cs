using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class EnvRawSecretProviderTests
{
    [Fact]
    public void Scheme_IsEnv() => Assert.Equal("env", new EnvRawSecretProvider().Scheme);

    [Fact]
    public void CanResolve_EnvPrefix_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("env:FOO"));

    [Fact]
    public void CanResolve_RawPrefix_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("raw:hello"));

    [Fact]
    public void CanResolve_BareString_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("MY_VAR"));

    [Fact]
    public void CanResolve_VaultPrefix_False()
        => Assert.False(new EnvRawSecretProvider().CanResolve("vault:secret/x"));

    [Fact]
    public async Task ResolveAsync_EnvPrefix_ReadsEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_1", "v");
        try
        {
            var p = new EnvRawSecretProvider();
            Assert.True(p.CanResolve("env:OPENCLAW_PROVIDER_TEST_1"));
            Assert.Equal("v", await p.ResolveAsync("env:OPENCLAW_PROVIDER_TEST_1", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_1", null);
        }
    }

    [Fact]
    public async Task ResolveAsync_RawPrefix_ReturnsLiteral()
    {
        var p = new EnvRawSecretProvider();
        Assert.Equal("my-secret", await p.ResolveAsync("raw:my-secret", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_BareString_EnvHit()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_2", "env-value");
        try
        {
            var p = new EnvRawSecretProvider();
            Assert.Equal("env-value", await p.ResolveAsync("OPENCLAW_PROVIDER_TEST_2", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_2", null);
        }
    }

    [Fact]
    public async Task ResolveAsync_BareString_EnvMiss_ReturnsLiteral()
    {
        var p = new EnvRawSecretProvider();
        // Use a string unlikely to be set
        Assert.Equal("OPENCLAW_PROVIDER_TEST_DEFINITELY_UNSET", await p.ResolveAsync("OPENCLAW_PROVIDER_TEST_DEFINITELY_UNSET", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_EmptyRef_ReturnsNull()
        => Assert.Null(await new EnvRawSecretProvider().ResolveAsync("", CancellationToken.None));
}

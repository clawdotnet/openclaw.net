using Xunit;

namespace OpenClaw.Tests.Security;

[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    private static bool ShouldRun()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_ADDR")) &&
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_TOKEN"));
    }

    [Fact]
    public async Task Smoke_PingOpenBao()
    {
        // Skipped unless a live OpenBao/Vault instance is provided (see docs/security/vault-integration-tests.md).
        if (!ShouldRun())
            return;

        var addr = Environment.GetEnvironmentVariable("OPENBAO_ADDR")!;
        using var http = new HttpClient { BaseAddress = new Uri(addr) };
        using var resp = await http.GetAsync("/v1/sys/health");
        Assert.True(resp.IsSuccessStatusCode);
    }
}

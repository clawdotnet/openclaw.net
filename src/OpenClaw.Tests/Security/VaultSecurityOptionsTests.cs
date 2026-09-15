using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultSecurityOptionsTests
{
    [Fact]
    public void ConfigValidator_VaultEnabled_MissingAddress_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions { Enabled = true, TokenRef = "env:X" };
        // Address is null
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("Address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_VaultEnabled_TokenRefStartsWithVault_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions
        {
            Enabled = true,
            Address = "https://vault.example.com",
            TokenRef = "vault:secret/data/x#k"
        };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("TokenRef", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_CacheTtlTooShort_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions
        {
            Enabled = true, Address = "https://vault.example.com", TokenRef = "env:X",
            CacheTtl = TimeSpan.FromSeconds(10)
        };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("CacheTtl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_VaultDisabled_NoError()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions { Enabled = false };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.DoesNotContain(errors, e => e.Contains("Vault", StringComparison.OrdinalIgnoreCase));
    }

    private static GatewayConfig MakeValidConfig()
    {
        var cfg = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "loopback",
            Security = new SecurityConfig { AuthMode = "token" }
        };
        return cfg;
    }
}

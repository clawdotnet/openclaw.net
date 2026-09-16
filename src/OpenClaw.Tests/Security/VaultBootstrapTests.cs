using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using Xunit;
using Microsoft.Extensions.Hosting;
using OpenClaw.Security.Vault;

namespace OpenClaw.Tests.Security;

[Collection(ResolverAccessorCollection.Name)]
public sealed class VaultBootstrapTests
{
    [Fact]
    public void ConfigurationChecks_DoNotResolveVaultBeforePrewarm()
    {
        ResolverAccessor.Reset();
        var config = new GatewayConfig();
        config.Llm.ApiKey = "vault:secret/data/llm#key";
        config.Plugins.Native.Notion.Enabled = true;
        config.Plugins.Native.Notion.ApiKeyRef = "vault:secret/data/notion#key";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OpenClaw:Llm:ApiKey"] = config.Llm.ApiKey }).Build();
        var diagnostics = ConfigurationSourceDiagnosticsBuilder.Build(configuration, config);
        Assert.DoesNotContain(config.Llm.ApiKey, ConfigurationSourceDiagnosticsBuilder.Render(diagnostics));
        _ = ConfigValidator.Validate(config);
    }

    [Fact]
    public void LlmClient_ResolvesCachedVaultCredentialAtConstruction()
    {
        var provider = Substitute.For<ISyncSecretProvider>();
        provider.CanResolve(Arg.Any<string>()).Returns(true);
        provider.ResolveSync("vault:secret/data/llm#key").Returns("resolved-key");
        using var services = new ServiceCollection().AddSingleton<ISecretResolver>(
            new CompositeSecretResolver([provider], NullLogger<CompositeSecretResolver>.Instance)).BuildServiceProvider();
        try
        {
            ResolverAccessor.Use(services);
            using var client = LlmClientFactory.CreateChatClient(new() { Provider = "openai", ApiKey = "vault:secret/data/llm#key" });
            provider.Received().ResolveSync("vault:secret/data/llm#key");
        }
        finally { ResolverAccessor.Reset(); }
    }

    [Fact]
    public async Task Registration_WarmsCacheBeforeCredentialConsumersAreCreated()
    {
        var builder = Host.CreateApplicationBuilder();
        var config = new GatewayConfig();
        config.Llm.ApiKey = "vault:secret/data/llm#key";
        config.Security.Vault = new() { Enabled = true, Address = "https://vault.test", TokenRef = "raw:test" };
        builder.Services.AddSingleton(config);
        builder.Services.AddOpenClawVaultSecrets(config.Security.Vault);
        var client = Substitute.For<IVaultClient>();
        client.ReadSecretV2Async("secret", "llm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Dictionary<string, object>?>(new() { ["key"] = "resolved-key" }));
        builder.Services.AddSingleton(client);
        using var host = builder.Build();
        await host.Services.GetRequiredService<VaultRefPrewarmService>().WarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal("resolved-key", host.Services.GetRequiredService<ISecretResolver>().Resolve(config.Llm.ApiKey));
    }
}

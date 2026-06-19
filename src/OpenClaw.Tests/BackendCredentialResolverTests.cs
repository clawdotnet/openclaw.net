using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Backends;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class BackendCredentialResolverTests
{
    [Fact]
    public async Task Resolver_ResolvesSecretRef_TokenFile_AndProtectedAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-backend-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previous = Environment.GetEnvironmentVariable("TEST_BACKEND_SECRET");
        Environment.SetEnvironmentVariable("TEST_BACKEND_SECRET", "env-secret");
        try
        {
            var store = new FileFeatureStore(root);
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")))
                .SetApplicationName("OpenClaw.Tests");
            using var provider = services.BuildServiceProvider();

            var protection = new ConnectedAccountProtectionService(provider.GetRequiredService<IDataProtectionProvider>());
            var accounts = new ConnectedAccountService(store, protection);
            var resolver = new BackendCredentialResolver(accounts, store);

            var envResolved = await resolver.ResolveAsync("codex", new ConnectedAccountSecretRef
            {
                SecretRef = "env:TEST_BACKEND_SECRET"
            }, TestContext.Current.CancellationToken);
            Assert.Equal("env-secret", envResolved!.Secret);

            var tokenFile = Path.Combine(root, "token.txt");
            await File.WriteAllTextAsync(tokenFile, "file-secret");
            var fileResolved = await resolver.ResolveAsync("gemini-cli", new ConnectedAccountSecretRef
            {
                TokenFilePath = tokenFile
            }, TestContext.Current.CancellationToken);
            Assert.Equal("file-secret", fileResolved!.Secret);

            var created = await accounts.CreateAsync(new ConnectedAccountCreateRequest
            {
                Provider = "github-copilot-cli",
                DisplayName = "Stored",
                Secret = "protected-secret"
            }, TestContext.Current.CancellationToken);

            var protectedResolved = await resolver.ResolveAsync("github-copilot-cli", new ConnectedAccountSecretRef
            {
                ConnectedAccountId = created.Id
            }, TestContext.Current.CancellationToken);
            Assert.Equal("protected-secret", protectedResolved!.Secret);
            Assert.Equal(created.Id, protectedResolved.AccountId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_BACKEND_SECRET", previous);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Workflows;
using OpenClaw.Tests.Security;
using Xunit;

namespace OpenClaw.Tests;

[Collection(ResolverAccessorCollection.Name)]
public sealed class WorkflowTokenExchangeSecretTests : IDisposable
{
    public WorkflowTokenExchangeSecretTests() => ResolverAccessor.Reset();
    public void Dispose() => ResolverAccessor.Reset();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeAsync_WhenSecretResolutionThrows_ShouldFailWithoutDetails(bool unsupported)
    {
        using var services = new ServiceCollection().AddSingleton<ISecretResolver>(new ThrowingResolver(unsupported)).BuildServiceProvider();
        ResolverAccessor.Use(services);
        var requests = 0;
        using var http = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(CreateResponse());
        }));
        var logger = new RecordingWorkflowLogger<WorkflowTokenExchangeClient>();
        var exchange = new WorkflowTokenExchangeClient(new WorkflowTestHttpClientFactory(http), TimeProvider.System, logger);
        var failure = await Assert.ThrowsAsync<WorkflowDelegationException>(() => exchange.ExchangeAsync("review",
            WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"),
            WorkflowDelegationTestSupport.CreateExecutionContext("review", "issuer", "user", "assertion", DateTimeOffset.UtcNow.AddMinutes(5)),
            TestContext.Current.CancellationToken));
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal("workflow_token_exchange_failed", failure.ErrorCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private-vault-path/key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("private-vault-path/key", StringComparison.Ordinal));
        Assert.Equal(0, requests);
    }

    private static HttpResponseMessage CreateResponse()
        => new(System.Net.HttpStatusCode.OK);

    private sealed class ThrowingResolver(bool unsupported) : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromException<string?>(unsupported
                ? new VaultNotConfiguredException("private-vault-path/key")
                : new SecretResolutionException("private-vault-path/key"));
        public string? Resolve(string? secretRef) => throw new NotSupportedException();
        public bool IsRawRef(string? secretRef) => false;
    }
}

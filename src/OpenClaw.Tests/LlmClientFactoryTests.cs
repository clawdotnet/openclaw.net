using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

[Collection(DynamicProviderRegistryCollection.Name)]
public sealed class LlmClientFactoryTests
{
    [Fact]
    public void TryRegisterProvider_DuplicateId_FirstRegistrationWins()
    {
        LlmClientFactory.ResetDynamicProviders();
        var firstClient = Substitute.For<IChatClient>();
        var secondClient = Substitute.For<IChatClient>();

        var first = LlmClientFactory.TryRegisterProvider("dup-provider", firstClient, "owner-a");
        var duplicate = LlmClientFactory.TryRegisterProvider("dup-provider", secondClient, "owner-b");
        var resolved = LlmClientFactory.CreateChatClient(new OpenClaw.Core.Models.LlmProviderConfig
        {
            Provider = "dup-provider",
            Model = "test"
        });

        Assert.Equal(LlmClientFactory.DynamicProviderRegistrationResult.Registered, first);
        Assert.Equal(LlmClientFactory.DynamicProviderRegistrationResult.Duplicate, duplicate);
        Assert.Same(firstClient, resolved);
    }

    [Fact]
    public void UnregisterProvidersOwnedBy_RemovesOnlyMatchingOwner()
    {
        LlmClientFactory.ResetDynamicProviders();
        var retainedClient = Substitute.For<IChatClient>();
        var removedClient = Substitute.For<IChatClient>();

        _ = LlmClientFactory.TryRegisterProvider("keep-provider", retainedClient, "owner-keep");
        _ = LlmClientFactory.TryRegisterProvider("remove-provider", removedClient, "owner-remove");

        LlmClientFactory.UnregisterProvidersOwnedBy("owner-remove");
        var owners = LlmClientFactory.GetDynamicProviderOwners();

        Assert.Contains("keep-provider", owners.Keys);
        Assert.DoesNotContain("remove-provider", owners.Keys);
    }

    [Fact]
    public void CreateTransportOptions_DisablesHiddenRetries()
    {
        var transport = LlmClientFactory.CreateTransportOptions("https://example.invalid/v1");

        Assert.Equal(new Uri("https://example.invalid/v1"), transport.Endpoint);
        Assert.Equal(0, transport.HiddenRetryCount);
    }

    [Fact]
    public void CreateTransportOptions_DefaultEndpoint_RemainsUnset()
    {
        var transport = LlmClientFactory.CreateTransportOptions(endpoint: null);

        Assert.Null(transport.Endpoint);
        Assert.Equal(0, transport.HiddenRetryCount);
    }

    [Theory]
    [InlineData("openai", "gpt-4.1")]
    [InlineData("anthropic", "claude-sonnet-4-5")]
    [InlineData("claude", "claude-sonnet-4-5")]
    [InlineData("gemini", "gemini-2.5-flash")]
    [InlineData("google", "gemini-2.5-flash")]
    public void CreateChatClient_BuiltInProviders_CreateNativeClients(string provider, string model)
    {
        LlmClientFactory.ResetDynamicProviders();

        var client = LlmClientFactory.CreateChatClient(new LlmProviderConfig
        {
            Provider = provider,
            Model = model,
            ApiKey = "test-key"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public async Task ApertureTailnetIdentity_DoesNotSendAuthorizationHeader()
    {
        await using var server = await StartOpenAiCompatibleServerAsync();
        var client = LlmClientFactory.CreateChatClient(new LlmProviderConfig
        {
            Provider = "aperture",
            Endpoint = $"{server.BaseUrl}/v1",
            Model = "aperture-route",
            AuthMode = "tailnet-identity"
        });

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ready?")],
            new ChatOptions { ModelId = "aperture-route" },
            TestContext.Current.CancellationToken);

        Assert.Equal("READY", response.Text);
        Assert.False(server.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ApertureMetadata_OptInSendsHeadersAndKeepsBearerAuth()
    {
        await using var server = await StartOpenAiCompatibleServerAsync();
        var client = LlmClientFactory.CreateChatClient(new LlmProviderConfig
        {
            Provider = "aperture",
            Endpoint = $"{server.BaseUrl}/v1",
            Model = "aperture-route",
            ApiKey = "test-token",
            SendRequestMetadata = true
        });

        var options = new ChatOptions { ModelId = "aperture-route" };
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[OpenClawProviderRequestPolicy.MetadataHeadersPropertyName] =
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer attacker",
                ["Cookie"] = "session=attacker",
                ["X-OpenClaw-Session-Id"] = "sess-1",
                ["X-OpenClaw-Model-Profile"] = "aperture-default"
            };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ready?")],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("READY", response.Text);
        Assert.Equal("Bearer test-token", server.Headers["Authorization"]);
        Assert.False(server.Headers.ContainsKey("Cookie"));
        Assert.Equal("sess-1", server.Headers["X-OpenClaw-Session-Id"]);
        Assert.Equal("aperture-default", server.Headers["X-OpenClaw-Model-Profile"]);
    }

    [Fact]
    public void ApertureTailnetIdentity_EmbeddingsFailFastWithoutApiKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LlmClientFactory.CreateEmbeddingGenerator(
                new LlmProviderConfig
                {
                    Provider = "aperture",
                    Endpoint = "https://aperture.example.test/v1",
                    Model = "aperture-route",
                    AuthMode = "tailnet-identity"
                },
                "text-embedding-3-small"));

        Assert.Contains("AuthMode=tailnet-identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApertureTailnetIdentity_SmokeProbeSuppressesAuthorizationEvenWithStaleKey()
    {
        await using var server = await StartOpenAiCompatibleServerAsync();

        var result = await ProviderSmokeProbe.ProbeAsync(
            new LlmProviderConfig
            {
                Provider = "aperture",
                Endpoint = $"{server.BaseUrl}/v1",
                Model = "aperture-route",
                ApiKey = "stale-token",
                AuthMode = "tailnet-identity"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStates.Pass, result.Status);
        Assert.False(server.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnsupportedTailnetIdentityProvider_SmokeProbeKeepsAuthorization()
    {
        await using var server = await StartOpenAiCompatibleServerAsync();

        var result = await ProviderSmokeProbe.ProbeAsync(
            new LlmProviderConfig
            {
                Provider = "groq",
                Endpoint = $"{server.BaseUrl}/v1",
                Model = "llama-test",
                ApiKey = "provider-token",
                AuthMode = "tailnet-identity"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStates.Pass, result.Status);
        Assert.Equal("Bearer provider-token", server.Headers["Authorization"]);
    }

    private static async Task<OpenAiCompatibleTestServer> StartOpenAiCompatibleServerAsync()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", (HttpContext ctx) =>
        {
            headers.Clear();
            foreach (var header in ctx.Request.Headers)
                headers[header.Key] = header.Value.ToString();

            return Results.Json(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion",
                created = 0,
                model = "aperture-route",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "READY" },
                        finish_reason = "stop"
                    }
                }
            });
        });

        await app.StartAsync();
        var address = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();
        return new OpenAiCompatibleTestServer(app, address.TrimEnd('/'), headers);
    }

    private sealed record OpenAiCompatibleTestServer(WebApplication App, string BaseUrl, Dictionary<string, string> Headers) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }
}

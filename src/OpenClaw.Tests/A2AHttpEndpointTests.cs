using System.Net;
using System.Net.Http.Json;
using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenClaw.Gateway;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.A2A;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2AHttpEndpointTests
{
    [Fact]
    public async Task RootWellKnownAgentCard_Returns_Standard_Discovery_Response()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var card = await client.GetFromJsonAsync<AgentCard>("/.well-known/agent-card.json");

        Assert.NotNull(card);
        Assert.Equal("TestAgent", card!.Name);
        Assert.Collection(
            card.SupportedInterfaces!,
            httpJson =>
            {
                Assert.Equal("http://localhost/a2a", httpJson.Url);
                Assert.Equal(ProtocolBindingNames.HttpJson, httpJson.ProtocolBinding);
                Assert.Equal("1.0", httpJson.ProtocolVersion);
            },
            jsonRpc =>
            {
                Assert.Equal("http://localhost/a2a/rpc", jsonRpc.Url);
                Assert.Equal(ProtocolBindingNames.JsonRpc, jsonRpc.ProtocolBinding);
                Assert.Equal("1.0", jsonRpc.ProtocolVersion);
            });
    }

    [Fact]
    public async Task LegacyWellKnownAgentCard_Alias_Remains_Available()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var rootCard = await client.GetFromJsonAsync<AgentCard>("/.well-known/agent-card.json");
        var legacyCard = await client.GetFromJsonAsync<AgentCard>("/a2a/.well-known/agent-card.json");

        Assert.NotNull(rootCard);
        Assert.NotNull(legacyCard);
        Assert.Equal(rootCard!.Name, legacyCard!.Name);
        Assert.Equal(rootCard.SupportedInterfaces![0].Url, legacyCard.SupportedInterfaces![0].Url);
    }

    [Fact]
    public async Task WellKnownAgentCard_Uses_Request_Host_When_Public_Base_Url_Is_Not_Configured()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        request.Headers.Host = "agent.example.test";

        using var response = await client.SendAsync(request);
        var card = await response.Content.ReadFromJsonAsync<AgentCard>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(card);
        Assert.Equal("http://agent.example.test/a2a", card!.SupportedInterfaces![0].Url);
        Assert.Equal("http://agent.example.test/a2a/rpc", card.SupportedInterfaces[1].Url);
    }

    [Fact]
    public async Task WellKnownAgentCard_Uses_Configured_Public_Base_Url_When_Present()
    {
        await using var app = await CreateAppAsync(options =>
            options.A2APublicBaseUrl = " https://public.example.test/root/ ");
        var client = app.GetTestClient();

        var card = await client.GetFromJsonAsync<AgentCard>("/.well-known/agent-card.json");

        Assert.NotNull(card);
        Assert.Equal("https://public.example.test/root/a2a", card!.SupportedInterfaces![0].Url);
        Assert.Equal("https://public.example.test/root/a2a/rpc", card.SupportedInterfaces[1].Url);
    }

    [Fact]
    public async Task AgentCard_Does_Not_Advertise_Protocol_Level_Streaming()
    {
        await using var app = await CreateAppAsync(options => options.EnableStreaming = true);
        var client = app.GetTestClient();

        var card = await client.GetFromJsonAsync<AgentCard>("/.well-known/agent-card.json");

        Assert.NotNull(card);
        Assert.False(card!.Capabilities!.Streaming);
    }

    [Fact]
    public async Task MessageSend_BridgeException_Returns_Agent_Error_Message()
    {
        await using var app = await CreateAppAsync(bridge: new ThrowingExecutionBridge());
        var client = app.GetTestClient();
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = "message-1",
                Parts = [Part.FromText("boom")]
            }
        };

        using var response = await client.PostAsJsonAsync(
            "/a2a/message:send",
            request,
            A2AJsonUtilities.DefaultOptions);

        var payload = await response.Content.ReadFromJsonAsync<SendMessageResponse>(A2AJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload?.Message);
        Assert.Contains(
            payload!.Message!.Parts!,
            part => string.Equals("A2A request failed.", part.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageSend_BridgeCompletesWithoutText_Returns_Fallback_Agent_Message()
    {
        await using var app = await CreateAppAsync(bridge: new CompleteOnlyExecutionBridge());
        var client = app.GetTestClient();
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = "message-1",
                Parts = [Part.FromText("complete only")]
            }
        };

        using var response = await client.PostAsJsonAsync(
            "/a2a/message:send",
            request,
            A2AJsonUtilities.DefaultOptions);

        var payload = await response.Content.ReadFromJsonAsync<SendMessageResponse>(A2AJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload?.Message);
        Assert.Contains(
            payload!.Message!.Parts!,
            part => string.Equals("[TestAgent] Request completed.", part.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageSend_WithoutMessageId_Returns_Agent_Message()
    {
        await using var app = await CreateAppAsync(bridge: new CompleteOnlyExecutionBridge());
        var client = app.GetTestClient();
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                Role = Role.User,
                Parts = [Part.FromText("complete only")]
            }
        };

        using var response = await client.PostAsJsonAsync(
            "/a2a/message:send",
            request,
            A2AJsonUtilities.DefaultOptions);

        var payload = await response.Content.ReadFromJsonAsync<SendMessageResponse>(A2AJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload?.Message);
        Assert.Contains(
            payload!.Message!.Parts!,
            part => string.Equals("[TestAgent] Request completed.", part.Text, StringComparison.Ordinal));
    }

    private static async Task<WebApplication> CreateAppAsync(
        Action<MafOptions>? configureOptions = null,
        IOpenClawA2AExecutionBridge? bridge = null)
    {
        var options = CreateOptions();
        configureOptions?.Invoke(options);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            var a2aResolver = A2AJsonUtilities.DefaultOptions.TypeInfoResolver;
            if (a2aResolver is not null)
                opts.SerializerOptions.TypeInfoResolverChain.Add(a2aResolver);

            opts.SerializerOptions.TypeInfoResolverChain.Add(GatewayJsonContext.Default);
            opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default);
        });
        builder.Services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(options));
        builder.Services.AddOpenClawA2AServices();
        builder.Services.AddSingleton(bridge ?? new FakeExecutionBridge());

        var app = builder.Build();
        app.MapOpenClawA2AEndpoints(CreateStartupContext(), runtime: null!);
        await app.StartAsync();
        return app;
    }

    private static MafOptions CreateOptions()
        => new()
        {
            AgentName = "TestAgent",
            AgentDescription = "Test agent for A2A HTTP endpoint tests.",
            EnableStreaming = true,
            EnableA2A = true,
            A2AVersion = "1.0.0"
        };

    private static GatewayStartupContext CreateStartupContext()
        => new()
        {
            Config = new GatewayConfig
            {
                BindAddress = "0.0.0.0",
                Port = 18789
            },
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = true
        };

    private sealed class FakeExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            await onEvent(AgentStreamEvent.TextDelta($"bridge:{request.UserText}"), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
    }

    private sealed class ThrowingExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Synthetic A2A execution failure.");
    }

    private sealed class CompleteOnlyExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
    }
}

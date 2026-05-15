using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    public async Task AgentCard_Advertises_Protocol_Level_Streaming_When_Enabled()
    {
        await using var app = await CreateAppAsync(options => options.EnableStreaming = true);
        var client = app.GetTestClient();

        var card = await client.GetFromJsonAsync<AgentCard>("/.well-known/agent-card.json");

        Assert.NotNull(card);
        Assert.True(card!.Capabilities!.Streaming);
    }

    [Fact]
    public async Task AgentCard_Does_Not_Advertise_Protocol_Level_Streaming_When_Disabled()
    {
        await using var app = await CreateAppAsync(options => options.EnableStreaming = false);
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

    [Fact]
    public async Task MessageStream_Returns_SseContentType()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("hello stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MessageStream_Emits_Task_Submitted_And_Working()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("hello stream"));
        var events = await ReadStreamResponsesAsync(response);

        Assert.True(events.Count >= 2);
        Assert.Equal(StreamResponseCase.Task, events[0].PayloadCase);
        Assert.Equal(TaskState.Submitted, events[0].Task!.Status!.State);
        Assert.Equal(StreamResponseCase.StatusUpdate, events[1].PayloadCase);
        Assert.Equal(TaskState.Working, events[1].StatusUpdate!.Status!.State);
        Assert.Null(events[1].StatusUpdate!.Status!.Message);
    }

    [Fact]
    public async Task MessageStream_Emits_Artifact_Delta_Chunks()
    {
        await using var app = await CreateAppAsync(bridge: new MultiDeltaExecutionBridge());
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("hello stream"));
        var events = await ReadStreamResponsesAsync(response);
        var artifactEvents = events.Where(static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate).ToList();

        Assert.Equal(2, artifactEvents.Count);
        Assert.All(artifactEvents, evt =>
        {
            Assert.NotNull(evt.ArtifactUpdate);
            Assert.Equal("text-delta", evt.ArtifactUpdate!.Artifact!.ArtifactId);
            Assert.True(evt.ArtifactUpdate.Append);
        });
        Assert.False(artifactEvents[0].ArtifactUpdate!.LastChunk);
        Assert.True(artifactEvents[1].ArtifactUpdate!.LastChunk);
        Assert.Equal("bridge:", artifactEvents[0].ArtifactUpdate!.Artifact!.Parts![0].Text);
        Assert.Equal("hello stream", artifactEvents[1].ArtifactUpdate!.Artifact!.Parts![0].Text);
    }

    [Fact]
    public async Task MessageStream_Emits_Completed_Task_With_Final_Message()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("hello stream"));
        var events = await ReadStreamResponsesAsync(response);
        var terminalEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Completed);

        Assert.NotNull(terminalEvent.StatusUpdate);
        Assert.Equal(TaskState.Completed, terminalEvent.StatusUpdate!.Status!.State);
        Assert.Equal("bridge:hello stream", terminalEvent.StatusUpdate.Status.Message!.Parts![0].Text);
    }

    [Fact]
    public async Task MessageStream_BridgeException_Emits_Failed_Task()
    {
        await using var app = await CreateAppAsync(bridge: new ThrowingExecutionBridge());
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("boom"));
        var events = await ReadStreamResponsesAsync(response);
        var failedEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Failed);

        Assert.Equal("A2A request failed.", failedEvent.StatusUpdate!.Status!.Message!.Parts![0].Text);
    }

    [Fact]
    public async Task MessageStream_NoDeltas_Emits_Fallback_Artifact_Then_Complete()
    {
        await using var app = await CreateAppAsync(bridge: new CompleteOnlyExecutionBridge());
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("complete only"));
        var events = await ReadStreamResponsesAsync(response);
        var artifactEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate);
        var completedEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Completed);

        Assert.True(artifactEvent.ArtifactUpdate!.LastChunk);
        Assert.Equal("[TestAgent] Request completed.", artifactEvent.ArtifactUpdate!.Artifact!.Parts![0].Text);
        Assert.Equal("[TestAgent] Request completed.", completedEvent.StatusUpdate!.Status!.Message!.Parts![0].Text);
    }

    [Fact]
    public async Task MessageStream_Emits_Partial_Text_Then_Failed_Status()
    {
        await using var app = await CreateAppAsync(bridge: new ErrorAfterPartialExecutionBridge());
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("partial"));
        var events = await ReadStreamResponsesAsync(response);
        var artifactEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate);
        var failedEvent = Assert.Single(events, static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Failed);

        Assert.False(artifactEvent.ArtifactUpdate!.LastChunk);
        Assert.Equal("partial:", artifactEvent.ArtifactUpdate!.Artifact!.Parts![0].Text);
        Assert.Equal("runtime failure after partial text", failedEvent.StatusUpdate!.Status!.Message!.Parts![0].Text);
    }

    [Fact]
    public async Task MessageStream_Artifact_Terminates_Deterministically()
    {
        await using var app = await CreateAppAsync(bridge: new MultiDeltaExecutionBridge());
        var client = app.GetTestClient();

        using var response = await PostMessageStreamAsync(client, CreateMessageRequest("hello stream"));
        var events = await ReadStreamResponsesAsync(response);
        var completedIndex = events.FindIndex(static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Completed);

        Assert.True(completedIndex >= 0);
        Assert.DoesNotContain(events.Skip(completedIndex + 1), static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate);
        Assert.Equal(1, events.Count(static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Completed));
        Assert.Equal("bridge:hello stream", events[completedIndex].StatusUpdate!.Status!.Message!.Parts![0].Text);
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

    private static SendMessageRequest CreateMessageRequest(string text)
        => new()
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = "message-1",
                Parts = [Part.FromText(text)]
            }
        };

    private static async Task<HttpResponseMessage> PostMessageStreamAsync(HttpClient client, SendMessageRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/a2a/message:stream")
        {
            Content = JsonContent.Create(request, options: A2AJsonUtilities.DefaultOptions)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(message);
    }

    private static async Task<List<StreamResponse>> ReadStreamResponsesAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var events = new List<StreamResponse>();
        using var reader = new StringReader(payload);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var json = line[6..];
            var streamResponse = JsonSerializer.Deserialize<StreamResponse>(json, A2AJsonUtilities.DefaultOptions);
            Assert.NotNull(streamResponse);
            events.Add(streamResponse!);
        }

        return events;
    }

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

    private sealed class MultiDeltaExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            await onEvent(AgentStreamEvent.TextDelta("bridge:"), cancellationToken);
            await onEvent(AgentStreamEvent.TextDelta(request.UserText), cancellationToken);
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

    private sealed class ErrorAfterPartialExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            await onEvent(AgentStreamEvent.TextDelta("partial:"), cancellationToken);
            await onEvent(AgentStreamEvent.ErrorOccurred("runtime failure after partial text"), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
    }
}

using System.Text.Json;
using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.A2A;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;
using System.Net.Http.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2AIntegrationTests
{
    [Fact]
    public void AgentCardFactory_Creates_DefaultSkill_When_NoneConfigured()
    {
        var factory = new OpenClawAgentCardFactory(Options.Create(CreateOptions()));

        var card = factory.Create("http://localhost:5000/a2a");

        Assert.Equal("TestAgent", card.Name);
        Assert.Equal("1.0.0", card.Version);
        Assert.Single(card.Skills!);
        Assert.Equal("general", card.Skills[0].Id);
        var agentInterface = Assert.Single(card.SupportedInterfaces!);
        Assert.Equal("http://localhost:5000/a2a", agentInterface.Url);
        Assert.Equal(ProtocolBindingNames.HttpJson, agentInterface.ProtocolBinding);
        Assert.True(card.Capabilities!.Streaming);
    }

    [Fact]
    public void AgentCardFactory_Does_Not_Advertise_Streaming_When_Disabled()
    {
        var options = CreateOptions();
        options.EnableStreaming = false;
        var factory = new OpenClawAgentCardFactory(Options.Create(options));

        var card = factory.Create("http://localhost:5000/a2a");

        Assert.False(card.Capabilities!.Streaming);
    }

    [Fact]
    public async Task A2AAgent_RunStreamingAsync_Completes_With_Bridged_Text()
    {
        var agent = new OpenClawA2AAgent(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgent>.Instance);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("Hello A2A"))
            updates.Add(update);

        Assert.Contains(updates, update => update.Text.Contains("bridge:Hello A2A", StringComparison.Ordinal));
    }

    [Fact]
    public void A2AAgent_Name_Uses_Stable_Hosted_Service_Id()
    {
        var agent = new OpenClawA2AAgent(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgent>.Instance);

        Assert.Equal(OpenClawA2AAgent.HostedAgentName, agent.Name);
        Assert.NotEqual(CreateOptions().AgentName, agent.Name);
    }

    [Fact]
    public async Task A2AAgent_RunStreamingAsync_Emits_Fallback_Text_When_Bridge_Completes_Without_Text()
    {
        var agent = new OpenClawA2AAgent(
            Options.Create(CreateOptions()),
            new CompleteOnlyExecutionBridge(),
            NullLogger<OpenClawA2AAgent>.Instance);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("Hello A2A"))
            updates.Add(update);

        var fallbackUpdate = Assert.Single(updates);
        Assert.Equal("[TestAgent] Request completed.", fallbackUpdate.Text);
    }

    [Fact]
    public async Task A2AAgent_RunStreamingAsync_Uses_Latest_User_Message_Only()
    {
        var bridge = new CapturingExecutionBridge();
        var agent = new OpenClawA2AAgent(
            Options.Create(CreateOptions()),
            bridge,
            NullLogger<OpenClawA2AAgent>.Instance);
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "system instructions"),
            new ChatMessage(ChatRole.User, "first user") { MessageId = "user-1" },
            new ChatMessage(ChatRole.Assistant, "assistant history") { MessageId = "assistant-1" },
            new ChatMessage(ChatRole.User, "latest user") { MessageId = "user-2" }
        };

        await foreach (var _ in agent.RunStreamingAsync(messages))
        {
        }

        Assert.NotNull(bridge.Request);
        Assert.Equal("latest user", bridge.Request!.UserText);
        Assert.Equal("user-2", bridge.Request.MessageId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SessionId\":\"\",\"SenderId\":\"sender\"}")]
    [InlineData("{\"SessionId\":\"session\",\"SenderId\":\" \"}")]
    public async Task A2AAgent_DeserializeSessionAsync_Falls_Back_When_Stored_Ids_Are_Invalid(string json)
    {
        var agent = new OpenClawA2AAgent(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgent>.Instance);
        using var document = JsonDocument.Parse(json);

        var session = await agent.DeserializeSessionAsync(
            document.RootElement,
            jsonSerializerOptions: null,
            TestContext.Current.CancellationToken);
        var serialized = await agent.SerializeSessionAsync(
            session,
            jsonSerializerOptions: null,
            TestContext.Current.CancellationToken);

        Assert.True(serialized.TryGetProperty("SessionId", out var sessionId));
        Assert.True(serialized.TryGetProperty("SenderId", out var senderId));
        var sessionIdText = sessionId.GetString();
        var senderIdText = senderId.GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionIdText));
        Assert.Equal(sessionIdText, senderIdText);
    }

    [Fact]
    public async Task A2AAgentHandler_CancelAsync_NoOps_When_TaskId_Is_Missing()
    {
        var handler = new OpenClawA2AAgentHandler(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgentHandler>.Instance);
        var eventQueue = new AgentEventQueue();

        await handler.CancelAsync(
            new RequestContext
            {
                Message = null!,
                TaskId = "",
                ContextId = "",
                StreamingResponse = false
            },
            eventQueue,
            TestContext.Current.CancellationToken);
        eventQueue.Complete(null!);

        var events = new List<StreamResponse>();
        await foreach (var evt in eventQueue)
            events.Add(evt);

        Assert.Empty(events);
    }

    [Fact]
    public async Task MessageStream_ViaJsonRpc_Emits_Streaming_Events()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        using var response = await PostJsonRpcStreamingMessageAsync(client, CreateMessageRequest("hello rpc"));
        var events = await ReadJsonRpcStreamResponsesAsync(response);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(events.Count >= 4);
        Assert.Equal(StreamResponseCase.Task, events[0].PayloadCase);
        Assert.Equal(TaskState.Submitted, events[0].Task!.Status!.State);
        Assert.Equal(StreamResponseCase.StatusUpdate, events[1].PayloadCase);
        Assert.Equal(TaskState.Working, events[1].StatusUpdate!.Status!.State);
        Assert.Contains(events, evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate);
        Assert.Contains(events, evt => evt.PayloadCase == StreamResponseCase.StatusUpdate && evt.StatusUpdate!.Status!.State == TaskState.Completed);
    }

    [Fact]
    public async Task MessageStream_ViaJsonRpc_Matches_Http_Event_Contract()
    {
        await using var app = await CreateAppAsync(bridge: new MultiDeltaExecutionBridge());
        var client = app.GetTestClient();

        using var httpResponse = await PostHttpStreamingMessageAsync(client, CreateMessageRequest("hello parity"));
        using var jsonRpcResponse = await PostJsonRpcStreamingMessageAsync(client, CreateMessageRequest("hello parity"));
        var httpEvents = await ReadStreamResponsesAsync(httpResponse);
        var jsonRpcEvents = await ReadJsonRpcStreamResponsesAsync(jsonRpcResponse);

        Assert.Equal(httpEvents.Select(static evt => evt.PayloadCase), jsonRpcEvents.Select(static evt => evt.PayloadCase));

        var httpArtifacts = httpEvents.Where(static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate).ToList();
        var jsonRpcArtifacts = jsonRpcEvents.Where(static evt => evt.PayloadCase == StreamResponseCase.ArtifactUpdate).ToList();

        Assert.Equal(httpArtifacts.Count, jsonRpcArtifacts.Count);
        Assert.All(jsonRpcArtifacts, evt => Assert.Equal("text-delta", evt.ArtifactUpdate!.Artifact!.ArtifactId));
        Assert.Equal(
            httpEvents.Last(static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate).StatusUpdate!.Status!.State,
            jsonRpcEvents.Last(static evt => evt.PayloadCase == StreamResponseCase.StatusUpdate).StatusUpdate!.Status!.State);
    }

    [Fact]
    public async Task AddOpenClawA2AServices_Registers_A2A_Server()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(CreateOptions()));
        services.AddOpenClawA2AServices();
        services.AddSingleton<IOpenClawA2AExecutionBridge>(new FakeExecutionBridge());

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<OpenClawA2AAgent>());
        Assert.NotNull(provider.GetService<OpenClawA2AAgentHandler>());
        Assert.NotNull(provider.GetService<OpenClawAgentCardFactory>());
        Assert.NotNull(provider.GetRequiredKeyedService<IAgentHandler>(OpenClawA2ANames.AgentName));
        Assert.NotNull(provider.GetRequiredKeyedService<ITaskStore>(OpenClawA2ANames.AgentName));
        Assert.NotNull(provider.GetRequiredKeyedService<A2AServer>(OpenClawA2ANames.AgentName));
        var registrationAgent = provider.GetRequiredKeyedService<AIAgent>(OpenClawA2ANames.AgentName);
        Assert.Equal(OpenClawA2ANames.AgentName, registrationAgent.Name);
        Assert.Equal("Test agent for A2A integration tests.", registrationAgent.Description);
    }

    [Fact]
    public void MafServiceCollectionExtensions_Parses_A2A_Config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.SectionName}:EnableA2A"] = "true",
                [$"{MafOptions.SectionName}:A2APathPrefix"] = "/agents/a2a",
                [$"{MafOptions.SectionName}:A2AVersion"] = "2.0.0-beta",
                [$"{MafOptions.SectionName}:A2APublicBaseUrl"] = " https://agents.example.test/root/ ",
                [$"{MafOptions.SectionName}:A2ASkills:0:Id"] = "search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Name"] = "Web Search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Tags:0"] = "web"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftAgentFramework(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.True(options.EnableA2A);
        Assert.Equal("/agents/a2a", options.A2APathPrefix);
        Assert.Equal("2.0.0-beta", options.A2AVersion);
        Assert.Equal("https://agents.example.test/root/", options.A2APublicBaseUrl);
        Assert.Single(options.A2ASkills);
        Assert.Equal("search", options.A2ASkills[0].Id);
        Assert.Equal("Web Search", options.A2ASkills[0].Name);
        Assert.Equal(["web"], options.A2ASkills[0].Tags);
    }

    [Fact]
    public void MafServiceCollectionExtensions_Parses_Legacy_Config_With_Migration_Flag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.LegacySectionName}:EnableA2A"] = "true",
                [$"{MafOptions.LegacySectionName}:A2APathPrefix"] = "/legacy/a2a"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftAgentFramework(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.True(options.EnableA2A);
        Assert.Equal("/legacy/a2a", options.A2APathPrefix);
        Assert.True(options.LegacySectionUsed);
    }

    [Fact]
    public void MafServiceCollectionExtensions_Prefers_New_Config_Over_Legacy_Config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.LegacySectionName}:EnableA2A"] = "true",
                [$"{MafOptions.LegacySectionName}:A2APathPrefix"] = "/legacy/a2a",
                [$"{MafOptions.SectionName}:EnableA2A"] = "false",
                [$"{MafOptions.SectionName}:A2APathPrefix"] = "/supported/a2a"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftAgentFramework(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.False(options.EnableA2A);
        Assert.Equal("/supported/a2a", options.A2APathPrefix);
        Assert.False(options.LegacySectionUsed);
    }

    [Fact]
    public void MafServiceCollectionExtensions_Skips_Invalid_A2A_Skills_And_Blank_Tags()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.SectionName}:A2ASkills:0:Id"] = "missing-name",
                [$"{MafOptions.SectionName}:A2ASkills:1:Name"] = "Missing Id",
                [$"{MafOptions.SectionName}:A2ASkills:2:Id"] = "search",
                [$"{MafOptions.SectionName}:A2ASkills:2:Name"] = "Web Search",
                [$"{MafOptions.SectionName}:A2ASkills:2:Tags:0"] = "web",
                [$"{MafOptions.SectionName}:A2ASkills:2:Tags:1"] = " ",
                [$"{MafOptions.SectionName}:A2ASkills:2:Tags:2"] = null
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftAgentFramework(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        var skill = Assert.Single(options.A2ASkills);
        Assert.Equal("search", skill.Id);
        Assert.Equal("Web Search", skill.Name);
        Assert.Equal(["web"], skill.Tags);
    }

    [Theory]
    [InlineData(null, "/a2a")]
    [InlineData("", "/a2a")]
    [InlineData("/", "/a2a")]
    [InlineData("///", "/a2a")]
    [InlineData(" agents/a2a/ ", "/agents/a2a")]
    [InlineData("/agents/a2a/", "/agents/a2a")]
    public void NormalizePathPrefix_Returns_Expected_Value(string? value, string expected)
    {
        Assert.Equal(expected, A2AEndpointExtensions.NormalizePathPrefix(value ?? ""));
    }

    [Fact]
    public void ResolvePublicBaseUrl_Uses_Request_Scheme_And_Host_When_Configured_BaseUrl_Is_Not_Set()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("agent.example.test");
        context.Request.PathBase = new PathString("/gateway");

        var resolved = A2AEndpointExtensions.ResolvePublicBaseUrl(
            context,
            CreateStartupContext(),
            CreateOptions());

        Assert.Equal("https://agent.example.test/gateway", resolved);
    }

    [Fact]
    public void ResolvePublicBaseUrl_Prefers_Configured_A2A_Public_Base_Url()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("ignored.example.test");

        var options = CreateOptions();
        options.A2APublicBaseUrl = " https://public.example.test/root/ ";

        var resolved = A2AEndpointExtensions.ResolvePublicBaseUrl(
            context,
            CreateStartupContext(),
            options);

        Assert.Equal("https://public.example.test/root", resolved);
    }

    [Fact]
    public void GetWellKnownAgentCardPath_Returns_Standard_Root_Discovery_Path()
    {
        Assert.Equal("/.well-known/agent-card.json", A2AEndpointExtensions.GetWellKnownAgentCardPath());
    }

    [Fact]
    public void GetLegacyWellKnownAgentCardPath_Returns_PathPrefix_Alias()
    {
        Assert.Equal(
            "/a2a/.well-known/agent-card.json",
            A2AEndpointExtensions.GetLegacyWellKnownAgentCardPath("/a2a"));
    }

    [Theory]
    [InlineData("/.well-known/agent-card.json", true)]
    [InlineData("/.WELL-KNOWN/AGENT-CARD.JSON", true)]
    [InlineData("/a2a/.well-known/agent-card.json", true)]
    [InlineData("/a2a", false)]
    [InlineData("/a2a/rpc", false)]
    public void IsA2ADiscoveryPath_Recognizes_Standard_And_Legacy_Discovery(string path, bool expected)
    {
        Assert.Equal(expected, A2AEndpointExtensions.IsA2ADiscoveryPath(new PathString(path), "/a2a"));
    }

    [Fact]
    public void BuildAgentCardForRequest_Uses_Request_Base_Url_For_Supported_Interfaces()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("agent.example.test");
        context.Request.PathBase = new PathString("/gateway");
        var factory = new OpenClawAgentCardFactory(Options.Create(CreateOptions()));

        var card = A2AEndpointExtensions.BuildAgentCardForRequest(
            context,
            CreateStartupContext(),
            CreateOptions(),
            factory,
            "/a2a",
            "/a2a/rpc");

        Assert.Collection(
            card.SupportedInterfaces!,
            httpJson => Assert.Equal("https://agent.example.test/gateway/a2a", httpJson.Url),
            jsonRpc => Assert.Equal("https://agent.example.test/gateway/a2a/rpc", jsonRpc.Url));
    }

    [Fact]
    public void AgentCardFactory_Creates_HttpJson_And_JsonRpc_Interfaces_When_JsonRpc_Url_Is_Provided()
    {
        var factory = new OpenClawAgentCardFactory(Options.Create(CreateOptions()));

        var card = factory.Create("http://localhost:5000/a2a", "http://localhost:5000/a2a/rpc");

        Assert.Collection(
            card.SupportedInterfaces!,
            httpJson =>
            {
                Assert.Equal("http://localhost:5000/a2a", httpJson.Url);
                Assert.Equal(ProtocolBindingNames.HttpJson, httpJson.ProtocolBinding);
            },
            jsonRpc =>
            {
                Assert.Equal("http://localhost:5000/a2a/rpc", jsonRpc.Url);
                Assert.Equal(ProtocolBindingNames.JsonRpc, jsonRpc.ProtocolBinding);
            });
    }

    private static MafOptions CreateOptions()
        => new()
        {
            AgentName = "TestAgent",
            AgentDescription = "Test agent for A2A integration tests.",
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

    private static async Task<System.Net.Http.HttpResponseMessage> PostHttpStreamingMessageAsync(System.Net.Http.HttpClient client, SendMessageRequest request)
    {
        using var message = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/a2a/message:stream")
        {
            Content = JsonContent.Create(request, options: A2AJsonUtilities.DefaultOptions)
        };

        return await client.SendAsync(message);
    }

    private static async Task<System.Net.Http.HttpResponseMessage> PostJsonRpcStreamingMessageAsync(System.Net.Http.HttpClient client, SendMessageRequest request)
    {
        var jsonRpcRequest = new JsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = A2AMethods.SendStreamingMessage,
            Params = JsonSerializer.SerializeToElement(request, A2AJsonUtilities.DefaultOptions)
        };

        using var message = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/a2a/rpc")
        {
            Content = new JsonRpcContent(jsonRpcRequest)
        };

        return await client.SendAsync(message);
    }

    private static async Task<List<StreamResponse>> ReadStreamResponsesAsync(System.Net.Http.HttpResponseMessage response)
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

    private static async Task<List<StreamResponse>> ReadJsonRpcStreamResponsesAsync(System.Net.Http.HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var events = new List<StreamResponse>();
        using var reader = new StringReader(payload);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            using var document = JsonDocument.Parse(line[6..]);
            if (!document.RootElement.TryGetProperty("result", out var result))
                continue;

            var streamResponse = JsonSerializer.Deserialize<StreamResponse>(result.GetRawText(), A2AJsonUtilities.DefaultOptions);
            Assert.NotNull(streamResponse);
            events.Add(streamResponse!);
        }

        return events;
    }

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

    private sealed class CapturingExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public OpenClawA2AExecutionRequest? Request { get; private set; }

        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            Request = request;
            await onEvent(AgentStreamEvent.TextDelta($"bridge:{request.UserText}"), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
    }
}

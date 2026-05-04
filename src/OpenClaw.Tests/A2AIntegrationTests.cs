#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using System.Text.Json;
using A2A;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.A2A;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;
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
        Assert.Equal("[openclaw] Request completed.", fallbackUpdate.Text);
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
            CancellationToken.None);
        var serialized = await agent.SerializeSessionAsync(
            session,
            jsonSerializerOptions: null,
            CancellationToken.None);

        Assert.True(serialized.TryGetProperty("SessionId", out var sessionId));
        Assert.True(serialized.TryGetProperty("SenderId", out var senderId));
        var sessionIdText = sessionId.GetString();
        var senderIdText = senderId.GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionIdText));
        Assert.Equal(sessionIdText, senderIdText);
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
        services.AddMicrosoftAgentFrameworkExperiment(configuration);

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
        services.AddMicrosoftAgentFrameworkExperiment(configuration);

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
#endif

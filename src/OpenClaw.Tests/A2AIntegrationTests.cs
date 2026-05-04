#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using Microsoft.AspNetCore.Http;
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
    }

    [Fact]
    public async Task AgentHandler_ExecuteAsync_Completes_With_Bridged_Text()
    {
        var handler = new OpenClawA2AAgentHandler(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgentHandler>.Instance);
        var queue = new AgentEventQueue();
        var context = new RequestContext
        {
            Message = new Message
            {
                Role = Role.User,
                Parts = [Part.FromText("Hello A2A")]
            },
            TaskId = "task-1",
            ContextId = "ctx-1",
            StreamingResponse = false
        };

        var events = new List<StreamResponse>();
        await handler.ExecuteAsync(context, queue, CancellationToken.None);
        queue.Complete();
        await foreach (var evt in queue)
            events.Add(evt);

        var completed = events.LastOrDefault(item => item.StatusUpdate?.Status.State == TaskState.Completed);
        Assert.NotNull(completed);
        Assert.Contains("bridge:Hello A2A", completed!.StatusUpdate!.Status.Message!.Parts![0].Text);
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

        Assert.NotNull(provider.GetService<OpenClawA2AAgentHandler>());
        Assert.NotNull(provider.GetService<OpenClawAgentCardFactory>());
        Assert.NotNull(provider.GetRequiredKeyedService<ITaskStore>(OpenClawA2ANames.AgentName));
        Assert.NotNull(provider.GetRequiredKeyedService<IAgentHandler>(OpenClawA2ANames.AgentName));
        Assert.NotNull(provider.GetRequiredKeyedService<A2AServer>(OpenClawA2ANames.AgentName));
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
}
#endif

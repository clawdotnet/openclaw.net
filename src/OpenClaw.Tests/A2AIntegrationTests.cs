#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.A2A;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2AIntegrationTests
{
    // ── AgentCardFactory Tests ──────────────────────────────────────

    [Fact]
    public void AgentCardFactory_Creates_Card_With_DefaultSkill_When_NoSkillsConfigured()
    {
        var options = CreateOptions();
        var factory = new OpenClawAgentCardFactory(Options.Create(options));

        var card = factory.Create("http://localhost:5000/a2a");

        Assert.Equal("TestAgent", card.Name);
        Assert.Equal("1.0.0", card.Version);
        Assert.NotNull(card.Provider);
        Assert.Equal("OpenClaw.NET", card.Provider!.Organization);
        Assert.NotNull(card.Capabilities);
        Assert.True(card.Capabilities!.Streaming);
        Assert.Single(card.Skills!);
        Assert.Equal("general", card.Skills![0].Id);
        Assert.Contains("text/plain", card.DefaultInputModes!);
        Assert.Contains("text/plain", card.DefaultOutputModes!);
    }

    [Fact]
    public void AgentCardFactory_Creates_Card_With_ConfiguredSkills()
    {
        var options = CreateOptions();
        options.A2ASkills =
        [
            new A2ASkillConfig
            {
                Id = "route-planner",
                Name = "Route Planner",
                Description = "Plans flight routes.",
                Tags = ["travel", "flights"]
            },
            new A2ASkillConfig
            {
                Id = "translator",
                Name = "Translator",
                Description = "Translates text between languages.",
                Tags = ["nlp"]
            }
        ];

        var factory = new OpenClawAgentCardFactory(Options.Create(options));
        var card = factory.Create("http://localhost:5000/a2a");

        Assert.Equal(2, card.Skills!.Count);
        Assert.Equal("route-planner", card.Skills[0].Id);
        Assert.Equal("Route Planner", card.Skills[0].Name);
        Assert.Contains("travel", card.Skills[0].Tags!);
        Assert.Equal("translator", card.Skills[1].Id);
    }

    [Fact]
    public void AgentCardFactory_Uses_SupportedInterfaces_For_Url()
    {
        var options = CreateOptions();
        var factory = new OpenClawAgentCardFactory(Options.Create(options));

        var card = factory.Create("http://10.0.0.1:8080/a2a");

        Assert.NotNull(card.SupportedInterfaces);
        Assert.Single(card.SupportedInterfaces!);
        Assert.Equal("http://10.0.0.1:8080/a2a", card.SupportedInterfaces[0].Url);
    }

    // ── A2A Agent Handler Tests ─────────────────────────────────────

    [Fact]
    public async Task AgentHandler_ExecuteAsync_Sends_WorkingThenCompleted()
    {
        var handler = CreateHandler();
        var eventQueue = new AgentEventQueue();
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
        await handler.ExecuteAsync(context, eventQueue, CancellationToken.None);
        eventQueue.Complete();
        await foreach (var evt in eventQueue)
            events.Add(evt);

        var workingUpdate = events.FirstOrDefault(e => e.StatusUpdate?.Status.State == TaskState.Working);
        var completedUpdate = events.LastOrDefault(e => e.StatusUpdate?.Status.State == TaskState.Completed);

        Assert.NotEmpty(events);
        Assert.NotNull(completedUpdate);
        Assert.NotNull(completedUpdate!.StatusUpdate!.Status.Message);
        Assert.Contains("bridge:Hello A2A", completedUpdate.StatusUpdate.Status.Message!.Parts![0].Text);
        Assert.NotNull(workingUpdate);
    }

    [Fact]
    public async Task AgentHandler_CancelAsync_Sends_CanceledStatus()
    {
        var handler = CreateHandler();
        var eventQueue = new AgentEventQueue();
        var context = new RequestContext
        {
            Message = new Message
            {
                Role = Role.User,
                Parts = [Part.FromText("Cancel this")]
            },
            TaskId = "task-cancel",
            ContextId = "ctx-cancel",
            StreamingResponse = false
        };

        var events = new List<StreamResponse>();
        await handler.CancelAsync(context, eventQueue, CancellationToken.None);
        eventQueue.Complete();
        await foreach (var evt in eventQueue)
            events.Add(evt);

        Assert.Single(events);
        Assert.NotNull(events[0].StatusUpdate);
        Assert.Equal(TaskState.Canceled, events[0].StatusUpdate!.Status.State);
    }

    // ── A2A Service Registration Tests ──────────────────────────────

    [Fact]
    public void AddOpenClawA2AServices_Registers_Required_Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(CreateOptions()));
        services.AddOpenClawA2AServices();
        services.AddSingleton<IOpenClawA2AExecutionBridge>(new FakeExecutionBridge());


        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ITaskStore>());
        Assert.NotNull(provider.GetService<OpenClawA2AAgentHandler>());
        Assert.NotNull(provider.GetService<OpenClawAgentCardFactory>());
        Assert.NotNull(provider.GetService<IA2ARequestHandler>());
    }

    [Fact]
    public void MafServiceCollectionExtensions_Parses_A2A_Options_Without_Registering_Endpoints()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.SectionName}:EnableA2A"] = "true",
                [$"{MafOptions.SectionName}:AgentName"] = "TestA2A"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new OpenClaw.Core.Models.GatewayConfig());
        services.AddMicrosoftAgentFrameworkExperiment(config);

        using var provider = services.BuildServiceProvider();
        var resolvedOptions = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.True(resolvedOptions.EnableA2A);
        Assert.Equal("TestA2A", resolvedOptions.AgentName);
        Assert.Null(provider.GetService<IA2ARequestHandler>());
    }

    // ── A2A Server Integration Tests ────────────────────────────────

    [Fact]
    public async Task A2AServer_SendMessage_Returns_CompletedTask()
    {
        using var provider = BuildA2AServiceProvider();
        var requestHandler = provider.GetRequiredService<IA2ARequestHandler>();

        var response = await requestHandler.SendMessageAsync(
            new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    Parts = [Part.FromText("What is 2+2?")]
                }
            },
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Task);
        Assert.NotNull(response.Task!.Id);
        Assert.Equal(TaskState.Completed, response.Task.Status.State);
        Assert.Contains("bridge:What is 2+2?", response.Task.Status.Message!.Parts![0].Text);
    }

    [Fact]
    public async Task A2AServer_SendStreamingMessage_Yields_Events()
    {
        using var provider = BuildA2AServiceProvider();
        var requestHandler = provider.GetRequiredService<IA2ARequestHandler>();

        var events = new List<StreamResponse>();
        await foreach (var evt in requestHandler.SendStreamingMessageAsync(
            new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    Parts = [Part.FromText("Stream test")]
                }
            },
            CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);
        // Last event should have a completed status
        var lastStatusUpdate = events.LastOrDefault(e => e.StatusUpdate?.Status.State == TaskState.Completed);
        Assert.NotNull(lastStatusUpdate);
    }

    [Fact]
    public async Task A2AServer_GetTask_Returns_StoredTask()
    {
        using var provider = BuildA2AServiceProvider();
        var requestHandler = provider.GetRequiredService<IA2ARequestHandler>();

        // First create a task via SendMessage
        var sendResponse = await requestHandler.SendMessageAsync(
            new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    Parts = [Part.FromText("Create task")]
                }
            },
            CancellationToken.None);

        Assert.NotNull(sendResponse.Task);
        var taskId = sendResponse.Task!.Id;

        // Now retrieve it
        var getResponse = await requestHandler.GetTaskAsync(
            new GetTaskRequest { Id = taskId },
            CancellationToken.None);

        Assert.NotNull(getResponse);
        Assert.Equal(taskId, getResponse!.Id);
    }

    [Fact]
    public async Task A2AServer_CancelTask_Returns_CanceledState()
    {
        using var provider = BuildA2AServiceProvider();
        var requestHandler = provider.GetRequiredService<IA2ARequestHandler>();

        // Create a task
        var sendResponse = await requestHandler.SendMessageAsync(
            new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    Parts = [Part.FromText("Cancel me")]
                }
            },
            CancellationToken.None);

        var taskId = sendResponse.Task!.Id;

        await Assert.ThrowsAsync<A2AException>(() =>
            requestHandler.CancelTaskAsync(
                new CancelTaskRequest { Id = taskId },
                CancellationToken.None));
    }

    // ── MafOptions A2A Configuration Tests ──────────────────────────

    [Fact]
    public void MafOptions_A2A_DefaultValues()
    {
        var options = new MafOptions();

        Assert.False(options.EnableA2A);
        Assert.Equal("/a2a", options.A2APathPrefix);
        Assert.Equal("1.0.0", options.A2AVersion);
        Assert.Empty(options.A2ASkills);
    }

    [Fact]
    public void MafOptions_A2A_ParsesConfigSection()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.SectionName}:EnableA2A"] = "true",
                [$"{MafOptions.SectionName}:A2APathPrefix"] = "/agents/a2a",
                [$"{MafOptions.SectionName}:A2AVersion"] = "2.0.0-beta",
                [$"{MafOptions.SectionName}:A2ASkills:0:Id"] = "search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Name"] = "Web Search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Description"] = "Searches the web",
                [$"{MafOptions.SectionName}:A2ASkills:0:Tags:0"] = "web",
                [$"{MafOptions.SectionName}:A2ASkills:0:Tags:1"] = "search"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new OpenClaw.Core.Models.GatewayConfig());
        services.AddMicrosoftAgentFrameworkExperiment(config);

        using var provider = services.BuildServiceProvider();
        var resolvedOptions = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.True(resolvedOptions.EnableA2A);
        Assert.Equal("/agents/a2a", resolvedOptions.A2APathPrefix);
        Assert.Equal("2.0.0-beta", resolvedOptions.A2AVersion);
        Assert.Single(resolvedOptions.A2ASkills);
        Assert.Equal("search", resolvedOptions.A2ASkills[0].Id);
        Assert.Equal("Web Search", resolvedOptions.A2ASkills[0].Name);
        Assert.NotNull(resolvedOptions.A2ASkills[0].Tags);
        Assert.Equal(2, resolvedOptions.A2ASkills[0].Tags!.Count);
    }

    // ── InMemoryTaskStore Tests ─────────────────────────────────────

    [Fact]
    public async Task InMemoryTaskStore_StoreAndRetrieve()
    {
        var store = new InMemoryTaskStore();
        var task = new AgentTask
        {
            Id = "test-task-1",
            ContextId = "ctx-1",
            Status = new A2A.TaskStatus { State = TaskState.Completed }
        };

        await store.SaveTaskAsync(task.Id, task, CancellationToken.None);
        var retrieved = await store.GetTaskAsync("test-task-1", CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal("test-task-1", retrieved!.Id);
        Assert.Equal(TaskState.Completed, retrieved.Status.State);
    }

    [Fact]
    public async Task InMemoryTaskStore_GetNonExistent_ReturnsNull()
    {
        var store = new InMemoryTaskStore();
        var retrieved = await store.GetTaskAsync("nonexistent", CancellationToken.None);
        Assert.Null(retrieved);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static MafOptions CreateOptions() => new()
    {
        AgentName = "TestAgent",
        AgentDescription = "Test agent for A2A integration tests.",
        EnableStreaming = true,
        EnableA2A = true,
        A2AVersion = "1.0.0"
    };

    private static OpenClawA2AAgentHandler CreateHandler()
        => new(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgentHandler>.Instance);

    private static ServiceProvider BuildA2AServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(CreateOptions()));
        services.AddOpenClawA2AServices();
        services.AddSingleton<IOpenClawA2AExecutionBridge>(new FakeExecutionBridge());
        return services.BuildServiceProvider();
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
}
#endif

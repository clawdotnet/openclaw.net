using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Memory;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FractalMemoryTests
{
    [Fact]
    public void GatewayConfig_DefaultsKeepFractalMemoryDisabled()
    {
        var config = new GatewayConfig();

        Assert.False(config.Memory.Fractal.Enabled);
        Assert.Equal("mcp", config.Memory.Fractal.Mode);
        Assert.Equal("fractalmem-mcp", config.Memory.Fractal.McpCommand);
        Assert.Equal("off", config.Memory.Fractal.AutoContextMode);
        Assert.False(config.Memory.Fractal.AllowWrites);
    }

    [Fact]
    public void ConfigValidator_RejectsInvalidFractalMemoryValues()
    {
        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.Mode = "native";
        config.Memory.Fractal.McpCommand = "";
        config.Memory.Fractal.DefaultDepth = 7;
        config.Memory.Fractal.DefaultView = "everything";
        config.Memory.Fractal.DefaultExportMode = "full";
        config.Memory.Fractal.AutoContextMode = "always";
        config.Memory.Fractal.MaxContextChars = 100;
        config.Memory.Fractal.MaxContextTokens = 64;

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Memory.Fractal.Mode", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.McpCommand", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.DefaultDepth", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.DefaultView", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.DefaultExportMode", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.AutoContextMode", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.MaxContextChars", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Memory.Fractal.MaxContextTokens", StringComparison.Ordinal));
    }

    [Fact]
    public void HarnessModels_SerializeFractalMemoryDtos()
    {
        var result = new StructuredMemorySearchResult
        {
            Success = true,
            Query = "context budget",
            Items =
            [
                new StructuredMemorySourceRef
                {
                    Path = "projects/openclaw",
                    Title = "OpenClaw",
                    SourcePath = "projects/openclaw/state.md",
                    StartLine = 3,
                    EndLine = 9
                }
            ]
        };

        var json = JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemorySearchResult);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.StructuredMemorySearchResult);

        Assert.NotNull(restored);
        Assert.True(restored!.Success);
        Assert.Equal("projects/openclaw", Assert.Single(restored.Items).Path);
    }

    [Fact]
    public async Task ContextBudgetPlanner_UsesSearchThenCompactExportAndEnforcesBudget()
    {
        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.AutoContextMode = "auto";
        config.Memory.Fractal.MaxContextChars = 500;
        config.Memory.Fractal.MaxContextTokens = 1000;
        var provider = new FakeStructuredMemoryProvider
        {
            ExportContent = new string('x', 1000)
        };

        var planner = new ContextBudgetPlanner(config, provider);
        var context = await planner.BuildContextAsync(new StructuredMemoryContextRequest
        {
            Query = "reduce context bloat",
            Mode = "auto",
            MaxChars = 500
        }, CancellationToken.None);

        Assert.True(context.Success);
        Assert.True(context.Truncated);
        Assert.NotNull(context.Context);
        Assert.True(context.Context!.Length <= 500);
        Assert.Contains("<fractal_memory_context>", context.Context, StringComparison.Ordinal);
        Assert.Contains("projects/demo/state.md", context.Context, StringComparison.Ordinal);
        Assert.Equal(1, provider.SearchCalls);
        Assert.Equal(1, provider.ExportCalls);
        Assert.Equal("compact", provider.LastExportMode);
    }

    [Fact]
    public async Task ContextBudgetPlanner_RejectsInvalidModeAndHandlesLargeTokenBudgets()
    {
        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.AutoContextMode = "auto";
        config.Memory.Fractal.MaxContextChars = int.MaxValue;
        config.Memory.Fractal.MaxContextTokens = int.MaxValue;
        var planner = new ContextBudgetPlanner(config, new FakeStructuredMemoryProvider());

        var invalid = await planner.BuildContextAsync(new StructuredMemoryContextRequest
        {
            Query = "anything",
            Mode = "surprise"
        }, CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Contains("Unsupported Fractal Memory context mode", invalid.Error, StringComparison.Ordinal);

        var valid = await planner.BuildContextAsync(new StructuredMemoryContextRequest
        {
            Query = "anything",
            Mode = "auto",
            MaxTokens = int.MaxValue
        }, CancellationToken.None);
        Assert.True(valid.Success);
    }

    [Fact]
    public async Task FractalTools_ExposeReadToolsAndApprovalMetadataForWrites()
    {
        var config = new FractalMemoryConfig();
        var provider = new FakeStructuredMemoryProvider();

        var search = new FractalMemorySearchTool(provider);
        var searchJson = await search.ExecuteAsync("""{"query":"handoff"}""", CancellationToken.None);
        var searchResult = JsonSerializer.Deserialize(searchJson, CoreJsonContext.Default.StructuredMemorySearchResult);
        Assert.True(searchResult?.Success);
        Assert.IsNotAssignableFrom<IToolActionDescriptorProvider>(search);

        var handoff = new FractalMemoryHandoffCreateTool(provider, config);
        var descriptor = handoff.ResolveActionDescriptor("""{"path":"projects/demo"}""");

        Assert.True(descriptor.IsMutation);
        Assert.True(descriptor.RequiresApproval);
        Assert.False(descriptor.ReadOnly);
        Assert.Equal("medium", descriptor.RiskLevel);
    }

    [Fact]
    public async Task FractalTools_ClampNumericArgumentsAndIgnoreMalformedScalarTypes()
    {
        var provider = new FakeStructuredMemoryProvider();
        var config = new FractalMemoryConfig();

        var search = new FractalMemorySearchTool(provider);
        var malformed = await search.ExecuteAsync("""{"query":123}""", CancellationToken.None);
        var malformedResult = JsonSerializer.Deserialize(malformed, CoreJsonContext.Default.MutationResponse);
        Assert.False(malformedResult?.Success);

        _ = await search.ExecuteAsync("""{"query":"handoff","limit":999}""", CancellationToken.None);
        Assert.Equal(50, provider.LastSearchLimit);

        var open = new FractalMemoryOpenTool(provider, config);
        _ = await open.ExecuteAsync("""{"path":"projects/demo","depth":999}""", CancellationToken.None);
        Assert.Equal(3, provider.LastOpenDepth);

        var recent = new FractalMemoryRecentTool(provider);
        _ = await recent.ExecuteAsync("""{"days":0,"limit":999}""", CancellationToken.None);
        Assert.Equal(1, provider.LastRecentDays);
        Assert.Equal(100, provider.LastRecentLimit);
    }

    [Fact]
    public async Task FractalMemoryMcpProvider_UnavailableCommandReturnsClearError()
    {
        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.McpCommand = "/definitely/not/a/fractalmem-mcp";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var provider = new FractalMemoryMcpProvider(
            config,
            workspacePath: Directory.GetCurrentDirectory(),
            NullLogger<FractalMemoryMcpProvider>.Instance);

        var result = await provider.SearchAsync("anything", 1, null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("Fractal Memory", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentRuntime_AutoModeInjectsCompactFractalContext()
    {
        var chatClient = Substitute.For<IChatClient>();
        IList<ChatMessage>? captured = null;
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(messages => captured = messages),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "ok") })));

        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.AutoContextMode = "auto";
        config.Memory.Fractal.MaxContextChars = 4000;
        config.Memory.Fractal.MaxContextTokens = 1000;
        var planner = new ContextBudgetPlanner(config, new FakeStructuredMemoryProvider());
        var memory = Substitute.For<IMemoryStore>();

        var agent = new AgentRuntime(
            chatClient,
            tools: [],
            memory,
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            gatewayConfig: config,
            contextBudgetPlanner: planner);

        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "u1" };
        _ = await agent.RunAsync(session, "what changed in project memory?", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains(captured!, message =>
            message.Role == ChatRole.User &&
            (message.Text ?? "").Contains("<fractal_memory_context>", StringComparison.Ordinal) &&
            (message.Text ?? "").Contains("untrusted_reference_data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentRuntime_DefaultConfigDoesNotInjectFractalContext()
    {
        var chatClient = Substitute.For<IChatClient>();
        IList<ChatMessage>? captured = null;
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(messages => captured = messages),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "ok") })));

        var config = new GatewayConfig();
        var planner = new ContextBudgetPlanner(config, new FakeStructuredMemoryProvider());
        var memory = Substitute.For<IMemoryStore>();

        var agent = new AgentRuntime(
            chatClient,
            tools: [],
            memory,
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            gatewayConfig: config,
            contextBudgetPlanner: planner);

        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "u1" };
        _ = await agent.RunAsync(session, "what changed in project memory?", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain(captured!, message =>
            (message.Text ?? "").Contains("<fractal_memory_context>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentRuntime_FractalContextDoesNotReorderMemoryRecall()
    {
        var chatClient = Substitute.For<IChatClient>();
        IList<ChatMessage>? captured = null;
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(messages => captured = messages),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "ok") })));

        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.AutoContextMode = "auto";
        var planner = new ContextBudgetPlanner(config, new FakeStructuredMemoryProvider());
        var memory = Substitute.For<IMemoryStore, IMemoryNoteSearch>();
        var search = (IMemoryNoteSearch)memory;
        search.SearchNotesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<MemoryNoteHit>>(
            [
                new MemoryNoteHit { Key = "note:1", Content = "remember this", UpdatedAt = DateTimeOffset.UtcNow, Score = 1 }
            ]));

        var agent = new AgentRuntime(
            chatClient,
            tools: [],
            memory,
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            recall: new MemoryRecallConfig { Enabled = true, MaxNotes = 5, MaxChars = 4000 },
            gatewayConfig: config,
            contextBudgetPlanner: planner);

        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "u1" };
        _ = await agent.RunAsync(session, "what should I remember?", CancellationToken.None);

        Assert.NotNull(captured);
        var recallIndex = captured!.Select((message, index) => (message, index))
            .First(item => (item.message.Text ?? "").Contains("[Relevant memory]", StringComparison.Ordinal))
            .index;
        var fractalIndex = captured!.Select((message, index) => (message, index))
            .First(item => (item.message.Text ?? "").Contains("<fractal_memory_context>", StringComparison.Ordinal))
            .index;
        Assert.True(recallIndex < fractalIndex);
    }

    private sealed class FakeStructuredMemoryProvider : IStructuredMemoryProvider
    {
        public int SearchCalls { get; private set; }
        public int ExportCalls { get; private set; }
        public int LastSearchLimit { get; private set; }
        public int LastOpenDepth { get; private set; }
        public int LastRecentDays { get; private set; }
        public int LastRecentLimit { get; private set; }
        public string? LastExportMode { get; private set; }
        public string ExportContent { get; set; } = "Current state: use compact structured project memory.";

        public Task<StructuredMemoryStatusResponse> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new StructuredMemoryStatusResponse
            {
                Enabled = true,
                Available = true,
                Status = "available",
                Mode = "mcp",
                ResolvedRepositoryRoot = "/tmp/fractal-memory"
            });

        public Task<StructuredMemorySearchResult> SearchAsync(string query, int limit, string? scope, CancellationToken ct)
        {
            SearchCalls++;
            LastSearchLimit = limit;
            return Task.FromResult(new StructuredMemorySearchResult
            {
                Success = true,
                Query = query,
                Scope = scope,
                Items =
                [
                    new StructuredMemorySourceRef
                    {
                        Path = "projects/demo",
                        Title = "Demo"
                    }
                ]
            });
        }

        public Task<StructuredMemoryOpenResult> OpenAsync(string path, int depth, string view, CancellationToken ct)
        {
            LastOpenDepth = depth;
            return Task.FromResult(new StructuredMemoryOpenResult
            {
                Success = true,
                Path = path,
                Depth = depth,
                View = view,
                Content = ExportContent
            });
        }

        public Task<StructuredMemoryRecentResult> RecentAsync(int days, int limit, string? scope, CancellationToken ct)
        {
            LastRecentDays = days;
            LastRecentLimit = limit;
            return Task.FromResult(new StructuredMemoryRecentResult
            {
                Success = true,
                Days = days,
                Scope = scope,
                Items = [new StructuredMemorySourceRef { Path = "projects/demo" }]
            });
        }

        public Task<StructuredMemoryExportResult> ExportAsync(string path, string mode, CancellationToken ct)
        {
            ExportCalls++;
            LastExportMode = mode;
            return Task.FromResult(new StructuredMemoryExportResult
            {
                Success = true,
                Path = path,
                Mode = mode,
                Content = ExportContent,
                Sources =
                [
                    new StructuredMemorySourceRef
                    {
                        Path = path,
                        SourcePath = "projects/demo/state.md",
                        StartLine = 1,
                        EndLine = 4
                    }
                ],
                CharCount = ExportContent.Length
            });
        }

        public Task<StructuredMemoryHandoffResult> CreateHandoffAsync(string path, CancellationToken ct)
            => Task.FromResult(new StructuredMemoryHandoffResult { Success = true, Path = path, HandoffFilePath = $"{path}/handoff.md" });

        public Task<StructuredMemoryValidationResult> ValidateAsync(CancellationToken ct)
            => Task.FromResult(new StructuredMemoryValidationResult { Success = true, Summary = "ok" });

        public Task<StructuredMemoryValidationResult> RefreshIndexAsync(CancellationToken ct)
            => Task.FromResult(new StructuredMemoryValidationResult { Success = true, Summary = "refreshed" });
    }
}

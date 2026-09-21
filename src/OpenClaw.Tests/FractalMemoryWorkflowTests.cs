using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent.Memory;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FractalMemoryWorkflowTests
{
    [Fact]
    public void ToolRegistration_RespectsEnabledAndWritesButIncludesPreviews()
    {
        var provider = Substitute.For<IStructuredMemoryWorkflowProvider>();
        var config = new FractalMemoryConfig();
        Assert.Empty(FractalMemoryWorkflowTool.CreateTools(provider, config));
        config.Enabled = true;
        var tools = FractalMemoryWorkflowTool.CreateTools(provider, config).ToArray();
        Assert.Equal(10, tools.Length);
        Assert.Contains(tools, tool => tool.Name == "fractal_memory_import");
        Assert.Contains(tools, tool => tool.Name == "fractal_memory_doctor");
        Assert.DoesNotContain(tools, tool => tool.Name == "fractal_memory_update");
        config.AllowWrites = true;
        Assert.Equal(14, FractalMemoryWorkflowTool.CreateTools(provider, config).Count());
    }

    [Theory]
    [InlineData("doctor", "{}", false)]
    [InlineData("doctor", "{\"repair\":false}", false)]
    [InlineData("doctor", "{\"repair\":true}", true)]
    [InlineData("doctor", "{\"repair\":\"false\"}", true)]
    [InlineData("import", "{\"path\":\"projects/demo\",\"sourceName\":\"note.md\",\"content\":\"hi\"}", false)]
    [InlineData("import", "{\"path\":\"projects/demo\",\"sourceName\":\"note.md\",\"content\":\"hi\",\"apply\":true}", true)]
    [InlineData("read", "[]", true)]
    public void Descriptors_ClassifyActualMutation(string operation, string arguments, bool mutation)
    {
        var tool = new FractalMemoryWorkflowTool(Substitute.For<IStructuredMemoryWorkflowProvider>(), new(), FractalMemoryWorkflows.Find(operation)!);
        var descriptor = tool.ResolveActionDescriptor(arguments);
        Assert.Equal(mutation, descriptor.IsMutation);
        Assert.Equal(mutation, descriptor.RequiresApproval);
        Assert.Equal(!mutation, descriptor.ReadOnly);
    }

    [Fact]
    public void ApprovalFingerprint_BindsContentHashAndFlags()
    {
        var tool = new FractalMemoryWorkflowTool(Substitute.For<IStructuredMemoryWorkflowProvider>(), new(), FractalMemoryWorkflows.Find("update")!);
        const string original = """{"path":"projects/demo","section":"Current Objective","content":"first","expectedHash":"v1"}""";
        var fingerprint = tool.ResolveActionDescriptor(original).ApprovalFingerprint;
        Assert.NotEqual(fingerprint, tool.ResolveActionDescriptor(original.Replace("first", "second")).ApprovalFingerprint);
        Assert.NotEqual(fingerprint, tool.ResolveActionDescriptor(original.Replace("v1", "v2")).ApprovalFingerprint);
    }

    [Theory]
    [InlineData("update", "{\"path\":\"projects/demo\",\"section\":\"Objective\",\"content\":\"new\"}")]
    [InlineData("doctor", "{\"repair\":\"false\"}")]
    [InlineData("doctor", "{\"repair\":false,\"repair\":true}")]
    [InlineData("context", "{\"path\":\"projects/demo\",\"maxCharacters\":255}")]
    [InlineData("read", "{\"path\":\"projects/demo\",\"unexpected\":true}")]
    [InlineData("read", "[]")]
    [InlineData("read", "{")]
    [InlineData("review", "{\"path\":\"projects/demo\",\"reviewAfter\":\"tomorrow\",\"expectedHash\":\"v1\"}")]
    public async Task InvalidArguments_DoNotReachProvider(string operation, string arguments)
    {
        var provider = Substitute.For<IStructuredMemoryWorkflowProvider>();
        var tool = new FractalMemoryWorkflowTool(provider, new(), FractalMemoryWorkflows.Find(operation)!);
        using var result = JsonDocument.Parse(await tool.ExecuteAsync(arguments, TestContext.Current.CancellationToken));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(provider.ReceivedCalls());
    }

    [Theory]
    [InlineData("doctor", "{\"repair\":true}")]
    [InlineData("import", "{\"path\":\"projects/demo\",\"sourceName\":\"note.md\",\"content\":\"hi\",\"apply\":true}")]
    [InlineData("node_create", "{\"path\":\"projects/demo\"}")]
    public async Task DisabledWrites_AreBlockedAtToolAndProvider(string operation, string arguments)
    {
        using var doc = JsonDocument.Parse(arguments);
        var provider = Substitute.For<IStructuredMemoryWorkflowProvider>();
        var tool = new FractalMemoryWorkflowTool(provider, new(), FractalMemoryWorkflows.Find(operation)!);
        Assert.Contains("writes are disabled", await tool.ExecuteAsync(arguments, TestContext.Current.CancellationToken));
        Assert.Empty(provider.ReceivedCalls());

        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.McpCommand = "/must/not/be/launched";
        await using var realProvider = new FractalMemoryMcpProvider(config, null, NullLogger<FractalMemoryMcpProvider>.Instance);
        var result = await realProvider.ExecuteWorkflowAsync(operation, doc.RootElement, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("writes are disabled", result.Error);
    }

    [Fact]
    public async Task Stdio_PreservesStructuredDataLinksAndLaunchArguments()
    {
        await using var fixture = new StdioFixture();
        using var arguments = JsonDocument.Parse("""{"path":"projects/demo","file":"state","section":"Current Objective"}""");
        var read = await fixture.Provider.ExecuteWorkflowAsync("read", arguments.RootElement, TestContext.Current.CancellationToken);
        Assert.True(read.Success, read.Error);
        Assert.Equal("hash-from-disk", read.Data!.Value.GetProperty("hash").GetString());
        Assert.Equal("memory://document/projects%2Fdemo/state.md", Assert.Single(read.Resources).Uri);
        var json = JsonSerializer.Serialize(read, CoreJsonContext.Default.StructuredMemoryWorkflowResult);
        Assert.Equal("hash-from-disk", JsonSerializer.Deserialize(json, CoreJsonContext.Default.StructuredMemoryWorkflowResult)!.Data!.Value.GetProperty("hash").GetString());

        using var previewArgs = JsonDocument.Parse("""{"path":"projects/demo","sourceName":"note.md","content":"hello\nworld"}""");
        var preview = await fixture.Provider.ExecuteWorkflowAsync("import", previewArgs.RootElement, TestContext.Current.CancellationToken);
        Assert.True(preview.Success, preview.Error);
        var data = preview.Data!.Value;
        Assert.Equal("hello\nworld", data.GetProperty("arguments").GetProperty("content").GetString());
        Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetFullPath(data.GetProperty("repositoryRoot").GetString()!));
        Assert.EndsWith(Path.GetFileName(fixture.Root), data.GetProperty("workingDirectory").GetString());
    }

    [Theory]
    [InlineData("no-repository")]
    [InlineData("protocol-error")]
    public async Task Status_RequiresUsableRepositoryAndContainsProtocolErrors(string mode)
    {
        await using var fixture = new StdioFixture(mode);
        var status = await fixture.Provider.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.Available);
        Assert.False(status.WriteToolsAvailable);
        Assert.Equal("unavailable", status.Status);
        Assert.NotEmpty(status.Error!);
        var search = await fixture.Provider.SearchAsync("hello", 3, null, TestContext.Current.CancellationToken);
        Assert.False(search.Success);
        Assert.NotEmpty(search.Error!);
    }

    [Theory]
    [InlineData("current", "context", "Bounded objective")]
    [InlineData("legacy", "compact", "Legacy objective")]
    public async Task Context_UsesBoundedWorkflowOrLegacyExport(string serverMode, string expectedMode, string content)
    {
        await using var fixture = new StdioFixture(serverMode);
        fixture.Config.Memory.Fractal.AutoContextMode = "auto";
        var planner = new ContextBudgetPlanner(fixture.Config, fixture.Provider);
        var result = await planner.BuildContextAsync(new() { PathHint = "projects/demo", Mode = "auto", MaxChars = 500 }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedMode, result.Mode);
        Assert.Contains(content, result.Context);
        Assert.True(result.Context!.Length <= 500);
        if (serverMode == "current")
        {
            Assert.True(result.Truncated);
            Assert.Equal("projects/demo/state.md", Assert.Single(result.Sources).SourcePath);
        }
        else
        {
            using var args = JsonDocument.Parse("""{"path":"projects/demo"}""");
            var read = await fixture.Provider.ExecuteWorkflowAsync("read", args.RootElement, TestContext.Current.CancellationToken);
            Assert.False(read.Success);
            Assert.Contains("Update FractalMemory.McpServer", read.Error);
        }
        var open = await fixture.Provider.OpenAsync("projects/demo", 2, "index", TestContext.Current.CancellationToken);
        Assert.Equal("state", open.View);
        Assert.True(open.StateTruncated);
    }

    [Fact]
    public async Task Context_ReservesRoomForOpenClawEnvelope()
    {
        var provider = Substitute.For<IStructuredMemoryProvider, IStructuredMemoryWorkflowProvider>();
        var workflows = (IStructuredMemoryWorkflowProvider)provider;
        workflows.BuildContextAsync("projects/demo", 488, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StructuredMemoryExportResult
            {
                Success = true,
                Path = "projects/demo",
                Mode = "context",
                Content = "Bounded objective"
            }));
        var config = new GatewayConfig();
        config.Memory.Fractal.Enabled = true;
        config.Memory.Fractal.AutoContextMode = "auto";
        config.Memory.Fractal.MaxContextChars = 1000;
        config.Memory.Fractal.MaxContextTokens = 1000;

        var result = await new ContextBudgetPlanner(config, provider).BuildContextAsync(
            new() { PathHint = "projects/demo", Mode = "auto", MaxChars = 1000, MaxTokens = 1000 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        await workflows.Received(1).BuildContextAsync("projects/demo", 488, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryRoot_DefaultResolvesRelativeWorkspaceOnce()
    {
        var root = Path.GetTempPath();
        var relativeWorkspace = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        await using var provider = new FractalMemoryMcpProvider(new(), relativeWorkspace, NullLogger<FractalMemoryMcpProvider>.Instance);
        var status = await provider.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(status.ResolvedRepositoryRoot));
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToProviderFailure()
    {
        await using var fixture = new StdioFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GetStatusAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.BuildContextAsync("projects/demo", 500, cts.Token));
    }

    private sealed class StdioFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "fractal fixture " + Guid.NewGuid().ToString("N"));
        public GatewayConfig Config { get; } = new();
        public FractalMemoryMcpProvider Provider { get; }
        public StdioFixture(string mode = "current")
        {
            Directory.CreateDirectory(Path.Join(Root, ".fractal-memory"));
            File.WriteAllText(Path.Join(Root, ".fractal-memory", "config.yaml"), "version: 0.1");
            Config.Memory.Fractal.Enabled = true;
            Config.Memory.Fractal.McpCommand = "node";
            Config.Memory.Fractal.RepositoryRoot = ".";
            Config.Memory.Fractal.McpArguments = [Path.Join(AppContext.BaseDirectory, "Fixtures", "fractal-memory-mcp.mjs"), mode];
            Provider = new(Config, Root, NullLogger<FractalMemoryMcpProvider>.Instance);
        }
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}

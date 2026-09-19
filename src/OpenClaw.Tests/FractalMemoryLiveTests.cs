using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Memory;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FractalMemoryLiveTests
{
    public static bool LiveEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_FRACTAL_SOURCE"));

    [Fact(Skip = "Set OPENCLAW_FRACTAL_SOURCE to a built FractalMemory source checkout.", SkipUnless = nameof(LiveEnabled))]
    public async Task UpstreamStdio_CaptureResumeAndImportRoundTrip()
    {
        var source = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCLAW_FRACTAL_SOURCE")!);
        var cli = Path.Join(source, "src", "FractalMemory.Cli", "bin", "Debug", "net10.0", "fm.dll");
        var server = Path.Join(source, "src", "FractalMemory.McpServer", "bin", "Debug", "net10.0", "fractalmem-mcp.dll");
        Assert.True(File.Exists(cli), $"Build the upstream CLI first: {cli}");
        Assert.True(File.Exists(server), $"Build the upstream MCP server first: {server}");
        var root = Path.Join(Path.GetTempPath(), "openclaw-fractal-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(cli);
            start.ArgumentList.Add("init");
            using (var process = Process.Start(start)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                Assert.True(process.ExitCode == 0, await stderr + await stdout);
            }
            var config = new GatewayConfig();
            config.Memory.Fractal.Enabled = true;
            config.Memory.Fractal.AllowWrites = true;
            config.Memory.Fractal.AutoContextMode = "auto";
            config.Memory.Fractal.McpCommand = "dotnet";
            config.Memory.Fractal.McpArguments = [server];
            config.Memory.Fractal.RepositoryRoot = root;
            await using var provider = new FractalMemoryMcpProvider(config, root, NullLogger<FractalMemoryMcpProvider>.Instance);

            async Task<StructuredMemoryWorkflowResult> Invoke(string operation, string json)
            {
                using var args = JsonDocument.Parse(json);
                var result = await provider.ExecuteWorkflowAsync(operation, args.RootElement, cts.Token);
                Assert.True(result.Success, $"{operation}: {result.Error}");
                return result;
            }
            const string pathArgs = """{"path":"projects/integration"}""";
            await Invoke("node_create", pathArgs);
            var read = await Invoke("read", pathArgs);
            Assert.NotEmpty(read.Resources);
            var hash = read.Data!.Value.GetProperty("hash").GetString();
            var updateArgs = $$"""{"path":"projects/integration","section":"Current Objective","content":"Verify OpenClaw integration end to end.","expectedHash":"{{hash}}"}""";
            var updated = await Invoke("update", updateArgs);
            Assert.NotEqual(hash, updated.Data!.Value.GetProperty("document").GetProperty("hash").GetString());
            using (var staleArgs = JsonDocument.Parse(updateArgs))
            {
                var stale = await provider.ExecuteWorkflowAsync("update", staleArgs.RootElement, cts.Token);
                Assert.False(stale.Success);
                Assert.NotEmpty(stale.Error!);
            }

            var decisions = await Invoke("read", """{"path":"projects/integration","file":"decisions"}""");
            var decisionHash = decisions.Data!.Value.GetProperty("hash").GetString();
            var appended = await Invoke("append", $$"""{"path":"projects/integration","file":"decisions","content":"Keep memory local.","expectedHash":"{{decisionHash}}"}""");
            Assert.NotEmpty(appended.Data!.Value.GetProperty("decisionId").GetString()!);
            await Invoke("decisions", pathArgs);
            var index = await Invoke("read", """{"path":"projects/integration","file":"index"}""");
            await Invoke("review", $$"""{"path":"projects/integration","reviewAfter":"2030-01-01T00:00:00Z","status":"Active","expectedHash":"{{index.Data!.Value.GetProperty("hash").GetString()}}"}""");
            await Invoke("list", """{"scope":"projects"}""");
            await Invoke("attention", """{"scope":"projects"}""");
            var context = await Invoke("context", """{"path":"projects/integration","maxCharacters":512}""");
            Assert.True(context.Data!.Value.GetProperty("text").GetString()!.Length <= 512);
            Assert.NotEmpty(context.Resources);
            Assert.True((await provider.CreateHandoffAsync("projects/integration", cts.Token)).Success);
            await Invoke("handoff_list", pathArgs);
            await Invoke("handoff_read", pathArgs);
            var resume = await Invoke("resume", pathArgs);
            var resumeData = Assert.IsType<JsonElement>(resume.Data);
            Assert.True(resumeData.GetProperty("comparisonAvailable").GetBoolean());
            Assert.Equal(0, resumeData.GetProperty("changedFiles").GetArrayLength());
            await Invoke("doctor", """{"repair":true}""");

            const string importArgs = """{"path":"research/imported","sourceName":"note.md","content":"# Imported\nOriginal notes."}""";
            var preview = await Invoke("import", importArgs);
            var previewData = Assert.IsType<JsonElement>(preview.Data);
            Assert.True(previewData.GetProperty("canApply").GetBoolean());
            Assert.False(previewData.GetProperty("applied").GetBoolean());
            Assert.False(Directory.Exists(Path.Join(root, ".fractal-memory", "research", "imported")));
            var imported = await Invoke("import", importArgs[..^1] + ",\"apply\":true}");
            Assert.True(imported.Data!.Value.GetProperty("applied").GetBoolean());
            await Invoke("read", """{"path":"research/imported","file":"artifacts/source.md"}""");

            var search = await provider.SearchAsync("integration", 5, "projects", cts.Token);
            Assert.True(search.Success, search.Error);
            Assert.Contains(search.Items, item => item.Path == "projects/integration");
            var recent = await provider.RecentAsync(30, 5, "projects", cts.Token);
            Assert.True(recent.Success, recent.Error);
            Assert.NotEmpty(recent.Items);
            var open = await provider.OpenAsync("projects/integration", 3, "state", cts.Token);
            Assert.True(open.Success, open.Error);
            Assert.Equal("state", open.View);
            Assert.Contains("Verify OpenClaw integration", open.Content);
            var export = await provider.ExportAsync("projects/integration", "compact", cts.Token);
            Assert.True(export.Success, export.Error);
            Assert.Equal("compact", export.Mode);
            Assert.NotEmpty(export.Sources);
            Assert.True((await provider.RefreshIndexAsync(cts.Token)).Success);
            var status = await provider.GetStatusAsync(cts.Token);
            Assert.True(status.Available, status.Error);
            Assert.False(status.Validation!.HasErrors);
            Assert.Contains("memory_update", status.AvailableTools);

            var automatic = await new ContextBudgetPlanner(config, provider).BuildContextAsync(
                new() { PathHint = "projects/integration", Mode = "auto", MaxChars = 1500 }, cts.Token);
            Assert.True(automatic.Success, automatic.Error);
            Assert.True(automatic.Context!.Length <= 1500);
            Assert.Contains("Verify OpenClaw integration", automatic.Context);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

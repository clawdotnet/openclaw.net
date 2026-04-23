using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MaintenanceCoordinatorTests
{
    [Fact]
    public async Task ScanAndFix_ReportManagedFindings_WithoutMutatingPromptFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-maintenance-tests", Guid.NewGuid().ToString("N"));
        var memoryRoot = Path.Combine(root, "memory");
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(memoryRoot);
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var agentsPath = Path.Combine(workspaceRoot, "AGENTS.md");
            var soulPath = Path.Combine(workspaceRoot, "SOUL.md");
            var agentsContent = new string('A', 13_000);
            var soulContent = new string('S', 13_500);
            await File.WriteAllTextAsync(agentsPath, agentsContent);
            await File.WriteAllTextAsync(soulPath, soulContent);

            var adminRoot = Path.Combine(memoryRoot, "admin");
            var evaluationRoot = Path.Combine(adminRoot, "model-evaluations");
            Directory.CreateDirectory(evaluationRoot);
            for (var i = 0; i < 48; i++)
                await File.WriteAllTextAsync(Path.Combine(evaluationRoot, $"eval-{i:D2}.json"), "{}");

            Directory.CreateDirectory(Path.Combine(memoryRoot, "logs"));
            await File.WriteAllTextAsync(Path.Combine(memoryRoot, "logs", "cache-trace.jsonl"), "trace");

            Directory.CreateDirectory(adminRoot);
            var metadata = new List<SessionMetadataSnapshot>
            {
                new() { SessionId = "missing-session", Tags = ["ops"] }
            };
            await File.WriteAllTextAsync(
                Path.Combine(adminRoot, "session-metadata.json"),
                JsonSerializer.Serialize(metadata, CoreJsonContext.Default.ListSessionMetadataSnapshot));

            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = memoryRoot
                },
                Tooling = new ToolingConfig
                {
                    WorkspaceRoot = workspaceRoot
                }
            };

            var lowScan = await MaintenanceCoordinator.ScanAsync(
                config,
                new MaintenanceScanInputs
                {
                    ConfigPath = Path.Combine(root, "openclaw.settings.json"),
                    RecentTurns =
                    [
                        BuildTurn("sess-1", 500),
                        BuildTurn("sess-2", 650)
                    ],
                    LoadedSkills =
                    [
                        new SkillDefinition
                        {
                            Name = "triage",
                            Description = "Triage messages.",
                            Instructions = "Do triage.",
                            Location = workspaceRoot
                        }
                    ]
                },
                CancellationToken.None);

            var report = await MaintenanceCoordinator.ScanAsync(
                config,
                new MaintenanceScanInputs
                {
                    ConfigPath = Path.Combine(root, "openclaw.settings.json"),
                    RecentTurns =
                    [
                        BuildTurn("sess-3", 32_000),
                        BuildTurn("sess-4", 34_000)
                    ],
                    LoadedSkills =
                    [
                        new SkillDefinition
                        {
                            Name = "triage",
                            Description = "Triage messages.",
                            Instructions = "Do triage.",
                            Location = workspaceRoot
                        }
                    ]
                },
                CancellationToken.None);

            Assert.Equal(0, lowScan.Drift.PromptP95Delta);
            Assert.True(report.Drift.PromptP95Delta > 0);
            Assert.Contains(report.Findings, finding => finding.Id == "orphaned-session-metadata");
            Assert.Contains(report.Findings, finding => finding.Id == "model-evaluation-artifacts");
            Assert.Contains(report.Findings, finding => finding.Id == "prompt-cache-traces");
            Assert.Contains(report.Findings, finding => finding.Id == "agents-file-size");
            Assert.Contains(report.Findings, finding => finding.Id == "soul-file-size");
            Assert.True(report.Reliability.Score >= 0);
            Assert.NotEmpty(report.Reliability.Recommendations);
            Assert.True(File.Exists(Path.Combine(adminRoot, "maintenance-history.json")));

            var dryRun = await MaintenanceCoordinator.FixAsync(
                config,
                new MaintenanceFixRequest
                {
                    DryRun = true,
                    Apply = "all"
                },
                new MaintenanceScanInputs(),
                CancellationToken.None);

            Assert.Contains(dryRun.Actions, action => action.Id == "metadata");
            Assert.Contains(dryRun.Actions, action => action.Id == "model-evaluations");
            Assert.Contains(dryRun.Actions, action => action.Id == "prompt-cache-traces");
            Assert.Equal(agentsContent, await File.ReadAllTextAsync(agentsPath));
            Assert.Equal(soulContent, await File.ReadAllTextAsync(soulPath));

            var applied = await MaintenanceCoordinator.FixAsync(
                config,
                new MaintenanceFixRequest
                {
                    DryRun = false,
                    Apply = "all"
                },
                new MaintenanceScanInputs(),
                CancellationToken.None);

            Assert.True(applied.Success);
            Assert.False(File.Exists(Path.Combine(memoryRoot, "logs", "cache-trace.jsonl")));
            Assert.Empty(JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(adminRoot, "session-metadata.json")),
                CoreJsonContext.Default.ListSessionMetadataSnapshot) ?? []);
            Assert.True(Directory.EnumerateFiles(evaluationRoot).Count() <= 20);
            Assert.Equal(agentsContent, await File.ReadAllTextAsync(agentsPath));
            Assert.Equal(soulContent, await File.ReadAllTextAsync(soulPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FixAsync_ContinuesWhenMetadataPruningFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-maintenance-tests", Guid.NewGuid().ToString("N"));
        var memoryRoot = Path.Combine(root, "memory");
        var metadataPath = Path.Combine(memoryRoot, "admin", "session-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);

        try
        {
            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(
                    new List<SessionMetadataSnapshot> { new() { SessionId = "missing-session" } },
                    CoreJsonContext.Default.ListSessionMetadataSnapshot));
            SetReadOnly(metadataPath, readOnly: true);

            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = memoryRoot
                }
            };

            var result = await MaintenanceCoordinator.FixAsync(
                config,
                new MaintenanceFixRequest
                {
                    DryRun = false,
                    Apply = "metadata"
                },
                new MaintenanceScanInputs(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(result.Warnings, warning => warning.Contains("Metadata pruning could not run", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(metadataPath))
                SetReadOnly(metadataPath, readOnly: false);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void SetReadOnly(string path, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
            return;
        }

        File.SetUnixFileMode(
            path,
            readOnly
                ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static ProviderTurnUsageEntry BuildTurn(string sessionId, long inputTokens)
        => new()
        {
            SessionId = sessionId,
            ChannelId = "websocket",
            ProviderId = "ollama",
            ModelId = "llama3.2",
            InputTokens = inputTokens,
            OutputTokens = 128,
            EstimatedInputTokensByComponent = new InputTokenComponentEstimate
            {
                SystemPrompt = inputTokens / 4,
                Skills = inputTokens / 4,
                History = inputTokens / 4,
                ToolOutputs = inputTokens / 8,
                UserInput = inputTokens / 8
            }
        };
}

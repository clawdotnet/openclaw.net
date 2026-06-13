using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class SkillCommandsTests
{
    [Fact]
    public async Task RunAsync_InstallDryRun_DoesNotCopySkill()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Release Notes", "Summarize release notes.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir, "--dry-run"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(workspace, "skills", "release-notes")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_CopiesSkillIntoWorkspaceSkills()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Inbox Triage", "Triage an inbox carefully.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(0, exitCode);
            var installedSkillPath = Path.Combine(workspace, "skills", "inbox-triage", "SKILL.md");
            Assert.True(File.Exists(installedSkillPath));
            var installedContents = await File.ReadAllTextAsync(installedSkillPath);
            Assert.Contains("name: Inbox Triage", installedContents, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_FromTarballWithSpecialPath_Succeeds()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Tarball Skill", "Install from a tarball.");
            var tarballPath = Path.Combine(root, "-tarball skill.tgz");
            await CreateTarballAsync(root, Path.GetFileName(sourceDir), tarballPath);

            var exitCode = await SkillCommands.RunAsync(["install", tarballPath]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(workspace, "skills", "tarball-skill", "SKILL.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_RejectsSymlinkEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Symlink Skill", "Should reject symlink content.");
            var outsideFile = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "secret");
            File.CreateSymbolicLink(Path.Combine(sourceDir, "escape.txt"), outsideFile);

            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsPersistedMetaRunSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-1",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 42
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-1", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Skill: meta-flow", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Status: completed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Steps: 1", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("- Step: draft | kind=llm_chat | status=completed | duration_ms=42", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown skills subcommand", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsStepFailureDetails()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-failed",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-err-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "search step failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "search",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 18.5
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-failed", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Run: run-err-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Error code: tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: search | kind=tool_call | status=failed | duration_ms=18.5 | failure_code=tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsContinuedStepFlag()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-continued",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-cont-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "fallback ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:04Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 7
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-continued", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: primary | kind=tool_call | status=failed | duration_ms=12 | failure_code=tool_failed | continued=true", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: fallback | kind=tool_call | status=completed | duration_ms=7", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_WithNoHistory_PrintsZeroRuns()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-empty", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-empty", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 0", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Verbose_PrintsStepSummaries()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-verbose-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "classify",
                                    Kind = "llm_classify",
                                    Status = "completed",
                                    DurationMs = 9
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-verbose", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: classify | kind=llm_classify | status=completed | duration_ms=9", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_PrintsOnlyRequestedRun()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-filter",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:02Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "selected",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:02Z")
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-filter", "--storage", memoryPath, "--run", "run-target"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-filter", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 2 total, showing 1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-target", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Run: run-older", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_WhenRunMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:01Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-missing-run", "--storage", memoryPath, "--run", "run-absent"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Run 'run-absent' not found in session 'sess-meta-missing-run'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_PrintsFilteredRunPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:01Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 21
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json", "--storage", memoryPath, "--run", "run-json-target", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(2, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, rootElement.GetProperty("shownCount").GetInt32());

            var runs = rootElement.GetProperty("runs");
            Assert.Equal(1, runs.GetArrayLength());
            Assert.Equal("run-json-target", runs[0].GetProperty("runId").GetString());
            Assert.Equal("json ok", runs[0].GetProperty("finalText").GetString());
            Assert.Equal(1, runs[0].GetProperty("stepResults").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_WithNoHistory_PrintsEmptyPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-empty", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json-empty", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(0, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("shownCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("runs").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_JsonVerbose_PrintsSameStructuredPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-verbose",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json verbose ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 5
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-verbose", "--storage", memoryPath, "--json", "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var run = document.RootElement.GetProperty("runs")[0];
            Assert.Equal("run-json-verbose", run.GetProperty("runId").GetString());
            Assert.Equal(1, run.GetProperty("stepResults").GetArrayLength());
            Assert.Equal("fallback", run.GetProperty("stepResults")[0].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-preview",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-preview", "--storage", memoryPath, "--run", "run-preview-001", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-replay-preview", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal("run-preview-001", rootElement.GetProperty("runId").GetString());
            Assert.False(rootElement.GetProperty("replayAvailable").GetBoolean());
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, rootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
            var availableArtifacts = rootElement.GetProperty("availableArtifacts");
            Assert.Equal(2, availableArtifacts.GetArrayLength());
            Assert.Equal(MetaRunReplayArtifactNames.FinalText, availableArtifacts[0].GetString());
            Assert.Equal(MetaRunReplayArtifactNames.StepResults, availableArtifacts[1].GetString());
            var retainedSteps = rootElement.GetProperty("retainedSteps");
            Assert.Single(retainedSteps.EnumerateArray());
            Assert.Equal("draft", retainedSteps[0].GetProperty("id").GetString());
            Assert.Equal("llm_chat", retainedSteps[0].GetProperty("kind").GetString());
            Assert.Equal("completed", retainedSteps[0].GetProperty("status").GetString());
            Assert.Equal(8, retainedSteps[0].GetProperty("durationMs").GetDouble());
            Assert.False(retainedSteps[0].GetProperty("continued").GetBoolean());
            var plan = rootElement.GetProperty("plan");
            Assert.Equal(MetaRunReplayPlanSummaries.AuditableNotReplayable, plan.GetProperty("summary").GetString());
            Assert.Equal(MetaRunReplayModes.PreviewOnly, plan.GetProperty("mode").GetString());
            Assert.False(plan.GetProperty("executable").GetBoolean());
            var replayableSteps = plan.GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("draft", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.TraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.TraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            var blockedBy = plan.GetProperty("blockedByRequirements");
            Assert.Equal(3, blockedBy.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, blockedBy[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, blockedBy[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, blockedBy[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[2].GetProperty("kind").GetString());
            var missingRequirements = rootElement.GetProperty("missingRequirements");
            Assert.Equal(3, missingRequirements.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, missingRequirements[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.PromptContextNotPersisted, missingRequirements[0].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, missingRequirements[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.StepInputsNotPersisted, missingRequirements[1].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, missingRequirements[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[2].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.ToolArgumentsNotPersisted, missingRequirements[2].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("primary", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceContinued, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceContinued, replayableSteps[0].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness-extra", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Equal(2, replayableSteps.GetArrayLength());
            Assert.Equal("failed", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            Assert.Equal("continued", replayableSteps[1].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.ContinuationTraceOnly, replayableSteps[1].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.ContinuationTraceOnly, replayableSteps[1].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:03Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text", "--storage", memoryPath, "--run", "run-preview-text"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Replay preview for run: run-preview-text", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay available: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay plan:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.MetadataOnlyNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Mode: {MetaRunReplayModes.PreviewOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Executable: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Available artifacts:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayArtifactNames.ErrorCode}", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Retained steps:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_WithRetainedSteps_PrintsStepReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-steps",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-steps",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-steps", "--storage", memoryPath, "--run", "run-preview-text-steps"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.AuditableNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Replayable steps: draft | readiness={MetaRunReplayStepReadinessKinds.TraceOnly} | reason={MetaRunReplayStepReadinessReasons.TraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Retained steps:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Replayable steps: primary | readiness={MetaRunReplayStepReadinessKinds.FailureTraceContinued} | reason={MetaRunReplayStepReadinessReasons.FailureTraceContinued}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness-extra"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"failed | readiness={MetaRunReplayStepReadinessKinds.FailureTraceOnly} | reason={MetaRunReplayStepReadinessReasons.FailureTraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"continued | readiness={MetaRunReplayStepReadinessKinds.ContinuationTraceOnly} | reason={MetaRunReplayStepReadinessReasons.ContinuationTraceOnly}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Json_PrintsHistoryOnlyCompletedRun()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json", "--storage", memoryPath, "--run", "run-reconstruct-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-reconstruct-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal("run-reconstruct-001", rootElement.GetProperty("runId").GetString());
            Assert.Equal(MetaRunReplayExecutionModes.AuditReconstruction, rootElement.GetProperty("mode").GetString());
            Assert.Equal(MetaRunReplayExecutionSources.HistoryOnly, rootElement.GetProperty("source").GetString());
            Assert.Equal("completed", rootElement.GetProperty("status").GetString());
            Assert.Equal("done", rootElement.GetProperty("finalText").GetString());
            Assert.Single(rootElement.GetProperty("timeline").EnumerateArray());
            Assert.False(rootElement.GetProperty("proposalSummary").GetProperty("available").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Json_PausedRun_UsesCheckpointAugmentation()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-paused",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["primary"] = "fallback"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-paused", "--storage", memoryPath, "--run", "run-reconstruct-paused-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal(MetaRunReplayExecutionSources.HistoryPlusCheckpoint, rootElement.GetProperty("source").GetString());
            Assert.Equal("ask_user", rootElement.GetProperty("checkpoint").GetProperty("pendingStepId").GetString());
            Assert.True(rootElement.GetProperty("checkpoint").GetProperty("promptPresent").GetBoolean());
            Assert.Equal("finalize", rootElement.GetProperty("checkpoint").GetProperty("blockedStepIds")[0].GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Text_PrintsTimelineAndCheckpointSections()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-text-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"]
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-text", "--storage", memoryPath, "--run", "run-reconstruct-text-001"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Replay reconstruction for run: run-reconstruct-text-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Mode: {MetaRunReplayExecutionModes.AuditReconstruction}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Source: {MetaRunReplayExecutionSources.HistoryPlusCheckpoint}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Timeline:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- 1 | step=draft | kind=llm_chat | status=completed | duration_ms=4", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Checkpoint:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Pending step: ask_user", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Replay available:", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_MissingRun_ReturnsUsage()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("--run <run-id> is required for meta-runs reconstruct.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_SessionMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-missing", "--storage", memoryPath, "--run", "run-001"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Session 'sess-meta-missing' not found.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_RunMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-missing-run", "--storage", memoryPath, "--run", "run-absent"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Run 'run-absent' not found in session 'sess-meta-reconstruct-missing-run'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_PrintsDerivedPausedAndFailedSummaries()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-proposals-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(2, rootElement.GetProperty("count").GetInt32());
            var proposals = rootElement.GetProperty("proposals");
            Assert.Equal("meta-run:run-paused-001:paused", proposals[0].GetProperty("id").GetString());
            Assert.Equal("paused_run_followup", proposals[0].GetProperty("kind").GetString());
            Assert.Equal("derived_meta_run_evidence", proposals[0].GetProperty("source").GetString());
            Assert.Equal("show", proposals[0].GetProperty("availableActions")[0].GetString());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[1].GetProperty("id").GetString());
            Assert.Equal("failed_run_review", proposals[1].GetProperty("kind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_PreservesMetaRunHistoryOrder()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json-order",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-002",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "model_timeout"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json-order", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposals = document.RootElement.GetProperty("proposals");
            Assert.Equal(3, proposals.GetArrayLength());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[0].GetProperty("id").GetString());
            Assert.Equal("meta-run:run-paused-001:paused", proposals[1].GetProperty("id").GetString());
            Assert.Equal("meta-run:run-failed-002:failed", proposals[2].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_WithRunFilter_PrintsRequestedProposalOnly()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json-run-filter",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json-run-filter", "--storage", memoryPath, "--run", "run-failed-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("count").GetInt32());
            var proposals = rootElement.GetProperty("proposals");
            Assert.Single(proposals.EnumerateArray());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[0].GetProperty("id").GetString());
            Assert.Equal("failed_run_review", proposals[0].GetProperty("kind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_PrintsDerivedPausedDetail()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 9,
                                    Continued = true
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");
            Assert.Equal("meta-run:run-paused-001:paused", proposal.GetProperty("id").GetString());
            Assert.Equal("paused_run_followup", proposal.GetProperty("kind").GetString());
            Assert.Equal("ask_user", proposal.GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("timelineStepIds")[0].GetString());
            var checkpoint = proposal.GetProperty("checkpoint");
            Assert.Equal("ask_user", checkpoint.GetProperty("pendingStepId").GetString());
            Assert.True(checkpoint.GetProperty("promptPresent").GetBoolean());
            Assert.Equal("draft", checkpoint.GetProperty("outputStepIds")[0].GetString());
            Assert.Equal("tool_call", checkpoint.GetProperty("failureAliasStepIds")[0].GetString());
            var steps = proposal.GetProperty("steps");
            Assert.Equal(2, steps.GetArrayLength());
            Assert.Equal("draft", steps[0].GetProperty("id").GetString());
            Assert.Equal("llm_chat", steps[0].GetProperty("kind").GetString());
            Assert.Equal("completed", steps[0].GetProperty("status").GetString());
            Assert.Equal(4, steps[0].GetProperty("durationMs").GetDouble());
            Assert.Equal("tool_call", steps[1].GetProperty("id").GetString());
            Assert.Equal("tool_call", steps[1].GetProperty("kind").GetString());
            Assert.Equal("failed", steps[1].GetProperty("status").GetString());
            Assert.Equal("tool_failed", steps[1].GetProperty("failureCode").GetString());
            Assert.Equal(9, steps[1].GetProperty("durationMs").GetDouble());
            Assert.True(steps[1].GetProperty("continued").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Text_PrintsDerivedList()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-text", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Session: sess-meta-proposals-text", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Derived proposals: 2", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Proposal: meta-run:run-paused-001:paused", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Source: derived_meta_run_evidence", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Available actions: show", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Accept:", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Proposal lifecycle:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Text_PrintsStepDetails()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 8,
                                    Continued = false
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-text", "--storage", memoryPath, "--proposal", "meta-run:run-failed-001:failed"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Proposal: meta-run:run-failed-001:failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Timeline steps: draft, tool_call", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence timeline steps: draft, tool_call", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence error code: tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence error: Tool call crashed.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Steps:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- draft | kind=llm_chat | status=completed | durationMs=3", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- tool_call | kind=tool_call | status=failed | failureCode=tool_failed | durationMs=8 | continued=false", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Text_PrintsCheckpointSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-checkpoint-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-checkpoint-text", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Checkpoint:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Pending step: ask_user", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Prompt present: yes", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Output steps: draft", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Failure alias steps: tool_call", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_PrintsEvidenceSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-evidence-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            FinalText = "partial output",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-evidence-json", "--storage", memoryPath, "--proposal", "meta-run:run-failed-001:failed", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");
            Assert.Equal("tool_failed", proposal.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", proposal.GetProperty("error").GetString());
            Assert.Equal("partial output", proposal.GetProperty("finalText").GetString());
            var evidence = proposal.GetProperty("evidence");
            Assert.Equal("draft", evidence.GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_call", evidence.GetProperty("timelineStepIds")[1].GetString());
            Assert.Equal("tool_failed", evidence.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", evidence.GetProperty("error").GetString());
            Assert.Equal("partial output", evidence.GetProperty("finalText").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_KeepsLegacyMirrorsAlongsideGroupedDetail()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-legacy-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            FinalText = "partial output",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"]
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-legacy-json", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");

            Assert.Equal("ask_user", proposal.GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_failed", proposal.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", proposal.GetProperty("error").GetString());
            Assert.Equal("partial output", proposal.GetProperty("finalText").GetString());

            Assert.Equal("ask_user", proposal.GetProperty("checkpoint").GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("evidence").GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_failed", proposal.GetProperty("evidence").GetProperty("errorCode").GetString());
            Assert.Equal("partial output", proposal.GetProperty("evidence").GetProperty("finalText").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_PrintsAppliedReview()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-accept-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("sess-meta-proposals-accept-json", response.GetProperty("sessionId").GetString());
            Assert.Equal("meta-run:run-paused-001:paused", response.GetProperty("proposalId").GetString());
            Assert.Equal("accepted", response.GetProperty("reviewStatus").GetString());
            Assert.False(response.GetProperty("alreadyReviewed").GetBoolean());
            Assert.True(response.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Dismiss_Json_WithReason_PrintsAppliedReview()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-dismiss-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-dismiss-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("dismissed", response.GetProperty("reviewStatus").GetString());
            Assert.Equal("operator reviewed", response.GetProperty("reason").GetString());
            Assert.False(response.GetProperty("alreadyReviewed").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_SecondCall_IsIdempotentSuccess()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-accept-idempotent",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var firstExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-idempotent",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, firstExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var secondExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-idempotent",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, secondExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.GetProperty("alreadyReviewed").GetBoolean());
            Assert.Equal("accepted", document.RootElement.GetProperty("reviewStatus").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_Conflict_WritesNoPartialJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-conflict-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-conflict-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-conflict-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(1, acceptExitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("already reviewed as dismissed", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-list-review-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-list-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var listExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "sess-meta-proposals-list-review-json",
                "--storage", memoryPath,
                "--json"]);

            Assert.Equal(0, listExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposals")[0];
            Assert.Equal("accepted", proposal.GetProperty("reviewStatus").GetString());
            Assert.True(proposal.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-review-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-show-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var review = document.RootElement.GetProperty("proposal").GetProperty("review");
            Assert.Equal("dismissed", review.GetProperty("status").GetString());
            Assert.Equal("operator reviewed", review.GetProperty("reason").GetString());
            Assert.True(review.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_MissingProposal_ReturnsUsage()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("--proposal <id> is required for meta-runs proposals show.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_MissingProposal_WritesNoPartialJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-missing-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-missing-json", "--storage", memoryPath, "--proposal", "meta-run:run-missing-001:paused", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Proposal 'meta-run:run-missing-001:paused' not found in session 'sess-meta-proposals-show-missing-json'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_InvalidProposalKindSuffix_ReturnsNotFound()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-invalid-kind",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-invalid-kind", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:failed"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Proposal 'meta-run:run-paused-001:failed' not found in session 'sess-meta-proposals-show-invalid-kind'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Text_WithCompletedRunsOnly_PrintsZero()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-empty",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-empty", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Session: sess-meta-proposals-empty", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Derived proposals: 0", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Proposal:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkill(string root, string name, string description)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var skillDir = Path.Combine(root, slug);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---{Environment.NewLine}" +
            $"name: {name}{Environment.NewLine}" +
            $"description: {description}{Environment.NewLine}" +
            $"metadata: {{\"openclaw\":{{\"homepage\":\"https://example.com/{slug}\",\"requires\":{{\"env\":[\"OPENAI_API_KEY\"]}}}}}}{Environment.NewLine}" +
            $"---{Environment.NewLine}{Environment.NewLine}" +
            "Follow the documented process." +
            Environment.NewLine);
        return skillDir;
    }

    private static async Task CreateTarballAsync(string workingDirectory, string sourceDirectoryName, string tarballPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--create");
        process.StartInfo.ArgumentList.Add("--gzip");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(tarballPath);
        process.StartInfo.ArgumentList.Add(sourceDirectoryName);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"tar create failed: {stderr}");
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ExternalCliTests
{
    [Fact]
    public void BuildPreview_DisabledGlobalConfig_RejectsExecution()
    {
        var config = CreateConfig(enabled: false);
        var registry = new ExternalCliConnectorRegistry(config);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.BuildPreview(new ExternalCliPreviewRequest
            {
                Connector = "test",
                Command = "echo",
                Parameters = Params(("value", "hello"))
            }, dryRun: false));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPreview_RejectsUnknownAndMissingParameters()
    {
        var config = CreateConfig(enabled: true);
        var registry = new ExternalCliConnectorRegistry(config);

        Assert.Throws<InvalidOperationException>(() =>
            registry.BuildPreview(new ExternalCliPreviewRequest
            {
                Connector = "test",
                Command = "echo"
            }, dryRun: false));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.BuildPreview(new ExternalCliPreviewRequest
            {
                Connector = "test",
                Command = "echo",
                Parameters = Params(("value", "hello"), ("extra", "nope"))
            }, dryRun: false));

        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPreview_ExpandsTemplateAsSeparateArguments_AndClassifiesApproval()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Connectors["test"].Commands["mutate"] = new ExternalCliCommandOptions
        {
            Description = "Mutate",
            ArgsTemplate = ["mutate", "{{value}}"],
            ReadOnly = false,
            RiskLevel = ExternalCliRiskLevel.Medium
        };
        var registry = new ExternalCliConnectorRegistry(config);

        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "test",
            Command = "mutate",
            Parameters = Params(("value", "repo; rm -rf /"))
        }, dryRun: false);

        Assert.Equal(["mutate", "repo; rm -rf /"], prepared.Arguments);
        Assert.True(prepared.Preview.RequiresApproval);
        Assert.False(prepared.Preview.ReadOnly);
        Assert.False(string.IsNullOrWhiteSpace(prepared.Preview.Fingerprint));
    }

    [Fact]
    public void BuildPreview_MatchesParametersCaseInsensitively()
    {
        var config = CreateConfig(enabled: true);
        var registry = new ExternalCliConnectorRegistry(config);

        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "test",
            Command = "echo",
            Parameters = Params(("VALUE", "hello"))
        }, dryRun: false);

        Assert.Equal(["hello"], prepared.Arguments);
    }

    [Fact]
    public async Task Runner_ParsesJsonOutput()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateUnixScript("printf '%s' \"$1\"");
        var config = CreateConfig(enabled: true, executable: script);
        config.ExternalCli.Connectors["test"].Commands["json"] = new ExternalCliCommandOptions
        {
            ArgsTemplate = ["{{payload}}"],
            ReadOnly = true,
            StructuredOutput = ExternalCliOutputFormat.Json
        };
        var registry = new ExternalCliConnectorRegistry(config);
        var runner = new ExternalCliRunner();
        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "test",
            Command = "json",
            Parameters = Params(("payload", """{"ok":true}"""))
        }, dryRun: false);

        var result = await runner.ExecuteAsync(prepared, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ParseError);
        Assert.True(result.ParsedJson.HasValue);
        Assert.True(result.ParsedJson.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Runner_TruncatesStdout()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateUnixScript("printf '%s' \"$1\"");
        var config = CreateConfig(enabled: true, executable: script);
        config.ExternalCli.MaxStdoutBytes = 4;
        var registry = new ExternalCliConnectorRegistry(config);
        var runner = new ExternalCliRunner();
        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "test",
            Command = "echo",
            Parameters = Params(("value", "abcdefgh"))
        }, dryRun: false);

        var result = await runner.ExecuteAsync(prepared, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.StdoutTruncated);
        Assert.Equal("abcd", result.Stdout);
    }

    [Fact]
    public async Task ExternalCliTool_AuditUsesPreviewFingerprint_WhenRequestHasNoFingerprint()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateUnixScript("printf '%s' \"$1\"");
        var config = CreateConfig(enabled: true, executable: script);
        var registry = new ExternalCliConnectorRegistry(config);
        var audit = new CapturingExternalCliAuditSink();
        var tool = new ExternalCliTool(registry, new ExternalCliRunner(), audit);
        var session = new Session
        {
            Id = "sess_external_cli_audit",
            ChannelId = "websocket",
            SenderId = "user1"
        };
        var context = new ToolExecutionContext
        {
            Session = session,
            TurnContext = new TurnContext
            {
                SessionId = session.Id,
                ChannelId = session.ChannelId
            }
        };

        _ = await tool.ExecuteAsync(
            """{"action":"execute","connector":"test","command":"echo","parameters":{"value":"hello"}}""",
            context,
            CancellationToken.None);

        Assert.NotNull(audit.Entry);
        Assert.False(string.IsNullOrWhiteSpace(audit.Entry.ApprovalFingerprint));
    }

    [Fact]
    public void ExternalCliTool_ActionDescriptor_UsesConfiguredRiskPolicy()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Connectors["test"].Commands["high"] = new ExternalCliCommandOptions
        {
            ArgsTemplate = ["high", "{{value}}"],
            ReadOnly = true,
            RiskLevel = ExternalCliRiskLevel.High
        };
        var registry = new ExternalCliConnectorRegistry(config);
        var tool = new ExternalCliTool(registry, new ExternalCliRunner());

        var readOnly = tool.ResolveActionDescriptor("""{"action":"execute","connector":"test","command":"echo","parameters":{"value":"hello"}}""");
        var high = tool.ResolveActionDescriptor("""{"action":"execute","connector":"test","command":"high","parameters":{"value":"hello"}}""");

        Assert.False(readOnly.RequiresApproval);
        Assert.True(readOnly.ReadOnly);
        Assert.True(high.RequiresApproval);
        Assert.Equal(ExternalCliRiskLevel.High, high.RiskLevel);
    }

    [Fact]
    public void ExternalCliTool_DryRunPreview_UsesCommandApprovalPolicy()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Connectors["test"].Commands["high_dry_run"] = new ExternalCliCommandOptions
        {
            ArgsTemplate = ["run", "{{value}}"],
            DryRunArgsTemplate = ["dry-run", "{{value}}"],
            SupportsDryRun = true,
            ReadOnly = true,
            RiskLevel = ExternalCliRiskLevel.High
        };
        var registry = new ExternalCliConnectorRegistry(config);
        var tool = new ExternalCliTool(registry, new ExternalCliRunner());

        var descriptor = tool.ResolveActionDescriptor("""{"action":"preview","connector":"test","command":"high_dry_run","execute_dry_run":true,"parameters":{"value":"hello"}}""");

        Assert.Equal("preview", descriptor.Action);
        Assert.True(descriptor.RequiresApproval);
        Assert.Equal(ExternalCliRiskLevel.High, descriptor.RiskLevel);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.ApprovalFingerprint));
    }

    [Fact]
    public async Task GetStatusAsync_DisabledConfig_DoesNotExecuteStatusCommand()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = Path.Join(Path.GetTempPath(), "openclaw-external-cli-tests", Guid.NewGuid().ToString("N"), "status-ran");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var script = CreateUnixScript($"touch '{marker}'");
        var config = CreateConfig(enabled: false, executable: script);
        config.ExternalCli.Connectors["test"].StatusCommand = new ExternalCliStatusCommandOptions
        {
            Args = ["status"]
        };
        var registry = new ExternalCliConnectorRegistry(config);

        var status = await registry.GetStatusAsync("test", CancellationToken.None);

        Assert.False(status.Authenticated.HasValue);
        Assert.Contains(status.Warnings, warning => warning.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void BuildPreview_ResolvesExecutableFromConfiguredPathForFingerprint()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateUnixScript("printf ok");
        var binDir = Path.GetDirectoryName(script)!;
        var executableName = Path.GetFileName(script);
        var config = CreateConfig(enabled: true, executable: executableName);
        config.ExternalCli.Connectors["test"].Environment["PATH"] = binDir;
        var registry = new ExternalCliConnectorRegistry(config);

        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "test",
            Command = "echo",
            Parameters = Params(("value", "hello"))
        }, dryRun: false);

        Assert.Equal(script, prepared.Executable);
        Assert.Equal(script, prepared.Preview.Executable);
        Assert.StartsWith(script, prepared.Preview.RedactedCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigValidator_RejectsInvalidExternalCliRegexes()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Connectors["test"].RedactionRules = ["["];
        config.ExternalCli.Connectors["test"].Commands["echo"].Parameters["value"].Pattern = "[";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("ExternalCli.Connectors.test.RedactionRules", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ExternalCli.Connectors.test.Commands.echo.Parameters.value.Pattern", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenClawToolExecutor_BlocksApprovalRequiredExternalCliCommand_WhenDenied()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Connectors["test"].Commands["mutate"] = new ExternalCliCommandOptions
        {
            ArgsTemplate = ["mutate", "{{value}}"],
            ReadOnly = false,
            RiskLevel = ExternalCliRiskLevel.Medium
        };
        var registry = new ExternalCliConnectorRegistry(config);
        var tool = new ExternalCliTool(registry, new ExternalCliRunner());
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            logger: NullLogger.Instance,
            config: config);
        var session = new Session
        {
            Id = "sess_external_cli",
            ChannelId = "websocket",
            SenderId = "user1"
        };
        var turn = new TurnContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };

        var result = await executor.ExecuteAsync(
            "external_cli",
            """{"action":"execute","connector":"test","command":"mutate","parameters":{"value":"hello"}}""",
            callId: "call_1",
            session,
            turn,
            isStreaming: false,
            approvalCallback: (_, _, _) => ValueTask.FromResult(false),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.ApprovalRequired, result.FailureCode);
    }

    [Fact]
    public void ExternalCliPresetCatalog_ListsConservativeBuiltInPresets()
    {
        var presets = ExternalCliPresetCatalog.List();

        Assert.Contains(presets, preset => preset.Id == "gh");
        Assert.Contains(presets, preset => preset.Id == "lark");
        Assert.Contains(presets, preset => preset.Id == "github-copilot");
        Assert.Contains(presets, preset => preset.Id == "codex");
        Assert.Contains(presets, preset => preset.Id == "gemini");
    }

    [Fact]
    public void ExternalCliPreset_OptInMaterializesDisabledConnector()
    {
        var config = new GatewayConfig
        {
            ExternalCli = new ExternalCliOptions
            {
                Enabled = true,
                Presets = ["gh"]
            }
        };
        var registry = new ExternalCliConnectorRegistry(config);

        var connector = Assert.Single(registry.ListConnectors(), item => item.Name == "gh");

        Assert.False(connector.Enabled);
        Assert.Equal("gh", connector.Executable);
        Assert.True(connector.CommandCount > 0);
    }

    [Fact]
    public void ExternalCliPreset_ConnectorOverrideEnablesPresetWithoutRedefiningCommands()
    {
        var config = new GatewayConfig
        {
            ExternalCli = new ExternalCliOptions
            {
                Enabled = true,
                Presets = ["gh"],
                Connectors = new Dictionary<string, ExternalCliConnectorOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gh"] = new() { Enabled = true }
                }
            }
        };
        var registry = new ExternalCliConnectorRegistry(config);

        var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
        {
            Connector = "gh",
            Command = "repo_view",
            Parameters = Params(("repo", "clawdotnet/openclaw.net"))
        }, dryRun: false);

        Assert.Equal(["repo", "view", "clawdotnet/openclaw.net", "--json", "name,owner,description,url,isPrivate"], prepared.Arguments);
        Assert.False(prepared.Preview.RequiresApproval);
        Assert.Equal(ExternalCliOutputFormat.Json, prepared.Preview.StructuredOutput);
    }

    [Fact]
    public void ConfigValidator_RejectsUnknownExternalCliPreset()
    {
        var config = CreateConfig(enabled: true);
        config.ExternalCli.Presets = ["missing-preset"];

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("unknown preset 'missing-preset'", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_PresetConnectorOverrideUsesPresetExecutable()
    {
        var config = new GatewayConfig
        {
            ExternalCli = new ExternalCliOptions
            {
                Enabled = true,
                Presets = ["gh"],
                Connectors = new Dictionary<string, ExternalCliConnectorOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gh"] = new() { Enabled = true }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, error => error.Contains("ExternalCli.Connectors.gh.Executable", StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalCliPreset_ConnectorOverrideCanSetOutputFormatToText()
    {
        var config = new GatewayConfig
        {
            ExternalCli = new ExternalCliOptions
            {
                Enabled = true,
                Presets = ["gh"],
                Connectors = new Dictionary<string, ExternalCliConnectorOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gh"] = new()
                    {
                        Enabled = true,
                        DefaultOutputFormat = ExternalCliOutputFormat.Text
                    }
                }
            }
        };
        var registry = new ExternalCliConnectorRegistry(config);

        var commands = registry.ListCommands("gh");
        var issueComment = Assert.Single(commands.Items, item => item.Name == "issue_comment");

        Assert.Equal(ExternalCliOutputFormat.Text, issueComment.StructuredOutput);
    }

    private static GatewayConfig CreateConfig(bool enabled, string executable = "dotnet")
        => new()
        {
            ExternalCli = new ExternalCliOptions
            {
                Enabled = enabled,
                MaxStdoutBytes = 1024,
                MaxStderrBytes = 1024,
                Connectors = new Dictionary<string, ExternalCliConnectorOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = new()
                    {
                        Enabled = true,
                        DisplayName = "Test CLI",
                        Executable = executable,
                        Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["echo"] = new()
                            {
                                Description = "Echo value",
                                ArgsTemplate = ["{{value}}"],
                                ReadOnly = true,
                                RiskLevel = ExternalCliRiskLevel.Low,
                                Parameters = new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["value"] = new() { Required = true, MaxLength = 1024 }
                                }
                            }
                        }
                    }
                }
            }
        };

    private static Dictionary<string, JsonElement> Params(params (string Key, string Value)[] values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            using var document = JsonDocument.Parse(QuoteJsonString(value));
            result[key] = document.RootElement.Clone();
        }

        return result;
    }

    private static string QuoteJsonString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            writer.WriteStringValue(value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateUnixScript(string body)
    {
        var dir = Path.Join(Path.GetTempPath(), "openclaw-external-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Join(dir, "cli.sh");
        File.WriteAllText(path, "#!/usr/bin/env sh\n" + body + "\n");
#pragma warning disable CA1416
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        return path;
    }

    private sealed class CapturingExternalCliAuditSink : IExternalCliAuditSink
    {
        public ExternalCliAuditEntry? Entry { get; private set; }

        public void Record(ExternalCliAuditEntry entry)
            => Entry = entry;
    }
}

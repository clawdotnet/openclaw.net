using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
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
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-external-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cli.sh");
        File.WriteAllText(path, "#!/usr/bin/env sh\n" + body + "\n");
#pragma warning disable CA1416
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        return path;
    }
}

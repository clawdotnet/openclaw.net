using System.Collections.Concurrent;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Backends;

internal sealed class FakeCodingAgentBackend : ICodingAgentBackend
{
    private readonly ConcurrentDictionary<string, IBackendSessionRuntime> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public BackendDefinition Definition { get; } = new()
    {
        BackendId = "fake-backend",
        Provider = "fake",
        DisplayName = "Fake Backend",
        Enabled = true,
        DefaultModel = "fake-model",
        Capabilities = new BackendCapabilities
        {
            SupportsSessions = true,
            SupportsInteractiveInput = true,
            SupportsJsonEvents = true,
            SupportsWorkspace = true,
            SupportsReadOnlyMode = true,
            SupportsWriteMode = true,
            SupportsModelOverride = true
        },
        AccessPolicy = new BackendAccessPolicy
        {
            ReadOnlyByDefault = false,
            WriteEnabled = true,
            RequireWorkspace = false
        }
    };

    public Task<BackendProbeResult> ProbeAsync(BackendProbeRequest request, CancellationToken ct)
        => Task.FromResult(new BackendProbeResult
        {
            BackendId = Definition.BackendId,
            Success = true,
            Message = "fake backend ready"
        });

    public async Task<BackendSessionHandle> StartSessionAsync(StartBackendSessionRequest request, IBackendSessionRuntime runtime, CancellationToken ct)
    {
        _sessions[runtime.Session.SessionId] = runtime;
        await runtime.AppendEventAsync(new BackendAssistantMessageEvent
        {
            SessionId = runtime.Session.SessionId,
            Text = string.IsNullOrWhiteSpace(request.Prompt)
                ? "fake session started"
                : $"fake received: {request.Prompt}"
        }, ct);

        return new BackendSessionHandle
        {
            BackendId = Definition.BackendId,
            SessionId = runtime.Session.SessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task SendInputAsync(string sessionId, BackendInput input, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var runtime))
            throw new InvalidOperationException($"Backend session '{sessionId}' is not active.");

        if (!string.IsNullOrWhiteSpace(input.Text))
        {
            await runtime.AppendEventAsync(new BackendAssistantMessageEvent
            {
                SessionId = sessionId,
                Text = $"fake echo: {input.Text}"
            }, ct);
        }

        if (input.CloseInput)
            await StopSessionAsync(sessionId, ct);
    }

    public async Task StopSessionAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryRemove(sessionId, out var runtime))
            return;

        await runtime.UpdateSessionAsync(runtime.Session with
        {
            State = BackendSessionState.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow
        }, ct);
        await runtime.AppendEventAsync(new BackendSessionCompletedEvent
        {
            SessionId = sessionId,
            ExitCode = 0,
            Reason = "stopped"
        }, ct);
    }
}

internal abstract class CliCodingAgentBackendBase : ICodingAgentBackend
{
    private readonly IBackendCredentialResolver _credentials;
    private readonly CodingBackendProcessHost _host;
    private bool? _structuredOutputSupported;
    protected readonly CodingCliBackendConfig Config;

    protected CliCodingAgentBackendBase(
        CodingCliBackendConfig config,
        IBackendCredentialResolver credentials,
        CodingBackendProcessHost host)
    {
        Config = config;
        _credentials = credentials;
        _host = host;
        Definition = new BackendDefinition
        {
            BackendId = config.BackendId,
            Provider = config.Provider,
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultDisplayName : config.DisplayName.Trim(),
            Enabled = config.Enabled,
            ExecutablePath = ResolveExecutablePath(config.ExecutablePath),
            DefaultModel = config.DefaultModel,
            Capabilities = Capabilities,
            AccessPolicy = new BackendAccessPolicy
            {
                ReadOnlyByDefault = config.ReadOnlyByDefault,
                WriteEnabled = config.WriteEnabled,
                RequireWorkspace = config.RequireWorkspace
            }
        };
    }

    public BackendDefinition Definition { get; }

    protected abstract string DefaultDisplayName { get; }
    protected abstract string DefaultExecutableName { get; }
    protected virtual BackendCapabilities Capabilities { get; } = new()
    {
        SupportsSessions = true,
        SupportsInteractiveInput = true,
        SupportsJsonEvents = false,
        SupportsStructuredStreaming = false,
        SupportsWorkspace = true,
        SupportsReadOnlyMode = true,
        SupportsWriteMode = true,
        SupportsModelOverride = true
    };
    protected virtual IReadOnlyList<string> StructuredOutputArguments => [];
    protected virtual string? StructuredOutputProbeToken => null;

    public async Task<BackendProbeResult> ProbeAsync(BackendProbeRequest request, CancellationToken ct)
    {
        if (!Definition.Enabled)
        {
            return new BackendProbeResult
            {
                BackendId = Definition.BackendId,
                Success = false,
                Message = "backend disabled"
            };
        }

        _ = await ResolveCredentialAsync(request.CredentialSource, ct);
        var spec = new CodingBackendProcessSpec
        {
            SessionId = $"probe_{Guid.NewGuid():N}"[..18],
            BackendId = Definition.BackendId,
            Command = Definition.ExecutablePath ?? DefaultExecutableName,
            Arguments = BuildProbeArguments().ToArray(),
            WorkingDirectory = ResolveWorkingDirectory(request.WorkspacePath),
            Environment = await BuildEnvironmentAsync(request.CredentialSource, request.Environment, ct),
            TimeoutSeconds = Math.Max(5, Math.Min(60, Config.TimeoutSeconds))
        };

        var result = await _host.ExecuteAsync(spec, ct);
        var structuredSupported = DetectStructuredOutputSupport(result);
        return new BackendProbeResult
        {
            BackendId = Definition.BackendId,
            Success = !result.TimedOut && result.ExitCode == 0,
            Message = result.TimedOut ? "probe timed out" : $"exit code {result.ExitCode}",
            ExecutablePath = spec.Command,
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            DurationMs = result.DurationMs,
            StructuredOutputSupported = structuredSupported
        };
    }

    public async Task<BackendSessionHandle> StartSessionAsync(StartBackendSessionRequest request, IBackendSessionRuntime runtime, CancellationToken ct)
    {
        if (!Definition.Enabled)
            throw new InvalidOperationException($"Backend '{Definition.BackendId}' is disabled.");

        var structuredOutputEnabled = await DetermineStructuredOutputEnabledAsync(ct);
        await runtime.UpdateSessionAsync(runtime.Session with
        {
            StructuredOutputEnabled = structuredOutputEnabled
        }, ct);

        var spec = new CodingBackendProcessSpec
        {
            SessionId = runtime.Session.SessionId,
            BackendId = Definition.BackendId,
            Command = Definition.ExecutablePath ?? DefaultExecutableName,
            Arguments = BuildSessionArguments(runtime.Session, structuredOutputEnabled).ToArray(),
            WorkingDirectory = ResolveWorkingDirectory(request.WorkspacePath),
            Environment = await BuildEnvironmentAsync(request.CredentialSource, request.Environment, ct),
            TimeoutSeconds = Config.TimeoutSeconds
        };

        await _host.StartAsync(
            spec,
            line => ParseStdout(runtime.Session.SessionId, line, structuredOutputEnabled),
            line => ParseStderr(runtime.Session.SessionId, line, structuredOutputEnabled),
            runtime,
            ct);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            await _host.WriteInputAsync(runtime.Session.SessionId, new BackendInput
            {
                Text = request.Prompt
            }, ct);
        }

        return new BackendSessionHandle
        {
            BackendId = Definition.BackendId,
            SessionId = runtime.Session.SessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public Task SendInputAsync(string sessionId, BackendInput input, CancellationToken ct)
        => _host.WriteInputAsync(sessionId, input, ct);

    public Task StopSessionAsync(string sessionId, CancellationToken ct)
        => _host.StopAsync(sessionId, ct);

    protected virtual IEnumerable<string> BuildProbeArguments()
        => Config.ProbeArgs is not { Length: > 0 } ? ["--help"] : Config.ProbeArgs;

    protected virtual IEnumerable<string> BuildSessionArguments(BackendSessionRecord session, bool structuredOutputEnabled)
    {
        var args = new List<string>(Config.Args ?? []);
        if (structuredOutputEnabled)
            ApplyStructuredOutputArguments(args);
        ApplyModelArgument(args, session.Model);
        ApplyWorkspaceArgument(args, session.WorkspacePath);
        ApplyAccessModeArguments(args, session.ReadOnly);
        return args;
    }

    protected virtual void ApplyModelArgument(List<string> args, string? model)
    {
    }

    protected virtual void ApplyWorkspaceArgument(List<string> args, string? workspacePath)
    {
    }

    protected virtual void ApplyAccessModeArguments(List<string> args, bool readOnly)
    {
    }

    protected virtual void ApplyStructuredOutputArguments(List<string> args)
    {
        foreach (var arg in StructuredOutputArguments)
        {
            if (!args.Contains(arg, StringComparer.OrdinalIgnoreCase))
                args.Add(arg);
        }
    }

    protected virtual IEnumerable<BackendEvent> ParseStdout(string sessionId, string line, bool structuredOutputEnabled)
        => StampRawLine(ParseLine(sessionId, line, stderr: false, structuredOutputEnabled), line);

    protected virtual IEnumerable<BackendEvent> ParseStderr(string sessionId, string line, bool structuredOutputEnabled)
        => StampRawLine(ParseLine(sessionId, line, stderr: true, structuredOutputEnabled), line);

    private IEnumerable<BackendEvent> ParseLine(string sessionId, string line, bool stderr, bool structuredOutputEnabled)
    {
        if (structuredOutputEnabled && TryParseStructuredEvent(sessionId, line, out var evt))
            return [evt];

        if (!stderr && line.StartsWith("$ ", StringComparison.Ordinal))
        {
            return [new BackendShellCommandProposedEvent
            {
                SessionId = sessionId,
                Command = line[2..].Trim()
            }];
        }

        if (!stderr && line.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
        {
            return [new BackendToolCallRequestedEvent
            {
                SessionId = sessionId,
                ToolName = line[5..].Trim()
            }];
        }

        if (!stderr && line.StartsWith("executed:", StringComparison.OrdinalIgnoreCase))
        {
            return [new BackendShellCommandExecutedEvent
            {
                SessionId = sessionId,
                Command = line["executed:".Length..].Trim()
            }];
        }

        if (!stderr && (line.StartsWith("patch proposed:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("*** Begin Patch", StringComparison.Ordinal)))
        {
            return [new BackendPatchProposedEvent
            {
                SessionId = sessionId,
                Path = TryExtractPath(line),
                Patch = line
            }];
        }

        if (!stderr && (line.StartsWith("patch applied:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("applied patch", StringComparison.OrdinalIgnoreCase)))
        {
            return [new BackendPatchAppliedEvent
            {
                SessionId = sessionId,
                Path = TryExtractPath(line),
                Summary = line
            }];
        }

        if (!stderr && (line.StartsWith("read ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("read:", StringComparison.OrdinalIgnoreCase)))
        {
            return [new BackendFileReadEvent
            {
                SessionId = sessionId,
                Path = line[5..].Trim()
            }];
        }

        if (!stderr && (line.StartsWith("write ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("write:", StringComparison.OrdinalIgnoreCase)))
        {
            return [new BackendFileWriteEvent
            {
                SessionId = sessionId,
                Path = line[6..].Trim()
            }];
        }

        return stderr
            ? [new BackendStderrOutputEvent { SessionId = sessionId, Text = line }]
            : [new BackendStdoutOutputEvent { SessionId = sessionId, Text = line }];
    }

    private static bool TryParseStructuredEvent(string sessionId, string line, out BackendEvent evt)
    {
        evt = null!;
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = GetOptionalString(root, "eventType") ?? GetOptionalString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                var text = GetOptionalString(root, "text") ?? GetOptionalString(root, "message");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    evt = new BackendAssistantMessageEvent
                    {
                        SessionId = sessionId,
                        Text = text
                    };
                    return true;
                }

                return false;
            }

            evt = type switch
            {
                "assistant_message" => new BackendAssistantMessageEvent
                {
                    SessionId = sessionId,
                    Text = GetOptionalString(root, "text") ?? GetOptionalString(root, "message") ?? string.Empty
                },
                "stdout_output" => new BackendStdoutOutputEvent
                {
                    SessionId = sessionId,
                    Text = GetOptionalString(root, "text") ?? string.Empty
                },
                "stderr_output" => new BackendStderrOutputEvent
                {
                    SessionId = sessionId,
                    Text = GetOptionalString(root, "text") ?? string.Empty
                },
                "tool_call_requested" => new BackendToolCallRequestedEvent
                {
                    SessionId = sessionId,
                    ToolName = GetOptionalString(root, "toolName") ?? "tool",
                    ArgumentsJson = root.TryGetProperty("arguments", out var args) ? args.GetRawText() : null
                },
                "shell_command_proposed" => new BackendShellCommandProposedEvent
                {
                    SessionId = sessionId,
                    Command = GetOptionalString(root, "command") ?? string.Empty
                },
                "shell_command_executed" => new BackendShellCommandExecutedEvent
                {
                    SessionId = sessionId,
                    Command = GetOptionalString(root, "command") ?? string.Empty,
                    ExitCode = root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number
                        ? exitCode.GetInt32()
                        : null,
                    Stdout = GetOptionalString(root, "stdout"),
                    Stderr = GetOptionalString(root, "stderr")
                },
                "patch_proposed" => new BackendPatchProposedEvent
                {
                    SessionId = sessionId,
                    Path = GetOptionalString(root, "path"),
                    Patch = GetOptionalString(root, "patch") ?? string.Empty
                },
                "patch_applied" => new BackendPatchAppliedEvent
                {
                    SessionId = sessionId,
                    Path = GetOptionalString(root, "path"),
                    Summary = GetOptionalString(root, "summary")
                },
                "file_read" => new BackendFileReadEvent
                {
                    SessionId = sessionId,
                    Path = GetOptionalString(root, "path") ?? string.Empty
                },
                "file_write" => new BackendFileWriteEvent
                {
                    SessionId = sessionId,
                    Path = GetOptionalString(root, "path") ?? string.Empty
                },
                "error" => new BackendErrorEvent
                {
                    SessionId = sessionId,
                    Message = GetOptionalString(root, "message") ?? string.Empty
                },
                "session_completed" => new BackendSessionCompletedEvent
                {
                    SessionId = sessionId,
                    ExitCode = root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number
                        ? exit.GetInt32()
                        : null,
                    Reason = GetOptionalString(root, "reason")
                },
                _ => new BackendAssistantMessageEvent
                {
                    SessionId = sessionId,
                    Text = GetOptionalString(root, "text") ?? GetOptionalString(root, "message") ?? line
                }
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ResolvedBackendCredential?> ResolveCredentialAsync(ConnectedAccountSecretRef? source, CancellationToken ct)
        => await _credentials.ResolveAsync(Definition.Provider, source ?? new ConnectedAccountSecretRef
        {
            SecretRef = Config.Credentials?.SecretRef,
            TokenFilePath = Config.Credentials?.TokenFilePath,
            ConnectedAccountId = Config.Credentials?.ConnectedAccountId
        }, ct);

    private async Task<Dictionary<string, string>> BuildEnvironmentAsync(
        ConnectedAccountSecretRef? credentialSource,
        IReadOnlyDictionary<string, string>? requestEnvironment,
        CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Config.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
            environment[pair.Key] = SecretResolver.Resolve(pair.Value) ?? pair.Value;

        foreach (var pair in requestEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal))
            environment[pair.Key] = pair.Value;

        var credential = await ResolveCredentialAsync(credentialSource, ct);
        if (!string.IsNullOrWhiteSpace(credential?.Secret))
        {
            foreach (var key in GetCredentialEnvironmentKeys())
                environment[key] = credential.Secret;

            environment["OPENCLAW_BACKEND_CREDENTIAL"] = credential.Secret;
        }

        return environment;
    }

    protected virtual IReadOnlyList<string> GetCredentialEnvironmentKeys()
        => ["OPENCLAW_BACKEND_CREDENTIAL"];

    private string? ResolveWorkingDirectory(string? requestWorkspacePath)
    {
        var candidate = requestWorkspacePath;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Config.DefaultWorkspacePath;
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        return Path.GetFullPath(candidate);
    }

    private string ResolveExecutablePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return DefaultExecutableName;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryExtractPath(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < line.Length - 1)
            return line[(colonIndex + 1)..].Trim();

        return null;
    }

    private IEnumerable<BackendEvent> StampRawLine(IEnumerable<BackendEvent> events, string line)
        => events.Select(evt => evt with { RawLine = line });

    private async Task<bool> DetermineStructuredOutputEnabledAsync(CancellationToken ct)
    {
        if (!Capabilities.SupportsStructuredStreaming || !Config.PreferStructuredOutput)
            return false;
        if (StructuredOutputArguments.Count == 0)
            return false;
        if (!CanUseStructuredOutputWithConfiguredArgs(Config.Args ?? []))
            return false;

        if (_structuredOutputSupported.HasValue)
            return _structuredOutputSupported.Value;

        var probeSpec = new CodingBackendProcessSpec
        {
            SessionId = $"probe_{Guid.NewGuid():N}"[..18],
            BackendId = Definition.BackendId,
            Command = Definition.ExecutablePath ?? DefaultExecutableName,
            Arguments = BuildProbeArguments().ToArray(),
            TimeoutSeconds = Math.Max(5, Math.Min(30, Config.TimeoutSeconds))
        };
        var result = await _host.ExecuteAsync(probeSpec, ct);
        return DetectStructuredOutputSupport(result);
    }

    private bool DetectStructuredOutputSupport(CodingBackendProcessResult result)
    {
        if (_structuredOutputSupported.HasValue)
            return _structuredOutputSupported.Value;

        var token = StructuredOutputProbeToken;
        var supported = !string.IsNullOrWhiteSpace(token)
            && (result.Stdout?.Contains(token, StringComparison.OrdinalIgnoreCase) == true
                || result.Stderr?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);
        _structuredOutputSupported = supported;
        return supported;
    }

    protected virtual bool CanUseStructuredOutputWithConfiguredArgs(IReadOnlyList<string> args)
        => true;
}

internal sealed class CodexCliBackend : CliCodingAgentBackendBase
{
    public CodexCliBackend(
        GatewayConfig config,
        IBackendCredentialResolver credentials,
        CodingBackendProcessHost host)
        : base(config.CodingBackends.Codex, credentials, host)
    {
    }

    protected override string DefaultDisplayName => "Codex CLI";
    protected override string DefaultExecutableName => "codex";
    protected override BackendCapabilities Capabilities { get; } = new()
    {
        SupportsSessions = true,
        SupportsInteractiveInput = true,
        SupportsJsonEvents = true,
        SupportsStructuredStreaming = true,
        SupportsWorkspace = true,
        SupportsReadOnlyMode = true,
        SupportsWriteMode = true,
        SupportsModelOverride = true
    };
    protected override IReadOnlyList<string> StructuredOutputArguments => ["--json"];
    protected override string? StructuredOutputProbeToken => "--json";

    protected override void ApplyModelArgument(List<string> args, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(model);
        }
    }

    protected override void ApplyWorkspaceArgument(List<string> args, string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            args.Add("-C");
            args.Add(workspacePath);
        }
    }

    protected override void ApplyAccessModeArguments(List<string> args, bool readOnly)
    {
        args.Add("-s");
        args.Add(readOnly ? "read-only" : "workspace-write");
    }

    protected override IReadOnlyList<string> GetCredentialEnvironmentKeys()
        => ["OPENAI_API_KEY", "CODEX_API_KEY"];

    protected override bool CanUseStructuredOutputWithConfiguredArgs(IReadOnlyList<string> args)
        => args.Any(arg => string.Equals(arg, "exec", StringComparison.OrdinalIgnoreCase));
}

internal sealed class GeminiCliBackend : CliCodingAgentBackendBase
{
    public GeminiCliBackend(
        GatewayConfig config,
        IBackendCredentialResolver credentials,
        CodingBackendProcessHost host)
        : base(config.CodingBackends.GeminiCli, credentials, host)
    {
    }

    protected override string DefaultDisplayName => "Gemini CLI";
    protected override string DefaultExecutableName => "gemini";

    protected override void ApplyModelArgument(List<string> args, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
    }

    protected override void ApplyAccessModeArguments(List<string> args, bool readOnly)
    {
        if (readOnly)
            args.Add("--sandbox");
        else if (Config.WriteEnabled)
            args.Add("--yolo");
    }

    protected override IReadOnlyList<string> GetCredentialEnvironmentKeys()
        => ["GEMINI_API_KEY", "GOOGLE_API_KEY"];
}

internal sealed class GitHubCopilotCliBackend : CliCodingAgentBackendBase
{
    public GitHubCopilotCliBackend(
        GatewayConfig config,
        IBackendCredentialResolver credentials,
        CodingBackendProcessHost host)
        : base(config.CodingBackends.GitHubCopilotCli, credentials, host)
    {
    }

    protected override string DefaultDisplayName => "GitHub Copilot CLI";
    protected override string DefaultExecutableName => "copilot";

    protected override void ApplyModelArgument(List<string> args, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
    }

    protected override void ApplyAccessModeArguments(List<string> args, bool readOnly)
    {
        if (!readOnly && Config.WriteEnabled)
            args.Add("--yolo");
    }

    protected override IReadOnlyList<string> GetCredentialEnvironmentKeys()
        => ["GITHUB_TOKEN", "COPILOT_TOKEN"];
}

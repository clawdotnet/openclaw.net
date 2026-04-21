using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Agent;

public sealed class ToolExecutionResult
{
    public required ToolInvocation Invocation { get; init; }
    public required string ResultText { get; init; }

    public FunctionResultContent ToFunctionResultContent(string callId)
        => new(callId, ResultText);
}

public sealed class OpenClawToolExecutor
{
    private readonly Dictionary<string, ITool> _toolsByName;
    private readonly AITool[] _toolDeclarations;
    private readonly int _toolTimeoutSeconds;
    private readonly bool _requireToolApproval;
    private readonly HashSet<string> _approvalRequiredTools;
    private readonly IReadOnlyList<IToolHook> _hooks;
    private readonly RuntimeMetrics? _metrics;
    private readonly ILogger? _logger;
    private readonly GatewayConfig _config;
    private readonly IToolSandbox? _toolSandbox;
    private readonly ToolUsageTracker? _toolUsageTracker;
    private readonly ToolExecutionRouter _executionRouter;
    private readonly IToolPresetResolver? _toolPresetResolver;
    private readonly ToolAuditLog? _auditLog;

    public OpenClawToolExecutor(
        IReadOnlyList<ITool> tools,
        int toolTimeoutSeconds,
        bool requireToolApproval,
        IReadOnlyList<string> approvalRequiredTools,
        IReadOnlyList<IToolHook> hooks,
        RuntimeMetrics? metrics = null,
        ILogger? logger = null,
        GatewayConfig? config = null,
        IToolSandbox? toolSandbox = null,
        ToolUsageTracker? toolUsageTracker = null,
        ToolExecutionRouter? executionRouter = null,
        IToolPresetResolver? toolPresetResolver = null,
        ToolAuditLog? auditLog = null)
    {
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _toolDeclarations = tools.Select(CreateDeclaration).Cast<AITool>().ToArray();
        _toolTimeoutSeconds = toolTimeoutSeconds;
        _requireToolApproval = requireToolApproval;
        _approvalRequiredTools = approvalRequiredTools
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => NormalizeApprovalToolName(item.Trim()))
            .ToHashSet(StringComparer.Ordinal);
        _hooks = hooks;
        _metrics = metrics;
        _logger = logger;
        _config = config ?? new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                ToolTimeoutSeconds = toolTimeoutSeconds,
                RequireToolApproval = requireToolApproval,
                ApprovalRequiredTools = [.. approvalRequiredTools]
            }
        };
        _toolSandbox = toolSandbox;
        _toolUsageTracker = toolUsageTracker;
        _executionRouter = executionRouter ?? new ToolExecutionRouter(_config, _toolSandbox, logger);
        _toolPresetResolver = toolPresetResolver;
        _auditLog = auditLog;
    }

    public IList<AITool> ToolDeclarations => _toolDeclarations;

    public IList<AITool> GetToolDeclarations(Session session)
    {
        var preset = _toolPresetResolver?.Resolve(session, _toolsByName.Keys);
        return _toolDeclarations
            .Where(item => IsToolAllowedForSession(session, item.Name, preset))
            .ToArray();
    }

    public bool SupportsStreaming(string toolName)
        => _toolsByName.TryGetValue(toolName, out var tool) && tool is IStreamingTool;

    public async Task<ToolExecutionResult> ExecuteAsync(
        FunctionCallContent call,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        Func<string, ValueTask>? onDelta = null)
    {
        var argsJson = call.Arguments is not null
            ? JsonSerializer.Serialize(call.Arguments, CoreJsonContext.Default.IDictionaryStringObject)
            : "{}";

        return await ExecuteAsync(call.Name, argsJson, call.CallId, session, turnCtx, isStreaming, approvalCallback, ct, onDelta);
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        string argsJson,
        string? callId,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        Func<string, ValueTask>? onDelta = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.ExecuteTool");
        activity?.SetTag("tool.name", toolName);

        if (!_toolsByName.TryGetValue(toolName, out var tool))
        {
            var unknown = new ToolInvocation
            {
                ToolName = toolName,
                Arguments = argsJson,
                Result = "Error: Unknown tool",
                Duration = TimeSpan.Zero
            };

            return new ToolExecutionResult
            {
                Invocation = unknown,
                ResultText = unknown.Result!
            };
        }

        var preset = _toolPresetResolver?.Resolve(session, _toolsByName.Keys);
        if (!IsToolAllowedForSession(session, tool.Name, preset))
        {
            var deniedByPreset = preset is not null
                ? $"Tool '{tool.Name}' is not allowed for preset '{preset.PresetId}'."
                : $"Tool '{tool.Name}' is not allowed for this session.";
            _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, deniedByPreset);
            return CreateImmediateResult(toolName, argsJson, deniedByPreset);
        }

        var hookCtx = new ToolHookContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnCtx.CorrelationId,
            ToolName = tool.Name,
            ArgumentsJson = argsJson,
            IsStreaming = isStreaming
        };

        foreach (var hook in _hooks)
        {
            try
            {
                var allowed = hook is IToolHookWithContext ctxHook
                    ? await ctxHook.BeforeExecuteAsync(hookCtx, ct)
                    : await hook.BeforeExecuteAsync(tool.Name, argsJson, ct);
                if (!allowed)
                {
                    var deniedByHook = $"Tool execution denied by hook: {hook.Name}";
                    _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, deniedByHook);
                    return CreateImmediateResult(toolName, argsJson, deniedByHook);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} BeforeExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var approvalDescriptor = ToolActionPolicyResolver.Resolve(tool.Name, argsJson);
        var normalizedToolName = NormalizeApprovalToolName(tool.Name);
        var explicitlyConfiguredApproval = _config.Tooling.ApprovalRequiredTools
            .Any(item => string.Equals(NormalizeApprovalToolName(item), normalizedToolName, StringComparison.Ordinal));
        var presetRequiresApproval = preset?.ApprovalRequiredTools.Contains(tool.Name) == true;
        var defaultActionAwareApproval = ToolActionPolicyResolver.SupportsActionAwareApproval(tool.Name) && approvalDescriptor.IsMutation;
        var listedApproval = _requireToolApproval && (_approvalRequiredTools.Contains(normalizedToolName) || presetRequiresApproval);
        var requiresApproval = ToolActionPolicyResolver.SupportsActionAwareApproval(tool.Name) && !explicitlyConfiguredApproval && !presetRequiresApproval
            ? defaultActionAwareApproval
            : listedApproval || defaultActionAwareApproval;

        if (requiresApproval)
        {
            if (approvalCallback is not null)
            {
                var approved = await approvalCallback(tool.Name, argsJson, ct);
                if (!approved)
                {
                    _logger?.LogInformation("[{CorrelationId}] Tool {Tool} denied by user", turnCtx.CorrelationId, tool.Name);
                    return CreateImmediateResult(toolName, argsJson, "Tool execution denied by user.");
                }
            }
            else
            {
                _logger?.LogWarning(
                    "[{CorrelationId}] Tool {Tool} requires approval but no approval channel is available — denied",
                    turnCtx.CorrelationId,
                    tool.Name);
                return CreateImmediateResult(
                    toolName,
                    argsJson,
                    $"Tool '{tool.Name}' requires approval but this session has no approval channel — auto-denied. " +
                    "To enable this tool: connect through the browser chat at /chat (it supports interactive approvals) " +
                    "or set OpenClaw:Tooling:RequireToolApproval=false for trusted local sessions.");
            }
        }

        var sw = Stopwatch.StartNew();
        string result;
        var toolFailed = false;
        var toolTimedOut = false;
        try
        {
            if (onDelta is not null && tool is IStreamingTool streamingTool)
                result = await ExecuteStreamingToolCollectAsync(streamingTool, argsJson, onDelta, ct);
            else
                result = await ExecuteToolWithRoutingAsync(tool, argsJson, session, turnCtx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = "Error: Tool execution timed out.";
            toolFailed = true;
            toolTimedOut = true;
            _metrics?.IncrementToolTimeouts();
            _logger?.LogWarning("[{CorrelationId}] Tool {Tool} timed out after {Timeout}s", turnCtx.CorrelationId, tool.Name, _toolTimeoutSeconds);
        }
        catch (ToolSandboxException ex)
        {
            result = ex.Message;
            toolFailed = true;
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} sandbox execution failed", turnCtx.CorrelationId, tool.Name);
        }
        catch (Exception ex)
        {
            result = "Error: Tool execution failed.";
            toolFailed = true;
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} failed", turnCtx.CorrelationId, tool.Name);
        }
        sw.Stop();

        _metrics?.IncrementToolCalls();
        Telemetry.ToolExecutionDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("tool.name", tool.Name),
            new KeyValuePair<string, object?>("tool.success", !toolFailed));
        turnCtx.RecordToolCall(sw.Elapsed, toolFailed, toolTimedOut);
        _toolUsageTracker?.RecordToolCall(tool.Name, sw.Elapsed, toolFailed, toolTimedOut);
        _auditLog?.Record(new ToolAuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ToolName = tool.Name,
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnCtx.CorrelationId,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Failed = toolFailed,
            TimedOut = toolTimedOut,
            ArgumentsBytes = Encoding.UTF8.GetByteCount(argsJson),
            ResultBytes = Encoding.UTF8.GetByteCount(result)
        });
        _logger?.LogDebug("[{CorrelationId}] Tool {Tool} completed in {Duration}ms ok={Ok}",
            turnCtx.CorrelationId,
            tool.Name,
            sw.Elapsed.TotalMilliseconds,
            !toolFailed);

        foreach (var hook in _hooks)
        {
            try
            {
                if (hook is IToolHookWithContext ctxHook)
                    await ctxHook.AfterExecuteAsync(hookCtx, result, sw.Elapsed, toolFailed, ct);
                else
                    await hook.AfterExecuteAsync(tool.Name, argsJson, result, sw.Elapsed, toolFailed, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} AfterExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var invocation = new ToolInvocation
        {
            ToolName = toolName,
            Arguments = argsJson,
            Result = result,
            Duration = sw.Elapsed
        };

        return new ToolExecutionResult
        {
            Invocation = invocation,
            ResultText = result
        };
    }

    private static ToolExecutionResult CreateImmediateResult(string toolName, string argsJson, string result)
    {
        var invocation = new ToolInvocation
        {
            ToolName = toolName,
            Arguments = argsJson,
            Result = result,
            Duration = TimeSpan.Zero
        };

        return new ToolExecutionResult
        {
            Invocation = invocation,
            ResultText = result
        };
    }

    private static bool IsToolAllowedForSession(Session session, string toolName, ResolvedToolPreset? preset)
    {
        if (preset is not null && !preset.AllowedTools.Contains(toolName))
            return false;

        if (session.RouteAllowedTools is { Length: > 0 })
            return session.RouteAllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

        return true;
    }

    private async Task<string> ExecuteStreamingToolCollectAsync(
        IStreamingTool tool,
        string argsJson,
        Func<string, ValueTask> onDelta,
        CancellationToken ct)
    {
        using var timeoutCts = _toolTimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));
        var effectiveCt = timeoutCts?.Token ?? ct;

        const int MaxChars = 1_000_000;
        var sb = new StringBuilder();

        await foreach (var chunk in tool.ExecuteStreamingAsync(argsJson, effectiveCt).WithCancellation(effectiveCt))
        {
            if (chunk is null)
                continue;

            await onDelta(chunk);

            if (sb.Length < MaxChars)
            {
                var remaining = MaxChars - sb.Length;
                sb.Append(chunk.Length <= remaining ? chunk : chunk[..remaining]);
            }
        }

        if (sb.Length >= MaxChars)
            sb.Append("…");

        return sb.ToString();
    }

    private async Task<SandboxResult> ExecuteSandboxWithTimeoutAsync(
        SandboxExecutionRequest request,
        CancellationToken ct)
    {
        if (_toolSandbox is null)
            throw new ToolSandboxException("Error: Tool requires sandboxing but no sandbox provider is configured.");

        if (_toolTimeoutSeconds <= 0)
            return await _toolSandbox.ExecuteAsync(request, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));
        return await _toolSandbox.ExecuteAsync(request, timeoutCts.Token);
    }

    private async Task<string> ExecuteToolWithRoutingAsync(
        ITool tool,
        string argsJson,
        Session session,
        TurnContext turnCtx,
        CancellationToken ct)
    {
        if (!_executionRouter.TryResolveRoute(tool, out var route, out var template, out var legacySandboxRoute, out var sandboxMode))
        {
            if (IsLocalExecutionDisabled(tool))
                throw new ToolSandboxException(BrowserTool.LocalExecutionUnavailableMessage);

            return await ExecuteToolWithTimeoutAsync(tool, argsJson, session, turnCtx, ct);
        }

        if (tool is not ISandboxCapableTool sandboxCapableTool)
            return await ExecuteToolWithTimeoutAsync(tool, argsJson, session, turnCtx, ct);

        var backendName = string.IsNullOrWhiteSpace(route?.Backend)
            ? _config.Execution.DefaultBackend
            : route.Backend;
        if (sandboxMode == ToolSandboxMode.Require && !legacySandboxRoute && route is null)
        {
            throw new ToolSandboxException(
                $"Error: Tool '{tool.Name}' requires sandboxing but no sandbox provider is configured.");
        }

        if (string.Equals(backendName, "local", StringComparison.OrdinalIgnoreCase) && IsLocalExecutionDisabled(tool))
            throw new ToolSandboxException(BrowserTool.LocalExecutionUnavailableMessage);

        if (string.Equals(backendName, "local", StringComparison.OrdinalIgnoreCase) && !legacySandboxRoute)
            return await ExecuteToolWithTimeoutAsync(tool, argsJson, session, turnCtx, ct);

        if (_executionRouter.RequiresWorkspace(backendName) && string.IsNullOrWhiteSpace(_config.Tooling.WorkspaceRoot))
        {
            throw new ToolSandboxException(
                $"Error: Tool '{tool.Name}' is configured to use execution backend '{backendName}' but Tooling.WorkspaceRoot is not set.");
        }

        if (legacySandboxRoute && string.IsNullOrWhiteSpace(template) && string.IsNullOrWhiteSpace(route?.FallbackBackend))
        {
            throw new ToolSandboxException(
                $"Error: Tool '{tool.Name}' requires sandboxing but no sandbox template is configured.");
        }

        if (legacySandboxRoute && _toolSandbox is null)
        {
            throw new ToolSandboxException(
                $"Error: Tool '{tool.Name}' requires sandboxing but no sandbox provider is configured.");
        }

        try
        {
            var sandboxRequest = sandboxCapableTool.CreateSandboxRequest(argsJson);
            sandboxRequest.LeaseKey ??= $"{session.Id}:{tool.Name}";
            sandboxRequest.Template ??= template;
            sandboxRequest.TimeToLiveSeconds = ToolSandboxPolicy.ResolveTimeToLiveSeconds(
                _config,
                tool.Name,
                sandboxRequest.TimeToLiveSeconds);

            var executionResult = await _executionRouter.ExecuteAsync(new ExecutionRequest
            {
                ToolName = tool.Name,
                BackendName = backendName,
                Command = sandboxRequest.Command,
                Arguments = sandboxRequest.Arguments,
                LeaseKey = sandboxRequest.LeaseKey,
                Environment = new Dictionary<string, string>(sandboxRequest.Environment, StringComparer.Ordinal),
                WorkingDirectory = sandboxRequest.WorkingDirectory,
                Template = sandboxRequest.Template,
                TimeToLiveSeconds = sandboxRequest.TimeToLiveSeconds,
                RequireWorkspace = route?.RequireWorkspace ?? true,
                AllowLocalFallback = !IsLocalExecutionDisabled(tool)
            }, route?.FallbackBackend, ct);

            var sandboxResult = new SandboxResult
            {
                ExitCode = executionResult.ExitCode,
                Stdout = executionResult.Stdout,
                Stderr = executionResult.Stderr
            };
            return sandboxCapableTool.FormatSandboxResult(argsJson, sandboxResult);
        }
        catch (ToolSandboxUnavailableException ex) when (legacySandboxRoute || !string.IsNullOrWhiteSpace(route?.FallbackBackend))
        {
            if (IsLocalExecutionDisabled(tool))
            {
                throw new ToolSandboxException(
                    legacySandboxRoute
                        ? $"Error: Tool '{tool.Name}' requires sandboxing but the sandbox provider is unavailable."
                        : $"Error: Tool '{tool.Name}' requires execution backend '{backendName}' but the provider is unavailable.",
                    ex);
            }

            if (sandboxMode == ToolSandboxMode.Require)
            {
                throw new ToolSandboxException(
                    $"Error: Tool '{tool.Name}' requires sandboxing but the sandbox provider is unavailable.",
                    ex);
            }

            _logger?.LogWarning(
                ex,
                "[{CorrelationId}] Execution backend unavailable for tool {Tool}; falling back to {Fallback}",
                turnCtx.CorrelationId,
                tool.Name,
                legacySandboxRoute ? "local tool execution" : route!.FallbackBackend);
            return await ExecuteToolWithTimeoutAsync(tool, argsJson, session, turnCtx, ct);
        }
        catch (ToolSandboxUnavailableException ex)
        {
            throw new ToolSandboxException(
                $"Error: Tool '{tool.Name}' requires execution backend '{backendName}' but the provider is unavailable.",
                ex);
        }
    }

    private static bool IsLocalExecutionDisabled(ITool tool)
        => tool is BrowserTool { LocalExecutionSupported: false };

    private async Task<string> ExecuteToolWithTimeoutAsync(
        ITool tool,
        string argsJson,
        Session session,
        TurnContext turnCtx,
        CancellationToken ct)
    {
        var context = new ToolExecutionContext
        {
            Session = session,
            TurnContext = turnCtx
        };

        if (_toolTimeoutSeconds <= 0)
            return await InvokeToolAsync(tool, argsJson, context, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));
        return await InvokeToolAsync(tool, argsJson, context, timeoutCts.Token);
    }

    private static ValueTask<string> InvokeToolAsync(
        ITool tool,
        string argsJson,
        ToolExecutionContext? context,
        CancellationToken ct)
        => tool is IToolWithContext contextualTool && context is not null
            ? contextualTool.ExecuteAsync(argsJson, context, ct)
            : tool.ExecuteAsync(argsJson, ct);

    internal static AIFunctionDeclaration CreateDeclaration(ITool tool)
    {
        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        return AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            doc.RootElement.Clone(),
            returnJsonSchema: null);
    }

    private static string NormalizeApprovalToolName(string toolName) =>
        string.Equals(toolName, "file_write", StringComparison.Ordinal)
            ? "write_file"
            : toolName;
}

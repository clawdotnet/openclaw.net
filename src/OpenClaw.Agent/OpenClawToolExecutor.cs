using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

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

    public OpenClawToolExecutor(
        IReadOnlyList<ITool> tools,
        int toolTimeoutSeconds,
        bool requireToolApproval,
        IReadOnlyList<string> approvalRequiredTools,
        IReadOnlyList<IToolHook> hooks,
        RuntimeMetrics? metrics = null,
        ILogger? logger = null)
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
    }

    public IList<AITool> ToolDeclarations => _toolDeclarations;

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

        if (_requireToolApproval && _approvalRequiredTools.Contains(NormalizeApprovalToolName(tool.Name)))
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
                    "Tool requires approval but no approval channel is available. Please confirm you want to execute this action.");
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
                result = await ExecuteToolWithTimeoutAsync(tool, argsJson, ct);
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
        catch (Exception ex)
        {
            result = $"Error: Tool execution failed ({ex.GetType().Name}).";
            toolFailed = true;
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} failed", turnCtx.CorrelationId, tool.Name);
        }
        sw.Stop();

        _metrics?.IncrementToolCalls();
        turnCtx.RecordToolCall(sw.Elapsed, toolFailed, toolTimedOut);
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

    private async Task<string> ExecuteToolWithTimeoutAsync(ITool tool, string argsJson, CancellationToken ct)
    {
        if (_toolTimeoutSeconds <= 0)
            return await tool.ExecuteAsync(argsJson, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));
        return await tool.ExecuteAsync(argsJson, timeoutCts.Token);
    }

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

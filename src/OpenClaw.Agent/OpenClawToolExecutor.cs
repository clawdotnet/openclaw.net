using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Execution;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Governance;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

public sealed class ToolExecutionResult
{
    public required ToolInvocation Invocation { get; init; }
    public required string ResultText { get; init; }
    public string ResultStatus { get; init; } = ToolResultStatuses.Completed;
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextStep { get; init; }

    public FunctionResultContent ToFunctionResultContent(string callId)
        => new(callId, ResultText);
}

public sealed class OpenClawToolExecutor
{
    private Dictionary<string, ITool> _toolsByName;
    private AITool[] _toolDeclarations;
    private readonly object _toolsMutationLock = new();
    private readonly int _toolTimeoutSeconds;
    private readonly bool _requireToolApproval;
    private readonly HashSet<string> _approvalRequiredTools;
    private readonly IReadOnlyList<IToolHook> _hooks;
    private readonly IReadOnlyList<IToolResultInterceptor>? _interceptors;
    private readonly RuntimeMetrics? _metrics;
    private readonly ILogger? _logger;
    private readonly GatewayConfig _config;
    private readonly IToolSandbox? _toolSandbox;
    private readonly ToolUsageTracker? _toolUsageTracker;
    private readonly ToolExecutionRouter _executionRouter;
    private readonly IToolPresetResolver? _toolPresetResolver;
    private readonly ToolAuditLog? _auditLog;
    private readonly IRedactionPipeline _redaction;
    private readonly ISentinelSubstitutionService _sentinelSubstitution;
    private readonly IToolGovernanceService _toolGovernance;
    private readonly IPlanExecuteVerifyOrchestrator _planExecuteVerify;
    private readonly Func<Session, string, string?, CancellationToken, Task<string>>? _metaInvokeExecutor;

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
        ToolAuditLog? auditLog = null,
        IRedactionPipeline? redaction = null,
        ISentinelSubstitutionService? sentinelSubstitution = null,
        IToolGovernanceService? toolGovernance = null,
        IPlanExecuteVerifyOrchestrator? planExecuteVerify = null,
        Func<Session, string, string?, CancellationToken, Task<string>>? metaInvokeExecutor = null,
        IReadOnlyList<IToolResultInterceptor>? interceptors = null)
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
        _redaction = redaction ?? new NoopRedactionPipeline();
        _sentinelSubstitution = sentinelSubstitution ?? new NoopSentinelSubstitutionService();
        _toolGovernance = toolGovernance ?? new NoopToolGovernanceService();
        _planExecuteVerify = planExecuteVerify ?? NoopPlanExecuteVerifyOrchestrator.Instance;
        _metaInvokeExecutor = metaInvokeExecutor;
        _interceptors = interceptors;
    }

    public IList<AITool> ToolDeclarations
    {
        get
        {
            lock (_toolsMutationLock)
            {
                return _toolDeclarations.ToArray();
            }
        }
    }

    public IList<AITool> GetToolDeclarations(Session session)
    {
        AITool[] declarations;
        string[] toolNames;
        lock (_toolsMutationLock)
        {
            declarations = _toolDeclarations.ToArray();
            toolNames = _toolsByName.Keys.ToArray();
        }

        var preset = _toolPresetResolver?.Resolve(session, toolNames);
        return declarations
            .Where(item => IsToolAllowedForSession(session, item.Name, preset))
            .ToArray();
    }

    public bool SupportsStreaming(string toolName)
    {
        lock (_toolsMutationLock)
        {
            return _toolsByName.TryGetValue(toolName, out var tool) && tool is IStreamingTool;
        }
    }

    /// <summary>
    /// Atomically replaces the workspace MCP tools in the dispatch table.
    /// Called during hot-reload; safe to call while requests are in-flight.
    /// </summary>
    public void ReplaceMcpTools(IReadOnlyList<ITool> toAdd, IReadOnlyList<string> toRemove)
    {
        ArgumentNullException.ThrowIfNull(toAdd);
        ArgumentNullException.ThrowIfNull(toRemove);

        lock (_toolsMutationLock)
        {
            var nextTools = new Dictionary<string, ITool>(_toolsByName, StringComparer.Ordinal);

            foreach (var name in toRemove.Where(static name => !string.IsNullOrWhiteSpace(name)))
                nextTools.Remove(name);

            foreach (var tool in toAdd)
                nextTools[tool.Name] = tool;

            _toolsByName = nextTools;
            _toolDeclarations = nextTools.Values
                .Select(CreateDeclaration)
                .Cast<AITool>()
                .ToArray();
        }
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        FunctionCallContent call,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        Func<string, ValueTask>? onDelta = null,
        int toolCallCount = 1)
    {
        var argsJson = call.Arguments is not null
            ? JsonSerializer.Serialize(call.Arguments, CoreJsonContext.Default.IDictionaryStringObject)
            : "{}";

        return await ExecuteAsync(call.Name, argsJson, call.CallId, session, turnCtx, isStreaming, approvalCallback, ct, onDelta, toolCallCount);
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
        Func<string, ValueTask>? onDelta = null,
        int toolCallCount = 1)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.ExecuteTool");
        activity?.SetTag("tool.name", toolName);
        var persistedArgsJson = _redaction.Redact(argsJson);

        ITool? tool;
        lock (_toolsMutationLock)
        {
            _toolsByName.TryGetValue(toolName, out tool);
        }

        if (tool is null)
        {
            return CreateImmediateResult(
                toolName,
                persistedArgsJson,
                "Error: Unknown tool",
                callId: callId,
                resultStatus: ToolResultStatuses.Failed,
                failureCode: ToolFailureCodes.ToolFailed,
                failureMessage: "Unknown tool.",
                nextStep: "Use one of the tools declared for this session.");
        }

        if (session.RouteToolsDisabled)
        {
            var disabledMessage = $"Tool '{tool.Name}' is disabled for this routed turn.";
            _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, disabledMessage);
            return CreateImmediateResult(
                toolName,
                persistedArgsJson,
                disabledMessage,
                callId: callId,
                resultStatus: ToolResultStatuses.Blocked,
                failureCode: ToolFailureCodes.PresetBlocked,
                failureMessage: disabledMessage,
                nextStep: "Continue without tools for this routed turn.");
        }

        var preset = _toolPresetResolver?.Resolve(session, _toolsByName.Keys);
        if (!IsToolAllowedForSession(session, tool.Name, preset))
        {
            var deniedByPreset = preset is not null
                ? $"Tool '{tool.Name}' is not allowed for preset '{preset.PresetId}'."
                : $"Tool '{tool.Name}' is not allowed for this session.";
            _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, deniedByPreset);
            return CreateImmediateResult(
                toolName,
                persistedArgsJson,
                deniedByPreset,
                callId: callId,
                resultStatus: ToolResultStatuses.Blocked,
                failureCode: ToolFailureCodes.PresetBlocked,
                failureMessage: deniedByPreset,
                nextStep: "Use a broader preset on this surface, or change the session preset if that access is intentional.");
        }

        var approvalDescriptor = ResolveToolActionDescriptor(tool, persistedArgsJson);
        var governanceDescriptor = ToolGovernanceDescriptorCatalog.Resolve(tool.Name, tool.Description, approvalDescriptor);
        var governanceContext = new ToolGovernanceContext
        {
            AgentId = session.Id,
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnCtx.CorrelationId,
            CallId = callId,
            ToolName = tool.Name,
            ArgumentsJson = persistedArgsJson,
            ActionDescriptor = approvalDescriptor,
            Descriptor = governanceDescriptor,
            IsStreaming = isStreaming
        };
        var governanceDecision = await _toolGovernance.AuthorizeAsync(governanceContext, ct);
        ApplyGovernanceActivityTags(activity, governanceDecision);

        if (!string.IsNullOrWhiteSpace(governanceDecision.RedactedArgumentsJson))
        {
            if (IsValidJson(governanceDecision.RedactedArgumentsJson))
            {
                persistedArgsJson = _redaction.Redact(governanceDecision.RedactedArgumentsJson);
            }
            else
            {
                _logger?.LogWarning(
                    "[{CorrelationId}] Governance returned invalid redacted tool arguments. Keeping existing redacted arguments. Tool={Tool}",
                    turnCtx.CorrelationId,
                    tool.Name);
            }
        }

        if (governanceDecision.Action == GovernanceAction.Redact &&
            !string.IsNullOrWhiteSpace(governanceDecision.ReplacementArgumentsJson))
        {
            if (!IsValidJson(governanceDecision.ReplacementArgumentsJson))
            {
                var invalidReplacementMessage = "Governance returned invalid replacement tool arguments.";
                _logger?.LogWarning(
                    "[{CorrelationId}] {Message} Tool={Tool}",
                    turnCtx.CorrelationId,
                    invalidReplacementMessage,
                    tool.Name);
                RecordImmediateGovernanceAudit(
                    tool,
                    session,
                    turnCtx,
                    persistedArgsJson,
                    invalidReplacementMessage,
                    governanceDecision);
                return CreateImmediateResult(
                    toolName,
                    persistedArgsJson,
                    invalidReplacementMessage,
                    callId: callId,
                    resultStatus: ToolResultStatuses.Blocked,
                    failureCode: ToolFailureCodes.GovernanceDenied,
                    failureMessage: invalidReplacementMessage,
                    nextStep: "Review the governance sidecar redaction response.",
                    governanceDecision: governanceDecision);
            }

            argsJson = governanceDecision.ReplacementArgumentsJson;
            persistedArgsJson = _redaction.Redact(governanceDecision.ReplacementArgumentsJson);
            approvalDescriptor = ResolveToolActionDescriptor(tool, persistedArgsJson);
            governanceDescriptor = ToolGovernanceDescriptorCatalog.Resolve(tool.Name, tool.Description, approvalDescriptor);
        }

        governanceContext = governanceContext with
        {
            ArgumentsJson = persistedArgsJson,
            ActionDescriptor = approvalDescriptor,
            Descriptor = governanceDescriptor
        };

        if (governanceDecision.Action != GovernanceAction.RequireApproval && !governanceDecision.Allowed)
        {
            var deniedByGovernance = governanceDecision.Reason ?? "Tool invocation denied by governance policy.";
            var governanceFailureCode = governanceDecision.IsUnavailable
                ? ToolFailureCodes.GovernanceUnavailable
                : ToolFailureCodes.GovernanceDenied;
            _logger?.LogWarning(
                "[{CorrelationId}] Tool invocation denied by governance. Tool={Tool}, Reason={Reason}",
                turnCtx.CorrelationId,
                tool.Name,
                deniedByGovernance);
            RecordImmediateGovernanceAudit(
                tool,
                session,
                turnCtx,
                persistedArgsJson,
                deniedByGovernance,
                governanceDecision);
            return CreateImmediateResult(
                toolName,
                persistedArgsJson,
                deniedByGovernance,
                callId: callId,
                resultStatus: ToolResultStatuses.Blocked,
                failureCode: governanceFailureCode,
                failureMessage: deniedByGovernance,
                nextStep: governanceFailureCode == ToolFailureCodes.GovernanceUnavailable
                    ? "Check governance sidecar availability or adjust fail-open/fail-closed policy before retrying."
                    : "Adjust the request or governance policy before retrying.",
                governanceDecision: governanceDecision);
        }

        var hookCtx = new ToolHookContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnCtx.CorrelationId,
            ToolName = tool.Name,
            ArgumentsJson = persistedArgsJson,
            IsStreaming = isStreaming
        };

        foreach (var hook in _hooks)
        {
            try
            {
                var allowed = hook is IToolHookWithContext ctxHook
                    ? await ctxHook.BeforeExecuteAsync(hookCtx, ct)
                    : await hook.BeforeExecuteAsync(tool.Name, persistedArgsJson, ct);
                if (!allowed)
                {
                    var deniedByHook = $"Tool execution denied by hook: {hook.Name}";
                    _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, deniedByHook);
                    return CreateImmediateResult(
                        toolName,
                        persistedArgsJson,
                        deniedByHook,
                        callId: callId,
                        resultStatus: ToolResultStatuses.Blocked,
                        failureCode: ToolFailureCodes.ToolFailed,
                        failureMessage: deniedByHook,
                        governanceDecision: governanceDecision);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} BeforeExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var normalizedToolName = NormalizeApprovalToolName(tool.Name);
        var explicitlyConfiguredApproval = _config.Tooling.ApprovalRequiredTools
            .Any(item => string.Equals(NormalizeApprovalToolName(item), normalizedToolName, StringComparison.Ordinal));
        var presetRequiresApproval = preset?.ApprovalRequiredTools.Contains(tool.Name) == true;
        var defaultActionAwareApproval = _requireToolApproval
            && ToolActionPolicyResolver.SupportsActionAwareApproval(tool.Name)
            && (approvalDescriptor.IsMutation || approvalDescriptor.RequiresApproval);
        var listedApproval = _requireToolApproval && (_approvalRequiredTools.Contains(normalizedToolName) || presetRequiresApproval);
        var governanceRequiresApproval = governanceDecision.Action == GovernanceAction.RequireApproval;
        var requiresApproval = governanceRequiresApproval || approvalDescriptor.RequiresApproval ||
            (ToolActionPolicyResolver.SupportsActionAwareApproval(tool.Name) && !explicitlyConfiguredApproval && !presetRequiresApproval
            ? defaultActionAwareApproval
            : listedApproval || defaultActionAwareApproval);
        var pevDecision = await _planExecuteVerify.EvaluateToolAsync(new PlanExecuteVerifyToolContext
        {
            Session = session,
            CorrelationId = turnCtx.CorrelationId,
            CallId = callId,
            ToolName = tool.Name,
            ArgumentsJson = persistedArgsJson,
            ActionDescriptor = approvalDescriptor,
            GovernanceDescriptor = governanceDescriptor,
            ExistingApprovalRequired = requiresApproval,
            IsStreaming = isStreaming,
            ToolCallCount = toolCallCount
        }, ct);
        if (BlocksPlanExecuteVerifyDecision(pevDecision.Decision))
        {
            var blocked = $"Plan-Execute-Verify decision '{pevDecision.Decision}' blocked tool execution: {pevDecision.Summary}";
            _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, blocked);
            return CreateImmediateResult(
                toolName,
                persistedArgsJson,
                _redaction.Redact(blocked),
                callId: callId,
                resultStatus: ToolResultStatuses.Blocked,
                failureCode: ToolFailureCodes.ApprovalRequired,
                failureMessage: blocked,
                nextStep: "Review the linked Plan-Execute-Verify run before retrying.",
                governanceDecision: governanceDecision);
        }
        requiresApproval = requiresApproval || pevDecision.RequiresApproval;

        if (requiresApproval)
        {
            if (approvalCallback is not null)
            {
                var approved = await approvalCallback(tool.Name, persistedArgsJson, ct);
                await _planExecuteVerify.RecordApprovalDecisionAsync(pevDecision.Run, approved, ct);
                if (!approved)
                {
                    _logger?.LogInformation("[{CorrelationId}] Tool {Tool} denied by user", turnCtx.CorrelationId, tool.Name);
                    var deniedResult = CreateImmediateResult(
                        toolName,
                        persistedArgsJson,
                        "Tool execution denied by user.",
                        callId: callId,
                        resultStatus: ToolResultStatuses.Blocked,
                        failureCode: ToolFailureCodes.ApprovalRequired,
                        failureMessage: "Tool execution was denied by the reviewer.",
                        nextStep: "Approve the tool request to allow this action.",
                        governanceDecision: governanceDecision);
                    return deniedResult;
                }
            }
            else
            {
                _logger?.LogWarning(
                    "[{CorrelationId}] Tool {Tool} requires approval but no approval channel is available — denied",
                    turnCtx.CorrelationId,
                    tool.Name);
                var approvalMessage =
                    $"Tool '{tool.Name}' requires approval but this session has no approval channel — auto-denied. " +
                    "To enable this tool: connect through the browser chat at /chat (it supports interactive approvals) " +
                    "or set OpenClaw:Tooling:RequireToolApproval=false for trusted local sessions.";
                var deniedResult = CreateImmediateResult(
                    toolName,
                    persistedArgsJson,
                    _redaction.Redact(approvalMessage),
                    callId: callId,
                    resultStatus: ToolResultStatuses.Blocked,
                    failureCode: ToolFailureCodes.ApprovalRequired,
                    failureMessage: approvalMessage,
                    nextStep: "Use an approval-capable surface such as /chat, or disable approval requirements for trusted local sessions.",
                    governanceDecision: governanceDecision);
                await _planExecuteVerify.RecordApprovalDecisionAsync(pevDecision.Run, approved: false, ct);
                return deniedResult;
            }
        }

        if (requiresApproval && !string.IsNullOrWhiteSpace(approvalDescriptor.ApprovalFingerprint))
        {
            var currentDescriptor = ResolveToolActionDescriptor(tool, persistedArgsJson);
            if (!string.Equals(currentDescriptor.ApprovalFingerprint, approvalDescriptor.ApprovalFingerprint, StringComparison.Ordinal))
            {
                var message = $"Tool '{tool.Name}' changed after approval was requested; execution blocked.";
                return CreateImmediateResult(
                    toolName,
                    persistedArgsJson,
                    message,
                    callId: callId,
                    resultStatus: ToolResultStatuses.Blocked,
                    failureCode: ToolFailureCodes.ApprovalRequired,
                    failureMessage: message,
                    nextStep: "Preview the command again and request approval for the updated fingerprint.",
                    governanceDecision: governanceDecision);
            }
        }

        var sw = Stopwatch.StartNew();
        string result;
        string resultStatus = ToolResultStatuses.Completed;
        string? failureCode = null;
        string? failureMessage = null;
        string? nextStep = null;
        var toolFailed = false;
        var toolTimedOut = false;
        var afterHookCtx = hookCtx;
        try
        {
            var substitution = await _sentinelSubstitution.SubstituteAsync(new SentinelSubstitutionContext
            {
                ToolName = tool.Name,
                ArgumentsJson = argsJson,
                SessionId = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                CorrelationId = turnCtx.CorrelationId
            }, ct);
            var executionArgsJson = substitution.ExecutionArgumentsJson;
            persistedArgsJson = _redaction.Redact(substitution.PersistedArgumentsJson);
            afterHookCtx = hookCtx with { ArgumentsJson = persistedArgsJson };

            if (onDelta is not null && tool is IStreamingTool streamingTool)
                result = await ExecuteStreamingToolCollectAsync(streamingTool, executionArgsJson, onDelta, ct);
            else if (_metaInvokeExecutor is not null &&
                string.Equals(tool.Name, "meta_invoke", StringComparison.Ordinal) &&
                TryGetMetaInvokeArguments(executionArgsJson, out var requestedSkill, out var requestedInput))
            {
                result = await _metaInvokeExecutor(session, requestedSkill!, requestedInput, ct);
                if (result.Contains("disabled by runtime policy", StringComparison.OrdinalIgnoreCase))
                {
                    toolFailed = true;
                    resultStatus = ToolResultStatuses.Blocked;
                    failureCode = ToolFailureCodes.RuntimeCapabilityUnavailable;
                    failureMessage = result;
                    nextStep = "Use a non-meta skill or enable meta invocation in runtime policy.";
                }
            }
            else
                result = await ExecuteToolWithRoutingAsync(tool, executionArgsJson, session, turnCtx, ct);
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
            resultStatus = ToolResultStatuses.Failed;
            failureCode = ToolFailureCodes.Timeout;
            failureMessage = result;
            nextStep = "Retry the tool call or increase Tooling.ToolTimeoutSeconds.";
            _metrics?.IncrementToolTimeouts();
            _logger?.LogWarning("[{CorrelationId}] Tool {Tool} timed out after {Timeout}s", turnCtx.CorrelationId, tool.Name, _toolTimeoutSeconds);
        }
        catch (ToolSandboxException ex)
        {
            result = ex.Message;
            toolFailed = true;
            resultStatus = ToolResultStatuses.Blocked;
            failureCode = ex.FailureCode ?? ClassifyToolFailureCode(tool, ex.Message);
            failureMessage = ex.Message;
            nextStep = BuildFailureNextStep(tool.Name, failureCode);
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} sandbox execution failed", turnCtx.CorrelationId, tool.Name);
        }
        catch (Exception ex)
        {
            failureCode = ClassifyToolFailureCode(tool, ex.Message);
            failureMessage = ex.Message;
            toolFailed = true;
            if (failureCode is ToolFailureCodes.OperatorAuthRequired or ToolFailureCodes.BrowserBackendMissing or ToolFailureCodes.RuntimeCapabilityUnavailable)
            {
                result = ex.Message.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    ? ex.Message
                    : $"Error: {ex.Message}";
                resultStatus = ToolResultStatuses.Blocked;
                nextStep = BuildFailureNextStep(tool.Name, failureCode);
            }
            else
            {
                result = "Error: Tool execution failed.";
                resultStatus = ToolResultStatuses.Failed;
            }
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} failed", turnCtx.CorrelationId, tool.Name);
        }
        sw.Stop();
        result = _redaction.Redact(result);
        failureMessage = failureMessage is null ? null : _redaction.Redact(failureMessage);
        nextStep = nextStep is null ? null : _redaction.Redact(nextStep);

        // Apply result interceptors (e.g., TokenJuice reduction)
        if (_interceptors is { Count: > 0 })
        {
            foreach (var interceptor in _interceptors.OrderBy(i => i.Order))
            {
                try
                {
                    result = await interceptor.InterceptAsync(
                        ReductionContext.From(
                            tool.Name,
                            persistedArgsJson,
                            result,
                            isError: toolFailed,
                            exitCode: toolFailed ? 1 : 0),
                        ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[{CorrelationId}] Interceptor {Interceptor} failed, returning raw output",
                        turnCtx.CorrelationId, interceptor.Name);
                }
            }
        }

        _metrics?.IncrementToolCalls();
        Telemetry.ToolExecutionDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("tool.name", tool.Name),
            new KeyValuePair<string, object?>("tool.success", !toolFailed));
        turnCtx.RecordToolCall(sw.Elapsed, toolFailed, toolTimedOut);
        _toolUsageTracker?.RecordToolCall(tool.Name, sw.Elapsed, toolFailed, toolTimedOut);
        var argumentsBytes = Encoding.UTF8.GetByteCount(persistedArgsJson);
        var resultBytes = Encoding.UTF8.GetByteCount(result);
        await RecordGovernanceResultAsync(
            governanceContext,
            governanceDecision,
            resultStatus,
            failureCode,
            failureMessage,
            toolFailed,
            toolTimedOut,
            sw.Elapsed,
            resultBytes,
            ct);
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
            ArgumentsBytes = argumentsBytes,
            ResultBytes = resultBytes,
            GovernanceAllowed = governanceDecision.Allowed,
            GovernanceAction = governanceDecision.Action.ToString(),
            GovernanceReason = governanceDecision.Reason,
            GovernancePolicyId = governanceDecision.PolicyId,
            GovernanceRuleId = governanceDecision.RuleId,
            GovernanceTrustScore = governanceDecision.TrustScore,
            GovernanceEvaluationMs = governanceDecision.EvaluationMs,
            GovernanceUnavailable = governanceDecision.IsUnavailable
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
                    await ctxHook.AfterExecuteAsync(afterHookCtx, result, sw.Elapsed, toolFailed, ct);
                else
                    await hook.AfterExecuteAsync(tool.Name, persistedArgsJson, result, sw.Elapsed, toolFailed, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} AfterExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var invocation = new ToolInvocation
        {
            CallId = callId,
            ToolName = toolName,
            Arguments = persistedArgsJson,
            Result = result,
            Duration = sw.Elapsed,
            ResultStatus = resultStatus,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            NextStep = nextStep,
            GovernanceAllowed = governanceDecision.Allowed,
            GovernanceAction = governanceDecision.Action.ToString(),
            GovernanceReason = governanceDecision.Reason,
            GovernancePolicyId = governanceDecision.PolicyId,
            GovernanceRuleId = governanceDecision.RuleId,
            GovernanceTrustScore = governanceDecision.TrustScore,
            GovernanceEvaluationMs = governanceDecision.EvaluationMs,
            GovernanceUnavailable = governanceDecision.IsUnavailable
        };
        await _planExecuteVerify.CompleteToolAsync(pevDecision.Run, invocation, ct);

        return new ToolExecutionResult
        {
            Invocation = invocation,
            ResultText = result,
            ResultStatus = resultStatus,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            NextStep = nextStep
        };
    }

    private static ToolExecutionResult CreateImmediateResult(
        string toolName,
        string argsJson,
        string result,
        string? callId = null,
        string resultStatus = ToolResultStatuses.Completed,
        string? failureCode = null,
        string? failureMessage = null,
        string? nextStep = null,
        GovernanceDecision? governanceDecision = null)
    {
        var invocation = new ToolInvocation
        {
            CallId = callId,
            ToolName = toolName,
            Arguments = argsJson,
            Result = result,
            Duration = TimeSpan.Zero,
            ResultStatus = resultStatus,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            NextStep = nextStep,
            GovernanceAllowed = governanceDecision?.Allowed,
            GovernanceAction = governanceDecision?.Action.ToString(),
            GovernanceReason = governanceDecision?.Reason,
            GovernancePolicyId = governanceDecision?.PolicyId,
            GovernanceRuleId = governanceDecision?.RuleId,
            GovernanceTrustScore = governanceDecision?.TrustScore,
            GovernanceEvaluationMs = governanceDecision?.EvaluationMs,
            GovernanceUnavailable = governanceDecision?.IsUnavailable
        };

        return new ToolExecutionResult
        {
            Invocation = invocation,
            ResultText = result,
            ResultStatus = resultStatus,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            NextStep = nextStep
        };
    }

    private static void ApplyGovernanceActivityTags(Activity? activity, GovernanceDecision decision)
    {
        activity?.SetTag("tool.governance.allowed", decision.Allowed);
        activity?.SetTag("tool.governance.action", decision.Action.ToString());
        if (!string.IsNullOrWhiteSpace(decision.PolicyId))
            activity?.SetTag("tool.governance.policy_id", decision.PolicyId);
        if (!string.IsNullOrWhiteSpace(decision.RuleId))
            activity?.SetTag("tool.governance.rule_id", decision.RuleId);
        if (decision.TrustScore is not null)
            activity?.SetTag("tool.governance.trust_score", decision.TrustScore);
        if (decision.EvaluationMs is not null)
            activity?.SetTag("tool.governance.evaluation_ms", decision.EvaluationMs);
        activity?.SetTag("tool.governance.unavailable", decision.IsUnavailable);
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetMetaInvokeArguments(string argsJson, out string? skill, out string? input)
    {
        skill = null;
        input = null;

        if (string.IsNullOrWhiteSpace(argsJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("skill", out var skillElement) ||
                skillElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var skillValue = skillElement.GetString();
            if (string.IsNullOrWhiteSpace(skillValue))
                return false;

            skill = skillValue;
            if (doc.RootElement.TryGetProperty("input", out var inputElement) &&
                inputElement.ValueKind == JsonValueKind.String)
            {
                input = inputElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async ValueTask RecordGovernanceResultAsync(
        ToolGovernanceContext context,
        GovernanceDecision decision,
        string resultStatus,
        string? failureCode,
        string? failureMessage,
        bool failed,
        bool timedOut,
        TimeSpan duration,
        int resultBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await _toolGovernance.RecordResultAsync(
                context,
                decision,
                new ToolGovernanceExecutionResult
                {
                    ResultStatus = resultStatus,
                    FailureCode = failureCode,
                    FailureMessage = failureMessage,
                    Failed = failed,
                    TimedOut = timedOut,
                    DurationMs = duration.TotalMilliseconds,
                    ResultBytes = resultBytes
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger?.LogWarning(ex, "[{CorrelationId}] Governance result audit failed for tool {Tool}", context.CorrelationId, context.ToolName);
        }
    }

    private void RecordImmediateGovernanceAudit(
        ITool tool,
        Session session,
        TurnContext turnCtx,
        string argumentsJson,
        string result,
        GovernanceDecision decision)
    {
        _auditLog?.Record(new ToolAuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ToolName = tool.Name,
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnCtx.CorrelationId,
            DurationMs = 0,
            Failed = true,
            TimedOut = false,
            ArgumentsBytes = Encoding.UTF8.GetByteCount(argumentsJson),
            ResultBytes = Encoding.UTF8.GetByteCount(result),
            GovernanceAllowed = decision.Allowed,
            GovernanceAction = decision.Action.ToString(),
            GovernanceReason = decision.Reason,
            GovernancePolicyId = decision.PolicyId,
            GovernanceRuleId = decision.RuleId,
            GovernanceTrustScore = decision.TrustScore,
            GovernanceEvaluationMs = decision.EvaluationMs,
            GovernanceUnavailable = decision.IsUnavailable
        });
    }

    private static bool IsToolAllowedForSession(Session session, string toolName, ResolvedToolPreset? preset)
    {
        // DisableTools routing decisions intentionally expose no tools to the model.
        if (session.RouteToolsDisabled)
            return false;

        if (preset is not null && !preset.AllowedTools.Contains(toolName))
            return false;

        if (session.RouteAllowedTools is { Length: > 0 })
            return session.RouteAllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

        return true;
    }

    private static ToolActionDescriptor ResolveToolActionDescriptor(ITool tool, string argsJson)
        => tool is IToolActionDescriptorProvider descriptorProvider
            ? descriptorProvider.ResolveActionDescriptor(argsJson)
            : ToolActionPolicyResolver.Resolve(tool.Name, argsJson);

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
        var chunks = new List<string>();

        await foreach (var chunk in tool.ExecuteStreamingAsync(argsJson, effectiveCt).WithCancellation(effectiveCt))
        {
            if (chunk is null)
                continue;

            chunks.Add(chunk);
            if (sb.Length < MaxChars)
            {
                var remaining = MaxChars - sb.Length;
                sb.Append(chunk.Length <= remaining ? chunk : chunk[..remaining]);
            }
        }

        if (sb.Length >= MaxChars)
            sb.Append("…");

        var result = sb.ToString();
        var redactedResult = _redaction.Redact(result);
        if (string.Equals(redactedResult, result, StringComparison.Ordinal))
        {
            foreach (var chunk in chunks)
                await onDelta(chunk);
        }
        else if (!string.IsNullOrEmpty(redactedResult))
        {
            await onDelta(redactedResult);
        }

        return result;
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
                throw CreateLocalExecutionUnavailableException(tool);

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
            throw CreateLocalExecutionUnavailableException(tool);

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
        => tool is IToolLocalExecutionPolicy { LocalExecutionSupported: false };

    internal async Task<ToolExecutionResult> ExecuteSkillEntrypointAsync(
        SkillDefinition skill,
        string entrypoint,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string parseMode,
        string? stdin,
        CancellationToken ct)
    {
        var script = ResolveSkillScript(skill, entrypoint);
        if (script is null)
        {
            return CreateImmediateResult(
                "skill_exec",
                "{}",
                $"Meta step skill_exec entrypoint '{entrypoint}' was not found in skill '{skill.Name}'.",
                resultStatus: ToolResultStatuses.Failed,
                failureCode: "skill_exec_entrypoint_not_found",
                failureMessage: $"Entrypoint '{entrypoint}' was not found.");
        }

        if (!IsPathWithinSkillRoot(script.AbsolutePath, skill) || ResourcePathContainsReparsePoint(skill.Location, script.AbsolutePath))
        {
            return CreateImmediateResult(
                "skill_exec",
                "{}",
                $"Meta step skill_exec entrypoint '{entrypoint}' was rejected because it resolves outside the skill root or through a reparse point.",
                resultStatus: ToolResultStatuses.Blocked,
                failureCode: "skill_exec_entrypoint_denied",
                failureMessage: $"Entrypoint '{entrypoint}' failed skill root validation.");
        }

        var command = ResolveScriptCommand(script.AbsolutePath, out var commandArguments);
        var allArguments = commandArguments.Concat(arguments).ToArray();
        var resolvedWorkingDirectory = ResolveSkillWorkingDirectory(skill, workingDirectory);

        try
        {
            var executionResult = await _executionRouter.ExecuteAsync(new ExecutionRequest
            {
                ToolName = "skill_exec",
                BackendName = _config.Execution.DefaultBackend,
                Command = command,
                Arguments = allArguments,
                StandardInput = stdin,
                WorkingDirectory = resolvedWorkingDirectory,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                RequireWorkspace = false,
                AllowLocalFallback = true
            }, fallbackBackend: null, ct);

            var output = NormalizeSkillExecOutput(parseMode, executionResult.Stdout, executionResult.Stderr);
            if (executionResult.TimedOut)
            {
                return CreateImmediateResult(
                    "skill_exec",
                    "{}",
                    output,
                    resultStatus: ToolResultStatuses.Failed,
                    failureCode: "step_timeout",
                    failureMessage: "skill_exec timed out.");
            }

            if (executionResult.ExitCode != 0)
            {
                return CreateImmediateResult(
                    "skill_exec",
                    "{}",
                    output,
                    resultStatus: ToolResultStatuses.Failed,
                    failureCode: "skill_exec_failed",
                    failureMessage: $"skill_exec exited with code {executionResult.ExitCode}.");
            }

            return CreateImmediateResult("skill_exec", "{}", output);
        }
        catch (Exception ex)
        {
            return CreateImmediateResult(
                "skill_exec",
                "{}",
                $"Meta step skill_exec failed: {ex.Message}",
                resultStatus: ToolResultStatuses.Failed,
                failureCode: "skill_exec_failed",
                failureMessage: ex.Message);
        }
    }

    private static ToolSandboxException CreateLocalExecutionUnavailableException(ITool tool)
        => new(
            GetLocalExecutionUnavailableMessage(tool),
            GetLocalExecutionUnavailableFailureCode(tool));

    private static string GetLocalExecutionUnavailableMessage(ITool tool)
        => tool is IToolLocalExecutionPolicy { LocalExecutionSupported: false } policy
            ? policy.LocalExecutionUnavailableMessage
            : $"Error: Tool '{tool.Name}' requires a configured execution backend or sandbox in this runtime. Local execution is unavailable.";

    private static string GetLocalExecutionUnavailableFailureCode(ITool tool)
        => tool is IToolLocalExecutionPolicy { LocalExecutionSupported: false } policy
            ? policy.LocalExecutionUnavailableFailureCode
            : ToolFailureCodes.RuntimeCapabilityUnavailable;

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

    private static bool BlocksPlanExecuteVerifyDecision(string? decision)
        => decision is not null &&
           !string.Equals(decision, PlanExecuteVerifyDecisionKinds.Proceed, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(decision, PlanExecuteVerifyDecisionKinds.RequireApproval, StringComparison.OrdinalIgnoreCase);

    private static string ClassifyToolFailureCode(ITool tool, string message)
    {
        if (LooksLikeOperatorAuthFailure(message))
            return ToolFailureCodes.OperatorAuthRequired;

        if (tool is IToolLocalExecutionPolicy { LocalExecutionSupported: false } policy &&
            string.Equals(message, policy.LocalExecutionUnavailableMessage, StringComparison.Ordinal))
        {
            return policy.LocalExecutionUnavailableFailureCode;
        }

        var toolName = tool.Name;
        if (toolName.Equals("browser", StringComparison.OrdinalIgnoreCase))
        {
            return message.Contains("execution backend", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Local Playwright execution is unavailable", StringComparison.OrdinalIgnoreCase)
                ? ToolFailureCodes.BrowserBackendMissing
                : ToolFailureCodes.RuntimeCapabilityUnavailable;
        }

        return message.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("execution backend", StringComparison.OrdinalIgnoreCase)
            ? ToolFailureCodes.RuntimeCapabilityUnavailable
            : ToolFailureCodes.ToolFailed;
    }

    private static SkillResource? ResolveSkillScript(SkillDefinition skill, string entrypoint)
        => skill.Resources.FirstOrDefault(resource =>
            resource.Kind == SkillResourceKind.Script &&
            (string.Equals(resource.Name, entrypoint, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resource.RelativePath, $"scripts/{entrypoint}", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resource.RelativePath, entrypoint.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)));

    private static string ResolveSkillWorkingDirectory(SkillDefinition skill, string? workingDirectory)
    {
        var skillRoot = Path.GetFullPath(skill.Location);
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return skillRoot;

        var candidate = Path.GetFullPath(Path.Combine(skillRoot, workingDirectory));
        var rootWithSep = skillRoot.EndsWith(Path.DirectorySeparatorChar) ? skillRoot : skillRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(candidate, skillRoot, comparison) && !candidate.StartsWith(rootWithSep, comparison))
            throw new InvalidOperationException("skill_exec working directory must remain inside the skill root.");

        return candidate;
    }

    private static string ResolveScriptCommand(string scriptAbsolutePath, out string[] prefixArguments)
    {
        var extension = Path.GetExtension(scriptAbsolutePath);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            prefixArguments = ["-NoProfile", "-File", scriptAbsolutePath];
            return OperatingSystem.IsWindows() ? "pwsh" : "pwsh";
        }

        prefixArguments = [scriptAbsolutePath];
        return scriptAbsolutePath;
    }

    private static string NormalizeSkillExecOutput(string parseMode, string stdout, string stderr)
    {
        var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        var trimmed = output.Trim();

        if (string.Equals(parseMode, "json", StringComparison.OrdinalIgnoreCase))
        {
            using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(trimmed) ? "null" : trimmed);
        }

        return trimmed;
    }

    private static bool IsPathWithinSkillRoot(string resourceAbsolutePath, SkillDefinition skill)
    {
        if (string.IsNullOrEmpty(skill.Location))
            return true;

        try
        {
            var skillRoot = Path.GetFullPath(skill.Location);
            var resolved = Path.GetFullPath(resourceAbsolutePath);
            var rootWithSep = skillRoot.EndsWith(Path.DirectorySeparatorChar)
                ? skillRoot
                : skillRoot + Path.DirectorySeparatorChar;
            return resolved.StartsWith(rootWithSep,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool ResourcePathContainsReparsePoint(string skillLocation, string resourceAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(skillLocation))
            return false;

        try
        {
            var skillRoot = Path.GetFullPath(skillLocation);
            var resolved = Path.GetFullPath(resourceAbsolutePath);
            var relative = Path.GetRelativePath(skillRoot, resolved);
            if (relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                return true;
            }

            var current = skillRoot;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? BuildFailureNextStep(string toolName, string? failureCode)
        => failureCode switch
        {
            ToolFailureCodes.OperatorAuthRequired => "Authenticate with a browser session or operator token on this surface before retrying the tool.",
            ToolFailureCodes.BrowserBackendMissing => "Configure a browser execution backend or sandbox, or disable the browser tool in this runtime.",
            ToolFailureCodes.RuntimeCapabilityUnavailable when toolName.Equals("shell", StringComparison.OrdinalIgnoreCase)
                => "Configure the required sandbox or execution backend for shell, or relax the tool policy for trusted local sessions.",
            ToolFailureCodes.RuntimeCapabilityUnavailable
                => "Configure the required execution backend or sandbox for this tool, or disable the tool in this runtime.",
            _ => null
        };

    private static bool LooksLikeOperatorAuthFailure(string message)
        => message.Contains("operator auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("operator authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("operator token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("browser-session", StringComparison.OrdinalIgnoreCase)
            || message.Contains("account-token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bootstrap token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("current surface", StringComparison.OrdinalIgnoreCase);
}

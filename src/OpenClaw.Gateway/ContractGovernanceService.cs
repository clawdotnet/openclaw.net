using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway;

/// <summary>
/// Central service for contract governance: pre-flight validation, cost computation,
/// and contract lifecycle management. Integrates with existing stores and trackers.
/// </summary>
internal sealed class ContractGovernanceService
{
    private static readonly TimeSpan VerificationHttpTimeout = TimeSpan.FromSeconds(15);

    private readonly GatewayStartupContext _startup;
    private readonly ContractStore _contractStore;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly ProviderUsageTracker _providerUsage;
    private readonly ILogger<ContractGovernanceService> _logger;

    /// <summary>Tools that require JIT runtime mode (dynamic/reflection-heavy).</summary>
    private static readonly HashSet<string> JitOnlyToolPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "delegate_agent" // delegation may load dynamic profiles
    };

    public ContractGovernanceService(
        GatewayStartupContext startup,
        ContractStore contractStore,
        RuntimeEventStore runtimeEvents,
        ProviderUsageTracker providerUsage,
        ILogger<ContractGovernanceService> logger)
    {
        _startup = startup;
        _contractStore = contractStore;
        _runtimeEvents = runtimeEvents;
        _providerUsage = providerUsage;
        _logger = logger;
    }

    /// <summary>
    /// Pre-flight validation of a contract policy against the current runtime state.
    /// Does not persist anything.
    /// </summary>
    public ContractValidationResult ValidatePreFlight(
        ContractPolicy policy,
        IReadOnlyCollection<string> registeredToolNames)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var granted = new List<string>();
        var denied = new List<string>();

        var effectiveMode = _startup.RuntimeState.EffectiveModeName;

        // Runtime mode check
        if (!string.IsNullOrWhiteSpace(policy.RequiredRuntimeMode))
        {
            var required = policy.RequiredRuntimeMode.Trim().ToLowerInvariant();
            if (required is not ("aot" or "jit"))
            {
                errors.Add($"Invalid RequiredRuntimeMode '{policy.RequiredRuntimeMode}'. Must be 'aot' or 'jit'.");
            }
            else if (!string.Equals(required, effectiveMode, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Contract requires runtime mode '{required}' but gateway is running in '{effectiveMode}'.");
            }
        }

        // Tool availability and JIT-compatibility check
        var registeredSet = new HashSet<string>(registeredToolNames, StringComparer.Ordinal);
        foreach (var tool in policy.RequestedTools)
        {
            if (string.IsNullOrWhiteSpace(tool))
                continue;

            if (!registeredSet.Contains(tool))
            {
                denied.Add(tool);
                warnings.Add($"Tool '{tool}' is not registered in the current runtime.");
                continue;
            }

            if (_startup.RuntimeState.EffectiveMode == GatewayRuntimeMode.Aot &&
                JitOnlyToolPatterns.Contains(tool))
            {
                denied.Add(tool);
                warnings.Add($"Tool '{tool}' requires JIT runtime mode but gateway is running in AOT.");
                continue;
            }

            granted.Add(tool);
        }

        // Budget validation
        if (policy.MaxCostUsd < 0)
            errors.Add("MaxCostUsd cannot be negative.");
        if (policy.SoftCostWarningUsd < 0)
            errors.Add("SoftCostWarningUsd cannot be negative.");
        if (policy.MaxCostUsd > 0 && policy.SoftCostWarningUsd > policy.MaxCostUsd)
            errors.Add("SoftCostWarningUsd cannot exceed MaxCostUsd.");
        if (policy.MaxTokens < 0)
            errors.Add("MaxTokens cannot be negative.");
        if (policy.MaxToolCalls < 0)
            errors.Add("MaxToolCalls cannot be negative.");
        if (policy.MaxRuntimeSeconds < 0)
            errors.Add("MaxRuntimeSeconds cannot be negative.");

        // Scoped capability validation
        foreach (var scope in policy.ScopedCapabilities)
        {
            if (!registeredSet.Contains(scope.ToolName))
                warnings.Add($"ScopedCapability references unregistered tool '{scope.ToolName}'.");
            if (scope.AllowedPaths.Length == 0)
                warnings.Add($"ScopedCapability for '{scope.ToolName}' has no allowed paths.");
        }

        ValidateVerificationPolicy(policy.Verification, errors, warnings);

        var isValid = errors.Count == 0;

        return new ContractValidationResult
        {
            IsValid = isValid,
            GrantedTools = granted.ToArray(),
            DeniedTools = denied.ToArray(),
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray(),
            EffectiveRuntimeMode = effectiveMode
        };
    }

    /// <summary>
    /// Create a contract, validate it, persist the snapshot, and emit a runtime event.
    /// </summary>
    public ContractCreateResponse CreateContract(
        ContractCreateRequest request,
        string sessionId,
        IReadOnlyCollection<string> registeredToolNames)
    {
        var contractId = $"ctr_{Guid.NewGuid():N}"[..20];

        var policy = new ContractPolicy
        {
            Id = contractId,
            Name = request.Name,
            RequiredRuntimeMode = request.RequiredRuntimeMode,
            RequestedTools = request.RequestedTools ?? [],
            ScopedCapabilities = request.ScopedCapabilities ?? [],
            MaxCostUsd = request.MaxCostUsd,
            SoftCostWarningUsd = request.SoftCostWarningUsd,
            MaxTokens = request.MaxTokens,
            MaxToolCalls = request.MaxToolCalls,
            MaxRuntimeSeconds = request.MaxRuntimeSeconds,
            CreatedBy = request.CreatedBy,
            Verification = request.Verification,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var validation = ValidatePreFlight(policy, registeredToolNames);

        // Persist initial snapshot
        var snapshot = new ContractExecutionSnapshot
        {
            ContractId = contractId,
            SessionId = sessionId,
            Status = validation.IsValid ? "active" : "invalid",
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        _contractStore.Append(snapshot);

        // Emit runtime event
        _runtimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = sessionId,
            CorrelationId = contractId,
            Component = "Contract",
            Action = "created",
            Severity = validation.IsValid ? "info" : "warning",
            Summary = validation.IsValid
                ? $"Contract '{contractId}' created with {policy.RequestedTools.Length} requested tools."
                : $"Contract '{contractId}' created with validation errors: {string.Join("; ", validation.Errors)}",
            Metadata = new Dictionary<string, string>
            {
                ["contractId"] = contractId,
                ["isValid"] = validation.IsValid.ToString()
            }
        });

        _logger.LogInformation("Contract {ContractId} created for session {SessionId}, valid={IsValid}",
            contractId, sessionId, validation.IsValid);

        return new ContractCreateResponse
        {
            Policy = policy,
            Validation = validation
        };
    }

    public void AttachToSession(Session session, ContractPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);

        session.ContractPolicy = policy;
        session.ContractAttachedAtUtc = DateTimeOffset.UtcNow;
        session.ContractBaselineInputTokens = session.TotalInputTokens;
        session.ContractBaselineOutputTokens = session.TotalOutputTokens;
        session.ContractBaselineToolCalls = CountToolCalls(session);
        session.ContractAccumulatedCostUsd = 0m;

        _contractStore.Append(BuildSnapshot(
            session,
            status: "active",
            lifecycleState: AutomationLifecycleStates.Running,
            verificationStatus: AutomationVerificationStatuses.NotRun));
    }

    public decimal ComputeSessionCostUsd(Session session)
        => session.ContractAccumulatedCostUsd;

    /// <summary>
    /// Estimates session cost from recent turn history. Note: this is observability-only
    /// and may undercount for sessions exceeding 256 turns. For budget enforcement,
    /// use the accumulated counter via <see cref="ComputeSessionCostUsd(Session)"/>.
    /// </summary>
    public decimal ComputeSessionCostUsd(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var totalCost = 0m;
        foreach (var turn in _providerUsage.RecentTurns(sessionId, limit: 256))
            totalCost += ComputeTurnCostUsd(turn.ProviderId, turn.ModelId, turn.InputTokens, turn.OutputTokens);

        return totalCost;
    }

    public decimal ComputeTurnCostUsd(string providerId, string modelId, long inputTokens, long outputTokens)
    {
        if (inputTokens <= 0 && outputTokens <= 0)
            return 0m;

        var rate = TokenCostRateResolver.Resolve(_startup.Config, providerId, modelId);
        return ((inputTokens * rate.InputUsdPer1K) + (outputTokens * rate.OutputUsdPer1K)) / 1000m;
    }

    public bool IsTokenBudgetExceeded(Session session)
    {
        if (session.ContractPolicy is not { MaxTokens: > 0 })
            return false;

        var usedTokens = GetContractTokenUsage(session);
        return usedTokens >= session.ContractPolicy.MaxTokens;
    }

    public bool IsRuntimeBudgetExceeded(Session session)
    {
        if (session.ContractPolicy is not { MaxRuntimeSeconds: > 0 })
            return false;

        var attachedAt = session.ContractAttachedAtUtc ?? session.ContractPolicy.CreatedAtUtc;
        return (DateTimeOffset.UtcNow - attachedAt).TotalSeconds >= session.ContractPolicy.MaxRuntimeSeconds;
    }

    public void RecordTurnUsage(Session session, string providerId, string modelId, long inputTokens, long outputTokens)
    {
        if (session.ContractPolicy is null)
            return;

        session.ContractAccumulatedCostUsd += ComputeTurnCostUsd(providerId, modelId, inputTokens, outputTokens);
    }

    public bool CancelContract(string contractId, SessionManager sessionManager, out Session? detachedSession)
    {
        detachedSession = sessionManager.TryGetActiveByContractId(contractId);
        if (detachedSession?.ContractPolicy is null)
            return false;

        AppendSnapshot(detachedSession, "cancelled");
        detachedSession.ContractPolicy = null;
        detachedSession.ContractAttachedAtUtc = null;
        detachedSession.ContractBaselineInputTokens = 0;
        detachedSession.ContractBaselineOutputTokens = 0;
        detachedSession.ContractBaselineToolCalls = 0;
        detachedSession.ContractAccumulatedCostUsd = 0m;
        return true;
    }

    public void DetachFromSession(Session session)
    {
        if (session.ContractPolicy is null)
            return;

        session.ContractPolicy = null;
        session.ContractAttachedAtUtc = null;
        session.ContractBaselineInputTokens = 0;
        session.ContractBaselineOutputTokens = 0;
        session.ContractBaselineToolCalls = 0;
        session.ContractAccumulatedCostUsd = 0m;
    }

    public void AppendSnapshot(
        Session session,
        string status,
        string? lifecycleState = null,
        string? verificationStatus = null,
        string? verificationSummary = null,
        IReadOnlyList<VerificationCheckResult>? verificationChecks = null)
    {
        if (session.ContractPolicy is null)
            return;

        _contractStore.Append(BuildSnapshot(
            session,
            status,
            lifecycleState,
            verificationStatus,
            verificationSummary,
            verificationChecks));
    }

    public async Task<(string VerificationStatus, string? VerificationSummary, IReadOnlyList<VerificationCheckResult> Checks)> EvaluateVerificationAsync(
        VerificationPolicy? policy,
        CancellationToken ct)
    {
        if (policy?.Checks is not { Length: > 0 })
        {
            return (
                AutomationVerificationStatuses.NotVerified,
                "Run completed but no verification policy was configured.",
                []);
        }

        var checks = new List<VerificationCheckResult>(policy.Checks.Length);
        using var httpClient = new HttpClient
        {
            Timeout = VerificationHttpTimeout
        };

        foreach (var check in policy.Checks)
        {
            checks.Add(await EvaluateCheckAsync(check, httpClient, ct));
        }

        var failed = checks.Where(static item => string.Equals(item.Status, AutomationVerificationStatuses.Failed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (failed.Length > 0)
        {
            return (
                AutomationVerificationStatuses.Failed,
                failed[0].Summary,
                checks);
        }

        var blocked = checks.Where(static item => string.Equals(item.Status, AutomationVerificationStatuses.Blocked, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (blocked.Length > 0)
        {
            return (
                AutomationVerificationStatuses.Blocked,
                blocked[0].Summary,
                checks);
        }

        var notVerified = checks.Where(static item => string.Equals(item.Status, AutomationVerificationStatuses.NotVerified, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (notVerified.Length > 0)
        {
            return (
                AutomationVerificationStatuses.NotVerified,
                notVerified[0].Summary,
                checks);
        }

        return (
            AutomationVerificationStatuses.Verified,
            "All verification checks passed.",
            checks);
    }

    /// <summary>
    /// Check if a session's contract cost budget has been exceeded.
    /// Returns (maxCost, currentCost, exceeded).
    /// </summary>
    public (decimal MaxCost, decimal CurrentCost, bool Exceeded) CheckCostBudget(
        string? sessionId,
        string channelId,
        string senderId,
        SessionManager sessionManager)
    {
        var session = !string.IsNullOrWhiteSpace(sessionId)
            ? sessionManager.TryGetActiveById(sessionId)
            : sessionManager.TryGetActive(channelId, senderId);
        if (session?.ContractPolicy is not { MaxCostUsd: > 0 } policy)
            return (0m, 0m, false);

        var currentCost = session.ContractAccumulatedCostUsd;

        if (policy.SoftCostWarningUsd > 0 && currentCost >= policy.SoftCostWarningUsd && currentCost < policy.MaxCostUsd)
        {
            _logger.LogWarning("Contract {ContractId} soft cost warning: {Current:C} / {Max:C}",
                policy.Id, currentCost, policy.MaxCostUsd);
        }

        return (policy.MaxCostUsd, currentCost, currentCost >= policy.MaxCostUsd);
    }

    public ContractExecutionSnapshot BuildSnapshot(
        Session session,
        string status,
        string? lifecycleState = null,
        string? verificationStatus = null,
        string? verificationSummary = null,
        IReadOnlyList<VerificationCheckResult>? verificationChecks = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ContractPolicy is null)
            throw new InvalidOperationException("Session does not have a contract policy.");

        var attachedAt = session.ContractAttachedAtUtc ?? session.ContractPolicy.CreatedAtUtc;
        var normalizedLifecycle = string.IsNullOrWhiteSpace(lifecycleState)
            ? (status is "completed" or "cancelled" or "budget_exceeded"
                ? AutomationLifecycleStates.Completed
                : AutomationLifecycleStates.Running)
            : lifecycleState;
        var normalizedVerification = string.IsNullOrWhiteSpace(verificationStatus)
            ? AutomationVerificationStatuses.NotRun
            : verificationStatus;
        return new ContractExecutionSnapshot
        {
            ContractId = session.ContractPolicy.Id,
            SessionId = session.Id,
            Status = status,
            AccumulatedCostUsd = session.ContractAccumulatedCostUsd,
            AccumulatedTokens = GetContractTokenUsage(session),
            ToolCallCount = GetContractToolCallUsage(session),
            ElapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - attachedAt).TotalSeconds),
            StartedAtUtc = attachedAt,
            EndedAtUtc = status is "completed" or "cancelled" or "budget_exceeded" ? DateTimeOffset.UtcNow : null,
            LifecycleState = normalizedLifecycle,
            VerificationStatus = normalizedVerification,
            VerificationSummary = verificationSummary,
            VerificationCompletedAtUtc = normalizedVerification == AutomationVerificationStatuses.NotRun ? null : DateTimeOffset.UtcNow,
            VerificationChecks = verificationChecks ?? []
        };
    }

    public ContractExecutionSnapshot? GetLatestSnapshot(string contractId, SessionManager sessionManager)
    {
        var snapshots = _contractStore.Query(contractId: contractId, limit: 1);
        var activeSession = sessionManager.TryGetActiveByContractId(contractId);
        if (activeSession?.ContractPolicy is not null)
            return BuildSnapshot(activeSession, status: "active");

        return snapshots.Count == 0 ? null : snapshots[0];
    }

    /// <summary>Query persisted contract snapshots.</summary>
    public ContractStatusResponse? GetContract(string contractId)
    {
        var snapshots = _contractStore.Query(contractId: contractId, limit: 1);
        if (snapshots.Count == 0)
            return null;

        // We don't persist the full policy in JSONL — the policy lives on the Session.
        // Return what we have from the snapshot.
        return new ContractStatusResponse
        {
            Policy = new ContractPolicy { Id = contractId },
            Snapshot = snapshots[0]
        };
    }

    /// <summary>List contract snapshots, optionally filtered by session.</summary>
    public ContractListResponse ListContracts(string? sessionId = null, int limit = 50)
    {
        var snapshots = _contractStore.Query(sessionId: sessionId, limit: limit);
        var items = snapshots.Select(s => new ContractStatusResponse
        {
            Policy = new ContractPolicy { Id = s.ContractId },
            Snapshot = s
        }).ToArray();

        return new ContractListResponse { Items = items };
    }

    private static long GetContractTokenUsage(Session session)
        => Math.Max(0, (session.TotalInputTokens - session.ContractBaselineInputTokens)
            + (session.TotalOutputTokens - session.ContractBaselineOutputTokens));

    private static int GetContractToolCallUsage(Session session)
        => Math.Max(0, CountToolCalls(session) - session.ContractBaselineToolCalls);

    private static void ValidateVerificationPolicy(VerificationPolicy? policy, List<string> errors, List<string> warnings)
    {
        if (policy is null)
            return;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < policy.Checks.Length; index++)
        {
            var check = policy.Checks[index];
            var label = string.IsNullOrWhiteSpace(check.Id) ? $"Verification check #{index + 1}" : $"Verification check '{check.Id}'";

            if (string.IsNullOrWhiteSpace(check.Id))
                warnings.Add($"{label} is missing an explicit Id and may be harder to troubleshoot.");
            else if (!seenIds.Add(check.Id))
                errors.Add($"{label} reuses a duplicate Id.");

            if (string.IsNullOrWhiteSpace(check.Kind))
            {
                errors.Add($"{label} is missing Kind.");
                continue;
            }

            switch (check.Kind)
            {
                case VerificationKinds.FileExists:
                    if (string.IsNullOrWhiteSpace(check.Path))
                        errors.Add($"{label} requires Path.");
                    break;
                case VerificationKinds.FileContains:
                    if (string.IsNullOrWhiteSpace(check.Path))
                        errors.Add($"{label} requires Path.");
                    if (string.IsNullOrWhiteSpace(check.Contains))
                        errors.Add($"{label} requires Contains.");
                    break;
                case VerificationKinds.HttpStatus:
                    if (string.IsNullOrWhiteSpace(check.Url))
                        errors.Add($"{label} requires Url.");
                    if (check.ExpectedStatusCode is null)
                        errors.Add($"{label} requires ExpectedStatusCode.");
                    break;
                case VerificationKinds.HttpBodyContains:
                    if (string.IsNullOrWhiteSpace(check.Url))
                        errors.Add($"{label} requires Url.");
                    if (string.IsNullOrWhiteSpace(check.Contains))
                        errors.Add($"{label} requires Contains.");
                    break;
                case VerificationKinds.OperatorConfirm:
                    break;
                default:
                    errors.Add($"{label} has unsupported Kind '{check.Kind}'.");
                    break;
            }
        }
    }

    private static async Task<VerificationCheckResult> EvaluateCheckAsync(
        VerificationCheckDefinition check,
        HttpClient httpClient,
        CancellationToken ct)
    {
        var checkId = string.IsNullOrWhiteSpace(check.Id) ? check.Kind : check.Id;
        var kind = check.Kind ?? "";
        try
        {
            switch (kind)
            {
                case VerificationKinds.FileExists:
                {
                    var path = check.Path!;
                    var exists = File.Exists(path);
                    return BuildCheckResult(
                        checkId,
                        kind,
                        exists ? AutomationVerificationStatuses.Verified : AutomationVerificationStatuses.NotVerified,
                        exists ? $"Verified file exists: {path}" : $"Expected file does not exist: {path}");
                }
                case VerificationKinds.FileContains:
                {
                    var path = check.Path!;
                    if (!File.Exists(path))
                    {
                        return BuildCheckResult(
                            checkId,
                            kind,
                            AutomationVerificationStatuses.NotVerified,
                            $"Expected file does not exist: {path}");
                    }

                    var contents = await File.ReadAllTextAsync(path, ct);
                    var contains = contents.Contains(check.Contains!, StringComparison.Ordinal);
                    return BuildCheckResult(
                        checkId,
                        kind,
                        contains ? AutomationVerificationStatuses.Verified : AutomationVerificationStatuses.NotVerified,
                        contains
                            ? $"Verified file '{path}' contains the expected text."
                            : $"File '{path}' did not contain the expected text.");
                }
                case VerificationKinds.HttpStatus:
                {
                    using var response = await httpClient.GetAsync(check.Url!, ct);
                    var matches = (int)response.StatusCode == check.ExpectedStatusCode;
                    return BuildCheckResult(
                        checkId,
                        kind,
                        matches ? AutomationVerificationStatuses.Verified : AutomationVerificationStatuses.NotVerified,
                        matches
                            ? $"Verified {check.Url} returned HTTP {check.ExpectedStatusCode}."
                            : $"Expected HTTP {check.ExpectedStatusCode} from {check.Url}, got {(int)response.StatusCode}.");
                }
                case VerificationKinds.HttpBodyContains:
                {
                    using var response = await httpClient.GetAsync(check.Url!, ct);
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var contains = body.Contains(check.Contains!, StringComparison.Ordinal);
                    return BuildCheckResult(
                        checkId,
                        kind,
                        contains ? AutomationVerificationStatuses.Verified : AutomationVerificationStatuses.NotVerified,
                        contains
                            ? $"Verified {check.Url} response body contains the expected text."
                            : $"Response body from {check.Url} did not contain the expected text.");
                }
                case VerificationKinds.OperatorConfirm:
                    return BuildCheckResult(
                        checkId,
                        kind,
                        AutomationVerificationStatuses.Blocked,
                        string.IsNullOrWhiteSpace(check.Prompt)
                            ? "Operator confirmation is required to verify this run."
                            : check.Prompt!);
                default:
                    return BuildCheckResult(
                        checkId,
                        kind,
                        AutomationVerificationStatuses.Failed,
                        $"Unsupported verification kind '{kind}'.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildCheckResult(
                checkId,
                kind,
                AutomationVerificationStatuses.Failed,
                $"Verification check '{checkId}' failed: {ex.Message}");
        }
    }

    private static VerificationCheckResult BuildCheckResult(string checkId, string kind, string status, string summary)
        => new()
        {
            CheckId = checkId,
            Kind = kind,
            Status = status,
            Summary = summary,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        };

    private static int CountToolCalls(Session session)
        => session.History
            .Where(static turn => turn.ToolCalls is { Count: > 0 })
            .Sum(static turn => turn.ToolCalls!.Count);
}

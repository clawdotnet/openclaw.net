using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal interface IHarnessVerifier
{
    string Name { get; }
    bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation);
    ValueTask<HarnessVerificationCheck> VerifyAsync(
        PlanExecuteVerifyRun run,
        ToolInvocation? invocation,
        CancellationToken ct);
}

internal sealed class PlanExecuteVerifyService : IPlanExecuteVerifyOrchestrator
{
    private readonly GatewayConfig _config;
    private readonly HarnessContractService _contracts;
    private readonly EvidenceBundleService _evidence;
    private readonly GovernanceLedgerService _governance;
    private readonly RuntimeEventStore _events;
    private readonly ILogger<PlanExecuteVerifyService> _logger;
    private readonly IReadOnlyList<IHarnessVerifier> _verifiers;
    private readonly ConcurrentDictionary<string, PlanExecuteVerifyRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ToolInvocation> _lastInvocations = new(StringComparer.Ordinal);

    public PlanExecuteVerifyService(
        GatewayConfig config,
        HarnessContractService contracts,
        EvidenceBundleService evidence,
        GovernanceLedgerService governance,
        RuntimeEventStore events,
        ILogger<PlanExecuteVerifyService> logger)
    {
        _config = config;
        _contracts = contracts;
        _evidence = evidence;
        _governance = governance;
        _events = events;
        _logger = logger;
        _verifiers =
        [
            new ToolOutcomeVerifier(),
            new ApprovalVerifier(),
            new ContractCompletenessVerifier(contracts),
            new SecurityPostureVerifier(config)
        ];
    }

    public async ValueTask<PlanExecuteVerifyDecision> EvaluateToolAsync(
        PlanExecuteVerifyToolContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled())
        {
            return new PlanExecuteVerifyDecision
            {
                Decision = PlanExecuteVerifyDecisionKinds.Proceed,
                Summary = "Plan-Execute-Verify mode is disabled."
            };
        }

        var risk = NormalizeRisk(context.ActionDescriptor.RiskLevel) ?? ToHarnessRisk(context.GovernanceDescriptor.RiskLevel);
        var triggers = ResolveTriggers(context, risk);
        if (triggers.Count == 0)
        {
            return new PlanExecuteVerifyDecision
            {
                Decision = PlanExecuteVerifyDecisionKinds.Proceed,
                RequiresPlanExecuteVerify = false,
                RiskLevel = risk,
                Summary = $"Tool '{context.ToolName}' does not match configured PEV triggers."
            };
        }

        var approvalRequired = context.ExistingApprovalRequired || RequiresApprovalForRisk(risk);
        var now = DateTimeOffset.UtcNow;
        var contract = await _contracts.CreateAsync(BuildContract(context, risk, approvalRequired, triggers, now), cancellationToken);
        var bundle = _config.Harness.PlanExecuteVerify.CreateEvidenceBundles
            ? await _evidence.CreateAsync(BuildEvidenceBundle(context, contract, risk, now), cancellationToken)
            : null;

        var status = approvalRequired ? PlanExecuteVerifyStatus.AwaitingApproval : PlanExecuteVerifyStatus.Executing;
        var run = new PlanExecuteVerifyRun
        {
            Id = $"pev_{Guid.NewGuid():N}"[..24],
            Status = status,
            Decision = approvalRequired ? PlanExecuteVerifyDecisionKinds.RequireApproval : PlanExecuteVerifyDecisionKinds.Proceed,
            HarnessContractId = contract.Id,
            EvidenceBundleId = bundle?.Id,
            SourceSessionId = context.Session.Id,
            ActorId = context.Session.SenderId,
            ChannelId = context.Session.ChannelId,
            SenderId = context.Session.SenderId,
            Goal = contract.Goal,
            ToolName = context.ToolName,
            RiskLevel = risk,
            ApprovalRequired = approvalRequired,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Warnings = contract.VerificationPlan.Count == 0 ? ["Contract has no verification plan."] : [],
            Recommendations = approvalRequired ? ["Use the existing approval flow to approve or reject before execution."] : []
        };
        Upsert(run);
        AppendEvent(run, "pev_run_created", "info", $"Created Plan-Execute-Verify run '{run.Id}' for tool '{context.ToolName}'.");

        return new PlanExecuteVerifyDecision
        {
            Decision = run.Decision,
            RequiresPlanExecuteVerify = true,
            RequiresApproval = approvalRequired,
            RiskLevel = risk,
            Summary = approvalRequired
                ? $"PEV contract '{contract.Id}' created and approval is required."
                : $"PEV contract '{contract.Id}' created.",
            Run = run
        };
    }

    public async ValueTask RecordApprovalDecisionAsync(
        PlanExecuteVerifyRun? run,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (run is null || !_runs.TryGetValue(run.Id, out var current))
            return;

        var updated = CopyRun(
            current,
            status: approved ? PlanExecuteVerifyStatus.Executing : PlanExecuteVerifyStatus.Rejected,
            decision: approved ? PlanExecuteVerifyDecisionKinds.Proceed : PlanExecuteVerifyDecisionKinds.Reject,
            approved: approved,
            completedAtUtc: approved ? current.CompletedAtUtc : DateTimeOffset.UtcNow);
        Upsert(updated);

        await _governance.CreateAsync(new GovernanceLedgerEntry
        {
            Id = $"gov_{Guid.NewGuid():N}"[..24],
            Decision = approved ? GovernanceDecisions.Approved : GovernanceDecisions.Rejected,
            Status = GovernanceDecisionStatuses.Active,
            Source = GovernanceLedgerSources.HarnessContract,
            ActionType = "plan_execute_verify",
            ToolName = current.ToolName,
            ActionSummary = approved
                ? $"Approved PEV run '{current.Id}'."
                : $"Rejected PEV run '{current.Id}'.",
            RiskLevel = current.RiskLevel,
            Scope = GovernanceScopes.Once,
            ScopeKey = current.Id,
            SessionId = current.SourceSessionId,
            HarnessContractId = current.HarnessContractId,
            EvidenceBundleId = current.EvidenceBundleId,
            ActorId = current.ActorId,
            ChannelId = current.ChannelId,
            SenderId = current.SenderId,
            DecidedBy = current.ActorId,
            DecisionReason = approved ? "approved through existing tool approval flow" : "rejected through existing tool approval flow",
            Tags = ["pev", "approval"],
            Metadata = new GovernanceLedgerMetadata
            {
                CorrelationId = current.Id
            }
        }, cancellationToken);

        if (current.EvidenceBundleId is not null)
        {
            await _evidence.AddItemAsync(current.EvidenceBundleId, new EvidenceItem
            {
                Kind = EvidenceItemKinds.Approval,
                Title = approved ? "PEV approval accepted" : "PEV approval rejected",
                Summary = approved ? "Existing approval flow allowed execution." : "Existing approval flow rejected execution.",
                Status = approved ? GovernanceDecisions.Approved : GovernanceDecisions.Rejected,
                ToolName = current.ToolName
            }, cancellationToken);
        }

        if (!approved && current.HarnessContractId is not null)
            await _contracts.MarkStatusAsync(current.HarnessContractId, HarnessContractStatus.Rejected, cancellationToken);
    }

    public async ValueTask<PlanExecuteVerifyRun?> CompleteToolAsync(
        PlanExecuteVerifyRun? run,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (run is null || !_runs.TryGetValue(run.Id, out var current))
            return run;

        if (current.EvidenceBundleId is not null)
            await _evidence.AddItemAsync(current.EvidenceBundleId, EvidenceBundleService.FromToolInvocation(invocation), cancellationToken);

        _lastInvocations[current.Id] = invocation;

        var verification = _config.Harness.PlanExecuteVerify.RunVerification
            ? await RunVerificationAsync(current, invocation, cancellationToken)
            : new HarnessVerificationResult
            {
                Status = HarnessVerificationStatus.Skipped,
                Summary = "Verification is disabled by PlanExecuteVerify.RunVerification."
            };

        var failed = string.Equals(verification.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase);
        var status = failed ? PlanExecuteVerifyStatus.Failed : PlanExecuteVerifyStatus.Verified;
        var decision = failed
            ? (_config.Harness.PlanExecuteVerify.AutoRollbackOnFailedVerification
                ? PlanExecuteVerifyDecisionKinds.Rollback
                : PlanExecuteVerifyDecisionKinds.Escalate)
            : PlanExecuteVerifyDecisionKinds.Proceed;
        var completed = CopyRun(
            current,
            status,
            decision,
            approved: current.Approved,
            verification,
            completedAtUtc: DateTimeOffset.UtcNow,
            recommendations: verification.Recommendations);
        Upsert(completed);

        if (current.HarnessContractId is not null)
            await _contracts.MarkStatusAsync(current.HarnessContractId, failed ? HarnessContractStatus.Failed : HarnessContractStatus.Verified, cancellationToken);

        if (current.EvidenceBundleId is not null)
        {
            await _evidence.AddCheckAsync(current.EvidenceBundleId, new EvidenceCheck
            {
                Name = "Plan-Execute-Verify result",
                Status = ToEvidenceStatus(verification.Status),
                Summary = verification.Summary,
                Details = string.Join("; ", verification.Checks.Select(static check => $"{check.Name}: {check.Status}"))
            }, cancellationToken);
        }

        AppendEvent(completed, "pev_run_completed", failed ? "warning" : "info", $"PEV run '{completed.Id}' completed with status '{completed.Status}'.");
        return completed;
    }

    public async ValueTask<PlanExecuteVerifyRun?> VerifyRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return null;

        _lastInvocations.TryGetValue(run.Id, out var invocation);
        var verification = await RunVerificationAsync(run, invocation, cancellationToken);
        var updated = CopyRun(
            run,
            status: string.Equals(verification.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase)
                ? PlanExecuteVerifyStatus.Failed
                : PlanExecuteVerifyStatus.Verified,
            decision: string.Equals(verification.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase)
                ? PlanExecuteVerifyDecisionKinds.Escalate
                : PlanExecuteVerifyDecisionKinds.Proceed,
            approved: run.Approved,
            verification: verification,
            completedAtUtc: DateTimeOffset.UtcNow,
            recommendations: verification.Recommendations);
        Upsert(updated);
        return updated;
    }

    public PlanExecuteVerifyRun? GetRun(string id)
        => _runs.TryGetValue(id, out var run) ? run : null;

    public IReadOnlyList<PlanExecuteVerifyRun> ListRuns(int limit = 100)
        => _runs.Values
            .OrderByDescending(static run => run.UpdatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();

    public PlanExecuteVerifyRun? CancelRun(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return null;

        var cancelled = CopyRun(
            run,
            PlanExecuteVerifyStatus.Cancelled,
            PlanExecuteVerifyDecisionKinds.Escalate,
            run.Approved,
            completedAtUtc: DateTimeOffset.UtcNow,
            recommendations: ["Review the linked contract and evidence before retrying."]);
        Upsert(cancelled);
        AppendEvent(cancelled, "pev_run_cancelled", "warning", $"PEV run '{cancelled.Id}' was cancelled by an operator.");
        return cancelled;
    }

    private async ValueTask<HarnessVerificationResult> RunVerificationAsync(
        PlanExecuteVerifyRun run,
        ToolInvocation? invocation,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var checks = new List<HarnessVerificationCheck>();
        foreach (var verifier in _verifiers)
        {
            if (!verifier.CanVerify(run, invocation))
                continue;

            checks.Add(await verifier.VerifyAsync(run, invocation, ct));
        }

        if (checks.Count == 0)
        {
            checks.Add(new HarnessVerificationCheck
            {
                Id = "verification.none",
                Name = "Verification availability",
                Status = HarnessVerificationStatus.Unknown,
                Required = false,
                Summary = "No verifier could evaluate this run."
            });
        }

        var failed = checks.Any(static check => check.Required && string.Equals(check.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase));
        var warnings = checks.Any(static check => string.Equals(check.Status, HarnessVerificationStatus.Warning, StringComparison.OrdinalIgnoreCase));
        var status = failed ? HarnessVerificationStatus.Failed : warnings ? HarnessVerificationStatus.Warning : HarnessVerificationStatus.Passed;

        return new HarnessVerificationResult
        {
            Status = status,
            Summary = failed
                ? "One or more required PEV verification checks failed."
                : warnings
                    ? "PEV verification passed with warnings."
                    : "PEV verification checks passed.",
            Checks = checks,
            Risks = checks
                .Where(static check => string.Equals(check.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase))
                .Select(static check => check.Summary)
                .ToArray(),
            UntestedAreas = checks
                .Where(static check => string.Equals(check.Status, HarnessVerificationStatus.Skipped, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(check.Status, HarnessVerificationStatus.Unknown, StringComparison.OrdinalIgnoreCase))
                .Select(static check => check.Name)
                .ToArray(),
            Recommendations = failed
                ? ["Revise the plan or escalate to an operator before retrying.", "Rollback only when an explicit safe rollback plan exists."]
                : warnings
                    ? ["Review warning checks before accepting high-impact work."]
                    : [],
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private bool IsEnabled()
        => _config.Harness.PlanExecuteVerify.Enabled ||
           string.Equals(_config.Harness.ExecutionMode, HarnessExecutionModes.PlanExecuteVerify, StringComparison.OrdinalIgnoreCase);

    private bool RequiresApprovalForRisk(string risk)
        => _config.Harness.PlanExecuteVerify.RequireApprovalForRisk
            .Any(item => string.Equals(item, risk, StringComparison.OrdinalIgnoreCase));

    private List<string> ResolveTriggers(PlanExecuteVerifyToolContext context, string risk)
    {
        var configured = _config.Harness.PlanExecuteVerify.ContractRequiredFor ?? [];
        bool Has(string value) => configured.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        var triggers = new List<string>();
        if (Has(PlanExecuteVerifyContractTriggers.HighRiskTools) && RiskRank(risk) >= RiskRank(HarnessContractRiskLevels.High))
            triggers.Add(PlanExecuteVerifyContractTriggers.HighRiskTools);
        if (Has(PlanExecuteVerifyContractTriggers.WriteTools) && (!context.GovernanceDescriptor.ReadOnly || context.ActionDescriptor.IsMutation))
            triggers.Add(PlanExecuteVerifyContractTriggers.WriteTools);
        if (Has(PlanExecuteVerifyContractTriggers.Shell) && IsShellLike(context.ToolName, context.GovernanceDescriptor))
            triggers.Add(PlanExecuteVerifyContractTriggers.Shell);
        if (Has(PlanExecuteVerifyContractTriggers.Browser) && IsBrowserLike(context.ToolName, context.GovernanceDescriptor))
            triggers.Add(PlanExecuteVerifyContractTriggers.Browser);
        if (Has(PlanExecuteVerifyContractTriggers.ExternalApi) &&
            (context.GovernanceDescriptor.CanAccessNetwork || context.GovernanceDescriptor.CanSendDataExternally))
            triggers.Add(PlanExecuteVerifyContractTriggers.ExternalApi);
        return triggers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private HarnessContract BuildContract(
        PlanExecuteVerifyToolContext context,
        string risk,
        bool approvalRequired,
        IReadOnlyList<string> triggers,
        DateTimeOffset now)
    {
        var action = string.IsNullOrWhiteSpace(context.ActionDescriptor.Action)
            ? context.ToolName
            : context.ActionDescriptor.Action;
        return new HarnessContract
        {
            Status = approvalRequired ? HarnessContractStatus.Proposed : HarnessContractStatus.Executing,
            Goal = BuildGoal(context),
            UserRequestSummary = LastUserMessage(context.Session),
            SourceSessionId = context.Session.Id,
            ActorId = context.Session.SenderId,
            ChannelId = context.Session.ChannelId,
            SenderId = context.Session.SenderId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            RiskLevel = risk,
            ApprovalRequired = approvalRequired
                ? HarnessContractApprovalRequirements.Required
                : HarnessContractApprovalRequirements.None,
            ApprovalReason = approvalRequired ? $"Risk level '{risk}' requires approval in PEV mode." : null,
            PlannedActions =
            [
                new HarnessContractAction
                {
                    Id = "action_1",
                    Title = $"Run tool '{context.ToolName}'",
                    Description = context.ActionDescriptor.Summary,
                    ToolName = context.ToolName,
                    ActionType = action,
                    RiskLevel = risk,
                    RequiresApproval = approvalRequired,
                    ReadSet = InferReadSet(context),
                    WriteSet = InferWriteSet(context),
                    ExpectedOutcome = "Tool completes successfully and verification checks pass.",
                    Status = approvalRequired ? PlanExecuteVerifyStatus.AwaitingApproval : PlanExecuteVerifyStatus.Executing
                }
            ],
            ReadSet = InferReadSet(context),
            WriteSet = InferWriteSet(context),
            ToolsRequired =
            [
                new HarnessContractToolRequirement
                {
                    ToolName = context.ToolName,
                    Purpose = context.ActionDescriptor.Summary,
                    RequiresApproval = approvalRequired,
                    ApprovalScope = approvalRequired ? GovernanceScopes.Once : null
                }
            ],
            VerificationPlan =
            [
                new HarnessContractVerificationStep
                {
                    Id = "tool_outcome",
                    Title = "Tool outcome completed",
                    Kind = "tool_outcome",
                    ToolName = context.ToolName,
                    ExpectedSignal = "Tool result status is completed.",
                    Required = true
                },
                new HarnessContractVerificationStep
                {
                    Id = "approval",
                    Title = "Required approvals satisfied",
                    Kind = "approval",
                    ExpectedSignal = approvalRequired ? "Approval was granted before execution." : "No approval required.",
                    Required = approvalRequired
                }
            ],
            RollbackPlan =
            [
                new HarnessContractRollbackStep
                {
                    Id = "operator_review",
                    Title = "Operator review before rollback",
                    Description = "Automatic rollback is disabled unless an explicit safe rollback plan is supplied."
                }
            ],
            SuccessCriteria = ["Required tool actions completed successfully.", "Required verification checks passed."],
            Tags = ["pev", .. triggers],
            Metadata = new HarnessContractMetadata
            {
                CreatedBy = "plan_execute_verify",
                Source = "tool_execution",
                CorrelationId = context.CorrelationId,
                Properties = new Dictionary<string, string>
                {
                    ["callId"] = context.CallId ?? "",
                    ["toolCategory"] = context.GovernanceDescriptor.Category,
                    ["triggers"] = string.Join(",", triggers)
                }
            }
        };
    }

    private static EvidenceBundle BuildEvidenceBundle(
        PlanExecuteVerifyToolContext context,
        HarnessContract contract,
        string risk,
        DateTimeOffset now)
        => new()
        {
            Title = $"PEV evidence for {context.ToolName}",
            Summary = $"Evidence for Plan-Execute-Verify run linked to contract '{contract.Id}'.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SourceSessionId = context.Session.Id,
            HarnessContractId = contract.Id,
            ToolCallId = context.CallId,
            ActorId = context.Session.SenderId,
            ChannelId = context.Session.ChannelId,
            SenderId = context.Session.SenderId,
            Confidence = EvidenceConfidenceLevels.Unknown,
            Risks = [new EvidenceRisk { RiskLevel = risk, Description = $"Tool '{context.ToolName}' classified as {risk} risk." }],
            Tags = ["pev", "tool_execution"],
            Metadata = new EvidenceBundleMetadata
            {
                CreatedBy = "plan_execute_verify",
                Source = "tool_execution",
                CorrelationId = context.CorrelationId
            }
        };

    private static IReadOnlyList<HarnessContractResourceRef> InferReadSet(PlanExecuteVerifyToolContext context)
        => InferResourceSet(context, write: false);

    private static IReadOnlyList<HarnessContractResourceRef> InferWriteSet(PlanExecuteVerifyToolContext context)
        => context.ActionDescriptor.IsMutation || !context.GovernanceDescriptor.ReadOnly
            ? InferResourceSet(context, write: true)
            : [];

    private static IReadOnlyList<HarnessContractResourceRef> InferResourceSet(PlanExecuteVerifyToolContext context, bool write)
    {
        var refs = new List<HarnessContractResourceRef>();
        var path = TryReadStringProperty(context.ArgumentsJson, "path")
                   ?? TryReadStringProperty(context.ArgumentsJson, "file")
                   ?? TryReadStringProperty(context.ArgumentsJson, "command")
                   ?? TryReadStringProperty(context.ArgumentsJson, "cmd");
        if (!string.IsNullOrWhiteSpace(path))
        {
            refs.Add(new HarnessContractResourceRef
            {
                Kind = context.GovernanceDescriptor.CanAccessFileSystem ? HarnessContractResourceKinds.File : HarnessContractResourceKinds.Unknown,
                Path = path,
                Description = write ? "PEV inferred write target." : "PEV inferred read target."
            });
        }
        else if (context.GovernanceDescriptor.CanAccessNetwork || context.GovernanceDescriptor.CanSendDataExternally)
        {
            refs.Add(new HarnessContractResourceRef
            {
                Kind = HarnessContractResourceKinds.ExternalApi,
                Description = write ? "PEV inferred external write target." : "PEV inferred external read target."
            });
        }

        return refs;
    }

    private static string? TryReadStringProperty(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildGoal(PlanExecuteVerifyToolContext context)
        => string.IsNullOrWhiteSpace(context.ActionDescriptor.Summary)
            ? $"Execute tool '{context.ToolName}' under Plan-Execute-Verify mode."
            : context.ActionDescriptor.Summary;

    private static string? LastUserMessage(Session session)
        => session.History.LastOrDefault(static turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;

    private static bool IsShellLike(string toolName, ToolGovernanceDescriptor descriptor)
        => descriptor.CanExecuteCode ||
           toolName.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
           toolName.Contains("process", StringComparison.OrdinalIgnoreCase) ||
           toolName.Contains("exec", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserLike(string toolName, ToolGovernanceDescriptor descriptor)
        => descriptor.Category.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
           toolName.Contains("browser", StringComparison.OrdinalIgnoreCase);

    private static string ToHarnessRisk(ToolGovernanceRiskLevel risk)
        => risk switch
        {
            ToolGovernanceRiskLevel.Critical => HarnessContractRiskLevels.Critical,
            ToolGovernanceRiskLevel.High => HarnessContractRiskLevels.High,
            ToolGovernanceRiskLevel.Medium => HarnessContractRiskLevels.Medium,
            _ => HarnessContractRiskLevels.Low
        };

    private static string? NormalizeRisk(string? risk)
        => string.IsNullOrWhiteSpace(risk)
            ? null
            : risk.Trim().ToLowerInvariant() switch
            {
                HarnessContractRiskLevels.Critical => HarnessContractRiskLevels.Critical,
                HarnessContractRiskLevels.High => HarnessContractRiskLevels.High,
                HarnessContractRiskLevels.Medium => HarnessContractRiskLevels.Medium,
                HarnessContractRiskLevels.Low => HarnessContractRiskLevels.Low,
                _ => null
            };

    private static int RiskRank(string risk)
        => NormalizeRisk(risk) switch
        {
            HarnessContractRiskLevels.Critical => 4,
            HarnessContractRiskLevels.High => 3,
            HarnessContractRiskLevels.Medium => 2,
            _ => 1
        };

    private static string ToEvidenceStatus(string status)
        => status switch
        {
            HarnessVerificationStatus.Passed => EvidenceCheckStatuses.Passed,
            HarnessVerificationStatus.Failed => EvidenceCheckStatuses.Failed,
            HarnessVerificationStatus.Warning => EvidenceCheckStatuses.Warning,
            HarnessVerificationStatus.Skipped => EvidenceCheckStatuses.Skipped,
            _ => EvidenceCheckStatuses.Unknown
        };

    private void Upsert(PlanExecuteVerifyRun run)
        => _runs[run.Id] = run;

    private void AppendEvent(PlanExecuteVerifyRun run, string action, string severity, string summary)
    {
        try
        {
            _events.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = run.SourceSessionId,
                ChannelId = run.ChannelId,
                SenderId = run.SenderId,
                CorrelationId = run.Id,
                Component = "harness",
                Action = action,
                Severity = severity,
                Summary = summary,
                Metadata = new Dictionary<string, string>
                {
                    ["pevRunId"] = run.Id,
                    ["contractId"] = run.HarnessContractId ?? "",
                    ["evidenceBundleId"] = run.EvidenceBundleId ?? "",
                    ["status"] = run.Status,
                    ["decision"] = run.Decision
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to append PEV runtime event for {RunId}.", run.Id);
        }
    }

    private static PlanExecuteVerifyRun CopyRun(
        PlanExecuteVerifyRun source,
        string status,
        string decision,
        bool approved,
        HarnessVerificationResult? verification = null,
        DateTimeOffset? completedAtUtc = null,
        IReadOnlyList<string>? recommendations = null)
        => new()
        {
            Id = source.Id,
            Status = status,
            Decision = decision,
            HarnessContractId = source.HarnessContractId,
            EvidenceBundleId = source.EvidenceBundleId,
            SourceSessionId = source.SourceSessionId,
            ActorId = source.ActorId,
            ChannelId = source.ChannelId,
            SenderId = source.SenderId,
            Goal = source.Goal,
            ToolName = source.ToolName,
            RiskLevel = source.RiskLevel,
            ApprovalRequired = source.ApprovalRequired,
            Approved = approved,
            StartedAtUtc = source.StartedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = completedAtUtc,
            Verification = verification ?? source.Verification,
            Warnings = source.Warnings,
            Recommendations = recommendations ?? source.Recommendations
        };
}

internal sealed class ToolOutcomeVerifier : IHarnessVerifier
{
    public string Name => "tool_outcome";

    public bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation) => invocation is not null;

    public ValueTask<HarnessVerificationCheck> VerifyAsync(PlanExecuteVerifyRun run, ToolInvocation? invocation, CancellationToken ct)
    {
        var passed = invocation is not null &&
                     string.Equals(invocation.ResultStatus, ToolResultStatuses.Completed, StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(invocation.FailureCode);
        return ValueTask.FromResult(new HarnessVerificationCheck
        {
            Id = "tool_outcome",
            Name = "Tool outcome",
            Status = passed ? HarnessVerificationStatus.Passed : HarnessVerificationStatus.Failed,
            Required = true,
            Summary = passed
                ? $"Tool '{run.ToolName}' completed successfully."
                : $"Tool '{run.ToolName}' did not complete successfully.",
            Details = invocation?.FailureMessage ?? invocation?.FailureCode
        });
    }
}

internal sealed class ApprovalVerifier : IHarnessVerifier
{
    public string Name => "approval";

    public bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation) => true;

    public ValueTask<HarnessVerificationCheck> VerifyAsync(PlanExecuteVerifyRun run, ToolInvocation? invocation, CancellationToken ct)
    {
        var passed = !run.ApprovalRequired || run.Approved;
        return ValueTask.FromResult(new HarnessVerificationCheck
        {
            Id = "approval",
            Name = "Approval",
            Status = passed ? HarnessVerificationStatus.Passed : HarnessVerificationStatus.Failed,
            Required = run.ApprovalRequired,
            Summary = passed
                ? "Approval requirements are satisfied."
                : "Approval was required but not recorded as approved."
        });
    }
}

internal sealed class ContractCompletenessVerifier(HarnessContractService contracts) : IHarnessVerifier
{
    public string Name => "contract_completeness";

    public bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation)
        => !string.IsNullOrWhiteSpace(run.HarnessContractId);

    public async ValueTask<HarnessVerificationCheck> VerifyAsync(PlanExecuteVerifyRun run, ToolInvocation? invocation, CancellationToken ct)
    {
        var contract = await contracts.GetAsync(run.HarnessContractId!, ct);
        if (contract is null)
        {
            return new HarnessVerificationCheck
            {
                Id = "contract_completeness",
                Name = "Contract completeness",
                Status = HarnessVerificationStatus.Failed,
                Required = true,
                Summary = "Linked harness contract could not be loaded."
            };
        }

        var missing = new List<string>();
        if (contract.SuccessCriteria.Count == 0) missing.Add("success criteria");
        if (contract.VerificationPlan.Count == 0) missing.Add("verification plan");
        if (contract.RollbackPlan.Count == 0) missing.Add("rollback plan");
        return new HarnessVerificationCheck
        {
            Id = "contract_completeness",
            Name = "Contract completeness",
            Status = missing.Count == 0 ? HarnessVerificationStatus.Passed : HarnessVerificationStatus.Warning,
            Required = false,
            Summary = missing.Count == 0
                ? "Harness contract includes success criteria, verification plan, and rollback plan."
                : $"Harness contract is missing {string.Join(", ", missing)}."
        };
    }
}

internal sealed class SecurityPostureVerifier(GatewayConfig config) : IHarnessVerifier
{
    public string Name => "security_posture";

    public bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation) => true;

    public ValueTask<HarnessVerificationCheck> VerifyAsync(PlanExecuteVerifyRun run, ToolInvocation? invocation, CancellationToken ct)
    {
        var publicBind = !string.Equals(config.BindAddress, "127.0.0.1", StringComparison.Ordinal) &&
                         !string.Equals(config.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(config.BindAddress, "::1", StringComparison.Ordinal);
        var unsafePublicApproval = publicBind && !config.Security.RequireRequesterMatchForHttpToolApproval;
        return ValueTask.FromResult(new HarnessVerificationCheck
        {
            Id = "security_posture",
            Name = "Security posture",
            Status = unsafePublicApproval ? HarnessVerificationStatus.Warning : HarnessVerificationStatus.Passed,
            Required = false,
            Summary = unsafePublicApproval
                ? "Public bind is configured without requester-matched HTTP tool approvals."
                : "No PEV-specific public-bind approval warning was detected."
        });
    }
}

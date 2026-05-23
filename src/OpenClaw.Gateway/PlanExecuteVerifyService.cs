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
    private const int MaxStoredRuns = 1_000;
    private static readonly TimeSpan RunRetention = TimeSpan.FromHours(24);

    private readonly GatewayConfig _config;
    private readonly HarnessContractService _contracts;
    private readonly EvidenceBundleService _evidence;
    private readonly GovernanceLedgerService _governance;
    private readonly RuntimeEventStore _events;
    private readonly ILogger<PlanExecuteVerifyService> _logger;
    private readonly IReadOnlyList<IHarnessVerifier> _verifiers;
    private readonly ConcurrentDictionary<string, PlanExecuteVerifyRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RetainedToolInvocation> _lastInvocations = new(StringComparer.Ordinal);

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
            new SecurityPostureVerifier(config),
            new RegressionVerifier(config)
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
        if (_config.Harness.PlanExecuteVerify.MaxPlanActions < 1)
        {
            return new PlanExecuteVerifyDecision
            {
                Decision = PlanExecuteVerifyDecisionKinds.Reject,
                RequiresPlanExecuteVerify = true,
                RiskLevel = risk,
                Summary = "Plan-Execute-Verify MaxPlanActions must be at least 1."
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

        if (current.HarnessContractId is not null)
        {
            if (approved)
            {
                await _contracts.MarkStatusAsync(current.HarnessContractId, HarnessContractStatus.Approved, cancellationToken);
                await _contracts.MarkStatusAsync(current.HarnessContractId, HarnessContractStatus.Executing, cancellationToken);
            }
            else
            {
                await _contracts.MarkStatusAsync(current.HarnessContractId, HarnessContractStatus.Rejected, cancellationToken);
            }
        }
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

        _lastInvocations[current.Id] = new RetainedToolInvocation(invocation, DateTimeOffset.UtcNow);

        if (IsTerminalNonExecutionStatus(current.Status))
            return current;

        var verification = _config.Harness.PlanExecuteVerify.RunVerification
            ? await RunVerificationAsync(current, invocation, cancellationToken)
            : new HarnessVerificationResult
            {
                Status = HarnessVerificationStatus.Skipped,
                Summary = "Verification is disabled by PlanExecuteVerify.RunVerification.",
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

        return await ApplyVerificationResultAsync(
            current,
            verification,
            eventAction: "pev_run_completed",
            eventSummary: "PEV run completed",
            cancellationToken);
    }

    public async ValueTask<PlanExecuteVerifyRun?> VerifyRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return null;
        if (IsTerminalNonExecutionStatus(run.Status))
            return run;

        _lastInvocations.TryGetValue(run.Id, out var retainedInvocation);
        var verification = await RunVerificationAsync(run, retainedInvocation?.Invocation, cancellationToken);
        return await ApplyVerificationResultAsync(
            run,
            verification,
            eventAction: "pev_run_verified",
            eventSummary: "PEV run manually verified",
            cancellationToken);
    }

    public PlanExecuteVerifyRun? GetRun(string id)
        => _runs.TryGetValue(id, out var run) ? run : null;

    public IReadOnlyList<PlanExecuteVerifyRun> ListRuns(int limit = 100)
        => _runs.Values
            .OrderByDescending(static run => run.UpdatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();

    public async ValueTask<PlanExecuteVerifyRun?> CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return null;
        if (IsTerminalStatus(run.Status))
            return run;

        var cancelled = CopyRun(
            run,
            PlanExecuteVerifyStatus.Cancelled,
            PlanExecuteVerifyDecisionKinds.Escalate,
            run.Approved,
            completedAtUtc: DateTimeOffset.UtcNow,
            recommendations: ["Review the linked contract and evidence before retrying."]);
        Upsert(cancelled);
        if (cancelled.HarnessContractId is not null)
            await _contracts.MarkStatusAsync(cancelled.HarnessContractId, HarnessContractStatus.Cancelled, cancellationToken);
        if (cancelled.EvidenceBundleId is not null)
        {
            await _evidence.AddCheckAsync(cancelled.EvidenceBundleId, new EvidenceCheck
            {
                Name = "Plan-Execute-Verify cancellation",
                Kind = EvidenceItemKinds.RuntimeEvent,
                Required = false,
                Status = EvidenceCheckStatuses.Warning,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Summary = "PEV run was cancelled by an operator."
            }, cancellationToken);
        }
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
        var maxSteps = Math.Clamp(_config.Harness.PlanExecuteVerify.MaxVerificationSteps, 1, _verifiers.Count);
        var applicableVerifiers = _verifiers.Where(verifier => verifier.CanVerify(run, invocation)).ToArray();
        foreach (var verifier in applicableVerifiers.Take(maxSteps))
        {
            checks.Add(await verifier.VerifyAsync(run, invocation, ct));
        }

        if (applicableVerifiers.Length > maxSteps)
        {
            checks.Add(new HarnessVerificationCheck
            {
                Id = "verification.omitted",
                Name = "Omitted verification checks",
                Status = HarnessVerificationStatus.Warning,
                Required = false,
                Summary = $"PEV MaxVerificationSteps limited verification to {maxSteps} checks.",
                Details = string.Join(", ", applicableVerifiers.Skip(maxSteps).Select(static verifier => verifier.Name))
            });
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
        if (Has(PlanExecuteVerifyContractTriggers.MultiToolWorkflows) && context.ToolCallCount > 1)
            triggers.Add(PlanExecuteVerifyContractTriggers.MultiToolWorkflows);
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
        var path = TryReadStringProperty(context.ArgumentsJson, "path", "file");
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

    private static string? TryReadStringProperty(string json, params string[] properties)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in properties.Where(property =>
                         doc.RootElement.TryGetProperty(property, out var value) &&
                         value.ValueKind == JsonValueKind.String))
            {
                return doc.RootElement.GetProperty(property).GetString();
            }

            return null;
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

    private async ValueTask<PlanExecuteVerifyRun> ApplyVerificationResultAsync(
        PlanExecuteVerifyRun run,
        HarnessVerificationResult verification,
        string eventAction,
        string eventSummary,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(run.Id, out var latest))
            return run;
        if (IsTerminalNonExecutionStatus(latest.Status))
            return latest;

        var failed = string.Equals(verification.Status, HarnessVerificationStatus.Failed, StringComparison.OrdinalIgnoreCase);
        var skipped = string.Equals(verification.Status, HarnessVerificationStatus.Skipped, StringComparison.OrdinalIgnoreCase);
        var status = skipped
            ? PlanExecuteVerifyStatus.Escalated
            : failed
                ? PlanExecuteVerifyStatus.Failed
                : PlanExecuteVerifyStatus.Verified;
        var decision = failed
            ? (_config.Harness.PlanExecuteVerify.AutoRollbackOnFailedVerification
                ? PlanExecuteVerifyDecisionKinds.Rollback
                : PlanExecuteVerifyDecisionKinds.Escalate)
            : skipped
                ? PlanExecuteVerifyDecisionKinds.Escalate
            : PlanExecuteVerifyDecisionKinds.Proceed;
        var completed = CopyRun(
            latest,
            status,
            decision,
            approved: latest.Approved,
            verification,
            completedAtUtc: DateTimeOffset.UtcNow,
            recommendations: verification.Recommendations);
        Upsert(completed);

        if (completed.HarnessContractId is not null && !skipped)
            await _contracts.MarkStatusAsync(completed.HarnessContractId, failed ? HarnessContractStatus.Failed : HarnessContractStatus.Verified, cancellationToken);

        await AddVerificationEvidenceAsync(completed, verification, cancellationToken);
        AppendEvent(completed, eventAction, failed ? "warning" : "info", $"{eventSummary} with status '{completed.Status}'.");
        return completed;
    }

    private async ValueTask AddVerificationEvidenceAsync(
        PlanExecuteVerifyRun run,
        HarnessVerificationResult verification,
        CancellationToken cancellationToken)
    {
        if (run.EvidenceBundleId is null)
            return;

        await _evidence.AddCheckAsync(run.EvidenceBundleId, new EvidenceCheck
        {
            Name = "Plan-Execute-Verify result",
            Kind = EvidenceItemKinds.VerificationResult,
            Status = ToEvidenceStatus(verification.Status),
            StartedAtUtc = verification.StartedAtUtc,
            CompletedAtUtc = verification.CompletedAtUtc,
            Summary = verification.Summary,
            Details = string.Join("; ", verification.Checks.Select(static check => $"{check.Name}: {check.Status}"))
        }, cancellationToken);
    }

    private void Upsert(PlanExecuteVerifyRun run)
    {
        _runs[run.Id] = run;
        PruneRetainedState(DateTimeOffset.UtcNow);
    }

    private void PruneRetainedState(DateTimeOffset now)
    {
        var cutoff = now - RunRetention;
        foreach (var (id, run) in _runs)
        {
            if (IsTerminalStatus(run.Status) && run.UpdatedAtUtc < cutoff)
            {
                _runs.TryRemove(id, out _);
                _lastInvocations.TryRemove(id, out _);
            }
        }

        foreach (var (id, retained) in _lastInvocations)
        {
            if (retained.UpdatedAtUtc < cutoff || !_runs.ContainsKey(id))
                _lastInvocations.TryRemove(id, out _);
        }

        if (_runs.Count <= MaxStoredRuns)
            return;

        var overflow = _runs.Count - MaxStoredRuns;
        foreach (var run in _runs.Values
                     .Where(static run => IsTerminalStatus(run.Status))
                     .OrderBy(static run => run.UpdatedAtUtc)
                     .Take(overflow))
        {
            _runs.TryRemove(run.Id, out _);
            _lastInvocations.TryRemove(run.Id, out _);
        }
    }

    private static bool IsTerminalNonExecutionStatus(string status)
        => string.Equals(status, PlanExecuteVerifyStatus.Rejected, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, PlanExecuteVerifyStatus.Cancelled, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status)
        => IsTerminalNonExecutionStatus(status) ||
           string.Equals(status, PlanExecuteVerifyStatus.Verified, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, PlanExecuteVerifyStatus.Failed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, PlanExecuteVerifyStatus.RolledBack, StringComparison.OrdinalIgnoreCase);

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

    private sealed record RetainedToolInvocation(ToolInvocation Invocation, DateTimeOffset UpdatedAtUtc);
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

internal sealed class RegressionVerifier(GatewayConfig config) : IHarnessVerifier
{
    public string Name => "regression";

    public bool CanVerify(PlanExecuteVerifyRun run, ToolInvocation? invocation)
        => config.Harness.PlanExecuteVerify.RegressionCategories.Length > 0;

    public ValueTask<HarnessVerificationCheck> VerifyAsync(PlanExecuteVerifyRun run, ToolInvocation? invocation, CancellationToken ct)
        => ValueTask.FromResult(new HarnessVerificationCheck
        {
            Id = "regression",
            Name = "Harness regression suite",
            Status = HarnessVerificationStatus.Skipped,
            Required = false,
            Summary = "Regression categories are configured; run `openclaw harness test` for the selected categories before accepting the work.",
            Details = string.Join(", ", config.Harness.PlanExecuteVerify.RegressionCategories)
        });
}

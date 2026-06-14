using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Cli;

internal static class SkillCommands
{
    private const string EnvWorkspace = "OPENCLAW_WORKSPACE";
    private const string DefaultMetaRunProposalRollbackReason = "meta_run_proposal_operator_rollback";

    public static async Task<int> RunAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "inspect" => await InspectAsync(rest),
            "install" => await InstallAsync(rest),
            "list" or "ls" => ListInstalled(rest),
            "catalog" => ListCatalog(rest),
            "create" => CreateSkillScaffold(rest),
            "proposals" => await ListReadOnlyProposalsEntryAsync(rest),
            "meta-runs" => await ListMetaRunsAsync(rest),
            _ => UnknownSubcommand(subcommand, asJson)
        };
    }

    private static Task<int> ListReadOnlyProposalsEntryAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        if (args.Length > 0
            && (string.Equals(args[0], "accept", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "dismiss", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "rollback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "change", StringComparison.OrdinalIgnoreCase)))
        {
            WriteSkillsCommandError(
                asJson,
                "skills proposals",
                "read_only_alias_lifecycle_action",
                "openclaw skills proposals is a read-only entry. Use `openclaw skills meta-runs proposals <accept|dismiss|rollback|change>` for lifecycle actions.");
            return Task.FromResult(2);
        }

        if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
            return ShowMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalEntrypoints.ReadOnlyAlias);

        return ListMetaRunProposalsAsync(args, MetaRunProposalEntrypoints.ReadOnlyAlias);
    }

    private static async Task<int> ListMetaRunsAsync(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
            return await PreviewMetaRunReplayAsync(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "reconstruct", StringComparison.OrdinalIgnoreCase))
            return await ReconstructMetaRunReplayAsync(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "proposals", StringComparison.OrdinalIgnoreCase))
            return await HandleMetaRunProposalsAsync(args.Skip(1).ToArray());

        var asJson = args.Contains("--json");
        var verbose = args.Contains("--verbose");
        var requestedRunId = GetOptionValue(args, "--run");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs",
                "missing_session_id",
                "Usage: openclaw skills meta-runs <session-id> [--storage <path>] [--limit <count>] [--run <run-id>] [--verbose] [--json]");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var limit = ParseIntOption(args, "--limit") ?? 20;
        limit = Math.Clamp(limit, 1, 100);

        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            if (session.MetaRunHistory.Count == 0)
            {
                if (asJson)
                {
                    WriteMetaRunsJson(sessionId, session.MetaRunHistory.Count, []);
                    return 0;
                }

                Console.WriteLine($"Session: {sessionId}");
                Console.WriteLine("Meta runs: 0");
                return 0;
            }

            var runs = session.MetaRunHistory
                .Where(run => string.IsNullOrWhiteSpace(requestedRunId) || string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal))
                .OrderByDescending(static run => run.CompletedAtUtc)
                .Take(limit)
                .ToArray();

            if (!string.IsNullOrWhiteSpace(requestedRunId) && runs.Length == 0)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs", "run_not_found", $"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            if (asJson)
            {
                WriteMetaRunsJson(sessionId, session.MetaRunHistory.Count, runs);
                return 0;
            }

            Console.WriteLine($"Session: {sessionId}");
            Console.WriteLine($"Meta runs: {session.MetaRunHistory.Count} total, showing {runs.Length}");
            Console.WriteLine();

            foreach (var run in runs)
            {
                Console.WriteLine($"Run: {run.RunId}");
                Console.WriteLine($"Skill: {run.SkillName}");
                Console.WriteLine($"Status: {run.Status}");
                Console.WriteLine($"Started: {run.StartedAtUtc:O}");
                Console.WriteLine($"Completed: {run.CompletedAtUtc:O}");
                Console.WriteLine($"Steps: {run.StepResults.Count}");
                if (!string.IsNullOrWhiteSpace(run.ErrorCode))
                    Console.WriteLine($"Error code: {run.ErrorCode}");
                if (!string.IsNullOrWhiteSpace(run.Error))
                    Console.WriteLine($"Error: {run.Error}");
                if (!string.IsNullOrWhiteSpace(run.FinalText))
                    Console.WriteLine($"Final text: {run.FinalText}");

                if (verbose)
                {
                    foreach (var step in run.StepResults)
                    {
                        var line = $"- Step: {step.Id} | kind={step.Kind} | status={step.Status} | duration_ms={step.DurationMs:0.###}";
                        if (!string.IsNullOrWhiteSpace(step.FailureCode))
                            line += $" | failure_code={step.FailureCode}";
                        if (step.Continued)
                            line += " | continued=true";
                        Console.WriteLine(line);
                    }

                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine();
                }
            }

            return 0;
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static Task<int> HandleMetaRunProposalsAsync(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "accept", StringComparison.OrdinalIgnoreCase))
            return ReviewMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalReviewStatuses.Accepted, allowReason: false);
        if (args.Length > 0 && string.Equals(args[0], "dismiss", StringComparison.OrdinalIgnoreCase))
            return ReviewMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalReviewStatuses.Dismissed, allowReason: true);
        if (args.Length > 0 && string.Equals(args[0], "rollback", StringComparison.OrdinalIgnoreCase))
            return RollbackMetaRunProposalAsync(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "change", StringComparison.OrdinalIgnoreCase))
            return ChangeMetaRunProposalAsync(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
            return ShowMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalEntrypoints.MetaRuns);

        return ListMetaRunProposalsAsync(args, MetaRunProposalEntrypoints.MetaRuns);
    }

    private static async Task<int> ListMetaRunProposalsAsync(string[] args, string entrypoint)
    {
        var asJson = args.Contains("--json");
        var requestedRunId = GetOptionValue(args, "--run");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals",
                "missing_session_id",
                "Usage: openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--storage <path>] [--json]");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var learningProposalStore = OpenLearningProposalStore(storagePath);
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var proposals = BuildDerivedProposals(session, requestedRunId);
            if (!string.IsNullOrWhiteSpace(requestedRunId)
                && !session.MetaRunHistory.Any(run => string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal)))
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals", "run_not_found", $"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            var reviews = await LoadMetaRunLearningReviewsAsync(learningProposalStore, sessionId, CancellationToken.None);
            proposals = ApplyReviewSummary(proposals, reviews);

            var response = new MetaRunDerivedProposalListResponse
            {
                SessionId = sessionId,
                Entrypoint = entrypoint,
                ReadOnlyAlias = string.Equals(entrypoint, MetaRunProposalEntrypoints.ReadOnlyAlias, StringComparison.Ordinal),
                Count = proposals.Length,
                Proposals = proposals
            };

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.MetaRunDerivedProposalListResponse));
            }
            else
            {
                WriteDerivedProposalListText(response);
            }

            return 0;
        }
        finally
        {
            switch (learningProposalStore)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> ShowMetaRunProposalAsync(string[] args, string entrypoint)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals show",
                "missing_session_id",
                "Usage: openclaw skills meta-runs proposals show <session-id> --proposal <id> [--storage <path>] [--json]");
            return 2;
        }

        var proposalId = GetOptionValue(args, "--proposal");
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals show",
                "missing_proposal_id",
                "--proposal <id> is required for meta-runs proposals show.");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var learningProposalStore = OpenLearningProposalStore(storagePath);
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals show", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var summary = BuildDerivedProposals(session, requestedRunId: null)
                .FirstOrDefault(item => string.Equals(item.Id, proposalId, StringComparison.Ordinal));
            if (summary is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals show", "proposal_not_found", $"Proposal '{proposalId}' not found in session '{sessionId}'.");
                return 1;
            }

            var run = session.MetaRunHistory.First(run => string.Equals(run.RunId, summary.RunId, StringComparison.Ordinal));
            var durableProposal = await GetMetaRunLearningProposalAsync(learningProposalStore, sessionId, summary.Id, CancellationToken.None);
            var review = durableProposal is null
                ? null
                : ToMetaRunProposalReviewRecord(durableProposal, sessionId, summary.Id);
            var detail = new MetaRunDerivedProposalDetailResponse
            {
                SessionId = sessionId,
                Entrypoint = entrypoint,
                ReadOnlyAlias = string.Equals(entrypoint, MetaRunProposalEntrypoints.ReadOnlyAlias, StringComparison.Ordinal),
                Proposal = ApplyReviewDetail(
                    BuildDerivedProposalDetail(summary, run, session.MetaExecutionCheckpoint),
                    review,
                    BuildMetaRunProposalProvenanceDetail(durableProposal),
                    BuildMetaRunProposalLifecycleDetail(durableProposal),
                    BuildMetaRunProposalAuditDetail(durableProposal),
                    BuildMetaRunProposalProvenanceHistory(durableProposal))
            };

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(detail, CoreJsonContext.Default.MetaRunDerivedProposalDetailResponse));
            }
            else
            {
                WriteDerivedProposalDetailText(detail);
            }

            return 0;
        }
        finally
        {
            switch (learningProposalStore)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> ReviewMetaRunProposalAsync(string[] args, string targetStatus, bool allowReason)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        var action = string.Equals(targetStatus, MetaRunProposalReviewStatuses.Accepted, StringComparison.Ordinal)
            ? MetaRunProposalActions.Accept
            : MetaRunProposalActions.Dismiss;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                $"skills meta-runs proposals {action}",
                "missing_session_id",
                $"Usage: openclaw skills meta-runs proposals {action} <session-id> --proposal <id> [--storage <path>] [--json]");
            return 2;
        }

        var proposalId = GetOptionValue(args, "--proposal");
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            WriteSkillsCommandError(
                asJson,
                $"skills meta-runs proposals {action}",
                "missing_proposal_id",
                $"--proposal <id> is required for meta-runs proposals {action}.");
            return 2;
        }

        var reason = GetOptionValue(args, "--reason");
        if (!allowReason && !string.IsNullOrWhiteSpace(reason))
        {
            WriteSkillsCommandError(
                asJson,
                $"skills meta-runs proposals {action}",
                "unsupported_reason",
                "--reason is only supported for meta-runs proposals dismiss.");
            return 2;
        }

        var operatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        if (!MetaRunProposalPolicy.CanMutate(operatorId))
        {
            WriteSkillsCommandError(
                asJson,
                $"skills meta-runs proposals {action}",
                "permission_denied",
                "Proposal mutation requires OPENCLAW_OPERATOR_ID.");
            return 1;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var learningProposalStore = OpenLearningProposalStore(storagePath);
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, $"skills meta-runs proposals {action}", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var proposal = BuildDerivedProposals(session, requestedRunId: null)
                .FirstOrDefault(item => string.Equals(item.Id, proposalId, StringComparison.Ordinal));
            if (proposal is null)
            {
                WriteSkillsCommandError(asJson, $"skills meta-runs proposals {action}", "proposal_not_found", $"Proposal '{proposalId}' not found in session '{sessionId}'.");
                return 1;
            }

            var durableProposalId = BuildMetaRunProposalDurableId(sessionId, proposalId);
            var existing = await learningProposalStore.GetProposalAsync(durableProposalId, CancellationToken.None);
            var alreadyReviewed = false;
            var lifecycleStatus = MapReviewStatusToLearningProposalStatus(targetStatus);
            var currentLifecycleStatus = existing?.Status ?? LearningProposalStatus.Pending;
            MetaRunProposalReviewRecord record;
            if (existing is null || string.Equals(existing.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                if (!MetaRunProposalPolicy.IsAllowedActionTransition(action, currentLifecycleStatus, lifecycleStatus))
                {
                    WriteSkillsCommandError(
                        asJson,
                        $"skills meta-runs proposals {action}",
                        "invalid_lifecycle_transition",
                        $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': {currentLifecycleStatus} -> {lifecycleStatus}.");
                    return 1;
                }

                var reviewedAtUtc = DateTimeOffset.UtcNow;
                var runSnapshot = session.MetaRunHistory.First(run => string.Equals(run.RunId, proposal.RunId, StringComparison.Ordinal));
                var checkpointSnapshot = string.Equals(runSnapshot.Status, "paused", StringComparison.OrdinalIgnoreCase)
                    && session.MetaExecutionCheckpoint is not null
                    && string.Equals(session.MetaExecutionCheckpoint.SkillName, runSnapshot.SkillName, StringComparison.OrdinalIgnoreCase)
                    ? session.MetaExecutionCheckpoint
                    : null;

                var metadata = BuildMetaRunProposalMetadata(existing, sessionId, proposal);
                if (!string.IsNullOrWhiteSpace(reason))
                    metadata[MetaRunProposalMetadata.Reason] = reason!;

                var durableRecord = BuildUpdatedMetaRunLearningProposal(
                    existing,
                    proposal,
                    sessionId,
                    lifecycleStatus,
                    reviewedAtUtc,
                    reviewedAtUtc,
                    allowReason ? reason : null,
                    rolledBack: false,
                    rolledBackAtUtc: null,
                    rollbackReason: null,
                    metadata);

                PopulateMetaRunProposalProvenanceMetadata(durableRecord.Metadata, runSnapshot, checkpointSnapshot, reviewedAtUtc);
                AppendMetaRunProposalTransitionMetadata(
                    durableRecord.Metadata,
                    string.Equals(targetStatus, MetaRunProposalReviewStatuses.Accepted, StringComparison.Ordinal)
                        ? MetaRunProposalActions.Accept
                        : MetaRunProposalActions.Dismiss,
                    existing?.Status ?? LearningProposalStatus.Pending,
                    lifecycleStatus,
                    reviewedAtUtc,
                    operatorId,
                    allowReason ? reason : null);
                await learningProposalStore.SaveProposalAsync(durableRecord, CancellationToken.None);

                record = new MetaRunProposalReviewRecord
                {
                    SessionId = sessionId,
                    ProposalId = proposalId,
                    ReviewStatus = targetStatus,
                    Reason = allowReason ? reason : null,
                    ReviewedAtUtc = reviewedAtUtc
                };
            }
            else if (string.Equals(existing.Status, lifecycleStatus, StringComparison.OrdinalIgnoreCase))
            {
                alreadyReviewed = true;
                lifecycleStatus = existing.Status;
                record = ToMetaRunProposalReviewRecord(existing, sessionId, proposalId);
            }
            else
            {
                WriteSkillsCommandError(asJson, $"skills meta-runs proposals {action}", "proposal_already_reviewed", $"Proposal '{proposalId}' in session '{sessionId}' is already reviewed as {MapLearningProposalStatusToReviewStatus(existing.Status)}.");
                return 1;
            }

            var responseAudit = alreadyReviewed
                ? BuildMetaRunProposalAuditDetail(existing)
                : new MetaRunProposalAuditDetail
                {
                    ActorId = operatorId,
                    ChangedAtUtc = record.ReviewedAtUtc,
                    TransitionAction = action
                };

            var response = new MetaRunProposalReviewMutationResponse
            {
                SessionId = sessionId,
                ProposalId = proposalId,
                ReviewStatus = record.ReviewStatus,
                LifecycleStatus = lifecycleStatus,
                AlreadyReviewed = alreadyReviewed,
                ReviewedAtUtc = record.ReviewedAtUtc,
                Reason = record.Reason,
                Audit = responseAudit
            };

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.MetaRunProposalReviewMutationResponse));
            }
            else
            {
                WriteProposalReviewMutationText(response);
            }

            return 0;
        }
        finally
        {
            switch (learningProposalStore)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> RollbackMetaRunProposalAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals rollback",
                "missing_session_id",
                "Usage: openclaw skills meta-runs proposals rollback <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]");
            return 2;
        }

        var proposalId = GetOptionValue(args, "--proposal");
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals rollback",
                "missing_proposal_id",
                "--proposal <id> is required for meta-runs proposals rollback.");
            return 2;
        }

        var operatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        if (!MetaRunProposalPolicy.CanMutate(operatorId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals rollback",
                "permission_denied",
                "Proposal mutation requires OPENCLAW_OPERATOR_ID.");
            return 1;
        }

        var reason = GetOptionValue(args, "--reason");
        var storagePath = GetOptionValue(args, "--storage");
        var learningProposalStore = OpenLearningProposalStore(storagePath);
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals rollback", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var proposal = BuildDerivedProposals(session, requestedRunId: null)
                .FirstOrDefault(item => string.Equals(item.Id, proposalId, StringComparison.Ordinal));
            if (proposal is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals rollback", "proposal_not_found", $"Proposal '{proposalId}' not found in session '{sessionId}'.");
                return 1;
            }

            var durableProposalId = BuildMetaRunProposalDurableId(sessionId, proposalId);
            var existing = await learningProposalStore.GetProposalAsync(durableProposalId, CancellationToken.None);
            var lifecycleStatus = LearningProposalStatus.RolledBack;
            var currentLifecycleStatus = existing?.Status ?? LearningProposalStatus.Pending;
            var alreadyReviewed = false;
            MetaRunProposalReviewRecord record;

            if (existing is null || string.Equals(existing.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals rollback", "invalid_lifecycle_transition", $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': pending -> {LearningProposalStatus.RolledBack}. Only approved or rejected proposals can be rolled back.");
                return 1;
            }

            if (string.Equals(existing.Status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase))
            {
                alreadyReviewed = true;
                lifecycleStatus = existing.Status;
                record = ToMetaRunProposalReviewRecord(existing, sessionId, proposalId);
            }
            else if (string.Equals(existing.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(existing.Status, LearningProposalStatus.Rejected, StringComparison.OrdinalIgnoreCase))
            {
                if (!MetaRunProposalPolicy.IsAllowedActionTransition(MetaRunProposalActions.Rollback, currentLifecycleStatus, lifecycleStatus))
                {
                    WriteSkillsCommandError(
                        asJson,
                        "skills meta-runs proposals rollback",
                        "invalid_lifecycle_transition",
                        $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': {currentLifecycleStatus} -> {lifecycleStatus}. Only approved or rejected proposals can be rolled back.");
                    return 1;
                }

                var reviewedAtUtc = DateTimeOffset.UtcNow;
                var rollbackReason = string.IsNullOrWhiteSpace(reason) ? DefaultMetaRunProposalRollbackReason : reason!;
                var runSnapshot = session.MetaRunHistory.First(run => string.Equals(run.RunId, proposal.RunId, StringComparison.Ordinal));
                var checkpointSnapshot = string.Equals(runSnapshot.Status, "paused", StringComparison.OrdinalIgnoreCase)
                    && session.MetaExecutionCheckpoint is not null
                    && string.Equals(session.MetaExecutionCheckpoint.SkillName, runSnapshot.SkillName, StringComparison.OrdinalIgnoreCase)
                    ? session.MetaExecutionCheckpoint
                    : null;

                var metadata = BuildMetaRunProposalMetadata(existing, sessionId, proposal);
                metadata[MetaRunProposalMetadata.Reason] = rollbackReason;

                var durableRecord = BuildUpdatedMetaRunLearningProposal(
                    existing,
                    proposal,
                    sessionId,
                    lifecycleStatus,
                    reviewedAtUtc,
                    reviewedAtUtc,
                    existing.ReviewNotes,
                    rolledBack: true,
                    rolledBackAtUtc: reviewedAtUtc,
                    rollbackReason,
                    metadata);

                PopulateMetaRunProposalProvenanceMetadata(durableRecord.Metadata, runSnapshot, checkpointSnapshot, reviewedAtUtc);
                AppendMetaRunProposalTransitionMetadata(
                    durableRecord.Metadata,
                    MetaRunProposalActions.Rollback,
                    existing.Status,
                    lifecycleStatus,
                    reviewedAtUtc,
                    operatorId,
                    rollbackReason);
                await learningProposalStore.SaveProposalAsync(durableRecord, CancellationToken.None);

                record = new MetaRunProposalReviewRecord
                {
                    SessionId = sessionId,
                    ProposalId = proposalId,
                    ReviewStatus = MetaRunProposalReviewStatuses.RolledBack,
                    Reason = rollbackReason,
                    ReviewedAtUtc = reviewedAtUtc
                };
            }
            else
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals rollback", "invalid_lifecycle_transition", $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': {existing.Status} -> {LearningProposalStatus.RolledBack}. Only approved or rejected proposals can be rolled back.");
                return 1;
            }

            var responseAudit = alreadyReviewed
                ? BuildMetaRunProposalAuditDetail(existing)
                : new MetaRunProposalAuditDetail
                {
                    ActorId = operatorId,
                    ChangedAtUtc = record.ReviewedAtUtc,
                    TransitionAction = MetaRunProposalActions.Rollback
                };

            var response = new MetaRunProposalReviewMutationResponse
            {
                SessionId = sessionId,
                ProposalId = proposalId,
                ReviewStatus = record.ReviewStatus,
                LifecycleStatus = lifecycleStatus,
                AlreadyReviewed = alreadyReviewed,
                ReviewedAtUtc = record.ReviewedAtUtc,
                Reason = record.Reason,
                Audit = responseAudit
            };

            if (asJson)
                Console.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.MetaRunProposalReviewMutationResponse));
            else
                WriteProposalReviewMutationText(response);

            return 0;
        }
        finally
        {
            switch (learningProposalStore)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> ChangeMetaRunProposalAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals change",
                "missing_session_id",
                "Usage: openclaw skills meta-runs proposals change <session-id> --proposal <id> --to <accept|dismiss> [--reason <text>] [--storage <path>] [--json]");
            return 2;
        }

        var proposalId = GetOptionValue(args, "--proposal");
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals change",
                "missing_proposal_id",
                "--proposal <id> is required for meta-runs proposals change.");
            return 2;
        }

        var to = GetOptionValue(args, "--to");
        if (!TryMapMetaRunProposalChangeTarget(to, out var targetReviewStatus, out var targetLifecycleStatus))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals change",
                "invalid_change_target",
                "--to must be one of: accept, dismiss.");
            return 2;
        }

        var operatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        if (!MetaRunProposalPolicy.CanMutate(operatorId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs proposals change",
                "permission_denied",
                "Proposal mutation requires OPENCLAW_OPERATOR_ID.");
            return 1;
        }

        var reason = GetOptionValue(args, "--reason");
        var storagePath = GetOptionValue(args, "--storage");
        var learningProposalStore = OpenLearningProposalStore(storagePath);
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals change", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var proposal = BuildDerivedProposals(session, requestedRunId: null)
                .FirstOrDefault(item => string.Equals(item.Id, proposalId, StringComparison.Ordinal));
            if (proposal is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals change", "proposal_not_found", $"Proposal '{proposalId}' not found in session '{sessionId}'.");
                return 1;
            }

            var durableProposalId = BuildMetaRunProposalDurableId(sessionId, proposalId);
            var existing = await learningProposalStore.GetProposalAsync(durableProposalId, CancellationToken.None);
            var alreadyReviewed = false;
            var lifecycleStatus = targetLifecycleStatus;
            var currentLifecycleStatus = existing?.Status ?? LearningProposalStatus.Pending;
            MetaRunProposalReviewRecord record;

            if (existing is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals change", "invalid_lifecycle_transition", $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': pending -> {targetLifecycleStatus}. Change only supports {LearningProposalStatus.RolledBack} -> {targetLifecycleStatus}.");
                return 1;
            }

            if (!string.Equals(existing.Status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase))
            {
                WriteSkillsCommandError(asJson, "skills meta-runs proposals change", "invalid_lifecycle_transition", $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': {existing.Status} -> {targetLifecycleStatus}. Change only supports {LearningProposalStatus.RolledBack} -> {targetLifecycleStatus}.");
                return 1;
            }
            else
            {
                if (!MetaRunProposalPolicy.IsAllowedActionTransition(MetaRunProposalActions.Change, currentLifecycleStatus, targetLifecycleStatus))
                {
                    WriteSkillsCommandError(
                        asJson,
                        "skills meta-runs proposals change",
                        "invalid_lifecycle_transition",
                        $"Invalid lifecycle transition for proposal '{proposalId}' in session '{sessionId}': {currentLifecycleStatus} -> {targetLifecycleStatus}. Change only supports {LearningProposalStatus.RolledBack} -> {targetLifecycleStatus}.");
                    return 1;
                }

                var reviewedAtUtc = DateTimeOffset.UtcNow;
                var runSnapshot = session.MetaRunHistory.First(run => string.Equals(run.RunId, proposal.RunId, StringComparison.Ordinal));
                var checkpointSnapshot = string.Equals(runSnapshot.Status, "paused", StringComparison.OrdinalIgnoreCase)
                    && session.MetaExecutionCheckpoint is not null
                    && string.Equals(session.MetaExecutionCheckpoint.SkillName, runSnapshot.SkillName, StringComparison.OrdinalIgnoreCase)
                    ? session.MetaExecutionCheckpoint
                    : null;

                var metadata = BuildMetaRunProposalMetadata(existing, sessionId, proposal);
                if (!string.IsNullOrWhiteSpace(reason))
                    metadata[MetaRunProposalMetadata.Reason] = reason!;

                var durableRecord = BuildUpdatedMetaRunLearningProposal(
                    existing,
                    proposal,
                    sessionId,
                    targetLifecycleStatus,
                    reviewedAtUtc,
                    reviewedAtUtc,
                    reason,
                    rolledBack: false,
                    rolledBackAtUtc: null,
                    rollbackReason: null,
                    metadata);

                PopulateMetaRunProposalProvenanceMetadata(durableRecord.Metadata, runSnapshot, checkpointSnapshot, reviewedAtUtc);
                AppendMetaRunProposalTransitionMetadata(
                    durableRecord.Metadata,
                    MetaRunProposalActions.Change,
                    existing.Status,
                    targetLifecycleStatus,
                    reviewedAtUtc,
                    operatorId,
                    reason);
                await learningProposalStore.SaveProposalAsync(durableRecord, CancellationToken.None);

                record = new MetaRunProposalReviewRecord
                {
                    SessionId = sessionId,
                    ProposalId = proposalId,
                    ReviewStatus = targetReviewStatus,
                    Reason = reason,
                    ReviewedAtUtc = reviewedAtUtc
                };
            }

            var responseAudit = alreadyReviewed
                ? BuildMetaRunProposalAuditDetail(existing)
                : new MetaRunProposalAuditDetail
                {
                    ActorId = operatorId,
                    ChangedAtUtc = record.ReviewedAtUtc,
                    TransitionAction = MetaRunProposalActions.Change
                };

            var response = new MetaRunProposalReviewMutationResponse
            {
                SessionId = sessionId,
                ProposalId = proposalId,
                ReviewStatus = record.ReviewStatus,
                LifecycleStatus = lifecycleStatus,
                AlreadyReviewed = alreadyReviewed,
                ReviewedAtUtc = record.ReviewedAtUtc,
                Reason = record.Reason,
                Audit = responseAudit
            };

            if (asJson)
                Console.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.MetaRunProposalReviewMutationResponse));
            else
                WriteProposalReviewMutationText(response);

            return 0;
        }
        finally
        {
            switch (learningProposalStore)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> ReconstructMetaRunReplayAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs reconstruct",
                "missing_session_id",
                "Usage: openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]");
            return 2;
        }

        var requestedRunId = GetOptionValue(args, "--run");
        if (string.IsNullOrWhiteSpace(requestedRunId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs reconstruct",
                "missing_run_id",
                "--run <run-id> is required for meta-runs reconstruct.");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs reconstruct", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var run = session.MetaRunHistory.FirstOrDefault(run => string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal));
            if (run is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs reconstruct", "run_not_found", $"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            var replay = BuildReplayResult(sessionId, run, session.MetaExecutionCheckpoint);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(replay, CoreJsonContext.Default.MetaRunReplayResultResponse));
            }
            else
            {
                WriteReplayResultText(replay);
            }

            return 0;
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> PreviewMetaRunReplayAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs replay",
                "missing_session_id",
                "Usage: openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]");
            return 2;
        }

        var requestedRunId = GetOptionValue(args, "--run");
        if (string.IsNullOrWhiteSpace(requestedRunId))
        {
            WriteSkillsCommandError(
                asJson,
                "skills meta-runs replay",
                "missing_run_id",
                "--run <run-id> is required for meta-runs replay preview.");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs replay", "session_not_found", $"Session '{sessionId}' not found.");
                return 1;
            }

            var run = session.MetaRunHistory.FirstOrDefault(run => string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal));
            if (run is null)
            {
                WriteSkillsCommandError(asJson, "skills meta-runs replay", "run_not_found", $"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            var preview = BuildReplayPreview(sessionId, run);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(preview, CoreJsonContext.Default.MetaRunReplayPreviewResponse));
            }
            else
            {
                Console.WriteLine($"Replay preview for run: {preview.RunId}");
                Console.WriteLine($"Session: {preview.SessionId}");
                Console.WriteLine($"Skill: {preview.SkillName}");
                Console.WriteLine(preview.ReplayAvailable ? "Replay available: yes" : "Replay available: no");
                Console.WriteLine($"Reason: {preview.Reason}");
                Console.WriteLine("Replay plan:");
                Console.WriteLine($"Summary: {preview.Plan.Summary}");
                Console.WriteLine($"Mode: {preview.Plan.Mode}");
                Console.WriteLine(preview.Plan.Executable ? "Executable: yes" : "Executable: no");
                if (preview.Plan.ReplayableSteps.Length > 0)
                    Console.WriteLine($"Replayable steps: {string.Join(", ", preview.Plan.ReplayableSteps.Select(static item => $"{item.Id} | readiness={item.Readiness} | reason={item.Reason}"))}");
                if (preview.Plan.BlockedByRequirements.Length > 0)
                    Console.WriteLine($"Blocked by requirements: {string.Join(", ", preview.Plan.BlockedByRequirements.Select(FormatReplayRequirement))}");
                WriteReplayOperatorDiagnosticsText(preview.OperatorSummary, preview.TriageHints);
                if (preview.AvailableArtifacts.Length > 0)
                {
                    Console.WriteLine("Available artifacts:");
                    foreach (var item in preview.AvailableArtifacts)
                        Console.WriteLine($"- {item}");
                }
                if (preview.RetainedSteps.Length > 0)
                {
                    Console.WriteLine("Retained steps:");
                    foreach (var step in preview.RetainedSteps)
                        Console.WriteLine(FormatReplayRetainedStep(step));
                }
                if (preview.MissingRequirements.Length > 0)
                {
                    Console.WriteLine("Missing replay inputs:");
                    foreach (var item in preview.MissingRequirements)
                        Console.WriteLine($"- {FormatReplayRequirement(item)}");
                }
            }

            return 1;
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static Task<int> InspectAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            WriteSkillsCommandError(
                asJson,
                "skills inspect",
                "missing_source_path",
                "Usage: openclaw skills inspect <path|tarball>");
            return Task.FromResult(2);
        }

        return InspectSourceAsync(sourcePath, printInstallTarget: false, asJson);
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            WriteSkillsCommandError(
                asJson,
                "skills install",
                "missing_source_path",
                "Usage: openclaw skills install <path|tarball>");
            return 2;
        }

        var dryRun = args.Contains("--dry-run");
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");

        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: true);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            WriteSkillsCommandError(
                asJson,
                "skills install",
                "inspect_failed",
                inspected.ErrorMessage ?? $"Failed to inspect source path: {sourcePath}");
            return 1;
        }

        try
        {
            PrintInspection(inspected, ResolveSkillsDirectory(managed, workdir));
            if (dryRun)
                return 0;

            var skillsDirectory = ResolveSkillsDirectory(managed, workdir);
            Directory.CreateDirectory(skillsDirectory);

            var targetDir = Path.Combine(skillsDirectory, inspected.InstallSlug);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            CopyDirectory(inspected.SkillRootPath, targetDir);
            Console.WriteLine($"Installed skill '{inspected.Definition.Name}' to {targetDir}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            WriteSkillsCommandError(
                asJson,
                "skills install",
                "install_failed",
                ex.Message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(resolved.TempDirectory))
            {
                try { Directory.Delete(resolved.TempDirectory, recursive: true); } catch { }
            }
        }
    }

    private static int ListInstalled(string[] args)
    {
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");
        var skillsDirectory = ResolveSkillsDirectory(managed, workdir);

        if (!Directory.Exists(skillsDirectory))
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        var source = managed ? SkillSource.Managed : SkillSource.Workspace;
        var inspections = SkillInspector.InspectInstalledRoot(skillsDirectory, source)
            .Where(static inspection => inspection.Success && inspection.Definition is not null)
            .Select(CreateInspection)
            .OrderBy(static item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (inspections.Length == 0)
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        Console.WriteLine($"Installed skills ({inspections.Length}):");
        foreach (var inspection in inspections)
        {
            Console.WriteLine($"  {inspection.Definition.Name} - {inspection.Definition.Description}");
            Console.WriteLine($"    Trust: {inspection.TrustLevel}");
            Console.WriteLine($"    Source: {inspection.SourceLabel}");
            Console.WriteLine($"    Path: {inspection.SkillRootPath}");
        }

        return 0;
    }

    private static int ListCatalog(string[] args)
    {
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");
        var asJson = args.Contains("--json");
        var kind = GetOptionValue(args, "--kind");

        if (!string.IsNullOrWhiteSpace(kind)
            && !string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase))
        {
            WriteSkillsCommandError(
                asJson,
                "skills catalog",
                "invalid_kind",
                "--kind supports only 'all' or 'meta'.");
            return 2;
        }

        var skillsDirectory = ResolveSkillsDirectory(managed, workdir);
        if (!Directory.Exists(skillsDirectory))
        {
            if (asJson)
            {
                Console.WriteLine("{\"count\":0,\"skills\":[]}");
                return 0;
            }

            Console.WriteLine("No skills installed.");
            return 0;
        }

        var source = managed ? SkillSource.Managed : SkillSource.Workspace;
        var catalog = SkillInspector.InspectInstalledRoot(skillsDirectory, source)
            .Where(static inspection => inspection.Success && inspection.Definition is not null)
            .Select(CreateInspection)
            .Where(item => string.IsNullOrWhiteSpace(kind)
                || string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase)
                    && item.Definition.Kind == SkillKind.Meta))
            .OrderBy(static item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (asJson)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("count", catalog.Length);
                writer.WriteStartArray("skills");
                foreach (var item in catalog)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", item.Definition.Name);
                    writer.WriteString("kind", item.Definition.Kind.ToString().ToLowerInvariant());
                    writer.WriteString("description", item.Definition.Description);
                    writer.WriteString("trust", item.TrustLevel);
                    writer.WriteString("source", item.SourceLabel);
                    writer.WriteString("path", item.SkillRootPath);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }

        Console.WriteLine($"Skill catalog ({catalog.Length}):");
        foreach (var item in catalog)
        {
            Console.WriteLine($"  {item.Definition.Name} [{item.Definition.Kind.ToString().ToLowerInvariant()}] - {item.Definition.Description}");
            Console.WriteLine($"    Trust: {item.TrustLevel}");
            Console.WriteLine($"    Source: {item.SourceLabel}");
            Console.WriteLine($"    Path: {item.SkillRootPath}");
        }

        return 0;
    }

    private static int CreateSkillScaffold(string[] args)
    {
        var asJson = args.Contains("--json");
        var force = args.Contains("--force");
        var proposalDraftRequested = args.Contains("--proposal-draft");
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");
        var name = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(name))
        {
            WriteCreateError(asJson, "invalid_create_usage", "Usage: openclaw skills create <name> [--kind <standard|meta>] [--description <text>] [--proposal-draft] [--workdir <path> | --managed] [--json] [--force]");
            return 2;
        }

        var kind = GetOptionValue(args, "--kind") ?? "standard";
        if (!string.Equals(kind, "standard", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase))
        {
            WriteCreateError(asJson, "invalid_kind", "--kind must be one of: standard, meta.");
            return 2;
        }

        name = NormalizeSingleLineValue(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            WriteCreateError(asJson, "invalid_skill_name", "Skill name cannot be empty.");
            return 2;
        }

        var slug = Slugify(name);
        if (string.IsNullOrWhiteSpace(slug))
        {
            WriteCreateError(asJson, "invalid_skill_name", "Skill name does not contain any letters or digits.");
            return 2;
        }

        var description = NormalizeSingleLineValue(GetOptionValue(args, "--description") ?? $"{name} workflow scaffold.");
        var skillsDirectory = ResolveSkillsDirectory(managed, workdir);
        var skillDirectory = Path.Combine(skillsDirectory, slug);
        var exists = Directory.Exists(skillDirectory);
        if (exists && !force)
        {
            WriteCreateError(asJson, "skill_already_exists", $"Skill scaffold already exists: {skillDirectory}. Use --force to overwrite SKILL.md.");
            return 1;
        }

        Directory.CreateDirectory(skillDirectory);
        var kindValue = string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase) ? "meta" : "standard";
        if (proposalDraftRequested && !string.Equals(kindValue, "meta", StringComparison.OrdinalIgnoreCase))
        {
            WriteCreateError(asJson, "invalid_proposal_draft_kind", "--proposal-draft is only supported for --kind meta.");
            return 2;
        }

        var skillMarkdown = BuildSkillScaffoldMarkdown(name, description, kindValue);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), skillMarkdown);

        var proposalDraftId = slug;
        var proposalDraftKind = "meta_skill_creator_draft";
        var proposalDraftStatus = "draft";
        var proposalDraftTitle = $"Meta skill draft proposal: {name}";
        var proposalDraftSummary = "Draft proposal prepared from scaffold metadata. Review and refine before lifecycle actions.";
        var proposalDraftQuality = BuildProposalDraftQuality(name, description, kindValue);

        if (asJson)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("slug", slug);
                writer.WriteString("kind", kindValue);
                writer.WriteString("path", skillDirectory);
                writer.WriteBoolean("created", true);
                writer.WriteBoolean("overwrote", exists);
                if (proposalDraftRequested)
                {
                    writer.WriteStartObject("proposalDraft");
                    writer.WriteBoolean("available", true);
                    writer.WriteString("id", proposalDraftId);
                    writer.WriteString("kind", proposalDraftKind);
                    writer.WriteString("status", proposalDraftStatus);
                    writer.WriteString("title", proposalDraftTitle);
                    writer.WriteString("summary", proposalDraftSummary);
                    writer.WriteStartObject("quality");
                    writer.WriteNumber("checksPassed", proposalDraftQuality.ChecksPassed);
                    writer.WriteNumber("checksTotal", proposalDraftQuality.ChecksTotal);
                    writer.WriteStartArray("checks");
                    foreach (var check in proposalDraftQuality.Checks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", check.Id);
                        writer.WriteString("status", check.Status);
                        writer.WriteString("message", check.Message);
                        if (!string.IsNullOrWhiteSpace(check.Recommendation))
                            writer.WriteString("recommendation", check.Recommendation);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("warnings");
                    foreach (var warning in proposalDraftQuality.Warnings)
                        writer.WriteStringValue(warning);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }

        Console.WriteLine($"Created skill scaffold: {name} [{kindValue}]");
        Console.WriteLine($"Path: {skillDirectory}");
        if (exists)
            Console.WriteLine("Overwrote existing SKILL.md via --force.");
        if (proposalDraftRequested)
        {
            Console.WriteLine($"Proposal draft: {proposalDraftStatus}");
            Console.WriteLine($"Proposal kind: {proposalDraftKind}");
            Console.WriteLine($"Proposal id: {proposalDraftId}");
            Console.WriteLine($"Proposal quality: {proposalDraftQuality.ChecksPassed}/{proposalDraftQuality.ChecksTotal} checks passed");
        }

        return 0;
    }

    private static async Task<int> InspectSourceAsync(string sourcePath, bool printInstallTarget, bool asJson)
    {
        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: false);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            WriteSkillsCommandError(
                asJson,
                "skills inspect",
                "inspect_failed",
                inspected.ErrorMessage ?? $"Failed to inspect source path: {sourcePath}");
            return 1;
        }

        PrintInspection(inspected, printInstallTarget ? ResolveSkillsDirectory(managed: false, workdir: null) : null);
        return 0;
    }

    private static async Task<(SkillCommandInspection Inspection, string? TempDirectory)> InspectResolvedSourceAsync(string sourcePath, bool retainExtractedDirectory)
    {
        var resolvedSourcePath = Path.GetFullPath(sourcePath);

        if (Directory.Exists(resolvedSourcePath))
        {
            var inspection = SkillInspector.InspectPath(resolvedSourcePath, SkillSource.Extra);
            return inspection.Success
                ? (CreateInspection(inspection), null)
                : (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect {resolvedSourcePath}."), null);
        }

        if (File.Exists(resolvedSourcePath) && resolvedSourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-install-{Guid.NewGuid():N}"[..24]);
            Directory.CreateDirectory(tempDir);

            try
            {
                var extractResult = await RunProcessAsync(
                    "tar",
                    ["--extract", "--gzip", "--file", resolvedSourcePath, "--directory", tempDir],
                    tempDir);
                if (extractResult.ExitCode != 0)
                    return (SkillCommandInspection.Failure($"Failed to extract skill tarball: {extractResult.Stderr}"), retainExtractedDirectory ? tempDir : null);

                var inspection = SkillInspector.InspectPath(tempDir, SkillSource.Extra);
                if (!inspection.Success)
                    return (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect extracted tarball {resolvedSourcePath}."), retainExtractedDirectory ? tempDir : null);

                return (CreateInspection(inspection), retainExtractedDirectory ? tempDir : null);
            }
            finally
            {
                if (!retainExtractedDirectory)
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        return (SkillCommandInspection.Failure($"Skill path not found: {sourcePath}"), null);
    }

    private static SkillCommandInspection CreateInspection(SkillInspectionResult inspection)
    {
        var definition = inspection.Definition!;
        var installSlug = Slugify(definition.Metadata.SkillKey ?? definition.Name);
        var trustLevel = definition.Source == SkillSource.Bundled ? "first-party" : "upstream-compatible";
        var trustReason = definition.Source == SkillSource.Bundled
            ? "Skill ships with OpenClaw.NET."
            : "Skill document parsed successfully and uses the OpenClaw skill format.";

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Description))
            warnings.Add("Skill description is empty.");
        if (definition.Metadata.RequireBins.Length == 0 &&
            definition.Metadata.RequireAnyBins.Length == 0 &&
            definition.Metadata.RequireEnv.Length == 0 &&
            definition.Metadata.RequireConfig.Length == 0)
        {
            warnings.Add("Skill does not declare host requirements.");
        }

        return new SkillCommandInspection
        {
            Success = true,
            Definition = definition,
            SkillRootPath = inspection.SkillRootPath!,
            SkillFilePath = inspection.SkillFilePath!,
            InstallSlug = installSlug,
            SourceLabel = definition.Source.ToString().ToLowerInvariant(),
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            Warnings = warnings
        };
    }

    private static void PrintInspection(SkillCommandInspection inspection, string? installDirectory)
    {
        Console.WriteLine($"Skill: {inspection.Definition.Name}");
        Console.WriteLine($"Description: {inspection.Definition.Description}");
        Console.WriteLine($"Trust: {inspection.TrustLevel}");
        Console.WriteLine($"Trust reason: {inspection.TrustReason}");
        Console.WriteLine($"Source: {inspection.SourceLabel}");
        Console.WriteLine($"Path: {inspection.SkillRootPath}");
        Console.WriteLine($"Install slug: {inspection.InstallSlug}");
        Console.WriteLine($"User invocable: {inspection.Definition.UserInvocable}");
        Console.WriteLine($"Disable model invocation: {inspection.Definition.DisableModelInvocation}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandDispatch))
            Console.WriteLine($"Command dispatch: {inspection.Definition.CommandDispatch}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandTool))
            Console.WriteLine($"Command tool: {inspection.Definition.CommandTool}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandArgMode))
            Console.WriteLine($"Command arg mode: {inspection.Definition.CommandArgMode}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.Homepage))
            Console.WriteLine($"Homepage: {inspection.Definition.Metadata.Homepage}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.PrimaryEnv))
            Console.WriteLine($"Primary env: {inspection.Definition.Metadata.PrimaryEnv}");
        Console.WriteLine($"Requirements: {BuildRequirementsSummary(inspection.Definition)}");
        foreach (var warning in inspection.Warnings)
            Console.WriteLine($"Warning: {warning}");
        if (!string.IsNullOrWhiteSpace(installDirectory))
            Console.WriteLine($"Install target: {Path.Combine(installDirectory, inspection.InstallSlug)}");
    }

    private static string BuildRequirementsSummary(SkillDefinition definition)
    {
        var items = new List<string>();
        if (definition.Metadata.RequireBins.Length > 0)
            items.Add($"bins={string.Join(",", definition.Metadata.RequireBins)}");
        if (definition.Metadata.RequireAnyBins.Length > 0)
            items.Add($"anyBins={string.Join(",", definition.Metadata.RequireAnyBins)}");
        if (definition.Metadata.RequireEnv.Length > 0)
            items.Add($"env={string.Join(",", definition.Metadata.RequireEnv)}");
        if (definition.Metadata.RequireConfig.Length > 0)
            items.Add($"config={string.Join(",", definition.Metadata.RequireConfig)}");
        if (definition.Metadata.Always)
            items.Add("always");

        return items.Count == 0 ? "none" : string.Join(" | ", items);
    }

    private static string BuildSkillScaffoldMarkdown(string name, string description, string kind)
    {
        var lines = new List<string>
        {
            "---",
            $"name: {name}",
            $"description: {description}"
        };

        if (string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("kind: meta");
            lines.Add("composition: {\"steps\":[{\"id\":\"draft\",\"kind\":\"llm_chat\",\"with\":{\"prompt\":\"Summarize the user intent and propose next steps.\"}}]}");
        }

        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add("Describe what this skill should do and how it should be used.");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static ProposalDraftQuality BuildProposalDraftQuality(string name, string description, string kind)
    {
        var checks = new List<ProposalDraftQualityCheck>();
        var warnings = new List<string>();

        var hasName = !string.IsNullOrWhiteSpace(name);
        checks.Add(new ProposalDraftQualityCheck(
            "name_present",
            hasName ? "pass" : "fail",
            hasName ? "Skill name is present." : "Skill name is empty.",
            hasName ? null : "Provide a non-empty skill name."));

        var hasDescription = !string.IsNullOrWhiteSpace(description);
        var descriptionStatus = hasDescription ? "pass" : "fail";
        var descriptionMessage = hasDescription ? "Skill description is present." : "Skill description is empty.";
        var descriptionRecommendation = hasDescription ? null : "Add a meaningful description that explains intent and outcomes.";
        if (hasDescription && description.Length < 16)
        {
            descriptionStatus = "warn";
            descriptionMessage = "Skill description is present but too short to guide proposal review.";
            descriptionRecommendation = "Expand description to include expected behavior and boundaries.";
            warnings.Add("description_too_short");
        }

        checks.Add(new ProposalDraftQualityCheck("description_present", descriptionStatus, descriptionMessage, descriptionRecommendation));

        var metaSeeded = string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase);
        checks.Add(new ProposalDraftQualityCheck(
            "meta_composition_seeded",
            metaSeeded ? "pass" : "fail",
            metaSeeded ? "Meta composition scaffold is seeded." : "Meta composition scaffold is missing.",
            metaSeeded ? null : "Use --kind meta to seed composition scaffolding."));

        var checksPassed = checks.Count(static check => string.Equals(check.Status, "pass", StringComparison.Ordinal));
        return new ProposalDraftQuality(checksPassed, checks.Count, checks, warnings);
    }

    private static string NormalizeSingleLineValue(string value) =>
        value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static void WriteCreateError(bool asJson, string errorCode, string message)
    {
        if (!asJson)
        {
            Console.Error.WriteLine(message);
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "error");
            writer.WriteString("command", "skills create");
            writer.WriteString("errorCode", errorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        Console.Error.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteSkillsCommandError(bool asJson, string command, string errorCode, string message)
    {
        if (!asJson)
        {
            Console.Error.WriteLine(message);
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "error");
            writer.WriteString("command", command);
            writer.WriteString("errorCode", errorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        Console.Error.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private sealed record ProposalDraftQuality(int ChecksPassed, int ChecksTotal, IReadOnlyList<ProposalDraftQualityCheck> Checks, IReadOnlyList<string> Warnings);

    private sealed record ProposalDraftQualityCheck(string Id, string Status, string Message, string? Recommendation = null);

    private static string ResolveSkillsDirectory(bool managed, string? workdir)
    {
        if (!string.IsNullOrWhiteSpace(workdir))
            return Path.Combine(Path.GetFullPath(workdir), "skills");

        if (managed)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "skills");
        }

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(Path.GetFullPath(workspace), "skills");

        throw new InvalidOperationException($"Missing {EnvWorkspace}. Set {EnvWorkspace} or pass --workdir or --managed.");
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static int? ParseIntOption(string[] args, string optionName)
    {
        var value = GetOptionValue(args, optionName);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static IMemoryStore OpenMemoryStore(string? storagePath)
    {
        var resolved = ResolveMemoryPath(storagePath);
        if (LooksLikeSqlitePath(resolved))
            return new SqliteMemoryStore(resolved, enableFts: false);

        return new FileMemoryStore(resolved);
    }

    private static string ResolveMemoryPath(string? storagePath)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
            return Path.GetFullPath(storagePath);

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(Path.GetFullPath(workspace), "memory");

        return Path.GetFullPath("./memory");
    }

    private static string ResolveProposalReviewRootPath(string resolvedMemoryPath)
    {
        if (!LooksLikeSqlitePath(resolvedMemoryPath))
            return resolvedMemoryPath;

        var directory = Path.GetDirectoryName(resolvedMemoryPath);
        var baseName = Path.GetFileNameWithoutExtension(resolvedMemoryPath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetFullPath(".");

        return Path.Combine(directory, $"{baseName}.memory");
    }

    private static ILearningProposalStore OpenLearningProposalStore(string? storagePath)
    {
        var resolved = ResolveMemoryPath(storagePath);
        if (LooksLikeSqlitePath(resolved))
            return new SqliteFeatureStore(resolved);

        return new FileFeatureStore(resolved);
    }

    private static bool LooksLikeSqlitePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        ThrowIfReparsePoint(new DirectoryInfo(source));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            ThrowIfReparsePoint(new FileInfo(file));
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            ThrowIfReparsePoint(new DirectoryInfo(dir));
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void ThrowIfReparsePoint(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to install skill package containing a symlink or reparse point: {info.FullName}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return (127, "", $"Command not found: {fileName}");
        }
    }

    private static string Slugify(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int UnknownSubcommand(string subcommand, bool asJson)
    {
        WriteSkillsCommandError(asJson, "skills", "unknown_subcommand", $"Unknown skills subcommand: {subcommand}");
        if (!asJson)
            PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            openclaw skills — Inspect and install local OpenClaw skill packages

            Usage:
              openclaw skills inspect <path|tarball>
              openclaw skills install <path|tarball> [--dry-run] [--workdir <path> | --managed]
              openclaw skills list [--workdir <path> | --managed]
              openclaw skills catalog [--workdir <path> | --managed] [--kind <all|meta>] [--json]
              openclaw skills create <name> [--kind <standard|meta>] [--description <text>] [--proposal-draft] [--workdir <path> | --managed] [--json] [--force]
              openclaw skills proposals <session-id> [--run <run-id>] [--storage <path>] [--json]
              openclaw skills proposals show <session-id> --proposal <id> [--storage <path>] [--json]
                            openclaw skills meta-runs <session-id> [--storage <path>] [--limit <count>] [--run <run-id>] [--verbose] [--json]
                            openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]
                            openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]
                            openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--storage <path>] [--json]
                            openclaw skills meta-runs proposals show <session-id> --proposal <id> [--storage <path>] [--json]
                            openclaw skills meta-runs proposals accept <session-id> --proposal <id> [--storage <path>] [--json]
                            openclaw skills meta-runs proposals dismiss <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]
                            openclaw skills meta-runs proposals rollback <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]
                            openclaw skills meta-runs proposals change <session-id> --proposal <id> --to <accept|dismiss> [--reason <text>] [--storage <path>] [--json]

            Notes:
              - Remote registry installs still go through `openclaw clawhub`.
              - `install --dry-run` prints trust and requirement details without copying files.
                            - `meta-runs` reads persisted local session state from `./memory`, `$OPENCLAW_WORKSPACE/memory`, or `--storage`.
                            - `meta-runs --run <run-id>` limits output to one persisted run inside the session history.
                            - `meta-runs --verbose` expands per-step trace summaries for each persisted run.
                            - `meta-runs --json` emits machine-readable output for operators and scripts.
                            - `meta-runs replay` is currently preview-only and reports whether persisted run history is sufficient for replay.
                                - `meta-runs reconstruct` builds an audit replay result from persisted run history and optional checkpoint state without re-executing tools or models.
                                - `meta-runs proposals` returns derived read-only proposal summaries from persisted meta-run evidence.
                                - `skills proposals` is a read-only shortcut to `skills meta-runs proposals` / `show`.
                                - `skills catalog` lists installed skills for discovery and supports `--kind meta` filtering.
                                - `meta-runs proposals show` expands a single derived proposal without implying durable lifecycle state.
                                - `meta-runs proposals accept|dismiss|rollback|change` records lifecycle decisions only; it does not execute tools, models, or replay.
            """);
    }

        private static void WriteMetaRunsJson(string sessionId, int totalCount, IReadOnlyList<SessionMetaRunRecord> runs)
        {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                        writer.WriteStartObject();
                        writer.WriteString("sessionId", sessionId);
                        writer.WriteNumber("totalCount", totalCount);
                        writer.WriteNumber("shownCount", runs.Count);
                        writer.WritePropertyName("runs");
                        writer.WriteStartArray();
                        foreach (var run in runs)
                                JsonSerializer.Serialize(writer, run, CoreJsonContext.Default.SessionMetaRunRecord);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                }

                Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

    private static MetaRunDerivedProposalSummary[] BuildDerivedProposals(Session session, string? requestedRunId)
        => [..
            session.MetaRunHistory
                .Where(run => string.IsNullOrWhiteSpace(requestedRunId) || string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal))
                .Select(run => TryBuildDerivedProposalSummary(run, session.MetaExecutionCheckpoint))
                .Where(static proposal => proposal is not null)
                .Cast<MetaRunDerivedProposalSummary>()
        ];

    private static MetaRunDerivedProposalSummary? TryBuildDerivedProposalSummary(
        SessionMetaRunRecord run,
        SessionMetaExecutionCheckpoint? checkpoint)
    {
        if (string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            var pendingStepId = string.Equals(checkpoint?.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase)
                ? checkpoint?.PendingStepId
                : null;

            return new MetaRunDerivedProposalSummary
            {
                Id = $"meta-run:{run.RunId}:paused",
                RunId = run.RunId,
                SkillName = run.SkillName,
                Status = run.Status,
                Kind = MetaRunProposalKinds.PausedRunFollowup,
                Title = $"Resume paused meta run {run.SkillName}",
                Summary = string.IsNullOrWhiteSpace(pendingStepId)
                    ? $"Run {run.RunId} is paused and needs operator follow-up."
                    : $"Run {run.RunId} is paused at step {pendingStepId}.",
                AvailableActions = [MetaRunProposalActions.Show, MetaRunProposalActions.Accept, MetaRunProposalActions.Dismiss]
            };
        }

        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new MetaRunDerivedProposalSummary
            {
                Id = $"meta-run:{run.RunId}:failed",
                RunId = run.RunId,
                SkillName = run.SkillName,
                Status = run.Status,
                Kind = MetaRunProposalKinds.FailedRunReview,
                Title = $"Review failed meta run {run.SkillName}",
                Summary = string.IsNullOrWhiteSpace(run.ErrorCode)
                    ? $"Run {run.RunId} failed and needs review."
                    : $"Run {run.RunId} failed with {run.ErrorCode}.",
                AvailableActions = [MetaRunProposalActions.Show, MetaRunProposalActions.Accept, MetaRunProposalActions.Dismiss]
            };
        }

        return null;
    }

    private static MetaRunDerivedProposalDetail BuildDerivedProposalDetail(
        MetaRunDerivedProposalSummary summary,
        SessionMetaRunRecord run,
        SessionMetaExecutionCheckpoint? checkpoint)
    {
        var checkpointMatches = string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase)
            && checkpoint is not null
            && string.Equals(checkpoint.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase);

        return new MetaRunDerivedProposalDetail
        {
            Id = summary.Id,
            RunId = summary.RunId,
            SkillName = summary.SkillName,
            Status = summary.Status,
            Kind = summary.Kind,
            Title = summary.Title,
            Summary = summary.Summary,
            Source = summary.Source,
            AvailableActions = [.. summary.AvailableActions],
            Checkpoint = checkpointMatches
                ? new MetaRunDerivedProposalCheckpointDetail
                {
                    PendingStepId = checkpoint!.PendingStepId,
                    PendingStepIds = [.. checkpoint.PendingStepIds],
                    BlockedStepIds = [.. checkpoint.BlockedStepIds],
                    PromptPresent = !string.IsNullOrWhiteSpace(checkpoint.Prompt),
                    OutputStepIds = [.. checkpoint.Outputs.Keys],
                    FailureAliasStepIds = [.. checkpoint.FailureAliases.Keys]
                }
                : null,
            Evidence = new MetaRunDerivedProposalEvidenceDetail
            {
                TimelineStepIds = [.. run.StepResults.Select(static step => step.Id)],
                ErrorCode = run.ErrorCode,
                Error = run.Error,
                FinalText = run.FinalText
            },
            PendingStepId = checkpointMatches ? checkpoint!.PendingStepId : null,
            PendingStepIds = checkpointMatches ? [.. checkpoint!.PendingStepIds] : [],
            BlockedStepIds = checkpointMatches ? [.. checkpoint!.BlockedStepIds] : [],
            TimelineStepIds = [.. run.StepResults.Select(static step => step.Id)],
            Steps = [..
                run.StepResults.Select(static step => new MetaRunDerivedProposalStepDetail
                {
                    Id = step.Id,
                    Kind = step.Kind,
                    Status = step.Status,
                    FailureCode = step.FailureCode,
                    DurationMs = step.DurationMs,
                    Continued = step.Continued
                })],
            ErrorCode = run.ErrorCode,
            Error = run.Error,
            FinalText = run.FinalText
        };
    }

    private static MetaRunDerivedProposalSummary[] ApplyReviewSummary(
        MetaRunDerivedProposalSummary[] proposals,
        IReadOnlyDictionary<string, MetaRunProposalReviewRecord> reviews)
    {
        if (proposals.Length == 0)
            return proposals;

        return [..
            proposals.Select(proposal =>
            {
                if (!reviews.TryGetValue(proposal.Id, out var review))
                    return proposal;

                return new MetaRunDerivedProposalSummary
                {
                    Id = proposal.Id,
                    RunId = proposal.RunId,
                    SkillName = proposal.SkillName,
                    Status = proposal.Status,
                    Kind = proposal.Kind,
                    Title = proposal.Title,
                    Summary = proposal.Summary,
                    Source = proposal.Source,
                    AvailableActions = [.. proposal.AvailableActions],
                    ReviewStatus = review.ReviewStatus,
                    ReviewedAtUtc = review.ReviewedAtUtc
                };
            })
        ];
    }

    private static MetaRunDerivedProposalDetail ApplyReviewDetail(
        MetaRunDerivedProposalDetail detail,
        MetaRunProposalReviewRecord? review,
        MetaRunProposalProvenanceDetail? provenance,
        MetaRunProposalLifecycleDetail? lifecycle,
        MetaRunProposalAuditDetail? audit,
        MetaRunProposalProvenanceTransition[]? provenanceHistory)
    {
        return new MetaRunDerivedProposalDetail
        {
            Id = detail.Id,
            RunId = detail.RunId,
            SkillName = detail.SkillName,
            Status = detail.Status,
            Kind = detail.Kind,
            Title = detail.Title,
            Summary = detail.Summary,
            Source = detail.Source,
            AvailableActions = [.. detail.AvailableActions],
            Checkpoint = detail.Checkpoint,
            Evidence = detail.Evidence,
            Provenance = provenance,
            Lifecycle = lifecycle,
            Audit = audit,
            ProvenanceHistory = provenanceHistory is null ? [] : [.. provenanceHistory],
            Review = review is null
                ? null
                : new MetaRunProposalReviewDetail
                {
                    Status = review.ReviewStatus,
                    ReviewedAtUtc = review.ReviewedAtUtc,
                    Reason = review.Reason
                },
            PendingStepId = detail.PendingStepId,
            PendingStepIds = [.. detail.PendingStepIds],
            BlockedStepIds = [.. detail.BlockedStepIds],
            TimelineStepIds = [.. detail.TimelineStepIds],
            Steps = [.. detail.Steps],
            ErrorCode = detail.ErrorCode,
            Error = detail.Error,
            FinalText = detail.FinalText
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, MetaRunProposalReviewRecord>> LoadMetaRunLearningReviewsAsync(
        ILearningProposalStore store,
        string sessionId,
        CancellationToken ct)
    {
        var candidates = await store.ListProposalsAsync(status: null, kind: LearningProposalKind.MetaRunProposal, ct);
        return candidates
            .Where(item => TryGetMetaRunProposalMetadata(item, MetaRunProposalMetadata.SessionId, out var value)
                && string.Equals(value, sessionId, StringComparison.Ordinal))
            .Where(item => TryGetMetaRunProposalMetadata(item, MetaRunProposalMetadata.ProposalId, out _))
            .ToDictionary(
                item => item.Metadata[MetaRunProposalMetadata.ProposalId],
                item => ToMetaRunProposalReviewRecord(item, sessionId, item.Metadata[MetaRunProposalMetadata.ProposalId]),
                StringComparer.Ordinal);
    }

    private static async ValueTask<MetaRunProposalReviewRecord?> GetMetaRunLearningReviewAsync(
        ILearningProposalStore store,
        string sessionId,
        string proposalId,
        CancellationToken ct)
    {
        var durableProposal = await store.GetProposalAsync(BuildMetaRunProposalDurableId(sessionId, proposalId), ct);
        return durableProposal is null
            ? null
            : ToMetaRunProposalReviewRecord(durableProposal, sessionId, proposalId);
    }

    private static async ValueTask<LearningProposal?> GetMetaRunLearningProposalAsync(
        ILearningProposalStore store,
        string sessionId,
        string proposalId,
        CancellationToken ct)
        => await store.GetProposalAsync(BuildMetaRunProposalDurableId(sessionId, proposalId), ct);

    private static void PopulateMetaRunProposalProvenanceMetadata(
        IDictionary<string, string> metadata,
        SessionMetaRunRecord run,
        SessionMetaExecutionCheckpoint? checkpoint,
        DateTimeOffset capturedAtUtc)
    {
        metadata[MetaRunProposalMetadata.ProvenanceSnapshotVersion] = "v1";
        metadata[MetaRunProposalMetadata.ProvenanceCapturedAtUtc] = capturedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.ProvenanceRunStatus] = run.Status;
        metadata[MetaRunProposalMetadata.ProvenanceRunStartedAtUtc] = run.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.ProvenanceRunCompletedAtUtc] = run.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.ProvenanceStepCount] = run.StepResults.Count.ToString(CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.ProvenanceStepIds] = string.Join(",", run.StepResults.Select(static step => step.Id));

        if (!string.IsNullOrWhiteSpace(checkpoint?.PendingStepId))
            metadata[MetaRunProposalMetadata.ProvenanceCheckpointPendingStepId] = checkpoint.PendingStepId;

        metadata[MetaRunProposalMetadata.ProvenanceCheckpointPromptPresent] = (!string.IsNullOrWhiteSpace(checkpoint?.Prompt))
            .ToString()
            .ToLowerInvariant();
    }

    private static MetaRunProposalProvenanceDetail? BuildMetaRunProposalProvenanceDetail(LearningProposal? proposal)
    {
        if (proposal is null
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceSnapshotVersion, out var snapshotVersion)
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceCapturedAtUtc, out var capturedAtRaw)
            || !DateTimeOffset.TryParse(capturedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var capturedAtUtc)
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceRunStatus, out var runStatus)
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceRunStartedAtUtc, out var runStartedRaw)
            || !DateTimeOffset.TryParse(runStartedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var runStartedAtUtc)
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceRunCompletedAtUtc, out var runCompletedRaw)
            || !DateTimeOffset.TryParse(runCompletedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var runCompletedAtUtc)
            || !TryGetMetaRunProposalMetadata(proposal, MetaRunProposalMetadata.ProvenanceStepCount, out var stepCountRaw)
            || !int.TryParse(stepCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stepCount))
        {
            return null;
        }

        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.ProvenanceStepIds, out var stepIdsRaw);
        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.ProvenanceCheckpointPendingStepId, out var checkpointPendingStepId);
        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.ProvenanceCheckpointPromptPresent, out var checkpointPromptPresentRaw);
        var checkpointPromptPresent = string.Equals(checkpointPromptPresentRaw, "true", StringComparison.OrdinalIgnoreCase);

        return new MetaRunProposalProvenanceDetail
        {
            SnapshotVersion = snapshotVersion,
            CapturedAtUtc = capturedAtUtc,
            RunStatus = runStatus,
            RunStartedAtUtc = runStartedAtUtc,
            RunCompletedAtUtc = runCompletedAtUtc,
            StepCount = stepCount,
            StepIds = string.IsNullOrWhiteSpace(stepIdsRaw)
                ? []
                : [.. stepIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            CheckpointPendingStepId = string.IsNullOrWhiteSpace(checkpointPendingStepId) ? null : checkpointPendingStepId,
            CheckpointPromptPresent = checkpointPromptPresent
        };
    }

    private static MetaRunProposalLifecycleDetail? BuildMetaRunProposalLifecycleDetail(LearningProposal? proposal)
    {
        if (proposal is null)
            return null;

        return new MetaRunProposalLifecycleDetail
        {
            Status = proposal.Status,
            RolledBack = proposal.RolledBack,
            ReviewedAtUtc = proposal.ReviewedAtUtc,
            RolledBackAtUtc = proposal.RolledBackAtUtc,
            ReviewNotes = proposal.ReviewNotes,
            RollbackReason = proposal.RollbackReason
        };
    }

    private static MetaRunProposalAuditDetail? BuildMetaRunProposalAuditDetail(LearningProposal? proposal)
    {
        if (proposal is null)
            return null;

        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.AuditSchemaVersion, out var schemaVersionRaw);
        var schemaVersion = string.IsNullOrWhiteSpace(schemaVersionRaw) ? "v1" : schemaVersionRaw;

        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.LastTransitionActorId, out var actorIdRaw);
        var actorId = string.IsNullOrWhiteSpace(actorIdRaw) ? null : actorIdRaw;

        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.LastTransitionAction, out var transitionActionRaw);
        var transitionAction = string.IsNullOrWhiteSpace(transitionActionRaw) ? null : transitionActionRaw;

        proposal.Metadata.TryGetValue(MetaRunProposalMetadata.LastTransitionChangedAtUtc, out var changedAtRaw);
        DateTimeOffset? changedAtUtc = null;
        if (!string.IsNullOrWhiteSpace(changedAtRaw)
            && DateTimeOffset.TryParse(changedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            changedAtUtc = parsed;
        }

        if (actorId is null && transitionAction is null && changedAtUtc is null)
            return null;

        return new MetaRunProposalAuditDetail
        {
            SchemaVersion = schemaVersion,
            ActorId = actorId,
            ChangedAtUtc = changedAtUtc,
            TransitionAction = transitionAction
        };
    }

    private static MetaRunProposalProvenanceTransition[] BuildMetaRunProposalProvenanceHistory(LearningProposal? proposal)
    {
        if (proposal is null
            || !proposal.Metadata.TryGetValue(MetaRunProposalMetadata.TransitionCount, out var countRaw)
            || !int.TryParse(countRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || count <= 0)
        {
            return [];
        }

        var transitions = new List<MetaRunProposalProvenanceTransition>(count);
        for (var index = 0; index < count; index++)
        {
            if (!proposal.Metadata.TryGetValue(BuildMetaRunProposalTransitionMetadataKey(index, MetaRunProposalMetadata.TransitionFieldAction), out var action)
                || string.IsNullOrWhiteSpace(action)
                || !proposal.Metadata.TryGetValue(BuildMetaRunProposalTransitionMetadataKey(index, MetaRunProposalMetadata.TransitionFieldFromStatus), out var fromStatus)
                || string.IsNullOrWhiteSpace(fromStatus)
                || !proposal.Metadata.TryGetValue(BuildMetaRunProposalTransitionMetadataKey(index, MetaRunProposalMetadata.TransitionFieldToStatus), out var toStatus)
                || string.IsNullOrWhiteSpace(toStatus)
                || !proposal.Metadata.TryGetValue(BuildMetaRunProposalTransitionMetadataKey(index, MetaRunProposalMetadata.TransitionFieldChangedAtUtc), out var changedAtRaw)
                || !DateTimeOffset.TryParse(changedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var changedAtUtc))
            {
                continue;
            }

            proposal.Metadata.TryGetValue(BuildMetaRunProposalTransitionMetadataKey(index, MetaRunProposalMetadata.TransitionFieldReason), out var reasonRaw);
            transitions.Add(new MetaRunProposalProvenanceTransition
            {
                Action = action,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                ChangedAtUtc = changedAtUtc,
                Reason = string.IsNullOrWhiteSpace(reasonRaw) ? null : reasonRaw
            });
        }

        return [.. transitions];
    }

    private static void AppendMetaRunProposalTransitionMetadata(
        IDictionary<string, string> metadata,
        string action,
        string fromStatus,
        string toStatus,
        DateTimeOffset changedAtUtc,
        string? actorId,
        string? reason)
    {
        var nextIndex = 0;
        if (metadata.TryGetValue(MetaRunProposalMetadata.TransitionCount, out var countRaw)
            && int.TryParse(countRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentCount)
            && currentCount > 0)
        {
            nextIndex = currentCount;
        }

        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldAction)] = action;
        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldFromStatus)] = fromStatus;
        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldToStatus)] = toStatus;
        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldChangedAtUtc)] = changedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldActorId)] = string.IsNullOrWhiteSpace(actorId)
            ? string.Empty
            : actorId!;
        metadata[BuildMetaRunProposalTransitionMetadataKey(nextIndex, MetaRunProposalMetadata.TransitionFieldReason)] = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason!;
        metadata[MetaRunProposalMetadata.TransitionCount] = (nextIndex + 1).ToString(CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.AuditSchemaVersion] = "v1";
        metadata[MetaRunProposalMetadata.LastTransitionAction] = action;
        metadata[MetaRunProposalMetadata.LastTransitionChangedAtUtc] = changedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        metadata[MetaRunProposalMetadata.LastTransitionActorId] = string.IsNullOrWhiteSpace(actorId)
            ? string.Empty
            : actorId!;
    }

    private static string BuildMetaRunProposalTransitionMetadataKey(int index, string field)
        => $"{MetaRunProposalMetadata.TransitionEntryPrefix}_{index:D4}_{field}";

    private static string BuildMetaRunProposalDurableId(string sessionId, string proposalId)
        => $"meta-run-proposal:{sessionId}:{proposalId}";

    private static bool TryGetMetaRunProposalMetadata(LearningProposal proposal, string key, out string value)
    {
        if (proposal.Metadata.TryGetValue(key, out var metadataValue) && !string.IsNullOrWhiteSpace(metadataValue))
        {
            value = metadataValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryMapMetaRunProposalChangeTarget(string? to, out string reviewStatus, out string lifecycleStatus)
    {
        if (string.Equals(to, "accept", StringComparison.OrdinalIgnoreCase))
        {
            reviewStatus = MetaRunProposalReviewStatuses.Accepted;
            lifecycleStatus = LearningProposalStatus.Approved;
            return true;
        }

        if (string.Equals(to, "dismiss", StringComparison.OrdinalIgnoreCase))
        {
            reviewStatus = MetaRunProposalReviewStatuses.Dismissed;
            lifecycleStatus = LearningProposalStatus.Rejected;
            return true;
        }

        reviewStatus = string.Empty;
        lifecycleStatus = string.Empty;
        return false;
    }

    private static Dictionary<string, string> BuildMetaRunProposalMetadata(
        LearningProposal? existing,
        string sessionId,
        MetaRunDerivedProposalSummary proposal)
    {
        var metadata = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Metadata, StringComparer.Ordinal);

        metadata[MetaRunProposalMetadata.SessionId] = sessionId;
        metadata[MetaRunProposalMetadata.ProposalId] = proposal.Id;
        metadata[MetaRunProposalMetadata.RunId] = proposal.RunId;
        metadata[MetaRunProposalMetadata.ProposalStatus] = proposal.Status;
        metadata[MetaRunProposalMetadata.ProposalKind] = proposal.Kind;
        metadata[MetaRunProposalMetadata.Source] = proposal.Source;
        return metadata;
    }

    private static LearningProposal BuildUpdatedMetaRunLearningProposal(
        LearningProposal? existing,
        MetaRunDerivedProposalSummary proposal,
        string sessionId,
        string lifecycleStatus,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? reviewedAtUtc,
        string? reviewNotes,
        bool rolledBack,
        DateTimeOffset? rolledBackAtUtc,
        string? rollbackReason,
        Dictionary<string, string> metadata)
        => new()
        {
            Id = existing?.Id ?? BuildMetaRunProposalDurableId(sessionId, proposal.Id),
            Kind = LearningProposalKind.MetaRunProposal,
            Status = lifecycleStatus,
            ActorId = existing?.ActorId,
            Title = string.IsNullOrWhiteSpace(existing?.Title) ? proposal.Title : existing.Title,
            Summary = string.IsNullOrWhiteSpace(existing?.Summary) ? proposal.Summary : existing.Summary,
            SkillName = string.IsNullOrWhiteSpace(existing?.SkillName) ? proposal.SkillName : existing.SkillName,
            DraftContent = existing?.DraftContent,
            DraftContentHash = existing?.DraftContentHash,
            DraftPreview = existing?.DraftPreview,
            ProfileUpdate = existing?.ProfileUpdate,
            AppliedProfileBefore = existing?.AppliedProfileBefore,
            AutomationDraft = existing?.AutomationDraft,
            AutomationIntent = existing?.AutomationIntent,
            AutomationQuality = existing?.AutomationQuality,
            AutomationSuggestionPreview = existing?.AutomationSuggestionPreview,
            AppliedAutomationId = existing?.AppliedAutomationId,
            ManagedSkillPath = existing?.ManagedSkillPath,
            ManagedSkillMetadata = existing?.ManagedSkillMetadata,
            Metadata = metadata,
            HarnessEvolution = existing?.HarnessEvolution,
            SourceSessionIds = existing?.SourceSessionIds ?? [sessionId],
            SourceTurnIds = existing?.SourceTurnIds ?? [],
            ToolNames = existing?.ToolNames ?? [],
            ToolSequence = existing?.ToolSequence ?? [],
            ToolObservations = existing?.ToolObservations ?? [],
            FeedbackEvents = existing?.FeedbackEvents ?? [],
            RepeatedCount = existing?.RepeatedCount ?? 0,
            ProposalFingerprint = existing?.ProposalFingerprint,
            RiskLevel = string.IsNullOrWhiteSpace(existing?.RiskLevel) ? LearningProposalRiskLevels.Low : existing.RiskLevel,
            Confidence = existing?.Confidence ?? 1f,
            CreatedReason = existing?.CreatedReason ?? "meta_run_proposal_lifecycle",
            ValidationStatus = existing?.ValidationStatus ?? LearningProposalValidationStatuses.NotRun,
            ValidationWarnings = existing?.ValidationWarnings ?? [],
            ValidationErrors = existing?.ValidationErrors ?? [],
            CreatedAtUtc = existing?.CreatedAtUtc ?? updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            ReviewedAtUtc = reviewedAtUtc,
            ReviewNotes = reviewNotes,
            RolledBack = rolledBack,
            RolledBackAtUtc = rolledBackAtUtc,
            RollbackReason = rollbackReason
        };

    private static MetaRunProposalReviewRecord ToMetaRunProposalReviewRecord(LearningProposal proposal, string sessionId, string proposalId)
        => new()
        {
            SessionId = sessionId,
            ProposalId = proposalId,
            ReviewStatus = MapLearningProposalStatusToReviewStatus(proposal.Status),
            ReviewedAtUtc = proposal.ReviewedAtUtc ?? proposal.UpdatedAtUtc,
            Reason = string.Equals(proposal.Status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase)
                ? proposal.RollbackReason
                : proposal.ReviewNotes
        };

    private static string MapReviewStatusToLearningProposalStatus(string reviewStatus)
        => reviewStatus switch
        {
            MetaRunProposalReviewStatuses.Accepted => LearningProposalStatus.Approved,
            MetaRunProposalReviewStatuses.Dismissed => LearningProposalStatus.Rejected,
            MetaRunProposalReviewStatuses.RolledBack => LearningProposalStatus.RolledBack,
            _ => LearningProposalStatus.Pending
        };

    private static string MapLearningProposalStatusToReviewStatus(string lifecycleStatus)
        => lifecycleStatus switch
        {
            LearningProposalStatus.Approved => MetaRunProposalReviewStatuses.Accepted,
            LearningProposalStatus.Rejected => MetaRunProposalReviewStatuses.Dismissed,
            LearningProposalStatus.RolledBack => MetaRunProposalReviewStatuses.RolledBack,
            _ => MetaRunProposalReviewStatuses.Pending
        };

    private static class MetaRunProposalMetadata
    {
        public const string SessionId = "meta_run_proposal_session_id";
        public const string ProposalId = "meta_run_proposal_id";
        public const string RunId = "meta_run_proposal_run_id";
        public const string ProposalStatus = "meta_run_proposal_status";
        public const string ProposalKind = "meta_run_proposal_kind";
        public const string Reason = "meta_run_proposal_reason";
        public const string Source = "meta_run_proposal_source";
        public const string ProvenanceSnapshotVersion = "meta_run_proposal_provenance_snapshot_version";
        public const string ProvenanceCapturedAtUtc = "meta_run_proposal_provenance_captured_at_utc";
        public const string ProvenanceRunStatus = "meta_run_proposal_provenance_run_status";
        public const string ProvenanceRunStartedAtUtc = "meta_run_proposal_provenance_run_started_at_utc";
        public const string ProvenanceRunCompletedAtUtc = "meta_run_proposal_provenance_run_completed_at_utc";
        public const string ProvenanceStepCount = "meta_run_proposal_provenance_step_count";
        public const string ProvenanceStepIds = "meta_run_proposal_provenance_step_ids";
        public const string ProvenanceCheckpointPendingStepId = "meta_run_proposal_provenance_checkpoint_pending_step_id";
        public const string ProvenanceCheckpointPromptPresent = "meta_run_proposal_provenance_checkpoint_prompt_present";
        public const string TransitionCount = "meta_run_proposal_transition_count";
        public const string TransitionEntryPrefix = "meta_run_proposal_transition";
        public const string TransitionFieldAction = "action";
        public const string TransitionFieldFromStatus = "from_status";
        public const string TransitionFieldToStatus = "to_status";
        public const string TransitionFieldChangedAtUtc = "changed_at_utc";
        public const string TransitionFieldActorId = "actor_id";
        public const string TransitionFieldReason = "reason";
        public const string AuditSchemaVersion = "meta_run_proposal_audit_schema_version";
        public const string LastTransitionAction = "meta_run_proposal_last_transition_action";
        public const string LastTransitionChangedAtUtc = "meta_run_proposal_last_transition_changed_at_utc";
        public const string LastTransitionActorId = "meta_run_proposal_last_transition_actor_id";
    }

    private static void WriteDerivedProposalListText(MetaRunDerivedProposalListResponse response)
    {
        Console.WriteLine($"Session: {response.SessionId}");
        Console.WriteLine($"Derived proposals: {response.Count}");

        foreach (var proposal in response.Proposals)
        {
            Console.WriteLine();
            Console.WriteLine($"Proposal: {proposal.Id}");
            Console.WriteLine($"Run: {proposal.RunId}");
            Console.WriteLine($"Skill: {proposal.SkillName}");
            Console.WriteLine($"Status: {proposal.Status}");
            Console.WriteLine($"Kind: {proposal.Kind}");
            Console.WriteLine($"Title: {proposal.Title}");
            Console.WriteLine($"Summary: {proposal.Summary}");
            Console.WriteLine($"Source: {proposal.Source}");
            Console.WriteLine($"Available actions: {string.Join(", ", proposal.AvailableActions)}");
            Console.WriteLine($"Review status: {proposal.ReviewStatus}");
            if (proposal.ReviewedAtUtc is not null)
                Console.WriteLine($"Reviewed at (UTC): {proposal.ReviewedAtUtc:O}");
        }
    }

    private static void WriteDerivedProposalDetailText(MetaRunDerivedProposalDetailResponse response)
    {
        var proposal = response.Proposal;
        Console.WriteLine($"Session: {response.SessionId}");
        Console.WriteLine($"Proposal: {proposal.Id}");
        Console.WriteLine($"Run: {proposal.RunId}");
        Console.WriteLine($"Skill: {proposal.SkillName}");
        Console.WriteLine($"Status: {proposal.Status}");
        Console.WriteLine($"Kind: {proposal.Kind}");
        Console.WriteLine($"Title: {proposal.Title}");
        Console.WriteLine($"Summary: {proposal.Summary}");
        Console.WriteLine($"Source: {proposal.Source}");
        Console.WriteLine($"Available actions: {string.Join(", ", proposal.AvailableActions)}");
        if (proposal.Review is not null)
        {
            Console.WriteLine("Review:");
            Console.WriteLine($"Status: {proposal.Review.Status}");
            Console.WriteLine($"Reviewed at (UTC): {proposal.Review.ReviewedAtUtc:O}");
            if (!string.IsNullOrWhiteSpace(proposal.Review.Reason))
                Console.WriteLine($"Reason: {proposal.Review.Reason}");
        }
        if (proposal.Checkpoint is not null)
        {
            Console.WriteLine("Checkpoint:");
            Console.WriteLine($"Pending step: {proposal.Checkpoint.PendingStepId}");
            if (proposal.Checkpoint.PendingStepIds.Length > 0)
                Console.WriteLine($"Pending steps: {string.Join(", ", proposal.Checkpoint.PendingStepIds)}");
            if (proposal.Checkpoint.BlockedStepIds.Length > 0)
                Console.WriteLine($"Blocked steps: {string.Join(", ", proposal.Checkpoint.BlockedStepIds)}");
            Console.WriteLine(proposal.Checkpoint.PromptPresent ? "Prompt present: yes" : "Prompt present: no");
            if (proposal.Checkpoint.OutputStepIds.Length > 0)
                Console.WriteLine($"Output steps: {string.Join(", ", proposal.Checkpoint.OutputStepIds)}");
            if (proposal.Checkpoint.FailureAliasStepIds.Length > 0)
                Console.WriteLine($"Failure alias steps: {string.Join(", ", proposal.Checkpoint.FailureAliasStepIds)}");
        }
        if (proposal.Evidence is not null)
        {
            Console.WriteLine("Evidence:");
            if (proposal.Evidence.TimelineStepIds.Length > 0)
                Console.WriteLine($"Evidence timeline steps: {string.Join(", ", proposal.Evidence.TimelineStepIds)}");
            if (!string.IsNullOrWhiteSpace(proposal.Evidence.ErrorCode))
                Console.WriteLine($"Evidence error code: {proposal.Evidence.ErrorCode}");
            if (!string.IsNullOrWhiteSpace(proposal.Evidence.Error))
                Console.WriteLine($"Evidence error: {proposal.Evidence.Error}");
            if (!string.IsNullOrWhiteSpace(proposal.Evidence.FinalText))
                Console.WriteLine($"Evidence final text: {proposal.Evidence.FinalText}");
        }
        if (proposal.Provenance is not null)
        {
            Console.WriteLine("Provenance:");
            Console.WriteLine($"Snapshot version: {proposal.Provenance.SnapshotVersion}");
            Console.WriteLine($"Captured at (UTC): {proposal.Provenance.CapturedAtUtc:O}");
            Console.WriteLine($"Run status snapshot: {proposal.Provenance.RunStatus}");
            Console.WriteLine($"Step count snapshot: {proposal.Provenance.StepCount}");
            if (proposal.Provenance.StepIds.Length > 0)
                Console.WriteLine($"Step ids snapshot: {string.Join(", ", proposal.Provenance.StepIds)}");
            if (!string.IsNullOrWhiteSpace(proposal.Provenance.CheckpointPendingStepId))
                Console.WriteLine($"Checkpoint pending step snapshot: {proposal.Provenance.CheckpointPendingStepId}");
            Console.WriteLine(proposal.Provenance.CheckpointPromptPresent
                ? "Checkpoint prompt snapshot: yes"
                : "Checkpoint prompt snapshot: no");
        }
        if (proposal.Lifecycle is not null)
        {
            Console.WriteLine("Lifecycle:");
            Console.WriteLine($"Status: {proposal.Lifecycle.Status}");
            Console.WriteLine(proposal.Lifecycle.RolledBack ? "Rolled back: yes" : "Rolled back: no");
            if (proposal.Lifecycle.ReviewedAtUtc is not null)
                Console.WriteLine($"Reviewed at (UTC): {proposal.Lifecycle.ReviewedAtUtc:O}");
            if (proposal.Lifecycle.RolledBackAtUtc is not null)
                Console.WriteLine($"Rolled back at (UTC): {proposal.Lifecycle.RolledBackAtUtc:O}");
            if (!string.IsNullOrWhiteSpace(proposal.Lifecycle.ReviewNotes))
                Console.WriteLine($"Review notes: {proposal.Lifecycle.ReviewNotes}");
            if (!string.IsNullOrWhiteSpace(proposal.Lifecycle.RollbackReason))
                Console.WriteLine($"Rollback reason: {proposal.Lifecycle.RollbackReason}");
        }
        if (proposal.ProvenanceHistory.Length > 0)
        {
            Console.WriteLine("Provenance history:");
            foreach (var transition in proposal.ProvenanceHistory)
            {
                var detail = $"- {transition.Action} | {transition.FromStatus} -> {transition.ToStatus} | changedAtUtc={transition.ChangedAtUtc:O}";
                if (!string.IsNullOrWhiteSpace(transition.Reason))
                    detail += $" | reason={transition.Reason}";
                Console.WriteLine(detail);
            }
        }
        if (proposal.TimelineStepIds.Length > 0)
            Console.WriteLine($"Timeline steps: {string.Join(", ", proposal.TimelineStepIds)}");
        if (proposal.Steps.Length > 0)
        {
            Console.WriteLine("Steps:");
            foreach (var step in proposal.Steps)
            {
                var detail = $"- {step.Id} | kind={step.Kind} | status={step.Status}";
                if (!string.IsNullOrWhiteSpace(step.FailureCode))
                    detail += $" | failureCode={step.FailureCode}";
                detail += $" | durationMs={step.DurationMs:0.###############}";
                detail += $" | continued={step.Continued.ToString().ToLowerInvariant()}";
                Console.WriteLine(detail);
            }
        }
        if (!string.IsNullOrWhiteSpace(proposal.ErrorCode))
            Console.WriteLine($"Error code: {proposal.ErrorCode}");
        if (!string.IsNullOrWhiteSpace(proposal.Error))
            Console.WriteLine($"Error: {proposal.Error}");
        if (!string.IsNullOrWhiteSpace(proposal.FinalText))
            Console.WriteLine($"Final text: {proposal.FinalText}");
    }

    private static void WriteProposalReviewMutationText(MetaRunProposalReviewMutationResponse response)
    {
        Console.WriteLine($"Session: {response.SessionId}");
        Console.WriteLine($"Proposal: {response.ProposalId}");
        Console.WriteLine($"Review status: {response.ReviewStatus}");
        Console.WriteLine($"Lifecycle status: {response.LifecycleStatus}");
        Console.WriteLine($"Reviewed at (UTC): {response.ReviewedAtUtc:O}");
        Console.WriteLine(response.AlreadyReviewed ? "Already reviewed: yes" : "Already reviewed: no");
        if (!string.IsNullOrWhiteSpace(response.Reason))
            Console.WriteLine($"Reason: {response.Reason}");
        if (!string.IsNullOrWhiteSpace(response.Audit?.ActorId))
            Console.WriteLine($"Actor id: {response.Audit.ActorId}");
        if (!string.IsNullOrWhiteSpace(response.Audit?.TransitionAction))
            Console.WriteLine($"Transition action: {response.Audit.TransitionAction}");
    }

    private static MetaRunReplayPreviewResponse BuildReplayPreview(string sessionId, SessionMetaRunRecord run)
    {
        var missingRequirements = GetReplayMissingRequirements(run);
        var operatorSummary = BuildReplayOperatorSummary(run);
        return new MetaRunReplayPreviewResponse
        {
            SessionId = sessionId,
            RunId = run.RunId,
            SkillName = run.SkillName,
            ReplayAvailable = false,
            Reason = MetaRunReplayReasons.NotEnoughInputsForExecutableReplay,
            AvailableArtifacts = GetReplayAvailableArtifacts(run),
            RetainedSteps = GetReplayRetainedSteps(run),
            Plan = GetReplayPlan(run, missingRequirements),
            MissingRequirements = missingRequirements,
            OperatorSummary = operatorSummary,
            TriageHints = BuildReplayTriageHints(run, missingRequirements, operatorSummary)
        };
    }

    private static MetaRunReplayResultResponse BuildReplayResult(
        string sessionId,
        SessionMetaRunRecord run,
        SessionMetaExecutionCheckpoint? checkpoint)
    {
        var checkpointSummary = TryBuildReplayCheckpointSummary(run, checkpoint);
        var operatorSummary = BuildReplayOperatorSummary(run);
        return new MetaRunReplayResultResponse
        {
            SessionId = sessionId,
            RunId = run.RunId,
            SkillName = run.SkillName,
            Mode = MetaRunReplayExecutionModes.AuditReconstruction,
            Status = run.Status,
            Source = checkpointSummary is null
                ? MetaRunReplayExecutionSources.HistoryOnly
                : MetaRunReplayExecutionSources.HistoryPlusCheckpoint,
            FinalText = run.FinalText,
            Error = run.Error,
            ErrorCode = run.ErrorCode,
            Timeline = [.. run.StepResults.Select(static (step, index) => new MetaRunReplayTimelineItem
            {
                Sequence = index + 1,
                StepId = step.Id,
                Kind = step.Kind,
                Status = step.Status,
                FailureCode = step.FailureCode,
                DurationMs = step.DurationMs,
                Continued = step.Continued,
                Source = MetaRunReplayTimelineSources.RunHistory,
                Notes = BuildReplayTimelineNotes(step)
            })],
            Checkpoint = checkpointSummary,
            ProposalSummary = new MetaRunProposalSummary(),
            OperatorSummary = operatorSummary,
            TriageHints = BuildReplayTriageHints(run, [], operatorSummary)
        };
    }

    private static MetaRunReplayCheckpointSummary? TryBuildReplayCheckpointSummary(
        SessionMetaRunRecord run,
        SessionMetaExecutionCheckpoint? checkpoint)
    {
        if (!string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase)
            || checkpoint is null
            || !string.Equals(checkpoint.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(checkpoint.PendingStepId))
        {
            return null;
        }

        return new MetaRunReplayCheckpointSummary
        {
            PendingStepId = checkpoint.PendingStepId,
            PendingStepIds = [.. checkpoint.PendingStepIds],
            BlockedStepIds = [.. checkpoint.BlockedStepIds],
            PromptPresent = !string.IsNullOrWhiteSpace(checkpoint.Prompt),
            OutputStepIds = [.. checkpoint.Outputs.Keys.OrderBy(static key => key, StringComparer.Ordinal)],
            FailureAliasStepIds = [.. checkpoint.FailureAliases.Keys.OrderBy(static key => key, StringComparer.Ordinal)]
        };
    }

    private static void WriteReplayResultText(MetaRunReplayResultResponse replay)
    {
        Console.WriteLine($"Replay reconstruction for run: {replay.RunId}");
        Console.WriteLine($"Session: {replay.SessionId}");
        Console.WriteLine($"Skill: {replay.SkillName}");
        Console.WriteLine($"Mode: {replay.Mode}");
        Console.WriteLine($"Source: {replay.Source}");
        Console.WriteLine($"Status: {replay.Status}");
        if (!string.IsNullOrWhiteSpace(replay.FinalText))
            Console.WriteLine($"Final text: {replay.FinalText}");
        if (!string.IsNullOrWhiteSpace(replay.ErrorCode))
            Console.WriteLine($"Error code: {replay.ErrorCode}");
        if (!string.IsNullOrWhiteSpace(replay.Error))
            Console.WriteLine($"Error: {replay.Error}");

        WriteReplayOperatorDiagnosticsText(replay.OperatorSummary, replay.TriageHints);

        Console.WriteLine("Timeline:");
        foreach (var item in replay.Timeline)
            Console.WriteLine(FormatReplayTimelineItem(item));

        if (replay.Checkpoint is not null)
        {
            Console.WriteLine("Checkpoint:");
            Console.WriteLine($"Pending step: {replay.Checkpoint.PendingStepId}");
            if (replay.Checkpoint.PendingStepIds.Length > 0)
                Console.WriteLine($"Pending steps: {string.Join(", ", replay.Checkpoint.PendingStepIds)}");
            if (replay.Checkpoint.BlockedStepIds.Length > 0)
                Console.WriteLine($"Blocked steps: {string.Join(", ", replay.Checkpoint.BlockedStepIds)}");
            Console.WriteLine(replay.Checkpoint.PromptPresent ? "Prompt retained: yes" : "Prompt retained: no");
        }

        Console.WriteLine("Proposal summary:");
        Console.WriteLine(replay.ProposalSummary.Available ? "Available: yes" : "Available: no");
        Console.WriteLine($"Reason: {replay.ProposalSummary.Reason}");
    }

    private static string FormatReplayTimelineItem(MetaRunReplayTimelineItem item)
    {
        var line = $"- {item.Sequence} | step={item.StepId} | kind={item.Kind} | status={item.Status} | duration_ms={item.DurationMs:0.###}";
        if (!string.IsNullOrWhiteSpace(item.FailureCode))
            line += $" | failure_code={item.FailureCode}";
        if (item.Continued)
            line += " | continued=true";
        if (!string.IsNullOrWhiteSpace(item.Notes))
            line += $" | notes={item.Notes}";

        return line;
    }

    private static string FormatReplayRequirement(MetaRunReplayRequirementPreview requirement)
        => $"{requirement.Name} | kind={requirement.Kind} | reason={requirement.Reason}";

    private static string FormatReplayRetainedStep(MetaRunReplayStepPreview step)
    {
        var line = $"- {step.Id} | kind={step.Kind} | status={step.Status} | duration_ms={step.DurationMs:0.###}";
        if (!string.IsNullOrWhiteSpace(step.FailureCode))
            line += $" | failure_code={step.FailureCode}";
        if (step.Continued)
            line += " | continued=true";

        return line;
    }

    private static string[] GetReplayAvailableArtifacts(SessionMetaRunRecord run)
    {
        var artifacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(run.FinalText))
            artifacts.Add(MetaRunReplayArtifactNames.FinalText);
        if (!string.IsNullOrWhiteSpace(run.ErrorCode))
            artifacts.Add(MetaRunReplayArtifactNames.ErrorCode);
        if (!string.IsNullOrWhiteSpace(run.Error))
            artifacts.Add(MetaRunReplayArtifactNames.ErrorMessage);
        if (HasRetainedSteps(run))
            artifacts.Add(MetaRunReplayArtifactNames.StepResults);

        return [.. artifacts];
    }

    private static MetaRunReplayRequirementPreview[] GetReplayMissingRequirements(SessionMetaRunRecord run)
    {
        var requirements = new List<MetaRunReplayRequirementPreview>
        {
            new()
            {
                Name = MetaRunReplayRequirementNames.PromptContext,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.PromptContextNotPersisted
            },
            new()
            {
                Name = MetaRunReplayRequirementNames.StepInputs,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.StepInputsNotPersisted
            },
            new()
            {
                Name = MetaRunReplayRequirementNames.ToolArguments,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.ToolArgumentsNotPersisted
            }
        };

        if (run.StepResults.Any(static step =>
                string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)
                && step.ExecutionEvidence is null))
        {
            requirements.Add(new MetaRunReplayRequirementPreview
            {
                Name = MetaRunReplayRequirementNames.SkillExecInputs,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.SkillExecInputsNotPersisted
            });
        }

        if (!HasRetainedSteps(run))
        {
            requirements.Add(new MetaRunReplayRequirementPreview
            {
                Name = MetaRunReplayRequirementNames.StepResults,
                Kind = MetaRunReplayRequirementKinds.NotRetained,
                Reason = MetaRunReplayRequirementReasons.StepResultsNotRetained
            });
        }

        return [.. requirements];
    }

    private static MetaRunReplayStepPreview[] GetReplayRetainedSteps(SessionMetaRunRecord run)
        => [.. run.StepResults.Select(static step => new MetaRunReplayStepPreview
        {
            Id = step.Id,
            Kind = step.Kind,
            Status = step.Status,
            FailureCode = step.FailureCode,
            DurationMs = step.DurationMs,
            Continued = step.Continued
        })];

    private static MetaRunReplayPlanPreview GetReplayPlan(SessionMetaRunRecord run, MetaRunReplayRequirementPreview[] missingRequirements)
    {
        return new MetaRunReplayPlanPreview
        {
            Summary = GetReplayPlanSummary(run),
            Mode = MetaRunReplayModes.PreviewOnly,
            Executable = false,
            ReplayableSteps = [.. run.StepResults.Select(GetReplayStepReadiness)],
            BlockedByRequirements = missingRequirements
        };
    }

    private static MetaRunReplayStepReadinessPreview GetReplayStepReadiness(SessionMetaStepResult step)
    {
        if ((string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(step.FailureCode))
            && step.Continued)
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.FailureTraceContinued,
                Reason = MetaRunReplayStepReadinessReasons.FailureTraceContinued
            };
        }

        if (string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(step.FailureCode))
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.FailureTraceOnly,
                Reason = MetaRunReplayStepReadinessReasons.FailureTraceOnly
            };
        }

        if (step.Continued)
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.ContinuationTraceOnly,
                Reason = MetaRunReplayStepReadinessReasons.ContinuationTraceOnly
            };
        }

        return new MetaRunReplayStepReadinessPreview
        {
            Id = step.Id,
            Readiness = MetaRunReplayStepReadinessKinds.TraceOnly,
            Reason = MetaRunReplayStepReadinessReasons.TraceOnly
        };
    }

    private static string GetReplayPlanSummary(SessionMetaRunRecord run)
        => HasRetainedSteps(run)
            ? MetaRunReplayPlanSummaries.AuditableNotReplayable
            : MetaRunReplayPlanSummaries.MetadataOnlyNotReplayable;

    private static MetaRunReplayOperatorSummary BuildReplayOperatorSummary(SessionMetaRunRecord run)
    {
        return new MetaRunReplayOperatorSummary
        {
            TotalSteps = run.StepResults.Count,
            FailedSteps = run.StepResults.Count(static step =>
                string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(step.FailureCode)),
            ContinuedSteps = run.StepResults.Count(static step => step.Continued),
            SkillExecSteps = run.StepResults.Count(static step => string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)),
            SkillExecStepsWithoutEvidence = run.StepResults.Count(static step =>
                string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)
                && step.ExecutionEvidence is null),
            StepKinds = [.. run.StepResults
                .GroupBy(static step => step.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new MetaRunReplayCountBucket
                {
                    Name = group.Key,
                    Count = group.Count()
                })],
            FailureClusters = [.. run.StepResults
                .Where(static step => !string.IsNullOrWhiteSpace(step.FailureCode))
                .GroupBy(static step => step.FailureCode!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new MetaRunReplayCountBucket
                {
                    Name = group.Key,
                    Count = group.Count()
                })]
        };
    }

    private static MetaRunReplayTriageHint[] BuildReplayTriageHints(
        SessionMetaRunRecord run,
        MetaRunReplayRequirementPreview[] missingRequirements,
        MetaRunReplayOperatorSummary summary)
    {
        var hints = new List<MetaRunReplayTriageHint>();

        var missingSkillExecInputs = missingRequirements.Any(static requirement =>
            string.Equals(requirement.Name, MetaRunReplayRequirementNames.SkillExecInputs, StringComparison.Ordinal));
        if (missingSkillExecInputs)
        {
            hints.Add(new MetaRunReplayTriageHint
            {
                Code = MetaRunReplayTriageHintCodes.SkillExecInputsNotPersisted,
                Priority = 1,
                Message = "Persist skill_exec execution evidence so replay can include stdin and parse diagnostics.",
                StepIds = [.. run.StepResults
                    .Where(static step => string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase) && step.ExecutionEvidence is null)
                    .Select(static step => step.Id)],
                RequirementNames = [MetaRunReplayRequirementNames.SkillExecInputs]
            });
        }

        var parseModeAnomalyStepIds = run.StepResults
            .Where(static step =>
                string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)
                && step.ExecutionEvidence is not null
                && !string.Equals(step.ExecutionEvidence.ParseMode, "text", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(step.ExecutionEvidence.ParseMode, "json", StringComparison.OrdinalIgnoreCase))
            .Select(static step => step.Id)
            .ToArray();
        if (parseModeAnomalyStepIds.Length > 0)
        {
            hints.Add(new MetaRunReplayTriageHint
            {
                Code = MetaRunReplayTriageHintCodes.SkillExecParseModeAnomaly,
                Priority = 2,
                Message = "Found skill_exec parse_mode values outside supported text/json set; validate skill definitions and evidence snapshots.",
                StepIds = parseModeAnomalyStepIds
            });
        }

        var likelyTruncatedCommandStepIds = run.StepResults
            .Where(static step =>
                string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)
                && step.ExecutionEvidence is not null
                && IsLikelyTruncatedCommandPreview(step.ExecutionEvidence.CommandPreview))
            .Select(static step => step.Id)
            .ToArray();
        if (likelyTruncatedCommandStepIds.Length > 0)
        {
            hints.Add(new MetaRunReplayTriageHint
            {
                Code = MetaRunReplayTriageHintCodes.SkillExecCommandPreviewPossiblyTruncated,
                Priority = 3,
                Message = "Command preview appears truncated; check original skill_exec args for full command context.",
                StepIds = likelyTruncatedCommandStepIds
            });
        }

        var dominantFailure = summary.FailureClusters.FirstOrDefault();
        if (dominantFailure is not null)
        {
            hints.Add(new MetaRunReplayTriageHint
            {
                Code = MetaRunReplayTriageHintCodes.DominantFailureCluster,
                Priority = 4,
                Message = $"Dominant failure cluster is '{dominantFailure.Name}' across {dominantFailure.Count} step(s); prioritize this class first.",
                StepIds = [.. run.StepResults
                    .Where(step => string.Equals(step.FailureCode, dominantFailure.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(static step => step.Id)]
            });
        }

        return [.. hints
            .OrderBy(static hint => hint.Priority)
            .ThenBy(static hint => hint.Code, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsLikelyTruncatedCommandPreview(string? commandPreview)
    {
        if (string.IsNullOrWhiteSpace(commandPreview))
            return false;

        return commandPreview.EndsWith("...", StringComparison.Ordinal)
            || commandPreview.EndsWith("…", StringComparison.Ordinal);
    }

    private static void WriteReplayOperatorDiagnosticsText(
        MetaRunReplayOperatorSummary summary,
        MetaRunReplayTriageHint[] triageHints)
    {
        Console.WriteLine("Operator summary:");
        Console.WriteLine($"Total steps: {summary.TotalSteps}");
        Console.WriteLine($"Failed steps: {summary.FailedSteps}");
        Console.WriteLine($"Continued steps: {summary.ContinuedSteps}");
        Console.WriteLine($"Skill exec steps: {summary.SkillExecSteps}");
        Console.WriteLine($"Skill exec without evidence: {summary.SkillExecStepsWithoutEvidence}");

        if (summary.StepKinds.Length > 0)
            Console.WriteLine($"Step kind mix: {string.Join(", ", summary.StepKinds.Select(static item => $"{item.Name}={item.Count}"))}");

        if (summary.FailureClusters.Length > 0)
            Console.WriteLine($"Failure clusters: {string.Join(", ", summary.FailureClusters.Select(static item => $"{item.Name}={item.Count}"))}");

        if (triageHints.Length == 0)
            return;

        Console.WriteLine("Priority triage:");
        foreach (var hint in triageHints)
        {
            var line = $"- P{hint.Priority} {hint.Code}: {hint.Message}";
            if (hint.StepIds.Length > 0)
                line += $" | steps={string.Join(",", hint.StepIds)}";
            if (hint.RequirementNames.Length > 0)
                line += $" | requirements={string.Join(",", hint.RequirementNames)}";

            Console.WriteLine(line);
        }
    }

    private static bool HasRetainedSteps(SessionMetaRunRecord run)
        => run.StepResults.Count > 0;

    private static string? BuildReplayTimelineNotes(SessionMetaStepResult step)
    {
        if (!string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase))
            return null;

        var evidence = step.ExecutionEvidence;
        if (evidence is null)
            return null;

        return $"input_mode={evidence.InputMode}; stdin_bytes={evidence.StdinBytes}; parse_mode={evidence.ParseMode}; command={evidence.CommandPreview}";
    }

    internal sealed class SkillCommandInspection
    {
        public required bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public required SkillDefinition Definition { get; init; }
        public required string SkillRootPath { get; init; }
        public required string SkillFilePath { get; init; }
        public required string InstallSlug { get; init; }
        public required string SourceLabel { get; init; }
        public required string TrustLevel { get; init; }
        public required string TrustReason { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public static SkillCommandInspection Failure(string errorMessage)
            => new()
            {
                Success = false,
                ErrorMessage = errorMessage,
                Definition = new SkillDefinition
                {
                    Name = string.Empty,
                    Description = string.Empty,
                    Instructions = string.Empty,
                    Location = string.Empty,
                    Source = SkillSource.Extra
                },
                SkillRootPath = string.Empty,
                SkillFilePath = string.Empty,
                InstallSlug = string.Empty,
                SourceLabel = string.Empty,
                TrustLevel = "untrusted",
                TrustReason = string.Empty
            };
    }
}

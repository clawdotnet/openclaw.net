using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal sealed class LearningService
{
    private static readonly Regex PreferenceRegex = new(@"\b(i prefer|call me|my name is|i like)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string ManagedSkillFileName = "SKILL.md";
    private const string ManagedSkillMetadataFileName = ".openclaw-learning.json";
    private readonly LearningConfig _config;
    private readonly ILearningProposalStore _proposalStore;
    private readonly IUserProfileStore _profileStore;
    private readonly IAutomationStore _automationStore;
    private readonly ISessionSearchStore _sessionSearchStore;
    private readonly ILogger<LearningService> _logger;
    private readonly LlmProviderRegistry? _providerRegistry;

    public LearningService(
        LearningConfig config,
        ILearningProposalStore proposalStore,
        IUserProfileStore profileStore,
        IAutomationStore automationStore,
        ISessionSearchStore sessionSearchStore,
        ILogger<LearningService> logger,
        LlmProviderRegistry? providerRegistry = null)
    {
        _config = config;
        _proposalStore = proposalStore;
        _profileStore = profileStore;
        _automationStore = automationStore;
        _sessionSearchStore = sessionSearchStore;
        _logger = logger;
        _providerRegistry = providerRegistry;
    }

    public ValueTask<IReadOnlyList<LearningProposal>> ListAsync(string? status, string? kind, CancellationToken ct)
        => _proposalStore.ListProposalsAsync(status, kind, ct);

    public ValueTask<LearningProposal?> GetAsync(string proposalId, CancellationToken ct)
        => _proposalStore.GetProposalAsync(proposalId, ct);

    public async ValueTask<LearningProposalDetailResponse?> GetDetailAsync(string proposalId, CancellationToken ct)
    {
        var proposal = await _proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        UserProfile? baselineProfile = null;
        UserProfile? currentProfile = null;
        IReadOnlyList<ProfileDiffEntry> profileDiff = [];
        var canRollback = false;

        if (proposal.ProfileUpdate is not null)
        {
            var actorId = proposal.ProfileUpdate.ActorId;
            currentProfile = await _profileStore.GetProfileAsync(actorId, ct);
            baselineProfile = string.Equals(proposal.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase)
                ? currentProfile
                : proposal.AppliedProfileBefore;
            profileDiff = BuildProfileDiff(baselineProfile, proposal.ProfileUpdate);
        }

        canRollback = string.Equals(proposal.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase) &&
                      !proposal.RolledBack &&
                      (string.Equals(proposal.Kind, LearningProposalKind.ProfileUpdate, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(proposal.Kind, LearningProposalKind.SkillDraft, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(proposal.Kind, LearningProposalKind.AutomationSuggestion, StringComparison.OrdinalIgnoreCase));

        return new LearningProposalDetailResponse
        {
            Proposal = proposal,
            BaselineProfile = baselineProfile,
            CurrentProfile = currentProfile,
            ProfileDiff = profileDiff,
            Provenance = new LearningProposalProvenance
            {
                ActorId = proposal.ActorId,
                SourceSessionIds = proposal.SourceSessionIds,
                SourceTurnIds = proposal.SourceTurnIds,
                ToolNames = proposal.ToolNames,
                ToolSequence = proposal.ToolSequence,
                ToolObservations = proposal.ToolObservations,
                RepeatedCount = proposal.RepeatedCount,
                ProposalFingerprint = proposal.ProposalFingerprint,
                CreatedReason = proposal.CreatedReason,
                Confidence = proposal.Confidence,
                CreatedAtUtc = proposal.CreatedAtUtc,
                UpdatedAtUtc = proposal.UpdatedAtUtc,
                ReviewedAtUtc = proposal.ReviewedAtUtc
            },
            CanRollback = canRollback
        };
    }

    public async ValueTask ObserveSessionAsync(Session session, CancellationToken ct)
    {
        if (!_config.Enabled || session.History.Count < 2)
            return;

        var lastUser = session.History.LastOrDefault(static turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase));
        var lastAssistant = session.History.LastOrDefault(static turn => string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) && turn.ToolCalls is { Count: > 1 });
        if (lastUser is null)
            return;

        var actorId = BuildActorId(session.ChannelId, session.SenderId);
        if (PreferenceRegex.IsMatch(lastUser.Content))
            await EnsureProfileProposalAsync(session, actorId, lastUser, ct);

        await EnsureAutomationProposalAsync(session, actorId, lastUser, ct);
        if (lastAssistant?.ToolCalls is { Count: > 1 })
            await EnsureSkillProposalAsync(session, actorId, lastAssistant, ct);
    }

    public async ValueTask<LearningProposal?> ApproveAsync(string proposalId, IAgentRuntime runtime, CancellationToken ct)
    {
        var proposal = await _proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        if (!string.Equals(proposal.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
            return proposal;

        UserProfile? appliedProfileBefore = proposal.AppliedProfileBefore;

        switch (proposal.Kind)
        {
            case LearningProposalKind.ProfileUpdate when proposal.ProfileUpdate is not null:
                appliedProfileBefore = await _profileStore.GetProfileAsync(proposal.ProfileUpdate.ActorId, ct);
                await _profileStore.SaveProfileAsync(proposal.ProfileUpdate, ct);
                break;
            case LearningProposalKind.AutomationSuggestion when proposal.AutomationDraft is not null:
                await _automationStore.SaveAutomationAsync(BuildManagedAutomationDraft(proposal.AutomationDraft, proposal.Id), ct);
                break;
            case LearningProposalKind.SkillDraft when !string.IsNullOrWhiteSpace(proposal.DraftContent):
                var validation = ValidateSkillDraft(proposal.SkillName ?? proposal.Title, proposal.DraftContent, proposal.DraftContentHash, proposal);
                if (validation.Errors.Count > 0)
                {
                    var rejected = CopyProposal(
                        proposal,
                        status: LearningProposalStatus.Rejected,
                        statusUpdatedAtUtc: DateTimeOffset.UtcNow,
                        reviewedAtUtc: DateTimeOffset.UtcNow,
                        reviewNotes: string.Join(" ", validation.Errors),
                        validationStatus: LearningProposalValidationStatuses.Error,
                        validationWarnings: validation.Warnings,
                        validationErrors: validation.Errors);
                    await _proposalStore.SaveProposalAsync(rejected, ct);
                    return rejected;
                }

                var conflictError = await GetManagedSkillTargetErrorAsync(proposal.SkillName ?? proposal.Title, proposal.Id, ct);
                if (conflictError is not null)
                {
                    var rejected = CopyProposal(
                        proposal,
                        status: LearningProposalStatus.Rejected,
                        statusUpdatedAtUtc: DateTimeOffset.UtcNow,
                        reviewedAtUtc: DateTimeOffset.UtcNow,
                        reviewNotes: conflictError,
                        validationStatus: LearningProposalValidationStatuses.Error,
                        validationWarnings: validation.Warnings,
                        validationErrors: validation.Errors.Append(conflictError).ToArray());
                    await _proposalStore.SaveProposalAsync(rejected, ct);
                    return rejected;
                }

                await SaveManagedSkillAsync(proposal.SkillName ?? proposal.Title, proposal.DraftContent, proposal.Id, proposal.DraftContentHash, ct);
                await runtime.ReloadSkillsAsync(ct);
                break;
        }

        var managedSkillPath = string.Equals(proposal.Kind, LearningProposalKind.SkillDraft, StringComparison.OrdinalIgnoreCase)
            ? GetManagedSkillPath(proposal.SkillName ?? proposal.Title)
            : proposal.ManagedSkillPath;
        var approvedAtUtc = DateTimeOffset.UtcNow;
        var approved = CopyProposal(
            proposal,
            status: LearningProposalStatus.Approved,
            appliedProfileBefore: appliedProfileBefore,
            automationDraft: proposal.AutomationDraft is null ? null : BuildManagedAutomationDraft(proposal.AutomationDraft, proposal.Id),
            appliedAutomationId: proposal.AutomationDraft?.Id ?? proposal.AppliedAutomationId,
            managedSkillPath: managedSkillPath,
            managedSkillMetadata: string.Equals(proposal.Kind, LearningProposalKind.SkillDraft, StringComparison.OrdinalIgnoreCase)
                ? new ManagedLearningSkillMetadata
                {
                    CreatedByProposalId = proposal.Id,
                    OriginalDraftHash = proposal.DraftContentHash,
                    ApprovedAtUtc = approvedAtUtc,
                    SkillName = proposal.SkillName ?? proposal.Title
                }
                : proposal.ManagedSkillMetadata,
            statusUpdatedAtUtc: approvedAtUtc,
            reviewedAtUtc: approvedAtUtc,
            reviewNotes: "approved",
            rolledBack: false,
            rolledBackAtUtc: null,
            rollbackReason: null);
        await _proposalStore.SaveProposalAsync(approved, ct);
        return approved;
    }

    public async ValueTask<LearningProposal?> RejectAsync(string proposalId, string? reason, CancellationToken ct)
    {
        var proposal = await _proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        if (!string.Equals(proposal.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
            return proposal;

        var rejected = CopyProposal(
            proposal,
            status: LearningProposalStatus.Rejected,
            statusUpdatedAtUtc: DateTimeOffset.UtcNow,
            reviewedAtUtc: DateTimeOffset.UtcNow,
            reviewNotes: string.IsNullOrWhiteSpace(reason) ? "rejected" : reason.Trim());
        await _proposalStore.SaveProposalAsync(rejected, ct);
        return rejected;
    }

    public async ValueTask<LearningProposal?> RollbackAsync(string proposalId, string? reason, IAgentRuntime? runtime, CancellationToken ct)
    {
        var proposal = await _proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        if (!string.Equals(proposal.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase) ||
            proposal.RolledBack)
        {
            return proposal;
        }

        switch (proposal.Kind)
        {
            case LearningProposalKind.ProfileUpdate when proposal.ProfileUpdate is not null:
                if (proposal.AppliedProfileBefore is null)
                    await _profileStore.DeleteProfileAsync(proposal.ProfileUpdate.ActorId, ct);
                else
                    await _profileStore.SaveProfileAsync(proposal.AppliedProfileBefore, ct);
                break;
            case LearningProposalKind.SkillDraft:
                if (!await RollbackManagedSkillAsync(proposal, ct))
                    return proposal;
                if (runtime is not null)
                    await runtime.ReloadSkillsAsync(ct);
                break;
            case LearningProposalKind.AutomationSuggestion:
                if (!await RollbackManagedAutomationAsync(proposal, ct))
                    return proposal;
                break;
            default:
                return proposal;
        }

        var rolledBack = CopyProposal(
            proposal,
            status: LearningProposalStatus.RolledBack,
            statusUpdatedAtUtc: DateTimeOffset.UtcNow,
            rolledBack: true,
            rolledBackAtUtc: DateTimeOffset.UtcNow,
            rollbackReason: string.IsNullOrWhiteSpace(reason) ? "rollback requested" : reason.Trim());

        await _proposalStore.SaveProposalAsync(rolledBack, ct);
        return rolledBack;
    }

    private async Task EnsureProfileProposalAsync(Session session, string actorId, ChatTurn lastUser, CancellationToken ct)
    {
        var existingProfile = await _profileStore.GetProfileAsync(actorId, ct);
        if (existingProfile is not null && existingProfile.Summary.Contains(lastUser.Content, StringComparison.OrdinalIgnoreCase))
            return;

        var fingerprint = BuildProposalFingerprint(LearningProposalKind.ProfileUpdate, actorId, "preference", lastUser.Content);
        var pending = await _proposalStore.ListProposalsAsync(LearningProposalStatus.Pending, LearningProposalKind.ProfileUpdate, ct);
        var duplicate = pending.FirstOrDefault(item => IsDuplicateProposal(item, fingerprint, actorId, title: null, skillName: null));
        if (duplicate is not null)
        {
            await _proposalStore.SaveProposalAsync(MergeDuplicateProposal(
                duplicate,
                [session.Id],
                [BuildTurnId(session, lastUser)],
                0.55f,
                duplicate.RepeatedCount + 1), ct);
            return;
        }

        var profile = new UserProfile
        {
            ActorId = actorId,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Summary = lastUser.Content.Length > 240 ? lastUser.Content[..240] : lastUser.Content,
            Tone = "learned",
            Preferences = [lastUser.Content],
            RecentIntents = [lastUser.Content],
            Facts =
            [
                new UserProfileFact
                {
                    Key = "preference",
                    Value = lastUser.Content,
                    Confidence = 0.55f,
                    SourceSessionIds = [session.Id]
                }
            ]
        };

        if (_config.ReviewRequired)
        {
            await _proposalStore.SaveProposalAsync(new LearningProposal
            {
                Id = $"lp_{Guid.NewGuid():N}"[..20],
                Kind = LearningProposalKind.ProfileUpdate,
                Status = LearningProposalStatus.Pending,
                ActorId = actorId,
                Title = "Profile update suggestion",
                Summary = "Detected a possible stable user preference or identity hint.",
                ProfileUpdate = profile,
                AppliedProfileBefore = existingProfile,
                SourceSessionIds = [session.Id],
                SourceTurnIds = [BuildTurnId(session, lastUser)],
                RepeatedCount = 1,
                ProposalFingerprint = fingerprint,
                RiskLevel = LearningProposalRiskLevels.Low,
                Confidence = 0.55f,
                CreatedReason = "Detected a possible stable user preference or identity hint.",
                ValidationStatus = LearningProposalValidationStatuses.Valid
            }, ct);
            return;
        }

        await _profileStore.SaveProfileAsync(profile, ct);
        await _proposalStore.SaveProposalAsync(new LearningProposal
        {
            Id = $"lp_{Guid.NewGuid():N}"[..20],
            Kind = LearningProposalKind.ProfileUpdate,
            Status = LearningProposalStatus.Approved,
            ActorId = actorId,
            Title = "Profile update suggestion",
            Summary = "Detected a possible stable user preference or identity hint.",
            ProfileUpdate = profile,
            AppliedProfileBefore = existingProfile,
            SourceSessionIds = [session.Id],
            SourceTurnIds = [BuildTurnId(session, lastUser)],
            RepeatedCount = 1,
            ProposalFingerprint = fingerprint,
            RiskLevel = LearningProposalRiskLevels.Low,
            Confidence = 0.55f,
            CreatedReason = "Detected a possible stable user preference or identity hint.",
            ValidationStatus = LearningProposalValidationStatuses.Valid,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewNotes = "auto-applied because Learning.ReviewRequired=false"
        }, ct);
    }

    private async Task EnsureAutomationProposalAsync(Session session, string actorId, ChatTurn lastUser, CancellationToken ct)
    {
        var search = await _sessionSearchStore.SearchSessionsAsync(new SessionSearchQuery
        {
            Text = lastUser.Content,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Limit = 10
        }, ct);

        if (search.Items.Count < _config.AutomationProposalThreshold)
            return;

        var fingerprint = BuildProposalFingerprint(LearningProposalKind.AutomationSuggestion, actorId, NormalizeForFingerprint(lastUser.Content));
        var allAutomationProposals = await _proposalStore.ListProposalsAsync(null, LearningProposalKind.AutomationSuggestion, ct);
        var pendingDuplicate = allAutomationProposals.FirstOrDefault(item =>
            string.Equals(item.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase) &&
            IsDuplicateProposal(item, fingerprint, actorId, lastUser.Content, skillName: null));
        if (pendingDuplicate is not null)
        {
            await _proposalStore.SaveProposalAsync(MergeDuplicateProposal(
                pendingDuplicate,
                search.Items.Select(static item => item.SessionId).Append(session.Id),
                [BuildTurnId(session, lastUser)],
                Math.Min(0.9f, 0.3f + (search.Items.Count * 0.1f)),
                Math.Max(pendingDuplicate.RepeatedCount + 1, search.Items.Count)), ct);
            return;
        }

        if (HasRecentlyApprovedDuplicate(allAutomationProposals, fingerprint, actorId, lastUser.Content, skillName: null))
            return;

        var automation = new AutomationDefinition
        {
            Id = $"suggested:{Guid.NewGuid():N}"[..20],
            Name = lastUser.Content.Length > 60 ? lastUser.Content[..60] : lastUser.Content,
            Enabled = false,
            Schedule = "@daily",
            Prompt = lastUser.Content,
            DeliveryChannelId = "cron",
            Tags = ["suggested", "learning"],
            IsDraft = true,
            Source = "learning",
            CreatedByLearningProposalId = null,
            TemplateKey = "custom"
        };

        var automationProposal = new LearningProposal
        {
            Id = $"lp_{Guid.NewGuid():N}"[..20],
            Kind = LearningProposalKind.AutomationSuggestion,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = lastUser.Content,
            Summary = "Repeated prompt detected; suggested as a disabled automation draft.",
            AutomationDraft = automation,
            SourceSessionIds = search.Items.Select(static item => item.SessionId).Append(session.Id).Distinct(StringComparer.Ordinal).ToArray(),
            SourceTurnIds = [BuildTurnId(session, lastUser)],
            RepeatedCount = search.Items.Count,
            ProposalFingerprint = fingerprint,
            RiskLevel = LearningProposalRiskLevels.Medium,
            Confidence = Math.Min(0.9f, 0.3f + (search.Items.Count * 0.1f)),
            CreatedReason = $"Observed {search.Items.Count} similar requests from the same actor.",
            ValidationStatus = LearningProposalValidationStatuses.Warning,
            ValidationWarnings = ["Automation suggestions are created as disabled drafts and require operator review before use."]
        };

        await _proposalStore.SaveProposalAsync(automationProposal, ct);
    }

    private async Task EnsureSkillProposalAsync(Session session, string actorId, ChatTurn assistantTurn, CancellationToken ct)
    {
        var toolSequenceItems = assistantTurn.ToolCalls!.Select(static item => item.ToolName).ToArray();
        var normalizedToolSequence = NormalizeToolSequence(toolSequenceItems);
        var toolSequence = string.Join(" -> ", toolSequenceItems);
        var repeatedCount = session.History
            .Where(static turn => string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) && turn.ToolCalls is { Count: > 1 })
            .Count(turn => string.Equals(
                NormalizeToolSequence(turn.ToolCalls!.Select(static item => item.ToolName)),
                normalizedToolSequence,
                StringComparison.OrdinalIgnoreCase));

        if (repeatedCount < _config.SkillProposalThreshold)
            return;

        var skillName = Slugify(toolSequence);
        var fingerprint = BuildProposalFingerprint(LearningProposalKind.SkillDraft, actorId, skillName, normalizedToolSequence);
        var allSkillProposals = await _proposalStore.ListProposalsAsync(null, LearningProposalKind.SkillDraft, ct);
        var pendingDuplicate = allSkillProposals.FirstOrDefault(item =>
            string.Equals(item.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase) &&
            IsDuplicateProposal(item, fingerprint, actorId, title: null, skillName));
        if (pendingDuplicate is not null)
        {
            await _proposalStore.SaveProposalAsync(MergeDuplicateProposal(
                pendingDuplicate,
                [session.Id],
                [BuildTurnId(session, assistantTurn)],
                Math.Min(0.95f, 0.4f + (repeatedCount * 0.1f)),
                repeatedCount), ct);
            return;
        }

        if (HasRecentlyApprovedDuplicate(allSkillProposals, fingerprint, actorId, title: null, skillName))
            return;

        var draftContent = await SummarizeSkillDraftAsync(skillName, toolSequence, repeatedCount, ct);
        var finalDraftContent = draftContent.Length > _config.MaxDraftChars ? draftContent[.._config.MaxDraftChars] : draftContent;
        var toolObservations = BuildToolObservations(assistantTurn.ToolCalls!);
        var validation = ValidateSkillDraft(skillName, finalDraftContent, ComputeDraftHash(finalDraftContent), toolObservations, repeatedCount, usedFallbackTemplate: IsFallbackSkillDraft(finalDraftContent));
        var riskLevel = DetermineSkillRisk(toolObservations);

        await _proposalStore.SaveProposalAsync(new LearningProposal
        {
            Id = $"lp_{Guid.NewGuid():N}"[..20],
            Kind = LearningProposalKind.SkillDraft,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = $"Skill draft for {toolSequence}",
            Summary = "Repeated multi-tool workflow detected.",
            SkillName = skillName,
            DraftContent = finalDraftContent,
            DraftContentHash = ComputeDraftHash(finalDraftContent),
            DraftPreview = Preview(finalDraftContent),
            SourceSessionIds = [session.Id],
            SourceTurnIds = [BuildTurnId(session, assistantTurn)],
            ToolNames = toolSequenceItems.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ToolSequence = toolSequenceItems,
            ToolObservations = toolObservations,
            RepeatedCount = repeatedCount,
            ProposalFingerprint = fingerprint,
            RiskLevel = riskLevel,
            Confidence = Math.Min(0.95f, 0.4f + (repeatedCount * 0.1f)),
            CreatedReason = $"Observed the same multi-tool sequence {repeatedCount} times in this session.",
            ValidationStatus = validation.Errors.Count > 0
                ? LearningProposalValidationStatuses.Error
                : validation.Warnings.Count > 0 ? LearningProposalValidationStatuses.Warning : LearningProposalValidationStatuses.Valid,
            ValidationWarnings = validation.Warnings,
            ValidationErrors = validation.Errors
        }, ct);
    }

    private async Task<string> SummarizeSkillDraftAsync(string skillName, string toolSequence, int repeatedCount, CancellationToken ct)
    {
        var templateFallback = $$"""
---
name: {{skillName}}
description: Learned workflow for {{toolSequence}}
---

When the task matches this workflow, prefer the following tool chain:
- {{toolSequence}}

Use it when repeated requests resemble the sessions that produced this draft.
""";

        if (_providerRegistry is null || !_providerRegistry.TryGet("default", out var registration) || registration is null)
            return templateFallback;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var prompt = $"""
                Summarize the following repeated tool workflow into a concise skill instruction document.
                Tool sequence: {toolSequence}
                This pattern was observed {repeatedCount} times.

                Generate a skill document in exactly this format:
                ---
                name: {skillName}
                description: <one-line description of what this workflow accomplishes>
                ---

                <2-4 sentences describing when to use this workflow and what each tool step does>
                """;

            var response = await registration.Client.GetResponseAsync(prompt, new ChatOptions { MaxOutputTokens = 500 }, timeoutCts.Token);
            var text = response.Text;
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM summarization failed for skill proposal '{SkillName}'; using template fallback.", skillName);
        }

        return templateFallback;
    }

    private static async Task SaveManagedSkillAsync(string skillName, string content, string proposalId, string? originalDraftHash, CancellationToken ct)
    {
        var root = GetManagedSkillPath(skillName);
        var skillPath = Path.Join(root, SafePathLeaf(ManagedSkillFileName, ManagedSkillFileName));
        var metadataPath = Path.Join(root, SafePathLeaf(ManagedSkillMetadataFileName, ManagedSkillMetadataFileName));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(skillPath, content, ct);
        await File.WriteAllTextAsync(
            metadataPath,
            System.Text.Json.JsonSerializer.Serialize(new ManagedLearningSkillMetadata
            {
                CreatedByProposalId = proposalId,
                OriginalDraftHash = originalDraftHash,
                SkillName = skillName
            }, CoreJsonContext.Default.ManagedLearningSkillMetadata),
            ct);
    }

    private static string GetManagedSkillPath(string skillName)
    {
        var slug = Slugify(skillName);
        var safeSlug = SafePathLeaf(slug, "learned-skill");
        var skillsRoot = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "skills");
        return Path.Join(skillsRoot, safeSlug);
    }

    private static string SafePathLeaf(string value, string fallback)
    {
        var leaf = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(leaf) || Path.IsPathRooted(leaf))
            return fallback;

        return leaf;
    }

    private static async Task<string?> GetManagedSkillTargetErrorAsync(string skillName, string proposalId, CancellationToken ct)
    {
        var skillPath = GetManagedSkillPath(skillName);
        if (!Directory.Exists(skillPath))
            return null;

        var metadataPath = Path.Join(skillPath, SafePathLeaf(ManagedSkillMetadataFileName, ManagedSkillMetadataFileName));
        if (!File.Exists(metadataPath))
            return "Managed skill target already exists without learning metadata; refusing to overwrite user-authored skill content.";

        try
        {
            var metadata = System.Text.Json.JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(metadataPath, ct),
                CoreJsonContext.Default.ManagedLearningSkillMetadata);
            if (metadata?.ManagedByLearning is true &&
                string.Equals(metadata.CreatedByProposalId, proposalId, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException)
        {
            return "Managed skill target metadata could not be read; refusing to overwrite it.";
        }

        return "Managed skill target already exists for a different learning proposal; refusing to overwrite it.";
    }

    private SkillDraftValidationResult ValidateSkillDraft(string skillName, string content, string? expectedHash, LearningProposal proposal)
        => ValidateSkillDraft(
            skillName,
            content,
            expectedHash,
            proposal.ToolObservations,
            proposal.RepeatedCount == 0 ? _config.SkillProposalThreshold : proposal.RepeatedCount,
            proposal.ValidationWarnings.Any(static warning => warning.Contains("fallback template", StringComparison.OrdinalIgnoreCase)));

    private SkillDraftValidationResult ValidateSkillDraft(
        string skillName,
        string content,
        string? expectedHash,
        IReadOnlyList<LearningToolObservation> toolObservations,
        int repeatedCount,
        bool usedFallbackTemplate)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add("Skill draft content is empty.");
            return new SkillDraftValidationResult(errors, warnings);
        }

        if (!string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(expectedHash, ComputeDraftHash(content), StringComparison.Ordinal))
        {
            errors.Add("Skill draft content no longer matches the reviewed proposal.");
        }

        if (content.Length > _config.MaxDraftChars)
        {
            errors.Add("Skill draft content exceeds the maximum allowed length.");
        }
        else if (content.Length >= Math.Max(1, (int)(_config.MaxDraftChars * 0.9)))
        {
            warnings.Add("Skill draft content is near the configured maximum draft length.");
        }

        if (content.Contains("..", StringComparison.Ordinal) || content.Contains('\0'))
        {
            errors.Add("Skill draft contains invalid path-like content.");
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 5 || lines[0] != "---")
        {
            errors.Add("Skill draft must begin with YAML frontmatter.");
            return new SkillDraftValidationResult(errors, warnings);
        }

        var endIndex = Array.IndexOf(lines, "---", 1);
        if (endIndex < 2)
        {
            errors.Add("Skill draft frontmatter is incomplete.");
            return new SkillDraftValidationResult(errors, warnings);
        }

        var frontmatter = lines[1..endIndex];
        var nameLine = frontmatter.FirstOrDefault(static line => line.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
        var descriptionLine = frontmatter.FirstOrDefault(static line => line.StartsWith("description:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(nameLine) || string.IsNullOrWhiteSpace(descriptionLine))
        {
            errors.Add("Skill draft frontmatter must include name and description.");
        }

        if (!string.IsNullOrWhiteSpace(descriptionLine) && descriptionLine["description:".Length..].Trim().Length < 16)
            warnings.Add("Skill draft description is short; review whether it clearly explains when to use the skill.");

        if (!string.IsNullOrWhiteSpace(nameLine))
        {
            var declaredName = nameLine["name:".Length..].Trim();
            if (!string.Equals(Slugify(declaredName), Slugify(skillName), StringComparison.Ordinal))
                errors.Add("Skill draft name does not match the target skill slug.");
        }

        if (toolObservations.Any(static tool => tool.IsMutating == true))
            warnings.Add("Observed tool sequence includes mutating tools.");
        if (toolObservations.Any(static tool => tool.IsInteractive == true))
            warnings.Add("Observed tool sequence includes interactive, messaging, browser, shell, or external side-effect tools.");
        if (toolObservations.Any(static tool => tool.IsApprovalGated == true))
            warnings.Add("Observed tool sequence includes tools that may require approval.");
        if (toolObservations.Any(static tool => tool.IsReadOnly is null || tool.IsMutating is null))
            warnings.Add("Some observed tools have unknown metadata; review risk manually.");
        if (repeatedCount < _config.SkillProposalThreshold)
            warnings.Add("Skill draft was generated from a low repeated count.");
        if (usedFallbackTemplate)
            warnings.Add("Skill draft used the fallback template because LLM summarization was unavailable or failed.");

        return new SkillDraftValidationResult(errors, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<bool> RollbackManagedSkillAsync(LearningProposal proposal, CancellationToken ct)
    {
        var skillPath = string.IsNullOrWhiteSpace(proposal.ManagedSkillPath)
            ? GetManagedSkillPath(proposal.SkillName ?? proposal.Title)
            : proposal.ManagedSkillPath;
        skillPath = Path.GetFullPath(skillPath);
        var skillFile = Path.Join(skillPath, SafePathLeaf(ManagedSkillFileName, ManagedSkillFileName));
        var metadataFile = Path.Join(skillPath, SafePathLeaf(ManagedSkillMetadataFileName, ManagedSkillMetadataFileName));

        if (!File.Exists(skillFile) || !File.Exists(metadataFile))
            return false;

        ManagedLearningSkillMetadata? metadata;
        try
        {
            metadata = System.Text.Json.JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(metadataFile, ct),
                CoreJsonContext.Default.ManagedLearningSkillMetadata);
        }
        catch
        {
            return false;
        }

        if (metadata?.ManagedByLearning != true ||
            !string.Equals(metadata.CreatedByProposalId, proposal.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var currentContent = await File.ReadAllTextAsync(skillFile, ct);
        var currentHash = ComputeDraftHash(currentContent);
        var archiveRoot = GetManagedSkillArchivePath(proposal.SkillName ?? proposal.Title);

        if (!string.Equals(currentHash, metadata.OriginalDraftHash ?? proposal.DraftContentHash, StringComparison.Ordinal))
        {
            ArchiveManagedSkill(skillPath, archiveRoot);
            _logger.LogWarning("Archived modified managed learning skill for proposal {ProposalId} at {ArchivePath}", proposal.Id, archiveRoot);
            return true;
        }

        if (ManagedSkillDirectoryHasUnexpectedEntries(skillPath))
        {
            ArchiveManagedSkill(skillPath, archiveRoot);
            _logger.LogWarning("Archived managed learning skill with extra files for proposal {ProposalId} at {ArchivePath}", proposal.Id, archiveRoot);
            return true;
        }

        Directory.Delete(skillPath, recursive: true);
        return true;
    }

    private static string GetManagedSkillArchivePath(string skillName)
    {
        var archiveLeaf = SafePathLeaf($"{Slugify(skillName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", $"learned-skill-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        var archiveRoot = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "rollback-archive",
            "skills");
        return Path.Join(archiveRoot, archiveLeaf);
    }

    private static void ArchiveManagedSkill(string skillPath, string archiveRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archiveRoot)!);
        Directory.Move(skillPath, archiveRoot);
    }

    private static bool ManagedSkillDirectoryHasUnexpectedEntries(string skillPath)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            ManagedSkillFileName,
            ManagedSkillMetadataFileName
        };
        return Directory.EnumerateFileSystemEntries(skillPath)
            .Select(Path.GetFileName)
            .Any(name => string.IsNullOrWhiteSpace(name) || !expected.Contains(name));
    }

    private async Task<bool> RollbackManagedAutomationAsync(LearningProposal proposal, CancellationToken ct)
    {
        var automationId = proposal.AppliedAutomationId ?? proposal.AutomationDraft?.Id;
        if (string.IsNullOrWhiteSpace(automationId))
            return false;

        var automation = await _automationStore.GetAutomationAsync(automationId, ct);
        if (automation is null)
            return false;

        var createdByThisProposal = string.Equals(automation.CreatedByLearningProposalId, proposal.Id, StringComparison.Ordinal) ||
                                    (string.Equals(automation.Source, "learning", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(automation.Id, proposal.AutomationDraft?.Id, StringComparison.Ordinal));
        if (!createdByThisProposal)
            return false;

        await _automationStore.SaveAutomationAsync(CloneAutomation(
            automation,
            enabled: false,
            isDraft: true,
            source: "learning",
            updatedAtUtc: DateTimeOffset.UtcNow), ct);
        return true;
    }

    private static AutomationDefinition BuildManagedAutomationDraft(AutomationDefinition source, string proposalId)
        => CloneAutomation(
            source,
            enabled: false,
            isDraft: true,
            source: "learning",
            createdByLearningProposalId: proposalId,
            updatedAtUtc: DateTimeOffset.UtcNow);

    private static AutomationDefinition CloneAutomation(
        AutomationDefinition automation,
        bool? enabled = null,
        bool? isDraft = null,
        string? source = null,
        string? createdByLearningProposalId = null,
        DateTimeOffset? updatedAtUtc = null)
        => new()
        {
            Id = automation.Id,
            Name = automation.Name,
            Enabled = enabled ?? automation.Enabled,
            Schedule = automation.Schedule,
            Timezone = automation.Timezone,
            Prompt = automation.Prompt,
            ModelId = automation.ModelId,
            ResponseMode = automation.ResponseMode,
            RunOnStartup = automation.RunOnStartup,
            SessionId = automation.SessionId,
            DeliveryChannelId = automation.DeliveryChannelId,
            DeliveryRecipientId = automation.DeliveryRecipientId,
            DeliverySubject = automation.DeliverySubject,
            Tags = automation.Tags,
            IsDraft = isDraft ?? automation.IsDraft,
            Source = source ?? automation.Source,
            TemplateKey = automation.TemplateKey,
            CreatedByLearningProposalId = createdByLearningProposalId ?? automation.CreatedByLearningProposalId,
            Verification = automation.Verification,
            RetryPolicy = automation.RetryPolicy,
            CreatedAtUtc = automation.CreatedAtUtc,
            UpdatedAtUtc = updatedAtUtc ?? automation.UpdatedAtUtc
        };

    private static IReadOnlyList<LearningToolObservation> BuildToolObservations(IReadOnlyList<ToolInvocation> toolCalls)
        => toolCalls.Select((toolCall, index) =>
        {
            var toolName = toolCall.ToolName;
            var mutating = ToolActionPolicyResolver.IsMutationCapable(toolName, toolCall.Arguments);
            var known = IsKnownToolCategory(toolName);
            var interactive = IsInteractiveOrExternalTool(toolName);
            var approvalGated = IsLikelyApprovalGatedTool(toolName);
            return new LearningToolObservation
            {
                ToolName = toolName,
                SequenceIndex = index,
                IsReadOnly = known ? !mutating && !interactive : null,
                IsMutating = known ? mutating : null,
                IsInteractive = interactive,
                IsApprovalGated = approvalGated,
                IsSandboxCapable = IsLikelySandboxCapableTool(toolName),
                ClassificationReason = known
                    ? mutating ? "Tool name or arguments indicate mutation." : "Tool appears read-oriented from known policy metadata."
                    : "Tool metadata is unknown; review conservatively."
            };
        }).ToArray();

    private static string DetermineSkillRisk(IReadOnlyList<LearningToolObservation> observations)
    {
        if (observations.Count == 0 ||
            observations.Any(static item => item.IsMutating != false || item.IsInteractive == true || item.IsApprovalGated == true || item.IsReadOnly is null))
        {
            return LearningProposalRiskLevels.High;
        }

        return observations.Count <= 2 ? LearningProposalRiskLevels.Low : LearningProposalRiskLevels.Medium;
    }

    private static bool IsKnownToolCategory(string toolName)
        => ContainsAny(
               toolName,
               "read",
               "list",
               "get",
               "search",
               "find",
               "open",
               "view",
               "fetch",
               "inspect",
               "query",
               "status",
               "log",
               "poll",
               "process",
               "automation",
               "todo",
               "file",
               "shell",
               "code_exec",
               "git",
               "browser",
               "web",
               "http",
               "email",
               "calendar",
               "slack",
               "teams",
               "github",
               "notion",
               "database",
               "home_assistant",
               "mqtt",
               "payment",
               "delegate");

    private static bool IsInteractiveOrExternalTool(string toolName)
        => ContainsAny(toolName, "browser", "web", "http", "email", "calendar", "slack", "teams", "discord", "telegram", "whatsapp", "sms", "signal", "notion", "github", "shell", "process", "code_exec", "database", "home_assistant", "mqtt", "payment");

    private static bool IsLikelyApprovalGatedTool(string toolName)
        => ContainsAny(toolName, "shell", "write", "edit", "apply_patch", "payment", "email", "calendar", "browser", "home_assistant", "mqtt", "database") ||
           ToolActionPolicyResolver.SupportsActionAwareApproval(toolName);

    private static bool IsLikelySandboxCapableTool(string toolName)
        => ContainsAny(toolName, "shell", "code_exec", "process", "browser");

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeToolSequence(IEnumerable<string> toolNames)
        => string.Join(">", toolNames.Select(static item => Slugify(item)).Where(static item => !string.IsNullOrWhiteSpace(item)));

    private static string BuildProposalFingerprint(string kind, string actorId, params string?[] parts)
        => ComputeDraftHash(string.Join("\n", new[] { kind, actorId }.Concat(parts.Select(static part => NormalizeForFingerprint(part ?? string.Empty)))));

    private static string NormalizeForFingerprint(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static bool IsDuplicateProposal(LearningProposal proposal, string fingerprint, string actorId, string? title, string? skillName)
        => string.Equals(proposal.ActorId, actorId, StringComparison.OrdinalIgnoreCase) &&
           (string.Equals(proposal.ProposalFingerprint, fingerprint, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(skillName) && string.Equals(proposal.SkillName, skillName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(title) && string.Equals(NormalizeForFingerprint(proposal.Title), NormalizeForFingerprint(title), StringComparison.Ordinal)));

    private static bool HasRecentlyApprovedDuplicate(IReadOnlyList<LearningProposal> proposals, string fingerprint, string actorId, string? title, string? skillName)
        => proposals.Any(item =>
            string.Equals(item.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase) &&
            item.UpdatedAtUtc >= DateTimeOffset.UtcNow.AddDays(-30) &&
            IsDuplicateProposal(item, fingerprint, actorId, title, skillName));

    private static LearningProposal MergeDuplicateProposal(
        LearningProposal proposal,
        IEnumerable<string> sourceSessionIds,
        IEnumerable<string> sourceTurnIds,
        float confidence,
        int? repeatedCount = null)
        => CopyProposal(
            proposal,
            sourceSessionIds: proposal.SourceSessionIds.Concat(sourceSessionIds).Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray(),
            sourceTurnIds: proposal.SourceTurnIds.Concat(sourceTurnIds).Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray(),
            confidence: Math.Max(proposal.Confidence, confidence),
            repeatedCount: Math.Max(proposal.RepeatedCount, repeatedCount ?? proposal.RepeatedCount),
            statusUpdatedAtUtc: DateTimeOffset.UtcNow);

    private static string BuildTurnId(Session session, ChatTurn turn)
    {
        var index = session.History.IndexOf(turn);
        return index < 0 ? $"{session.Id}:{turn.Timestamp:O}" : $"{session.Id}:turn:{index}";
    }

    private static string Preview(string content)
        => content.Length <= 600 ? content : content[..600];

    private static bool IsFallbackSkillDraft(string content)
        => content.Contains("When the task matches this workflow", StringComparison.OrdinalIgnoreCase) &&
           content.Contains("prefer the following tool chain", StringComparison.OrdinalIgnoreCase);

    private static string ComputeDraftHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string BuildActorId(string channelId, string senderId)
        => $"{channelId}:{senderId}";

    private static IReadOnlyList<ProfileDiffEntry> BuildProfileDiff(UserProfile? baseline, UserProfile proposed)
    {
        var diff = new List<ProfileDiffEntry>();

        AddScalarDiff(diff, "channelId", baseline?.ChannelId, proposed.ChannelId);
        AddScalarDiff(diff, "senderId", baseline?.SenderId, proposed.SenderId);
        AddScalarDiff(diff, "summary", baseline?.Summary, proposed.Summary);
        AddScalarDiff(diff, "tone", baseline?.Tone, proposed.Tone);
        AddSequenceDiff(diff, "preferences", baseline?.Preferences, proposed.Preferences);
        AddSequenceDiff(diff, "activeProjects", baseline?.ActiveProjects, proposed.ActiveProjects);
        AddSequenceDiff(diff, "recentIntents", baseline?.RecentIntents, proposed.RecentIntents);
        AddFactsDiff(diff, baseline?.Facts, proposed.Facts);

        return diff;
    }

    private static void AddScalarDiff(List<ProfileDiffEntry> diff, string path, string? before, string? after)
    {
        before ??= string.Empty;
        after ??= string.Empty;
        if (string.Equals(before, after, StringComparison.Ordinal))
            return;

        diff.Add(new ProfileDiffEntry
        {
            Path = path,
            ChangeType = string.IsNullOrEmpty(before) ? "added" : string.IsNullOrEmpty(after) ? "removed" : "updated",
            Before = string.IsNullOrEmpty(before) ? null : before,
            After = string.IsNullOrEmpty(after) ? null : after
        });
    }

    private static void AddSequenceDiff(List<ProfileDiffEntry> diff, string path, IReadOnlyList<string>? before, IReadOnlyList<string>? after)
    {
        var normalizedBefore = before ?? [];
        var normalizedAfter = after ?? [];
        if (normalizedBefore.SequenceEqual(normalizedAfter, StringComparer.Ordinal))
            return;

        diff.Add(new ProfileDiffEntry
        {
            Path = path,
            ChangeType = normalizedBefore.Count == 0 ? "added" : normalizedAfter.Count == 0 ? "removed" : "updated",
            Before = normalizedBefore.Count == 0 ? null : SerializeStringList(normalizedBefore),
            After = normalizedAfter.Count == 0 ? null : SerializeStringList(normalizedAfter)
        });
    }

    private static void AddFactsDiff(List<ProfileDiffEntry> diff, IReadOnlyList<UserProfileFact>? before, IReadOnlyList<UserProfileFact>? after)
    {
        var normalizedBefore = before ?? [];
        var normalizedAfter = after ?? [];
        if (FactsEqual(normalizedBefore, normalizedAfter))
            return;

        diff.Add(new ProfileDiffEntry
        {
            Path = "facts",
            ChangeType = normalizedBefore.Count == 0 ? "added" : normalizedAfter.Count == 0 ? "removed" : "updated",
            Before = normalizedBefore.Count == 0 ? null : SerializeFacts(normalizedBefore),
            After = normalizedAfter.Count == 0 ? null : SerializeFacts(normalizedAfter)
        });
    }

    private static bool FactsEqual(IReadOnlyList<UserProfileFact> before, IReadOnlyList<UserProfileFact> after)
    {
        var normalizedBefore = NormalizeFacts(before);
        var normalizedAfter = NormalizeFacts(after);
        if (normalizedBefore.Count != normalizedAfter.Count)
            return false;

        for (var i = 0; i < normalizedBefore.Count; i++)
        {
            var left = normalizedBefore[i];
            var right = normalizedAfter[i];
            if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal) ||
                !string.Equals(left.Value, right.Value, StringComparison.Ordinal) ||
                left.ConfidenceBits != right.ConfidenceBits ||
                !left.SourceSessionIds.SequenceEqual(right.SourceSessionIds, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string SerializeStringList(IReadOnlyList<string> items)
        => "[" + string.Join(", ", items.Select(static item => $"\"{EscapeValue(item)}\"")) + "]";

    private static string SerializeFacts(IReadOnlyList<UserProfileFact> facts)
        => "[" + string.Join(", ", NormalizeFacts(facts).Select(static fact =>
            $"{{key:\"{EscapeValue(fact.Key)}\", value:\"{EscapeValue(fact.Value)}\", confidence:{fact.Confidence.ToString("R", CultureInfo.InvariantCulture)}, sessions:{SerializeStringList(fact.SourceSessionIds)}}}")) + "]";

    private static IReadOnlyList<NormalizedFact> NormalizeFacts(IReadOnlyList<UserProfileFact> facts)
        => facts
            .Select(static fact => new NormalizedFact
            {
                Key = fact.Key,
                Value = fact.Value,
                Confidence = fact.Confidence,
                ConfidenceBits = BitConverter.SingleToInt32Bits(fact.Confidence),
                SourceSessionIds = fact.SourceSessionIds.OrderBy(static item => item, StringComparer.Ordinal).ToArray()
            })
            .OrderBy(static fact => fact.Key, StringComparer.Ordinal)
            .ThenBy(static fact => fact.Value, StringComparer.Ordinal)
            .ThenBy(static fact => fact.ConfidenceBits)
            .ThenBy(static fact => string.Join("\u001F", fact.SourceSessionIds), StringComparer.Ordinal)
            .ToArray();

    private static string EscapeValue(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static LearningProposal CopyProposal(
        LearningProposal proposal,
        string? status = null,
        UserProfile? appliedProfileBefore = null,
        AutomationDefinition? automationDraft = null,
        string? appliedAutomationId = null,
        string? managedSkillPath = null,
        ManagedLearningSkillMetadata? managedSkillMetadata = null,
        IReadOnlyList<string>? sourceSessionIds = null,
        IReadOnlyList<string>? sourceTurnIds = null,
        float? confidence = null,
        int? repeatedCount = null,
        DateTimeOffset? statusUpdatedAtUtc = null,
        DateTimeOffset? reviewedAtUtc = null,
        string? reviewNotes = null,
        string? validationStatus = null,
        IReadOnlyList<string>? validationWarnings = null,
        IReadOnlyList<string>? validationErrors = null,
        bool? rolledBack = null,
        DateTimeOffset? rolledBackAtUtc = null,
        string? rollbackReason = null)
        => new()
        {
            Id = proposal.Id,
            Kind = proposal.Kind,
            Status = status ?? proposal.Status,
            ActorId = proposal.ActorId,
            Title = proposal.Title,
            Summary = proposal.Summary,
            SkillName = proposal.SkillName,
            DraftContent = proposal.DraftContent,
            DraftContentHash = proposal.DraftContentHash,
            DraftPreview = proposal.DraftPreview,
            ProfileUpdate = proposal.ProfileUpdate,
            AppliedProfileBefore = appliedProfileBefore ?? proposal.AppliedProfileBefore,
            AutomationDraft = automationDraft ?? proposal.AutomationDraft,
            AppliedAutomationId = appliedAutomationId ?? proposal.AppliedAutomationId,
            ManagedSkillPath = managedSkillPath ?? proposal.ManagedSkillPath,
            ManagedSkillMetadata = managedSkillMetadata ?? proposal.ManagedSkillMetadata,
            SourceSessionIds = sourceSessionIds ?? proposal.SourceSessionIds,
            SourceTurnIds = sourceTurnIds ?? proposal.SourceTurnIds,
            ToolNames = proposal.ToolNames,
            ToolSequence = proposal.ToolSequence,
            ToolObservations = proposal.ToolObservations,
            RepeatedCount = repeatedCount ?? proposal.RepeatedCount,
            ProposalFingerprint = proposal.ProposalFingerprint,
            RiskLevel = proposal.RiskLevel,
            Confidence = confidence ?? proposal.Confidence,
            CreatedReason = proposal.CreatedReason,
            ValidationStatus = validationStatus ?? proposal.ValidationStatus,
            ValidationWarnings = validationWarnings ?? proposal.ValidationWarnings,
            ValidationErrors = validationErrors ?? proposal.ValidationErrors,
            CreatedAtUtc = proposal.CreatedAtUtc,
            UpdatedAtUtc = statusUpdatedAtUtc ?? proposal.UpdatedAtUtc,
            ReviewedAtUtc = reviewedAtUtc ?? proposal.ReviewedAtUtc,
            ReviewNotes = reviewNotes ?? proposal.ReviewNotes,
            RolledBack = rolledBack ?? proposal.RolledBack,
            RolledBackAtUtc = rolledBackAtUtc ?? proposal.RolledBackAtUtc,
            RollbackReason = rollbackReason ?? proposal.RollbackReason
        };

    private static string Slugify(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class NormalizedFact
    {
        public required string Key { get; init; }
        public required string Value { get; init; }
        public required float Confidence { get; init; }
        public required int ConfidenceBits { get; init; }
        public required IReadOnlyList<string> SourceSessionIds { get; init; }
    }

    private sealed class SkillDraftValidationResult
    {
        public SkillDraftValidationResult(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        {
            Errors = errors;
            Warnings = warnings;
        }

        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<string> Warnings { get; }
    }
}

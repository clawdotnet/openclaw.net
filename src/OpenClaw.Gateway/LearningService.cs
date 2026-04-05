using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class LearningService
{
    private static readonly Regex PreferenceRegex = new(@"\b(i prefer|call me|my name is|i like)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    public async ValueTask ObserveSessionAsync(Session session, CancellationToken ct)
    {
        if (!_config.Enabled || session.History.Count < 2)
            return;

        var lastUser = session.History.LastOrDefault(static turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase));
        var lastAssistant = session.History.LastOrDefault(static turn => string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase));
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

        switch (proposal.Kind)
        {
            case LearningProposalKind.ProfileUpdate when proposal.ProfileUpdate is not null:
                await _profileStore.SaveProfileAsync(proposal.ProfileUpdate, ct);
                break;
            case LearningProposalKind.AutomationSuggestion when proposal.AutomationDraft is not null:
                if (proposal.Status != LearningProposalStatus.Pending)
                    break;
                await _automationStore.SaveAutomationAsync(proposal.AutomationDraft, ct);
                break;
            case LearningProposalKind.SkillDraft when !string.IsNullOrWhiteSpace(proposal.DraftContent):
                if (!ValidateSkillDraft(proposal.SkillName ?? proposal.Title, proposal.DraftContent, proposal.DraftContentHash, out var validationError))
                {
                    return await RejectAsync(proposal.Id, validationError, ct);
                }

                await SaveManagedSkillAsync(proposal.SkillName ?? proposal.Title, proposal.DraftContent, ct);
                await runtime.ReloadSkillsAsync(ct);
                break;
        }

        var approved = new LearningProposal
        {
            Id = proposal.Id,
            Kind = proposal.Kind,
            Status = LearningProposalStatus.Approved,
            ActorId = proposal.ActorId,
            Title = proposal.Title,
            Summary = proposal.Summary,
            SkillName = proposal.SkillName,
            DraftContent = proposal.DraftContent,
            DraftContentHash = proposal.DraftContentHash,
            ProfileUpdate = proposal.ProfileUpdate,
            AutomationDraft = proposal.AutomationDraft,
            SourceSessionIds = proposal.SourceSessionIds,
            Confidence = proposal.Confidence,
            CreatedAtUtc = proposal.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewNotes = "approved"
        };
        await _proposalStore.SaveProposalAsync(approved, ct);
        return approved;
    }

    public async ValueTask<LearningProposal?> RejectAsync(string proposalId, string? reason, CancellationToken ct)
    {
        var proposal = await _proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        var rejected = new LearningProposal
        {
            Id = proposal.Id,
            Kind = proposal.Kind,
            Status = LearningProposalStatus.Rejected,
            ActorId = proposal.ActorId,
            Title = proposal.Title,
            Summary = proposal.Summary,
            SkillName = proposal.SkillName,
            DraftContent = proposal.DraftContent,
            DraftContentHash = proposal.DraftContentHash,
            ProfileUpdate = proposal.ProfileUpdate,
            AutomationDraft = proposal.AutomationDraft,
            SourceSessionIds = proposal.SourceSessionIds,
            Confidence = proposal.Confidence,
            CreatedAtUtc = proposal.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewNotes = string.IsNullOrWhiteSpace(reason) ? "rejected" : reason.Trim()
        };
        await _proposalStore.SaveProposalAsync(rejected, ct);
        return rejected;
    }

    private async Task EnsureProfileProposalAsync(Session session, string actorId, ChatTurn lastUser, CancellationToken ct)
    {
        var existingProfile = await _profileStore.GetProfileAsync(actorId, ct);
        if (existingProfile is not null && existingProfile.Summary.Contains(lastUser.Content, StringComparison.OrdinalIgnoreCase))
            return;

        var pending = await _proposalStore.ListProposalsAsync(LearningProposalStatus.Pending, LearningProposalKind.ProfileUpdate, ct);
        if (pending.Any(item => string.Equals(item.ActorId, actorId, StringComparison.OrdinalIgnoreCase)))
            return;

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
                SourceSessionIds = [session.Id],
                Confidence = 0.55f
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
            SourceSessionIds = [session.Id],
            Confidence = 0.55f,
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

        var pending = await _proposalStore.ListProposalsAsync(LearningProposalStatus.Pending, LearningProposalKind.AutomationSuggestion, ct);
        if (pending.Any(item => string.Equals(item.ActorId, actorId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(item.Title, lastUser.Content, StringComparison.OrdinalIgnoreCase)))
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
            SourceSessionIds = [session.Id],
            Confidence = Math.Min(0.9f, 0.3f + (search.Items.Count * 0.1f))
        };

        await _proposalStore.SaveProposalAsync(automationProposal, ct);
    }

    private async Task EnsureSkillProposalAsync(Session session, string actorId, ChatTurn assistantTurn, CancellationToken ct)
    {
        var toolSequence = string.Join(" -> ", assistantTurn.ToolCalls!.Select(static item => item.ToolName));
        var repeatedCount = session.History
            .Where(static turn => string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) && turn.ToolCalls is { Count: > 1 })
            .Count(turn => string.Equals(
                string.Join(" -> ", turn.ToolCalls!.Select(static item => item.ToolName)),
                toolSequence,
                StringComparison.Ordinal));

        if (repeatedCount < _config.SkillProposalThreshold)
            return;

        var pending = await _proposalStore.ListProposalsAsync(LearningProposalStatus.Pending, LearningProposalKind.SkillDraft, ct);
        if (pending.Any(item => string.Equals(item.ActorId, actorId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(item.SkillName, Slugify(toolSequence), StringComparison.OrdinalIgnoreCase)))
            return;

        var skillName = Slugify(toolSequence);
        var draftContent = await SummarizeSkillDraftAsync(skillName, toolSequence, repeatedCount, ct);

        await _proposalStore.SaveProposalAsync(new LearningProposal
        {
            Id = $"lp_{Guid.NewGuid():N}"[..20],
            Kind = LearningProposalKind.SkillDraft,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = $"Skill draft for {toolSequence}",
            Summary = "Repeated multi-tool workflow detected.",
            SkillName = skillName,
            DraftContent = draftContent.Length > _config.MaxDraftChars ? draftContent[.._config.MaxDraftChars] : draftContent,
            DraftContentHash = ComputeDraftHash(draftContent.Length > _config.MaxDraftChars ? draftContent[.._config.MaxDraftChars] : draftContent),
            SourceSessionIds = [session.Id],
            Confidence = Math.Min(0.95f, 0.4f + (repeatedCount * 0.1f))
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

    private static async Task SaveManagedSkillAsync(string skillName, string content, CancellationToken ct)
    {
        var slug = Slugify(skillName);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "skills", slug);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), content, ct);
    }

    private static bool ValidateSkillDraft(string skillName, string content, string? expectedHash, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Skill draft content is empty.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(expectedHash, ComputeDraftHash(content), StringComparison.Ordinal))
        {
            error = "Skill draft content no longer matches the reviewed proposal.";
            return false;
        }

        if (content.Length > 4_000)
        {
            error = "Skill draft content exceeds the maximum allowed length.";
            return false;
        }

        if (content.Contains("..", StringComparison.Ordinal) || content.Contains('\0'))
        {
            error = "Skill draft contains invalid path-like content.";
            return false;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 5 || lines[0] != "---")
        {
            error = "Skill draft must begin with YAML frontmatter.";
            return false;
        }

        var endIndex = Array.IndexOf(lines, "---", 1);
        if (endIndex < 2)
        {
            error = "Skill draft frontmatter is incomplete.";
            return false;
        }

        var frontmatter = lines[1..endIndex];
        var nameLine = frontmatter.FirstOrDefault(static line => line.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
        var descriptionLine = frontmatter.FirstOrDefault(static line => line.StartsWith("description:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(nameLine) || string.IsNullOrWhiteSpace(descriptionLine))
        {
            error = "Skill draft frontmatter must include name and description.";
            return false;
        }

        var declaredName = nameLine["name:".Length..].Trim();
        if (!string.Equals(Slugify(declaredName), Slugify(skillName), StringComparison.Ordinal))
        {
            error = "Skill draft name does not match the target skill slug.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string ComputeDraftHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string BuildActorId(string channelId, string senderId)
        => $"{channelId}:{senderId}";

    private static string Slugify(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}

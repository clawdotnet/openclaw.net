using System.Linq;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class SharedHarnessStateService
{
    private readonly ISharedHarnessStateStore _store;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly ILogger<SharedHarnessStateService> _logger;

    public SharedHarnessStateService(
        ISharedHarnessStateStore store,
        RuntimeEventStore runtimeEvents,
        ILogger<SharedHarnessStateService> logger)
    {
        _store = store;
        _runtimeEvents = runtimeEvents;
        _logger = logger;
    }

    public async ValueTask<SharedHarnessState> CreateAsync(SharedHarnessState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.IsNullOrWhiteSpace(state.Id))
        {
            var requestedId = state.Id.Trim();
            if (await _store.GetAsync(requestedId, ct) is not null)
                throw new ArgumentException($"Shared harness state '{requestedId}' already exists.", nameof(state));
        }

        var normalized = Normalize(state, DateTimeOffset.UtcNow, isNew: true);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(normalized, "shared_state_created", "info", $"Created shared harness state '{normalized.Id}'.");
        return normalized;
    }

    public ValueTask<SharedHarnessState> CreateForSessionAsync(
        string sessionId,
        string? goal,
        string? parentSessionId,
        string? harnessContractId,
        CancellationToken ct)
        => CreateAsync(new SharedHarnessState
        {
            SessionId = sessionId,
            ParentSessionId = parentSessionId,
            HarnessContractId = harnessContractId,
            Goal = goal ?? ""
        }, ct);

    public ValueTask<SharedHarnessState?> GetAsync(string id, CancellationToken ct)
        => _store.GetAsync(id, ct);

    public ValueTask<SharedHarnessState?> GetBySessionAsync(string sessionId, CancellationToken ct)
        => _store.GetBySessionAsync(sessionId, ct);

    public ValueTask<IReadOnlyList<SharedHarnessState>> ListAsync(SharedHarnessStateListQuery query, CancellationToken ct)
        => _store.ListAsync(query, ct);

    public async ValueTask<SharedHarnessState?> AddParticipantAsync(string stateId, HarnessParticipant participant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var existing = await _store.GetAsync(stateId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var existingParticipants = CleanList(existing.Participants);
        var participantIndex = string.IsNullOrWhiteSpace(participant.Id)
            ? FirstUnusedGeneratedIndex(existingParticipants.Select(static item => item.Id), "participant")
            : 0;
        var normalizedParticipant = NormalizeParticipant(participant, participantIndex, now);
        var participants = existingParticipants
            .Where(item => !string.Equals(item.Id, normalizedParticipant.Id, StringComparison.Ordinal))
            .Append(normalizedParticipant)
            .ToArray();

        var updated = Copy(existing, participants: participants, updatedAtUtc: now);
        await _store.SaveAsync(updated, ct);
        AppendEvent(updated, "shared_state_participant_added", "info", $"Added participant '{normalizedParticipant.Id}' to shared harness state '{updated.Id}'.");
        return updated;
    }

    public async ValueTask<SharedHarnessState?> AddActionAsync(string stateId, HarnessStateAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        var existing = await _store.GetAsync(stateId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var existingActions = CleanList(existing.Actions);
        var actionIndex = string.IsNullOrWhiteSpace(action.Id)
            ? FirstUnusedGeneratedIndex(existingActions.Select(static item => item.Id), "action")
            : 0;
        var normalizedAction = NormalizeAction(action, actionIndex, now);
        var actions = existingActions
            .Where(item => !string.Equals(item.Id, normalizedAction.Id, StringComparison.Ordinal))
            .Append(normalizedAction)
            .ToArray();

        var updated = Copy(existing, actions: actions, updatedAtUtc: now);
        await _store.SaveAsync(updated, ct);
        AppendEvent(updated, "shared_state_action_added", "info", $"Added action '{normalizedAction.Id}' to shared harness state '{updated.Id}'.");
        return updated;
    }

    public async ValueTask<SharedHarnessState?> UpdateActionStatusAsync(string stateId, string actionId, string status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actionId))
            throw new ArgumentException("Action id is required.", nameof(actionId));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Action status is required.", nameof(status));

        var existing = await _store.GetAsync(stateId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var normalizedStatus = NormalizeStatus(status);
        var found = false;
        var actions = CleanList(existing.Actions).Select(action =>
            string.Equals(action.Id, actionId, StringComparison.Ordinal)
                ? CopyAction(
                    action,
                    status: normalizedStatus,
                    completedAtUtc: IsTerminalStatus(normalizedStatus) ? now : action.CompletedAtUtc)
                : action).ToArray();

        for (var i = 0; i < actions.Length; i++)
        {
            if (!string.Equals(actions[i].Id, actionId, StringComparison.Ordinal))
                continue;
            found = true;
            break;
        }

        if (!found)
            throw new ArgumentException($"Action '{actionId}' was not found in shared harness state '{stateId}'.", nameof(actionId));

        var updated = Copy(existing, actions: actions, updatedAtUtc: now);
        await _store.SaveAsync(updated, ct);
        AppendEvent(updated, "shared_state_action_status_changed", "info", $"Updated action '{actionId}' to '{normalizedStatus}'.");
        return updated;
    }

    public async ValueTask<SharedHarnessState?> DetectConflictsAsync(string stateId, CancellationToken ct)
    {
        var existing = await _store.GetAsync(stateId, ct);
        if (existing is null)
            return null;

        var conflicts = DetectConflicts(existing);
        var updated = Copy(existing, conflicts: conflicts, updatedAtUtc: DateTimeOffset.UtcNow);
        await _store.SaveAsync(updated, ct);
        AppendEvent(
            updated,
            "shared_state_conflicts_detected",
            conflicts.Count == 0 ? "info" : "warning",
            $"Detected {conflicts.Count} shared harness state conflict(s) in '{updated.Id}'.");
        return updated;
    }

    internal IReadOnlyList<HarnessConflict> DetectConflicts(SharedHarnessState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var conflicts = new List<HarnessConflict>();
        DetectResourceConflicts(state, conflicts);
        DetectAssumptionConflicts(state, conflicts);
        DetectVerifierConflicts(state, conflicts);
        return conflicts;
    }

    private static void DetectResourceConflicts(SharedHarnessState state, List<HarnessConflict> conflicts)
    {
        var actions = CleanList(state.Actions).ToArray();
        for (var i = 0; i < actions.Length; i++)
        {
            for (var j = i + 1; j < actions.Length; j++)
            {
                var left = actions[i];
                var right = actions[j];
                var writeWrite = MatchingResources(left.WriteSet, right.WriteSet).ToArray();
                if (writeWrite.Length > 0)
                {
                    AddConflict(
                        conflicts,
                        state,
                        HarnessConflictTypes.WriteWrite,
                        left,
                        right,
                        writeWrite,
                        $"Actions '{left.Id}' and '{right.Id}' write the same resource.",
                        "Serialize or split ownership before accepting both write paths.");
                }

                var leftReadRightWrite = MatchingResources(left.ReadSet, right.WriteSet).ToArray();
                if (leftReadRightWrite.Length > 0 && HasVersionDependency(left, right, leftReadRightWrite))
                {
                    AddConflict(
                        conflicts,
                        state,
                        HarnessConflictTypes.ReadWrite,
                        left,
                        right,
                        leftReadRightWrite,
                        $"Action '{right.Id}' writes a resource read by '{left.Id}' under a version dependency.",
                        "Re-run the reader verification or serialize the writer after the reader dependency is released.");
                }

                var rightReadLeftWrite = MatchingResources(right.ReadSet, left.WriteSet).ToArray();
                if (rightReadLeftWrite.Length > 0 && HasVersionDependency(right, left, rightReadLeftWrite))
                {
                    AddConflict(
                        conflicts,
                        state,
                        HarnessConflictTypes.ReadWrite,
                        right,
                        left,
                        rightReadLeftWrite,
                        $"Action '{left.Id}' writes a resource read by '{right.Id}' under a version dependency.",
                        "Re-run the reader verification or serialize the writer after the reader dependency is released.");
                }
            }
        }
    }

    private static void DetectAssumptionConflicts(SharedHarnessState state, List<HarnessConflict> conflicts)
    {
        var actionsWithAssumptions = CleanList(state.Actions);
        var assumptions = CleanList(state.Assumptions)
            .Select(item => (Owner: "state", Action: (HarnessStateAction?)null, Assumption: item))
            .Concat(actionsWithAssumptions.SelectMany(action => CleanList(action.Assumptions).Select(item => (Owner: action.Id, Action: (HarnessStateAction?)action, Assumption: item))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Assumption.Key))
            .ToArray();

        for (var i = 0; i < assumptions.Length; i++)
        {
            for (var j = i + 1; j < assumptions.Length; j++)
            {
                var left = assumptions[i];
                var right = assumptions[j];
                if (!string.Equals(left.Assumption.Key, right.Assumption.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(left.Assumption.Value, right.Assumption.Value, StringComparison.Ordinal))
                    continue;

                var actions = new[] { left.Action?.Id, right.Action?.Id }
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var participants = actions
                    .Select(actionId => actionsWithAssumptions.FirstOrDefault(action => string.Equals(action.Id, actionId, StringComparison.Ordinal))?.ParticipantId)
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                conflicts.Add(new HarnessConflict
                {
                    Id = $"conflict_{conflicts.Count + 1}",
                    Type = HarnessConflictTypes.Assumption,
                    Summary = $"Assumption '{left.Assumption.Key}' has conflicting values.",
                    Actions = actions,
                    Participants = participants,
                    Policy = HarnessConflictPolicies.Warn,
                    Severity = HarnessContractRiskLevels.Medium,
                    Status = HarnessStateStatuses.Active,
                    Recommendation = "Resolve the assumption explicitly before relying on either workstream."
                });
            }
        }
    }

    private static void DetectVerifierConflicts(SharedHarnessState state, List<HarnessConflict> conflicts)
    {
        var stateVerifierObligations = CleanList(state.VerifierObligations);
        foreach (var action in CleanList(state.Actions).Where(action => IsHighRisk(action.RiskLevel) && CleanList(action.WriteSet).Count > 0))
        {
            var writeSet = CleanList(action.WriteSet);
            var hasRequiredVerifier = CleanList(action.VerifierObligations).Any(static item => item.Required)
                || stateVerifierObligations.Any(static item => item.Required);
            if (hasRequiredVerifier)
                continue;

            conflicts.Add(new HarnessConflict
            {
                Id = $"conflict_{conflicts.Count + 1}",
                Type = HarnessConflictTypes.VerifierObligation,
                Summary = $"High-risk write action '{action.Id}' has no required verifier obligation.",
                Actions = [action.Id],
                Participants = string.IsNullOrWhiteSpace(action.ParticipantId) ? [] : [action.ParticipantId],
                Resources = writeSet,
                Policy = HarnessConflictPolicies.Escalate,
                Severity = HarnessContractRiskLevels.High,
                Status = HarnessStateStatuses.Active,
                Recommendation = "Add a required verifier obligation or escalate to an operator before accepting the action."
            });
        }
    }

    private static void AddConflict(
        List<HarnessConflict> conflicts,
        SharedHarnessState state,
        string type,
        HarnessStateAction left,
        HarnessStateAction right,
        IReadOnlyList<HarnessResourceRef> resources,
        string summary,
        string recommendation)
    {
        var risk = MaxRisk(left.RiskLevel, right.RiskLevel);
        conflicts.Add(new HarnessConflict
        {
            Id = $"conflict_{conflicts.Count + 1}",
            Type = type,
            Summary = summary,
            Participants = new[] { left.ParticipantId, right.ParticipantId }
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray()!,
            Actions = [left.Id, right.Id],
            Resources = resources,
            Policy = ConflictPolicyForRisk(risk),
            Severity = risk,
            Status = HarnessStateStatuses.Active,
            Recommendation = recommendation
        });
    }

    private static IEnumerable<HarnessResourceRef> MatchingResources(
        IReadOnlyList<HarnessResourceRef>? left,
        IReadOnlyList<HarnessResourceRef>? right)
    {
        foreach (var leftResource in CleanList(left))
        {
            foreach (var rightResource in CleanList(right).Where(resource => ResourcesMatch(leftResource, resource)))
                yield return PreferDescriptiveResource(leftResource, rightResource);
        }
    }

    private static bool HasVersionDependency(
        HarnessStateAction reader,
        HarnessStateAction writer,
        IReadOnlyList<HarnessResourceRef> resources)
    {
        var readerDependencies = CleanList(reader.VersionDependencies);
        var writerDependencies = CleanList(writer.VersionDependencies);
        if (readerDependencies.Count == 0 && writerDependencies.Count == 0)
            return false;

        return readerDependencies.Any(dep => dep.Resource is null || resources.Any(resource => ResourcesMatch(dep.Resource, resource)))
            || writerDependencies.Any(dep => dep.Resource is null || resources.Any(resource => ResourcesMatch(dep.Resource, resource)));
    }

    private static bool ResourcesMatch(HarnessResourceRef? left, HarnessResourceRef? right)
    {
        if (left is null || right is null)
            return false;

        if (!string.IsNullOrWhiteSpace(left.Id) && string.Equals(left.Id, right.Id, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrWhiteSpace(left.Key) && string.Equals(left.Key, right.Key, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrWhiteSpace(left.Uri) && string.Equals(left.Uri, right.Uri, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(left.Path) || string.IsNullOrWhiteSpace(right.Path))
            return false;

        var leftPath = NormalizeResourcePath(left.Path);
        var rightPath = NormalizeResourcePath(right.Path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(leftPath, rightPath, comparison))
            return KindsMatch(left, right) || IsDirectoryResource(left) || IsDirectoryResource(right);

        if (!IsDirectoryResource(left) && !IsDirectoryResource(right))
            return false;

        return IsPathPrefix(leftPath, rightPath, comparison) || IsPathPrefix(rightPath, leftPath, comparison);
    }

    private static HarnessResourceRef PreferDescriptiveResource(HarnessResourceRef left, HarnessResourceRef right)
        => !string.IsNullOrWhiteSpace(left.Description) ? left : right;

    private static bool IsDirectoryResource(HarnessResourceRef resource)
        => string.Equals(resource.Kind, HarnessContractResourceKinds.Directory, StringComparison.OrdinalIgnoreCase);

    private static bool KindsMatch(HarnessResourceRef left, HarnessResourceRef right)
        => string.IsNullOrWhiteSpace(left.Kind)
            || string.IsNullOrWhiteSpace(right.Kind)
            || string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathPrefix(string prefix, string value, StringComparison comparison)
        => value.Length > prefix.Length
            && value.StartsWith(prefix, comparison)
            && value[prefix.Length] is '/' or '\\';

    private static string NormalizeResourcePath(string path)
        => path.Replace('\\', '/').Trim().TrimEnd('/');

    private static SharedHarnessState Normalize(SharedHarnessState state, DateTimeOffset now, bool isNew)
    {
        var id = string.IsNullOrWhiteSpace(state.Id)
            ? $"shs_{Guid.NewGuid():N}"[..24]
            : state.Id.Trim();
        var createdAt = state.CreatedAtUtc == default || isNew ? now : state.CreatedAtUtc;
        var status = string.IsNullOrWhiteSpace(state.Status) ? HarnessStateStatuses.Active : NormalizeStatus(state.Status);

        return Copy(
            state,
            id: id,
            status: status,
            createdAtUtc: createdAt,
            updatedAtUtc: now,
            participants: NormalizeParticipants(state.Participants, now),
            actions: NormalizeActions(state.Actions, now),
            conflicts: CleanList(state.Conflicts));
    }

    private static SharedHarnessState Copy(
        SharedHarnessState source,
        string? id = null,
        string? status = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null,
        IReadOnlyList<HarnessParticipant>? participants = null,
        IReadOnlyList<HarnessStateAction>? actions = null,
        IReadOnlyList<HarnessConflict>? conflicts = null)
        => new()
        {
            Id = id ?? source.Id,
            SessionId = NullIfWhiteSpace(source.SessionId),
            ParentSessionId = NullIfWhiteSpace(source.ParentSessionId),
            HarnessContractId = NullIfWhiteSpace(source.HarnessContractId),
            CreatedAtUtc = createdAtUtc ?? source.CreatedAtUtc,
            UpdatedAtUtc = updatedAtUtc ?? source.UpdatedAtUtc,
            Status = status ?? source.Status,
            Goal = source.Goal,
            Participants = participants ?? CleanList(source.Participants),
            Actions = actions ?? CleanList(source.Actions),
            SharedReadSet = CleanList(source.SharedReadSet),
            SharedWriteSet = CleanList(source.SharedWriteSet),
            Assumptions = CleanList(source.Assumptions),
            VersionDependencies = CleanList(source.VersionDependencies),
            VerifierObligations = CleanList(source.VerifierObligations),
            Conflicts = conflicts ?? CleanList(source.Conflicts),
            EvidenceBundleIds = CleanStrings(source.EvidenceBundleIds),
            GovernanceLedgerIds = CleanStrings(source.GovernanceLedgerIds),
            Tags = CleanStrings(source.Tags),
            Metadata = CleanDictionary(source.Metadata)
        };

    private static IReadOnlyList<HarnessParticipant> NormalizeParticipants(IReadOnlyList<HarnessParticipant>? participants, DateTimeOffset now)
    {
        var items = CleanList(participants);
        var usedIds = ToIdSet(items.Select(static item => item.Id));
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<HarnessParticipant>(items.Count);
        foreach (var item in items)
        {
            var index = string.IsNullOrWhiteSpace(item.Id)
                ? FirstUnusedGeneratedIndex(usedIds, "participant")
                : normalized.Count;
            var participant = NormalizeParticipant(item, index, now);
            if (!normalizedIds.Add(participant.Id))
                throw new ArgumentException($"Duplicate participant id '{participant.Id}'.", nameof(participants));
            usedIds.Add(participant.Id);
            normalized.Add(participant);
        }

        return normalized;
    }

    private static HarnessParticipant NormalizeParticipant(HarnessParticipant participant, int index, DateTimeOffset now)
    {
        var status = string.IsNullOrWhiteSpace(participant.Status) ? HarnessStateStatuses.Active : NormalizeStatus(participant.Status);
        return new HarnessParticipant
        {
            Id = string.IsNullOrWhiteSpace(participant.Id) ? $"participant_{index + 1}" : participant.Id.Trim(),
            AgentId = NullIfWhiteSpace(participant.AgentId),
            SessionId = NullIfWhiteSpace(participant.SessionId),
            Role = NormalizeRole(participant.Role),
            DisplayName = NullIfWhiteSpace(participant.DisplayName),
            ModelProfileId = NullIfWhiteSpace(participant.ModelProfileId),
            ToolPreset = NullIfWhiteSpace(participant.ToolPreset),
            StartedAtUtc = participant.StartedAtUtc == default ? now : participant.StartedAtUtc,
            CompletedAtUtc = participant.CompletedAtUtc,
            Status = status,
            ParentParticipantId = NullIfWhiteSpace(participant.ParentParticipantId),
            Notes = NullIfWhiteSpace(participant.Notes)
        };
    }

    private static IReadOnlyList<HarnessStateAction> NormalizeActions(IReadOnlyList<HarnessStateAction>? actions, DateTimeOffset now)
    {
        var items = CleanList(actions);
        var usedIds = ToIdSet(items.Select(static item => item.Id));
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<HarnessStateAction>(items.Count);
        foreach (var item in items)
        {
            var index = string.IsNullOrWhiteSpace(item.Id)
                ? FirstUnusedGeneratedIndex(usedIds, "action")
                : normalized.Count;
            var action = NormalizeAction(item, index, now);
            if (!normalizedIds.Add(action.Id))
                throw new ArgumentException($"Duplicate action id '{action.Id}'.", nameof(actions));
            usedIds.Add(action.Id);
            normalized.Add(action);
        }

        return normalized;
    }

    private static HarnessStateAction NormalizeAction(HarnessStateAction action, int index, DateTimeOffset now)
    {
        var status = string.IsNullOrWhiteSpace(action.Status) ? HarnessStateStatuses.Active : NormalizeStatus(action.Status);
        return CopyAction(
            action,
            id: string.IsNullOrWhiteSpace(action.Id) ? $"action_{index + 1}" : action.Id.Trim(),
            status: status,
            startedAtUtc: action.StartedAtUtc == default ? now : action.StartedAtUtc,
            completedAtUtc: action.CompletedAtUtc);
    }

    private static HarnessStateAction CopyAction(
        HarnessStateAction source,
        string? id = null,
        string? status = null,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? completedAtUtc = null)
        => new()
        {
            Id = id ?? source.Id,
            ParticipantId = NullIfWhiteSpace(source.ParticipantId),
            Title = source.Title,
            Summary = NullIfWhiteSpace(source.Summary),
            Status = status ?? source.Status,
            ToolName = NullIfWhiteSpace(source.ToolName),
            ReadSet = CleanList(source.ReadSet),
            WriteSet = CleanList(source.WriteSet),
            Assumptions = CleanList(source.Assumptions),
            VersionDependencies = CleanList(source.VersionDependencies),
            VerifierObligations = CleanList(source.VerifierObligations),
            EvidenceBundleId = NullIfWhiteSpace(source.EvidenceBundleId),
            HarnessContractId = NullIfWhiteSpace(source.HarnessContractId),
            RiskLevel = NormalizeRiskOrNull(source.RiskLevel),
            StartedAtUtc = startedAtUtc ?? source.StartedAtUtc,
            CompletedAtUtc = completedAtUtc
        };

    private static IReadOnlyList<T> CleanList<T>(IReadOnlyList<T>? items)
        where T : class
        => items?.Where(static item => item is not null).ToArray() ?? [];

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? items)
        => items?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static Dictionary<string, string> CleanDictionary(Dictionary<string, string>? items)
        => items is null
            ? []
            : items
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(static item => item.Key.Trim(), static item => item.Value ?? "", StringComparer.Ordinal);

    private static HashSet<string> ToIdSet(IEnumerable<string?> ids)
        => ids
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static int FirstUnusedGeneratedIndex(IEnumerable<string?> existingIds, string prefix)
        => FirstUnusedGeneratedIndex(ToIdSet(existingIds), prefix);

    private static int FirstUnusedGeneratedIndex(ISet<string> existingIds, string prefix)
    {
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            if (!existingIds.Contains($"{prefix}_{suffix}"))
                return suffix - 1;
        }

        throw new InvalidOperationException($"No generated {prefix} id is available.");
    }

    private void AppendEvent(SharedHarnessState state, string action, string severity, string summary)
    {
        try
        {
            _runtimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = state.SessionId,
                CorrelationId = state.Id,
                Component = "harness",
                Action = action,
                Severity = severity,
                Summary = summary,
                Metadata = new Dictionary<string, string>
                {
                    ["sharedHarnessStateId"] = state.Id,
                    ["status"] = state.Status
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to append shared harness state runtime event for {StateId}.", state.Id);
        }
    }

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            HarnessParticipantRoles.Manager => HarnessParticipantRoles.Manager,
            HarnessParticipantRoles.Planner => HarnessParticipantRoles.Planner,
            HarnessParticipantRoles.Coder => HarnessParticipantRoles.Coder,
            HarnessParticipantRoles.Reviewer => HarnessParticipantRoles.Reviewer,
            HarnessParticipantRoles.Tester => HarnessParticipantRoles.Tester,
            HarnessParticipantRoles.SecurityReviewer => HarnessParticipantRoles.SecurityReviewer,
            HarnessParticipantRoles.OpsVerifier => HarnessParticipantRoles.OpsVerifier,
            HarnessParticipantRoles.DocsWriter => HarnessParticipantRoles.DocsWriter,
            HarnessParticipantRoles.Researcher => HarnessParticipantRoles.Researcher,
            HarnessParticipantRoles.Operator => HarnessParticipantRoles.Operator,
            _ => HarnessParticipantRoles.Custom
        };

    private static string NormalizeStatus(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            HarnessStateStatuses.Active => HarnessStateStatuses.Active,
            HarnessStateStatuses.Completed => HarnessStateStatuses.Completed,
            HarnessStateStatuses.Failed => HarnessStateStatuses.Failed,
            HarnessStateStatuses.Cancelled => HarnessStateStatuses.Cancelled,
            HarnessStateStatuses.Blocked => HarnessStateStatuses.Blocked,
            HarnessStateStatuses.Unknown => HarnessStateStatuses.Unknown,
            _ => throw new ArgumentException($"Unsupported shared harness state status '{status}'.", nameof(status))
        };

    private static bool IsTerminalStatus(string status)
        => status.Trim().ToLowerInvariant() is HarnessStateStatuses.Completed
            or HarnessStateStatuses.Failed
            or HarnessStateStatuses.Cancelled;

    private static string? NormalizeRiskOrNull(string? risk)
        => string.IsNullOrWhiteSpace(risk) ? null : NormalizeRisk(risk);

    private static string NormalizeRisk(string risk)
        => risk.Trim().ToLowerInvariant() switch
        {
            HarnessContractRiskLevels.Critical => HarnessContractRiskLevels.Critical,
            HarnessContractRiskLevels.High => HarnessContractRiskLevels.High,
            HarnessContractRiskLevels.Medium => HarnessContractRiskLevels.Medium,
            HarnessContractRiskLevels.Low => HarnessContractRiskLevels.Low,
            EvidenceRiskLevels.Unknown => EvidenceRiskLevels.Unknown,
            _ => HarnessContractRiskLevels.Medium
        };

    private static bool IsHighRisk(string? risk)
        => NormalizeRisk(risk ?? EvidenceRiskLevels.Unknown) is HarnessContractRiskLevels.High or HarnessContractRiskLevels.Critical;

    private static string MaxRisk(string? left, string? right)
        => RiskRank(right) > RiskRank(left) ? NormalizeRisk(right ?? "") : NormalizeRisk(left ?? "");

    private static int RiskRank(string? risk)
        => NormalizeRisk(risk ?? EvidenceRiskLevels.Unknown) switch
        {
            HarnessContractRiskLevels.Critical => 4,
            HarnessContractRiskLevels.High => 3,
            HarnessContractRiskLevels.Medium => 2,
            HarnessContractRiskLevels.Low => 1,
            _ => 0
        };

    private static string ConflictPolicyForRisk(string risk)
        => RiskRank(risk) >= RiskRank(HarnessContractRiskLevels.High)
            ? HarnessConflictPolicies.Escalate
            : HarnessConflictPolicies.Warn;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

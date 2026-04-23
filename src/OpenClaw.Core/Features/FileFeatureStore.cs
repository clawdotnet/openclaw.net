using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class FileFeatureStore : IAutomationStore, IUserProfileStore, ILearningProposalStore, IConnectedAccountStore, IBackendSessionStore
{
    private readonly string _automationsPath;
    private readonly string _automationRunsPath;
    private readonly string _automationRunHistoryPath;
    private readonly string _accountsPath;
    private readonly string _backendEventsPath;
    private readonly string _backendSessionsPath;
    private readonly string _profilesPath;
    private readonly string _proposalsPath;

    public FileFeatureStore(string storagePath)
    {
        var root = Path.GetFullPath(storagePath);
        _automationsPath = Path.Combine(root, "automations");
        _automationRunsPath = Path.Combine(root, "automation-runs");
        _automationRunHistoryPath = Path.Combine(root, "automation-run-history");
        _accountsPath = Path.Combine(root, "connected-accounts");
        _backendSessionsPath = Path.Combine(root, "backend-sessions");
        _backendEventsPath = Path.Combine(root, "backend-session-events");
        _profilesPath = Path.Combine(root, "profiles");
        _proposalsPath = Path.Combine(root, "learning-proposals");

        Directory.CreateDirectory(_automationsPath);
        Directory.CreateDirectory(_automationRunsPath);
        Directory.CreateDirectory(_automationRunHistoryPath);
        Directory.CreateDirectory(_accountsPath);
        Directory.CreateDirectory(_backendSessionsPath);
        Directory.CreateDirectory(_backendEventsPath);
        Directory.CreateDirectory(_profilesPath);
        Directory.CreateDirectory(_proposalsPath);
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> ListAutomationsAsync(CancellationToken ct)
        => await LoadAllAsync(_automationsPath, CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask<AutomationDefinition?> GetAutomationAsync(string automationId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_automationsPath, $"{EncodeKey(automationId)}.json"), CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask SaveAutomationAsync(AutomationDefinition automation, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_automationsPath, $"{EncodeKey(automation.Id)}.json"), automation, CoreJsonContext.Default.AutomationDefinition, ct);

    public async ValueTask DeleteAutomationAsync(string automationId, CancellationToken ct)
    {
        await DeleteOneAsync(Path.Combine(_automationsPath, $"{EncodeKey(automationId)}.json"));
        await DeleteOneAsync(Path.Combine(_automationRunsPath, $"{EncodeKey(automationId)}.json"));
        DeleteDirectory(Path.Combine(_automationRunHistoryPath, EncodeKey(automationId)));
    }

    public ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_automationRunsPath, $"{EncodeKey(automationId)}.json"), CoreJsonContext.Default.AutomationRunState, ct);

    public ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_automationRunsPath, $"{EncodeKey(runState.AutomationId)}.json"), runState, CoreJsonContext.Default.AutomationRunState, ct);

    public async ValueTask<IReadOnlyList<AutomationRunRecord>> ListRunRecordsAsync(string automationId, int limit, CancellationToken ct)
    {
        var directory = Path.Combine(_automationRunHistoryPath, EncodeKey(automationId));
        if (!Directory.Exists(directory))
            return [];

        var items = await LoadAllAsync(directory, CoreJsonContext.Default.AutomationRunRecord, ct);
        return items
            .OrderByDescending(static item => item.StartedAtUtc)
            .ThenByDescending(static item => item.CompletedAtUtc ?? item.StartedAtUtc)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public ValueTask<AutomationRunRecord?> GetRunRecordAsync(string automationId, string runId, CancellationToken ct)
        => LoadOneAsync(
            Path.Combine(_automationRunHistoryPath, EncodeKey(automationId), $"{EncodeKey(runId)}.json"),
            CoreJsonContext.Default.AutomationRunRecord,
            ct);

    public async ValueTask SaveRunRecordAsync(AutomationRunRecord runRecord, CancellationToken ct)
    {
        var directory = Path.Combine(_automationRunHistoryPath, EncodeKey(runRecord.AutomationId));
        Directory.CreateDirectory(directory);
        await SaveOneAsync(
            Path.Combine(directory, $"{EncodeKey(runRecord.RunId)}.json"),
            runRecord,
            CoreJsonContext.Default.AutomationRunRecord,
            ct);
    }

    public async ValueTask PruneRunRecordsAsync(string automationId, int retainCount, CancellationToken ct)
    {
        var directory = Path.Combine(_automationRunHistoryPath, EncodeKey(automationId));
        if (!Directory.Exists(directory))
            return;

        var retain = Math.Max(1, retainCount);
        var records = await LoadAllAsync(directory, CoreJsonContext.Default.AutomationRunRecord, ct);
        var toDelete = records
            .OrderByDescending(static item => item.StartedAtUtc)
            .ThenByDescending(static item => item.CompletedAtUtc ?? item.StartedAtUtc)
            .Skip(retain)
            .Select(item => Path.Combine(directory, $"{EncodeKey(item.RunId)}.json"))
            .ToArray();

        foreach (var path in toDelete)
            await DeleteOneAsync(path);
    }

    public async ValueTask<IReadOnlyList<UserProfile>> ListProfilesAsync(CancellationToken ct)
        => await LoadAllAsync(_profilesPath, CoreJsonContext.Default.UserProfile, ct);

    public ValueTask<UserProfile?> GetProfileAsync(string actorId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_profilesPath, $"{EncodeKey(actorId)}.json"), CoreJsonContext.Default.UserProfile, ct);

    public ValueTask SaveProfileAsync(UserProfile profile, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_profilesPath, $"{EncodeKey(profile.ActorId)}.json"), profile, CoreJsonContext.Default.UserProfile, ct);

    public ValueTask DeleteProfileAsync(string actorId, CancellationToken ct)
        => DeleteOneAsync(Path.Combine(_profilesPath, $"{EncodeKey(actorId)}.json"));

    public async ValueTask<IReadOnlyList<LearningProposal>> ListProposalsAsync(string? status, string? kind, CancellationToken ct)
    {
        var all = await LoadAllAsync(_proposalsPath, CoreJsonContext.Default.LearningProposal, ct);
        return all
            .Where(item => string.IsNullOrWhiteSpace(status) || string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(kind) || string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ToArray();
    }

    public ValueTask<LearningProposal?> GetProposalAsync(string proposalId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_proposalsPath, $"{EncodeKey(proposalId)}.json"), CoreJsonContext.Default.LearningProposal, ct);

    public ValueTask SaveProposalAsync(LearningProposal proposal, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_proposalsPath, $"{EncodeKey(proposal.Id)}.json"), proposal, CoreJsonContext.Default.LearningProposal, ct);

    public async ValueTask<IReadOnlyList<ConnectedAccount>> ListAccountsAsync(CancellationToken ct)
        => await LoadAllAsync(_accountsPath, CoreJsonContext.Default.ConnectedAccount, ct);

    public ValueTask<ConnectedAccount?> GetAccountAsync(string accountId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_accountsPath, $"{EncodeKey(accountId)}.json"), CoreJsonContext.Default.ConnectedAccount, ct);

    public ValueTask SaveAccountAsync(ConnectedAccount account, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_accountsPath, $"{EncodeKey(account.Id)}.json"), account, CoreJsonContext.Default.ConnectedAccount, ct);

    public ValueTask DeleteAccountAsync(string accountId, CancellationToken ct)
        => DeleteOneAsync(Path.Combine(_accountsPath, $"{EncodeKey(accountId)}.json"));

    public async ValueTask<IReadOnlyList<BackendSessionRecord>> ListBackendSessionsAsync(string? backendId, CancellationToken ct)
    {
        var all = await LoadAllAsync(_backendSessionsPath, CoreJsonContext.Default.BackendSessionRecord, ct);
        return string.IsNullOrWhiteSpace(backendId)
            ? all
            : all.Where(item => string.Equals(item.BackendId, backendId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public ValueTask<BackendSessionRecord?> GetBackendSessionAsync(string sessionId, CancellationToken ct)
        => LoadOneAsync(Path.Combine(_backendSessionsPath, $"{EncodeKey(sessionId)}.json"), CoreJsonContext.Default.BackendSessionRecord, ct);

    public ValueTask SaveBackendSessionAsync(BackendSessionRecord session, CancellationToken ct)
        => SaveOneAsync(Path.Combine(_backendSessionsPath, $"{EncodeKey(session.SessionId)}.json"), session, CoreJsonContext.Default.BackendSessionRecord, ct);

    public ValueTask DeleteBackendSessionAsync(string sessionId, CancellationToken ct)
        => DeleteOneAsync(Path.Combine(_backendSessionsPath, $"{EncodeKey(sessionId)}.json"));

    public async ValueTask AppendBackendEventAsync(BackendEvent evt, CancellationToken ct)
    {
        var path = Path.Combine(_backendEventsPath, $"{EncodeKey(evt.SessionId)}.json");
        var events = await LoadOneAsync(path, CoreJsonContext.Default.ListBackendEvent, ct) ?? [];
        events.Add(evt);
        await SaveOneAsync(path, events, CoreJsonContext.Default.ListBackendEvent, ct);
    }

    public async ValueTask<IReadOnlyList<BackendEvent>> ListBackendEventsAsync(string sessionId, long afterSequence, int limit, CancellationToken ct)
    {
        var path = Path.Combine(_backendEventsPath, $"{EncodeKey(sessionId)}.json");
        var events = await LoadOneAsync(path, CoreJsonContext.Default.ListBackendEvent, ct) ?? [];
        return events
            .Where(item => item.Sequence > afterSequence)
            .OrderBy(item => item.Sequence)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    private static async ValueTask<IReadOnlyList<T>> LoadAllAsync<T>(
        string directory,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        var results = new List<T>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json");
        }
        catch
        {
            return [];
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var item = await LoadOneAsync(file, typeInfo, ct);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    private static async ValueTask<T?> LoadOneAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            return default;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch
        {
            return default;
        }
    }

    private static async ValueTask SaveOneAsync<T>(
        string path,
        T item,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(item, typeInfo);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private static ValueTask DeleteOneAsync(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        return ValueTask.CompletedTask;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static string EncodeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "item";

        var bytes = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

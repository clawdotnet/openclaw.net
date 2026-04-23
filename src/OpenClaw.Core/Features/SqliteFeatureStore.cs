using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class SqliteFeatureStore : IAutomationStore, IUserProfileStore, ILearningProposalStore, IConnectedAccountStore, IBackendSessionStore, IDisposable
{
    private readonly string _dbPath;

    public SqliteFeatureStore(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        Initialize();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Cache = SqliteCacheMode.Shared,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    private void Initialize()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS automations (
              id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS automation_runs (
              automation_id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS automation_run_history (
              automation_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              started_at INTEGER NOT NULL,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL,
              PRIMARY KEY(automation_id, run_id)
            );

            CREATE TABLE IF NOT EXISTS user_profiles (
              actor_id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS connected_accounts (
              id TEXT PRIMARY KEY,
              provider TEXT NOT NULL,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS backend_sessions (
              session_id TEXT PRIMARY KEY,
              backend_id TEXT NOT NULL,
              state TEXT NOT NULL,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS backend_session_events (
              session_id TEXT NOT NULL,
              sequence INTEGER NOT NULL,
              json TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              PRIMARY KEY(session_id, sequence)
            );

            CREATE TABLE IF NOT EXISTS learning_proposals (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              status TEXT NOT NULL,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_learning_status ON learning_proposals(status, kind, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_automation_run_history_lookup ON automation_run_history(automation_id, started_at DESC, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_connected_accounts_provider ON connected_accounts(provider, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_backend_sessions_backend ON backend_sessions(backend_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_backend_session_events_lookup ON backend_session_events(session_id, sequence);
            """;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> ListAutomationsAsync(CancellationToken ct)
        => await QueryJsonListAsync("SELECT json FROM automations ORDER BY updated_at DESC;", CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask<AutomationDefinition?> GetAutomationAsync(string automationId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM automations WHERE id = $id LIMIT 1;", "$id", automationId, CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask SaveAutomationAsync(AutomationDefinition automation, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO automations(id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", automation.Id, JsonSerializer.Serialize(automation, CoreJsonContext.Default.AutomationDefinition), ct);

    public async ValueTask DeleteAutomationAsync(string automationId, CancellationToken ct)
    {
        await ExecuteAsync("DELETE FROM automations WHERE id = $id;", "$id", automationId, ct);
        await ExecuteAsync("DELETE FROM automation_runs WHERE automation_id = $id;", "$id", automationId, ct);
        await ExecuteAsync("DELETE FROM automation_run_history WHERE automation_id = $id;", "$id", automationId, ct);
    }

    public ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM automation_runs WHERE automation_id = $id LIMIT 1;", "$id", automationId, CoreJsonContext.Default.AutomationRunState, ct);

    public ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO automation_runs(automation_id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(automation_id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", runState.AutomationId, JsonSerializer.Serialize(runState, CoreJsonContext.Default.AutomationRunState), ct);

    public async ValueTask<IReadOnlyList<AutomationRunRecord>> ListRunRecordsAsync(string automationId, int limit, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT json FROM automation_run_history
            WHERE automation_id = $automation_id
            ORDER BY started_at DESC, updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$automation_id", automationId);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var results = new List<AutomationRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize(reader.GetString(0), CoreJsonContext.Default.AutomationRunRecord);
            if (record is not null)
                results.Add(record);
        }

        return results;
    }

    public ValueTask<AutomationRunRecord?> GetRunRecordAsync(string automationId, string runId, CancellationToken ct)
        => QuerySingleAsync(
            "SELECT json FROM automation_run_history WHERE automation_id = $id AND run_id = $run_id LIMIT 1;",
            parameters =>
            {
                parameters.AddWithValue("$id", automationId);
                parameters.AddWithValue("$run_id", runId);
            },
            CoreJsonContext.Default.AutomationRunRecord,
            ct);

    public async ValueTask SaveRunRecordAsync(AutomationRunRecord runRecord, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO automation_run_history(automation_id, run_id, started_at, json, updated_at)
            VALUES($automation_id, $run_id, $started_at, $json, $updated_at)
            ON CONFLICT(automation_id, run_id) DO UPDATE SET
                started_at=excluded.started_at,
                json=excluded.json,
                updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$automation_id", runRecord.AutomationId);
        cmd.Parameters.AddWithValue("$run_id", runRecord.RunId);
        cmd.Parameters.AddWithValue("$started_at", runRecord.StartedAtUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(runRecord, CoreJsonContext.Default.AutomationRunRecord));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask PruneRunRecordsAsync(string automationId, int retainCount, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM automation_run_history
            WHERE automation_id = $automation_id
              AND run_id IN (
                SELECT run_id
                FROM automation_run_history
                WHERE automation_id = $automation_id
                ORDER BY started_at DESC, updated_at DESC
                LIMIT -1 OFFSET $retain
              );
            """;
        cmd.Parameters.AddWithValue("$automation_id", automationId);
        cmd.Parameters.AddWithValue("$retain", Math.Max(1, retainCount));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<IReadOnlyList<UserProfile>> ListProfilesAsync(CancellationToken ct)
        => await QueryJsonListAsync("SELECT json FROM user_profiles ORDER BY updated_at DESC;", CoreJsonContext.Default.UserProfile, ct);

    public ValueTask<UserProfile?> GetProfileAsync(string actorId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM user_profiles WHERE actor_id = $id LIMIT 1;", "$id", actorId, CoreJsonContext.Default.UserProfile, ct);

    public ValueTask SaveProfileAsync(UserProfile profile, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO user_profiles(actor_id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(actor_id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", profile.ActorId, JsonSerializer.Serialize(profile, CoreJsonContext.Default.UserProfile), ct);

    public ValueTask DeleteProfileAsync(string actorId, CancellationToken ct)
        => ExecuteAsync("DELETE FROM user_profiles WHERE actor_id = $id;", "$id", actorId, ct);

    public async ValueTask<IReadOnlyList<LearningProposal>> ListProposalsAsync(string? status, string? kind, CancellationToken ct)
    {
        var where = new List<string>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("status = $status");
            cmd.Parameters.AddWithValue("$status", status);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            where.Add("kind = $kind");
            cmd.Parameters.AddWithValue("$kind", kind);
        }

        cmd.CommandText = $"SELECT json FROM learning_proposals{(where.Count == 0 ? "" : $" WHERE {string.Join(" AND ", where)}")} ORDER BY updated_at DESC;";
        var results = new List<LearningProposal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var proposal = JsonSerializer.Deserialize(reader.GetString(0), CoreJsonContext.Default.LearningProposal);
            if (proposal is not null)
                results.Add(proposal);
        }

        return results;
    }

    public ValueTask<LearningProposal?> GetProposalAsync(string proposalId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM learning_proposals WHERE id = $id LIMIT 1;", "$id", proposalId, CoreJsonContext.Default.LearningProposal, ct);

    public ValueTask SaveProposalAsync(LearningProposal proposal, CancellationToken ct)
        => UpsertLearningProposalAsync(proposal, ct);

    public async ValueTask<IReadOnlyList<ConnectedAccount>> ListAccountsAsync(CancellationToken ct)
        => await QueryJsonListAsync("SELECT json FROM connected_accounts ORDER BY updated_at DESC;", CoreJsonContext.Default.ConnectedAccount, ct);

    public ValueTask<ConnectedAccount?> GetAccountAsync(string accountId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM connected_accounts WHERE id = $id LIMIT 1;", "$id", accountId, CoreJsonContext.Default.ConnectedAccount, ct);

    public ValueTask SaveAccountAsync(ConnectedAccount account, CancellationToken ct)
        => UpsertConnectedAccountAsync(account, ct);

    public ValueTask DeleteAccountAsync(string accountId, CancellationToken ct)
        => ExecuteAsync("DELETE FROM connected_accounts WHERE id = $id;", "$id", accountId, ct);

    public async ValueTask<IReadOnlyList<BackendSessionRecord>> ListBackendSessionsAsync(string? backendId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(backendId))
        {
            cmd.CommandText = "SELECT json FROM backend_sessions ORDER BY updated_at DESC;";
        }
        else
        {
            cmd.CommandText = "SELECT json FROM backend_sessions WHERE backend_id = $backend_id ORDER BY updated_at DESC;";
            cmd.Parameters.AddWithValue("$backend_id", backendId);
        }

        var results = new List<BackendSessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize(reader.GetString(0), CoreJsonContext.Default.BackendSessionRecord);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    public ValueTask<BackendSessionRecord?> GetBackendSessionAsync(string sessionId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM backend_sessions WHERE session_id = $id LIMIT 1;", "$id", sessionId, CoreJsonContext.Default.BackendSessionRecord, ct);

    public ValueTask SaveBackendSessionAsync(BackendSessionRecord session, CancellationToken ct)
        => UpsertBackendSessionAsync(session, ct);

    public ValueTask DeleteBackendSessionAsync(string sessionId, CancellationToken ct)
        => ExecuteAsync("DELETE FROM backend_sessions WHERE session_id = $id;", "$id", sessionId, ct);

    public async ValueTask AppendBackendEventAsync(BackendEvent evt, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backend_session_events(session_id, sequence, json, created_at)
            VALUES($session_id, $sequence, $json, $created_at)
            ON CONFLICT(session_id, sequence) DO UPDATE SET json=excluded.json, created_at=excluded.created_at;
            """;
        cmd.Parameters.AddWithValue("$session_id", evt.SessionId);
        cmd.Parameters.AddWithValue("$sequence", evt.Sequence);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize<BackendEvent>(evt, CoreJsonContext.Default.BackendEvent));
        cmd.Parameters.AddWithValue("$created_at", evt.TimestampUtc.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<IReadOnlyList<BackendEvent>> ListBackendEventsAsync(string sessionId, long afterSequence, int limit, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT json FROM backend_session_events
            WHERE session_id = $session_id AND sequence > $after_sequence
            ORDER BY sequence ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$after_sequence", afterSequence);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var results = new List<BackendEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<BackendEvent>(reader.GetString(0), CoreJsonContext.Default.BackendEvent);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    private async ValueTask UpsertAsync(string sql, string idParamName, string id, string json, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask UpsertLearningProposalAsync(LearningProposal proposal, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learning_proposals(id, kind, status, json, updated_at)
            VALUES($id, $kind, $status, $json, $updated_at)
            ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, status=excluded.status, json=excluded.json, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", proposal.Id);
        cmd.Parameters.AddWithValue("$kind", proposal.Kind);
        cmd.Parameters.AddWithValue("$status", proposal.Status);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(proposal, CoreJsonContext.Default.LearningProposal));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask UpsertConnectedAccountAsync(ConnectedAccount account, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connected_accounts(id, provider, json, updated_at)
            VALUES($id, $provider, $json, $updated_at)
            ON CONFLICT(id) DO UPDATE SET provider=excluded.provider, json=excluded.json, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.Parameters.AddWithValue("$provider", account.Provider);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(account, CoreJsonContext.Default.ConnectedAccount));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask UpsertBackendSessionAsync(BackendSessionRecord session, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backend_sessions(session_id, backend_id, state, json, updated_at)
            VALUES($session_id, $backend_id, $state, $json, $updated_at)
            ON CONFLICT(session_id) DO UPDATE SET backend_id=excluded.backend_id, state=excluded.state, json=excluded.json, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$session_id", session.SessionId);
        cmd.Parameters.AddWithValue("$backend_id", session.BackendId);
        cmd.Parameters.AddWithValue("$state", session.State);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(session, CoreJsonContext.Default.BackendSessionRecord));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask ExecuteAsync(string sql, string idParamName, string id, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask<T?> QuerySingleAsync<T>(
        string sql,
        string idParamName,
        string id,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private async ValueTask<T?> QuerySingleAsync<T>(
        string sql,
        Action<SqliteParameterCollection> configureParameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configureParameters(cmd.Parameters);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private async ValueTask<IReadOnlyList<T>> QueryJsonListAsync<T>(
        string sql,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var results = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize(reader.GetString(0), typeInfo);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    public void Dispose()
    {
    }
}

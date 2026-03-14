using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Memory;

public sealed class SqliteMemoryStore : IMemoryStore, IMemoryNoteSearch, IMemoryRetentionStore, ISessionAdminStore, IDisposable
{
    private readonly string _dbPath;
    private readonly bool _enableFtsRequested;
    private bool _ftsEnabled;

    public SqliteMemoryStore(string dbPath, bool enableFts)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _enableFtsRequested = enableFts;

        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
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

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS sessions (
                  id TEXT PRIMARY KEY,
                  json TEXT NOT NULL,
                  updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS notes (
                  key TEXT PRIMARY KEY,
                  content TEXT NOT NULL,
                  updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS branches (
                  branch_id TEXT PRIMARY KEY,
                  session_id TEXT NOT NULL,
                  name TEXT NOT NULL,
                  json TEXT NOT NULL,
                  updated_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_sessions_updated_at ON sessions(updated_at);
                CREATE INDEX IF NOT EXISTS idx_branches_updated_at ON branches(updated_at);
                """;
            cmd.ExecuteNonQuery();
        }

        if (_enableFtsRequested)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(key, content);

                    CREATE TRIGGER IF NOT EXISTS notes_ai AFTER INSERT ON notes BEGIN
                      INSERT INTO notes_fts(key, content) VALUES (new.key, new.content);
                    END;

                    CREATE TRIGGER IF NOT EXISTS notes_ad AFTER DELETE ON notes BEGIN
                      INSERT INTO notes_fts(notes_fts, key, content) VALUES ('delete', old.key, old.content);
                    END;

                    CREATE TRIGGER IF NOT EXISTS notes_au AFTER UPDATE ON notes BEGIN
                      INSERT INTO notes_fts(notes_fts, key, content) VALUES ('delete', old.key, old.content);
                      INSERT INTO notes_fts(key, content) VALUES (new.key, new.content);
                    END;
                    """;
                cmd.ExecuteNonQuery();

                // Best-effort backfill for existing notes (idempotent enough for local-first)
                using var backfill = conn.CreateCommand();
                backfill.CommandText = """
                    INSERT INTO notes_fts(key, content)
                    SELECT key, content FROM notes
                    WHERE key NOT IN (SELECT key FROM notes_fts);
                    """;
                backfill.ExecuteNonQuery();

                _ftsEnabled = true;
            }
            catch
            {
                _ftsEnabled = false;
            }
        }
    }

    public async ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM sessions WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", sessionId);

        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask SaveSessionAsync(Session session, CancellationToken ct)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));

        var json = JsonSerializer.Serialize(session, CoreJsonContext.Default.Session);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(id, json, updated_at)
            VALUES($id, $json, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
              json=excluded.json,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", session.Id);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM notes WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);

        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key must be set.", nameof(key));

        content ??= "";
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes(key, content, updated_at)
            VALUES($key, $content, $updated_at)
            ON CONFLICT(key) DO UPDATE SET
              content=excluded.content,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DeleteNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
    {
        prefix ??= "";

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key FROM notes WHERE key LIKE $prefix || '%' ORDER BY key LIMIT 500;";
        cmd.Parameters.AddWithValue("$prefix", prefix);

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        prefix ??= "";
        limit = Math.Clamp(limit, 1, 50);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        if (_ftsEnabled)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT n.key, n.content, n.updated_at, bm25(notes_fts) AS rank
                FROM notes_fts
                JOIN notes n ON n.key = notes_fts.key
                WHERE notes_fts MATCH $q
                  AND n.key LIKE $prefix || '%'
                ORDER BY rank ASC, n.updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$prefix", prefix);
            cmd.Parameters.AddWithValue("$limit", limit);

            var hits = new List<MemoryNoteHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                var content = reader.GetString(1);
                var updatedAt = reader.GetInt64(2);
                var rank = reader.GetDouble(3);

                hits.Add(new MemoryNoteHit
                {
                    Key = key,
                    Content = content,
                    UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(updatedAt),
                    Score = (float)(-rank)
                });
            }
            return hits;
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT key, content, updated_at
                FROM notes
                WHERE key LIKE $prefix || '%'
                  AND (key LIKE '%' || $q || '%' OR content LIKE '%' || $q || '%')
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$prefix", prefix);
            cmd.Parameters.AddWithValue("$limit", limit);

            var hits = new List<MemoryNoteHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new MemoryNoteHit
                {
                    Key = reader.GetString(0),
                    Content = reader.GetString(1),
                    UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    Score = 1.0f
                });
            }
            return hits;
        }
    }

    public async ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
    {
        if (branch is null)
            throw new ArgumentNullException(nameof(branch));

        var json = JsonSerializer.Serialize(branch, CoreJsonContext.Default.SessionBranch);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO branches(branch_id, session_id, name, json, updated_at)
            VALUES($bid, $sid, $name, $json, $updated_at)
            ON CONFLICT(branch_id) DO UPDATE SET
              session_id=excluded.session_id,
              name=excluded.name,
              json=excluded.json,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$bid", branch.BranchId);
        cmd.Parameters.AddWithValue("$sid", branch.SessionId);
        cmd.Parameters.AddWithValue("$name", branch.Name);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branchId))
            return null;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM branches WHERE branch_id = $bid LIMIT 1;";
        cmd.Parameters.AddWithValue("$bid", branchId);

        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionBranch);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM branches WHERE session_id = $sid ORDER BY updated_at DESC LIMIT 200;";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        var list = new List<SessionBranch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            var b = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionBranch);
            if (b is not null)
                list.Add(b);
        }

        return list;
    }

    public async ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branchId))
            return;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM branches WHERE branch_id = $bid;";
        cmd.Parameters.AddWithValue("$bid", branchId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<RetentionSweepResult> SweepAsync(
        RetentionSweepRequest request,
        IReadOnlySet<string> protectedSessionIds,
        CancellationToken ct)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        protectedSessionIds ??= new HashSet<string>(StringComparer.Ordinal);

        var result = new RetentionSweepResult
        {
            StartedAtUtc = request.NowUtc,
            DryRun = request.DryRun
        };

        var remaining = Math.Max(1, request.MaxItems);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        remaining = await SweepSessionsAsync(conn, request, protectedSessionIds, result, remaining, ct);
        if (remaining > 0)
            remaining = await SweepBranchesAsync(conn, request, result, remaining, ct);

        if (remaining <= 0)
            result.MaxItemsLimitReached = true;

        if (request.ArchiveEnabled && !request.DryRun)
        {
            var purgeResult = MemoryRetentionArchive.PurgeExpiredArchives(
                request.ArchivePath,
                request.NowUtc,
                request.ArchiveRetentionDays,
                ct);

            result.ArchivePurgedFiles = purgeResult.DeletedFiles;
            result.ArchivePurgeErrors = purgeResult.Errors;
            foreach (var error in purgeResult.ErrorMessages)
            {
                if (result.Errors.Count >= 16)
                    break;
                result.Errors.Add(error);
            }
        }

        result.CompletedAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    public async ValueTask<RetentionStoreStats> GetRetentionStatsAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM sessions) AS sessions_count,
              (SELECT COUNT(*) FROM branches) AS branches_count;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new RetentionStoreStats
            {
                Backend = "sqlite",
                PersistedSessions = 0,
                PersistedBranches = 0
            };
        }

        return new RetentionStoreStats
        {
            Backend = "sqlite",
            PersistedSessions = reader.GetInt64(0),
            PersistedBranches = reader.GetInt64(1)
        };
    }

    private async ValueTask<int> SweepSessionsAsync(
        SqliteConnection conn,
        RetentionSweepRequest request,
        IReadOnlySet<string> protectedSessionIds,
        RetentionSweepResult result,
        int remaining,
        CancellationToken ct)
    {
        if (remaining <= 0)
            return 0;

        var cutoff = request.SessionExpiresBeforeUtc.ToUnixTimeSeconds();
        var scanLimit = Math.Min(Math.Max(remaining * 4, remaining), 20_000);
        var pendingDeletes = new List<string>(capacity: Math.Min(remaining, 256));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, json
            FROM sessions
            WHERE updated_at < $cutoff
            ORDER BY updated_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$limit", scanLimit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (remaining <= 0)
            {
                result.MaxItemsLimitReached = true;
                break;
            }

            ct.ThrowIfCancellationRequested();

            var sessionId = reader.GetString(0);
            var payloadJson = reader.GetString(1);

            Session? session;
            try
            {
                session = JsonSerializer.Deserialize(payloadJson, CoreJsonContext.Default.Session);
            }
            catch
            {
                session = null;
            }

            if (session is null)
            {
                result.SkippedCorruptSessionItems++;
                continue;
            }

            if (protectedSessionIds.Contains(sessionId))
            {
                result.SkippedProtectedSessions++;
                continue;
            }

            if (session.LastActiveAt >= request.SessionExpiresBeforeUtc)
                continue;

            result.EligibleSessions++;
            remaining--;

            if (request.DryRun)
                continue;

            if (request.ArchiveEnabled)
            {
                try
                {
                    await MemoryRetentionArchive.ArchivePayloadAsync(
                        request.ArchivePath,
                        request.NowUtc,
                        kind: "sessions",
                        sessionId,
                        request.SessionExpiresBeforeUtc,
                        sourceBackend: "sqlite",
                        payloadJson,
                        ct);
                    result.ArchivedSessions++;
                }
                catch (Exception ex)
                {
                    if (result.Errors.Count < 16)
                        result.Errors.Add($"Failed to archive session '{sessionId}': {ex.Message}");
                    continue;
                }
            }

            pendingDeletes.Add(sessionId);
        }

        if (pendingDeletes.Count > 0)
            result.DeletedSessions += await DeleteSessionsByIdAsync(conn, pendingDeletes, ct);

        return remaining;
    }

    private async ValueTask<int> SweepBranchesAsync(
        SqliteConnection conn,
        RetentionSweepRequest request,
        RetentionSweepResult result,
        int remaining,
        CancellationToken ct)
    {
        if (remaining <= 0)
            return 0;

        var cutoff = request.BranchExpiresBeforeUtc.ToUnixTimeSeconds();
        var pendingDeletes = new List<string>(capacity: Math.Min(remaining, 256));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT branch_id, json
            FROM branches
            WHERE updated_at < $cutoff
            ORDER BY updated_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$limit", remaining);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (remaining <= 0)
            {
                result.MaxItemsLimitReached = true;
                break;
            }

            ct.ThrowIfCancellationRequested();

            var branchId = reader.GetString(0);
            var payloadJson = reader.GetString(1);

            result.EligibleBranches++;
            remaining--;

            if (request.DryRun)
                continue;

            if (request.ArchiveEnabled)
            {
                try
                {
                    await MemoryRetentionArchive.ArchivePayloadAsync(
                        request.ArchivePath,
                        request.NowUtc,
                        kind: "branches",
                        branchId,
                        request.BranchExpiresBeforeUtc,
                        sourceBackend: "sqlite",
                        payloadJson,
                        ct);
                    result.ArchivedBranches++;
                }
                catch (Exception ex)
                {
                    if (result.Errors.Count < 16)
                        result.Errors.Add($"Failed to archive branch '{branchId}': {ex.Message}");
                    continue;
                }
            }

            pendingDeletes.Add(branchId);
        }

        if (pendingDeletes.Count > 0)
            result.DeletedBranches += await DeleteBranchesByIdAsync(conn, pendingDeletes, ct);

        return remaining;
    }

    private static async ValueTask<int> DeleteSessionsByIdAsync(
        SqliteConnection conn,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        return await DeleteByIdInBatchesAsync(conn, ids, ct, static (cmd, id) =>
        {
            cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", id);
        });
    }

    private static async ValueTask<int> DeleteBranchesByIdAsync(
        SqliteConnection conn,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        return await DeleteByIdInBatchesAsync(conn, ids, ct, static (cmd, id) =>
        {
            cmd.CommandText = "DELETE FROM branches WHERE branch_id = $id;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", id);
        });
    }

    private static async ValueTask<int> DeleteByIdInBatchesAsync(
        SqliteConnection conn,
        IReadOnlyList<string> ids,
        CancellationToken ct,
        Action<SqliteCommand, string> configureCommand)
    {
        if (ids.Count == 0)
            return 0;

        var deleted = 0;
        const int batchSize = 100;

        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var count = Math.Min(batchSize, ids.Count - i);

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var j = 0; j < count; j++)
            {
                configureCommand(cmd, ids[i + j]);
                deleted += await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        return deleted;
    }

    public void Dispose()
    {
        // No pooled resources at the moment; connections are per-call.
    }

    // ── ISessionAdminStore ────────────────────────────────────────────────

    public async ValueTask<PagedSessionList> ListSessionsAsync(
        int page, int pageSize, SessionListQuery query, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        // Build a filtered count + paged query
        var where = new System.Text.StringBuilder("WHERE 1=1");
        if (!string.IsNullOrEmpty(query.ChannelId))
            where.Append(" AND json_extract(json,'$.channelId') = $channelId");
        if (!string.IsNullOrEmpty(query.SenderId))
            where.Append(" AND json_extract(json,'$.senderId') = $senderId");
        if (query.FromUtc is not null)
            where.Append(" AND json_extract(json,'$.lastActiveAt') >= $fromUtc");
        if (query.ToUtc is not null)
            where.Append(" AND json_extract(json,'$.lastActiveAt') <= $toUtc");
        if (query.State is not null)
            where.Append(" AND (json_extract(json,'$.state') = $stateInt OR json_extract(json,'$.state') = $stateText)");
        if (!string.IsNullOrEmpty(query.Search))
            where.Append(" AND (id LIKE $search OR json_extract(json,'$.channelId') LIKE $search OR json_extract(json,'$.senderId') LIKE $search)");

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM sessions {where}";
        if (!string.IsNullOrEmpty(query.ChannelId)) countCmd.Parameters.AddWithValue("$channelId", query.ChannelId);
        if (!string.IsNullOrEmpty(query.SenderId)) countCmd.Parameters.AddWithValue("$senderId", query.SenderId);
        if (query.FromUtc is not null) countCmd.Parameters.AddWithValue("$fromUtc", query.FromUtc.Value.ToString("O"));
        if (query.ToUtc is not null) countCmd.Parameters.AddWithValue("$toUtc", query.ToUtc.Value.ToString("O"));
        if (query.State is not null)
        {
            countCmd.Parameters.AddWithValue("$stateInt", (int)query.State.Value);
            countCmd.Parameters.AddWithValue("$stateText", query.State.Value.ToString());
        }
        if (!string.IsNullOrEmpty(query.Search)) countCmd.Parameters.AddWithValue("$search", $"%{query.Search}%");

        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        var skip = (page - 1) * pageSize;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT json FROM sessions {where}
            ORDER BY json_extract(json,'$.lastActiveAt') DESC, id ASC
            LIMIT $limit OFFSET $offset;
            """;
        if (!string.IsNullOrEmpty(query.ChannelId)) cmd.Parameters.AddWithValue("$channelId", query.ChannelId);
        if (!string.IsNullOrEmpty(query.SenderId)) cmd.Parameters.AddWithValue("$senderId", query.SenderId);
        if (query.FromUtc is not null) cmd.Parameters.AddWithValue("$fromUtc", query.FromUtc.Value.ToString("O"));
        if (query.ToUtc is not null) cmd.Parameters.AddWithValue("$toUtc", query.ToUtc.Value.ToString("O"));
        if (query.State is not null)
        {
            cmd.Parameters.AddWithValue("$stateInt", (int)query.State.Value);
            cmd.Parameters.AddWithValue("$stateText", query.State.Value.ToString());
        }
        if (!string.IsNullOrEmpty(query.Search)) cmd.Parameters.AddWithValue("$search", $"%{query.Search}%");
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", skip);

        var items = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session = JsonSerializer.Deserialize(reader.GetString(0), CoreJsonContext.Default.Session);
            if (session is null) continue;
            items.Add(new SessionSummary
            {
                Id = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                CreatedAt = session.CreatedAt,
                LastActiveAt = session.LastActiveAt,
                State = session.State,
                HistoryTurns = session.History.Count,
                TotalInputTokens = session.TotalInputTokens,
                TotalOutputTokens = session.TotalOutputTokens,
                IsActive = false
            });
        }

        return new PagedSessionList
        {
            Page = page,
            PageSize = pageSize,
            HasMore = total > skip + pageSize,
            Items = items
        };
    }
}

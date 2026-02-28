using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Memory;

public sealed class SqliteMemoryStore : IMemoryStore, IMemoryNoteSearch, IDisposable
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

    public void Dispose()
    {
        // No pooled resources at the moment; connections are per-call.
    }
}


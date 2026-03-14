using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Memory;

/// <summary>
/// File-based implementation of <see cref="IMemoryStore"/>.
/// Sessions and notes are stored as JSON files with URL-safe base64 encoded filenames
/// to prevent path traversal attacks. Includes in-memory LRU cache for sessions.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore, IMemoryNoteSearch, IMemoryRetentionStore, ISessionAdminStore
{
    private readonly string _basePath;
    private readonly string _sessionsPath;
    private readonly string _notesPath;
    private readonly string _branchesPath;
    private readonly IMemoryCache _sessionCache;

    public FileMemoryStore(string basePath, int maxCachedSessions = 100)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        
        _sessionsPath = Path.Combine(_basePath, "sessions");
        _notesPath = Path.Combine(_basePath, "notes");
        _branchesPath = Path.Combine(_basePath, "branches");

        Directory.CreateDirectory(_sessionsPath);
        Directory.CreateDirectory(_notesPath);
        Directory.CreateDirectory(_branchesPath);

        _sessionCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Max(1, maxCachedSessions)
        });
    }

    public async ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        // Check cache first
        if (_sessionCache.TryGetValue(sessionId, out Session? cached))
            return cached;

        var encodedId = EncodeKey(sessionId);
        var filePath = Path.Combine(_sessionsPath, $"{encodedId}.json");

        // Legacy migration: check for unencoded filename
        var legacyPath = Path.Combine(_sessionsPath, $"{sessionId}.json");
        if (!File.Exists(filePath) && File.Exists(legacyPath))
        {
            try
            {
                await using var legacyStream = new FileStream(legacyPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                var session = await JsonSerializer.DeserializeAsync(legacyStream, CoreJsonContext.Default.Session, ct);
                if (session is not null)
                {
                    // Migrate to encoded filename
                    await SaveSessionAsync(session, ct);
                    File.Delete(legacyPath);
                    return session;
                }
            }
            catch
            {
                // Fall through to normal path
            }
        }

        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var stream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            var loaded = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.Session, ct);
            
            if (loaded is not null)
                await AddToCacheAsync(sessionId, loaded);
            
            return loaded;
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

        var encodedId = EncodeKey(session.Id);
        var filePath = Path.Combine(_sessionsPath, $"{encodedId}.json");
        var tempPath = $"{filePath}.tmp";

        try
        {
            // Write to temp file first (atomic write pattern)
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            }))
            {
                await JsonSerializer.SerializeAsync(stream, session, CoreJsonContext.Default.Session, ct);
                await stream.FlushAsync(ct);
            }

            // Atomic rename
            File.Move(tempPath, filePath, overwrite: true);

            // Update cache
            await AddToCacheAsync(session.Id, session);
        }
        catch
        {
            // Clean up temp file on failure
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    public async ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var encodedKey = EncodeKey(key);
        var filePath = Path.Combine(_notesPath, $"{encodedKey}.md");

        if (!File.Exists(filePath))
            return null;

        try
        {
            return await File.ReadAllTextAsync(filePath, ct);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Note key cannot be empty", nameof(key));

        var encodedKey = EncodeKey(key);
        var filePath = Path.Combine(_notesPath, $"{encodedKey}.md");
        var tempPath = $"{filePath}.tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    public ValueTask DeleteNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return ValueTask.CompletedTask;

        var encodedKey = EncodeKey(key);
        var filePath = Path.Combine(_notesPath, $"{encodedKey}.md");

        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Ignore deletion errors
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
    {
        var results = new List<string>();

        try
        {
            var files = Directory.EnumerateFiles(_notesPath, "*.md");
            foreach (var file in files)
            {
                var encodedKey = Path.GetFileNameWithoutExtension(file);
                var key = DecodeKey(encodedKey);
                
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    results.Add(key);
            }
        }
        catch
        {
            // Return empty list on error
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(results);
    }

    public async ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        limit = Math.Clamp(limit, 1, 50);
        prefix ??= "";

        var hits = new List<MemoryNoteHit>(capacity: Math.Min(limit, 16));
        try
        {
            foreach (var file in Directory.EnumerateFiles(_notesPath, "*.md"))
            {
                ct.ThrowIfCancellationRequested();

                var encodedKey = Path.GetFileNameWithoutExtension(file);
                var key = DecodeKey(encodedKey);

                if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, ct);
                }
                catch
                {
                    continue;
                }

                if (content.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var updatedAt = File.GetLastWriteTimeUtc(file);

                hits.Add(new MemoryNoteHit
                {
                    Key = key,
                    Content = content,
                    UpdatedAt = new DateTimeOffset(updatedAt, TimeSpan.Zero),
                    Score = 1.0f
                });

                if (hits.Count >= limit)
                    break;
            }
        }
        catch
        {
            return [];
        }

        return hits;
    }

    public async ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
    {
        if (branch is null)
            throw new ArgumentNullException(nameof(branch));

        var encodedId = EncodeKey(branch.BranchId);
        var filePath = Path.Combine(_branchesPath, $"{encodedId}.json");
        var tempPath = $"{filePath}.tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            }))
            {
                await JsonSerializer.SerializeAsync(stream, branch, CoreJsonContext.Default.SessionBranch, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    public async ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branchId))
            return null;

        var encodedId = EncodeKey(branchId);
        var filePath = Path.Combine(_branchesPath, $"{encodedId}.json");

        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var stream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.SessionBranch, ct);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
    {
        var results = new List<SessionBranch>();

        try
        {
            var files = Directory.EnumerateFiles(_branchesPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var branch = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionBranch);

                    if (branch is not null && branch.SessionId == sessionId)
                        results.Add(branch);
                }
                catch
                {
                    // Skip invalid files
                }
            }
        }
        catch
        {
            // Return empty list on error
        }

        return results;
    }

    public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branchId))
            return ValueTask.CompletedTask;

        var encodedId = EncodeKey(branchId);
        var filePath = Path.Combine(_branchesPath, $"{encodedId}.json");

        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Ignore deletion errors
        }

        return ValueTask.CompletedTask;
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
        remaining = await SweepSessionFilesAsync(request, protectedSessionIds, result, remaining, ct);
        if (remaining > 0)
            remaining = await SweepBranchFilesAsync(request, result, remaining, ct);

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

    public ValueTask<RetentionStoreStats> GetRetentionStatsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RetentionStoreStats
        {
            Backend = "file",
            PersistedSessions = CountJsonFilesSafe(_sessionsPath),
            PersistedBranches = CountJsonFilesSafe(_branchesPath)
        });
    }

    private async ValueTask<int> SweepSessionFilesAsync(
        RetentionSweepRequest request,
        IReadOnlySet<string> protectedSessionIds,
        RetentionSweepResult result,
        int remaining,
        CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_sessionsPath, "*.json");
        }
        catch (Exception ex)
        {
            if (result.Errors.Count < 16)
                result.Errors.Add($"Failed to enumerate session files: {ex.Message}");
            return remaining;
        }

        foreach (var file in files)
        {
            if (remaining <= 0)
            {
                result.MaxItemsLimitReached = true;
                break;
            }

            ct.ThrowIfCancellationRequested();

            string payloadJson;
            try
            {
                payloadJson = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                result.SkippedCorruptSessionItems++;
                continue;
            }

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

            if (protectedSessionIds.Contains(session.Id))
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
                        session.Id,
                        request.SessionExpiresBeforeUtc,
                        sourceBackend: "file",
                        payloadJson,
                        ct);
                    result.ArchivedSessions++;
                }
                catch (Exception ex)
                {
                    if (result.Errors.Count < 16)
                        result.Errors.Add($"Failed to archive session '{session.Id}': {ex.Message}");
                    continue;
                }
            }

            try
            {
                File.Delete(file);
                _sessionCache.Remove(session.Id);
                result.DeletedSessions++;
            }
            catch (Exception ex)
            {
                if (result.Errors.Count < 16)
                    result.Errors.Add($"Failed to delete session '{session.Id}': {ex.Message}");
            }
        }

        return remaining;
    }

    private async ValueTask<int> SweepBranchFilesAsync(
        RetentionSweepRequest request,
        RetentionSweepResult result,
        int remaining,
        CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_branchesPath, "*.json");
        }
        catch (Exception ex)
        {
            if (result.Errors.Count < 16)
                result.Errors.Add($"Failed to enumerate branch files: {ex.Message}");
            return remaining;
        }

        foreach (var file in files)
        {
            if (remaining <= 0)
            {
                result.MaxItemsLimitReached = true;
                break;
            }

            ct.ThrowIfCancellationRequested();

            string payloadJson;
            try
            {
                payloadJson = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                result.SkippedCorruptBranchItems++;
                continue;
            }

            SessionBranch? branch;
            try
            {
                branch = JsonSerializer.Deserialize(payloadJson, CoreJsonContext.Default.SessionBranch);
            }
            catch
            {
                branch = null;
            }

            if (branch is null)
            {
                result.SkippedCorruptBranchItems++;
                continue;
            }

            if (branch.CreatedAt >= request.BranchExpiresBeforeUtc)
                continue;

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
                        branch.BranchId,
                        request.BranchExpiresBeforeUtc,
                        sourceBackend: "file",
                        payloadJson,
                        ct);
                    result.ArchivedBranches++;
                }
                catch (Exception ex)
                {
                    if (result.Errors.Count < 16)
                        result.Errors.Add($"Failed to archive branch '{branch.BranchId}': {ex.Message}");
                    continue;
                }
            }

            try
            {
                File.Delete(file);
                result.DeletedBranches++;
            }
            catch (Exception ex)
            {
                if (result.Errors.Count < 16)
                    result.Errors.Add($"Failed to delete branch '{branch.BranchId}': {ex.Message}");
            }
        }

        return remaining;
    }

    private static long CountJsonFilesSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*.json").LongCount();
        }
        catch
        {
            return 0;
        }
    }

    private ValueTask AddToCacheAsync(string sessionId, Session session)
    {
        _sessionCache.Set(sessionId, session, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromHours(2)
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Encodes a key to a URL-safe base64 string to prevent path traversal.
    /// Uses SHA256 hash for keys longer than 200 characters to avoid filesystem limits.
    /// </summary>
    private static string EncodeKey(string key)
    {
        // For very long keys, use hash to avoid filesystem path limits
        if (key.Length > 200)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToBase64String(hash)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        var bytes = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Decodes a URL-safe base64 string back to the original key.
    /// </summary>
    private static string DecodeKey(string encoded)
    {
        var base64 = encoded
            .Replace('-', '+')
            .Replace('_', '/');

        // Add padding
        var padding = (4 - (base64.Length % 4)) % 4;
        base64 += new string('=', padding);

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // If decode fails, return the encoded string (shouldn't happen in normal operation)
            return encoded;
        }
    }

    // ── ISessionAdminStore ────────────────────────────────────────────────

    public async ValueTask<PagedSessionList> ListSessionsAsync(
        int page, int pageSize, SessionListQuery query, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_sessionsPath, "*.json"); }
        catch { files = []; }

        var summaries = new List<SessionSummary>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var session = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);
                if (session is null) continue;

                if (!string.IsNullOrEmpty(query.ChannelId) &&
                    !string.Equals(session.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(query.SenderId) &&
                    !string.Equals(session.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (query.FromUtc is { } fromUtc && session.LastActiveAt < fromUtc)
                    continue;

                if (query.ToUtc is { } toUtc && session.LastActiveAt > toUtc)
                    continue;

                if (query.State is { } state && session.State != state)
                    continue;

                if (!string.IsNullOrEmpty(query.Search))
                {
                    var s = query.Search;
                    if (!session.Id.Contains(s, StringComparison.OrdinalIgnoreCase) &&
                        !session.ChannelId.Contains(s, StringComparison.OrdinalIgnoreCase) &&
                        !session.SenderId.Contains(s, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                summaries.Add(new SessionSummary
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
            catch { /* skip corrupt files */ }
        }

        summaries.Sort((a, b) => b.LastActiveAt.CompareTo(a.LastActiveAt));

        var skip = (page - 1) * pageSize;
        var items = summaries.Skip(skip).Take(pageSize).ToList();
        return new PagedSessionList
        {
            Page = page,
            PageSize = pageSize,
            HasMore = summaries.Count > skip + pageSize,
            Items = items
        };
    }
}

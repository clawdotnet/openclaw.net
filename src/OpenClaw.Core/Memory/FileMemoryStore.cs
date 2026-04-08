using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Core.Memory;

public sealed class MemoryStoreCorruptionException : IOException
{
    public MemoryStoreCorruptionException(string message, string sessionId, string filePath, Exception innerException)
        : base(message, innerException)
    {
        SessionId = sessionId;
        FilePath = filePath;
    }

    public string SessionId { get; }
    public string FilePath { get; }
}

/// <summary>
/// File-based implementation of <see cref="IMemoryStore"/>.
/// Sessions and notes are stored as JSON files with URL-safe base64 encoded filenames
/// to prevent path traversal attacks. Includes in-memory LRU cache for sessions.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore, IMemoryNoteSearch, IMemoryRetentionStore, ISessionAdminStore, ISessionSearchStore, IAsyncDisposable, IDisposable
{
    private const int SessionLoadStripeCount = 64;

    private readonly string _basePath;
    private readonly string _sessionsPath;
    private readonly string _notesPath;
    private readonly string _branchesPath;
    private readonly IMemoryCache _sessionCache;
    private readonly SemaphoreSlim[] _sessionLoadStripes;
    private readonly SemaphoreSlim _noteIndexGate = new(1, 1);
    private readonly ConcurrentDictionary<string, NoteIndexEntry> _noteIndex = new(StringComparer.Ordinal);
    private readonly ILogger<FileMemoryStore>? _logger;
    private readonly RuntimeMetrics? _metrics;
    private int _noteIndexInitialized;

    public FileMemoryStore(string basePath, int maxCachedSessions = 100, ILogger<FileMemoryStore>? logger = null, RuntimeMetrics? metrics = null)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _logger = logger;
        _metrics = metrics;
        
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
        _sessionLoadStripes = Enumerable.Range(0, SessionLoadStripeCount)
            .Select(static _ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public async ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        // Check cache first
        if (_sessionCache.TryGetValue(sessionId, out Session? cached))
        {
            _metrics?.IncrementSessionCacheHits();
            return cached;
        }
        _metrics?.IncrementSessionCacheMisses();

        var loadGate = ResolveSessionLoadStripe(sessionId);
        await loadGate.WaitAsync(ct);
        try
        {
            if (_sessionCache.TryGetValue(sessionId, out cached))
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw QuarantineCorruptSessionFile(legacyPath, sessionId, ex);
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
                {
                    if (_sessionCache.TryGetValue(sessionId, out Session? canonical))
                        return canonical;

                    await AddToCacheAsync(sessionId, loaded);
                }

                return loaded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw QuarantineCorruptSessionFile(filePath, sessionId, ex);
            }
        }
        finally
        {
            loadGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var stripe in _sessionLoadStripes)
            stripe.Dispose();

        _noteIndexGate.Dispose();
        _sessionCache.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private SemaphoreSlim ResolveSessionLoadStripe(string sessionId)
    {
        var index = (sessionId.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % _sessionLoadStripes.Length;
        return _sessionLoadStripes[index];
    }

    private MemoryStoreCorruptionException QuarantineCorruptSessionFile(string filePath, string sessionId, Exception ex)
    {
        string? quarantinePath = null;

        try
        {
            if (File.Exists(filePath))
            {
                quarantinePath = filePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                File.Move(filePath, quarantinePath, overwrite: true);
            }
        }
        catch (Exception quarantineEx)
        {
            _logger?.LogWarning(
                quarantineEx,
                "Failed to quarantine corrupt session file {FilePath} for session {SessionId}",
                filePath,
                sessionId);
        }

        var effectivePath = quarantinePath ?? filePath;
        _logger?.LogError(
            ex,
            "Session file for {SessionId} is corrupt or unreadable and was quarantined to {FilePath}",
            sessionId,
            effectivePath);
        return new MemoryStoreCorruptionException(
            $"Session '{sessionId}' could not be loaded because its persisted state is corrupt.",
            sessionId,
            effectivePath,
            ex);
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
        var keyPath = Path.Combine(_notesPath, $"{encodedKey}.key");
        var keyTempPath = $"{keyPath}.tmp";
        var nowUtc = DateTimeOffset.UtcNow;

        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct);
            File.Move(tempPath, filePath, overwrite: true);
            await PersistOriginalNoteKeyAsync(key, keyPath, keyTempPath, ct);
            UpsertNoteIndexEntry(key, content, nowUtc);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            try { File.Delete(keyTempPath); } catch { /* ignore */ }
            throw;
        }
    }

    public ValueTask DeleteNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return ValueTask.CompletedTask;

        var encodedKey = EncodeKey(key);
        var filePath = Path.Combine(_notesPath, $"{encodedKey}.md");
        var keyPath = Path.Combine(_notesPath, $"{encodedKey}.key");

        try
        {
            File.Delete(filePath);
            File.Delete(keyPath);
            _noteIndex.TryRemove(key, out _);
        }
        catch
        {
            // Ignore deletion errors
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
    {
        return ListNotesWithPrefixCoreAsync(prefix ?? "", ct);
    }

    public async ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        limit = Math.Clamp(limit, 1, 50);
        prefix ??= "";
        try
        {
            await EnsureNoteIndexLoadedAsync(ct);
            var normalizedQuery = NormalizeSearchText(query);
            if (normalizedQuery.Length == 0)
                return [];

            var terms = BuildQueryTerms(normalizedQuery);
            var candidates = _noteIndex.Values
                .Where(entry => string.IsNullOrEmpty(prefix) || entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => new { Entry = entry, Score = ScoreNoteEntry(entry, normalizedQuery, terms) })
                .Where(static item => item.Score > 0)
                .OrderByDescending(static item => item.Score)
                .ThenByDescending(static item => item.Entry.UpdatedAt)
                .ThenBy(static item => item.Entry.Key, StringComparer.Ordinal)
                .Take(Math.Min(limit * 4, 64))
                .ToArray();

            var hits = new List<MemoryNoteHit>(capacity: Math.Min(limit, candidates.Length));
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var content = await LoadNoteAsync(candidate.Entry.Key, ct) ?? candidate.Entry.PreviewContent;
                hits.Add(new MemoryNoteHit
                {
                    Key = candidate.Entry.Key,
                    Content = content,
                    UpdatedAt = candidate.Entry.UpdatedAt,
                    Score = candidate.Score
                });

                if (hits.Count >= limit)
                    break;
            }

            return hits;
        }
        catch
        {
            return [];
        }
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
            catch (Exception ex)
            {
                result.SkippedCorruptSessionItems++;
                _logger?.LogWarning(ex, "Skipping unreadable session file during retention sweep: {Path}", file);
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
                _logger?.LogWarning("Skipping corrupt session file during retention sweep: {Path}", file);
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

    private async ValueTask<IReadOnlyList<string>> ListNotesWithPrefixCoreAsync(string prefix, CancellationToken ct)
    {
        try
        {
            await EnsureNoteIndexLoadedAsync(ct);
            return _noteIndex.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async ValueTask EnsureNoteIndexLoadedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _noteIndexInitialized) != 0)
            return;

        await _noteIndexGate.WaitAsync(ct);
        try
        {
            if (_noteIndexInitialized != 0)
                return;

            _noteIndex.Clear();
            foreach (var file in Directory.EnumerateFiles(_notesPath, "*.md"))
            {
                ct.ThrowIfCancellationRequested();

                var encodedKey = Path.GetFileNameWithoutExtension(file);
                var key = ResolveNoteKey(encodedKey);

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, ct);
                }
                catch
                {
                    continue;
                }

                var updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                _noteIndex[key] = CreateNoteIndexEntry(key, content, updatedAt);
            }

            Volatile.Write(ref _noteIndexInitialized, 1);
        }
        finally
        {
            _noteIndexGate.Release();
        }
    }

    private void UpsertNoteIndexEntry(string key, string content, DateTimeOffset updatedAt)
    {
        if (Volatile.Read(ref _noteIndexInitialized) == 0)
            return;

        _noteIndex[key] = CreateNoteIndexEntry(key, content, updatedAt);
    }

    private static NoteIndexEntry CreateNoteIndexEntry(string key, string content, DateTimeOffset updatedAt)
    {
        content ??= "";
        return new NoteIndexEntry
        {
            Key = key,
            PreviewContent = content.Length <= 4_096 ? content : content[..4_096] + "…",
            SearchText = NormalizeSearchText($"{key}\n{content}"),
            UpdatedAt = updatedAt
        };
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .ToLowerInvariant();

        return normalized.Length <= 16_384 ? normalized : normalized[..16_384];
    }

    private static string[] BuildQueryTerms(string normalizedQuery)
    {
        return normalizedQuery
            .Split([' ', '\n', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static float ScoreNoteEntry(NoteIndexEntry entry, string normalizedQuery, IReadOnlyList<string> terms)
    {
        var score = 0f;
        if (entry.SearchText.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 6f;
        if (entry.Key.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            score += 4f;

        foreach (var term in terms)
        {
            if (entry.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2f;
            if (entry.SearchText.Contains(term, StringComparison.Ordinal))
                score += 1f;
        }

        if (score <= 0f)
            return 0f;

        var ageDays = Math.Max(0d, (DateTimeOffset.UtcNow - entry.UpdatedAt).TotalDays);
        var recencyBoost = (float)Math.Max(0.1d, 1.5d - Math.Min(1.4d, ageDays / 14d));
        return score + recencyBoost;
    }

    private async ValueTask PersistOriginalNoteKeyAsync(string key, string keyPath, string keyTempPath, CancellationToken ct)
    {
        if (!RequiresKeySidecar(key))
        {
            try { File.Delete(keyPath); } catch { /* ignore */ }
            return;
        }

        await File.WriteAllTextAsync(keyTempPath, key, ct);
        File.Move(keyTempPath, keyPath, overwrite: true);
    }

    private string ResolveNoteKey(string encodedKey)
    {
        var keyPath = Path.Combine(_notesPath, $"{encodedKey}.key");
        try
        {
            if (File.Exists(keyPath))
            {
                var originalKey = File.ReadAllText(keyPath);
                if (!string.IsNullOrWhiteSpace(originalKey))
                    return originalKey;
            }
        }
        catch
        {
            // Fall back to the decodable key below.
        }

        return DecodeKey(encodedKey);
    }

    private static bool RequiresKeySidecar(string key) => key.Length > 200;

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

    private sealed class NoteIndexEntry
    {
        public required string Key { get; init; }
        public required string PreviewContent { get; init; }
        public required string SearchText { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
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

    public async ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct)
    {
        var results = new List<SessionSearchHit>();
        var searchText = (query.Text ?? "").Trim();
        if (searchText.Length == 0)
            return new SessionSearchResult { Query = query, Items = [] };

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_sessionsPath, "*.json"); }
        catch { files = []; }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            Session? session;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                session = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);
            }
            catch
            {
                continue;
            }

            if (session is null)
                continue;

            if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
                !string.Equals(session.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(query.SenderId) &&
                !string.Equals(session.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var turn in session.History)
            {
                if (query.FromUtc is { } fromUtc && turn.Timestamp < fromUtc)
                    continue;

                if (query.ToUtc is { } toUtc && turn.Timestamp > toUtc)
                    continue;

                AddHitIfMatch(results, session, turn.Role, turn.Timestamp, turn.Content, searchText, query.SnippetLength);
                if (turn.ToolCalls is null)
                    continue;

                foreach (var toolCall in turn.ToolCalls)
                    AddHitIfMatch(results, session, "tool", turn.Timestamp, toolCall.Result ?? toolCall.Arguments, searchText, query.SnippetLength);
            }
        }

        return new SessionSearchResult
        {
            Query = query,
            Items = results
                .OrderByDescending(static item => item.Score)
                .ThenByDescending(static item => item.Timestamp)
                .Take(Math.Clamp(query.Limit, 1, 200))
                .ToArray()
        };
    }

    private static void AddHitIfMatch(
        ICollection<SessionSearchHit> results,
        Session session,
        string role,
        DateTimeOffset timestamp,
        string? content,
        string searchText,
        int snippetLength)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        var index = content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return;

        results.Add(new SessionSearchHit
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Role = role,
            Timestamp = timestamp,
            Snippet = BuildSnippet(content, index, snippetLength),
            Score = 1f + Math.Max(0, 100 - index) / 100f
        });
    }

    private static string BuildSnippet(string content, int index, int snippetLength)
    {
        snippetLength = Math.Clamp(snippetLength, 40, 400);
        var start = Math.Max(0, index - (snippetLength / 3));
        var length = Math.Min(snippetLength, content.Length - start);
        var snippet = content.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (start > 0)
            snippet = $"...{snippet}";
        if (start + length < content.Length)
            snippet = $"{snippet}...";
        return snippet;
    }
}

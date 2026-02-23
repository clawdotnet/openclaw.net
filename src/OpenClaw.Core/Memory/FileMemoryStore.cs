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
public sealed class FileMemoryStore : IMemoryStore
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
                await using var legacyStream = File.OpenRead(legacyPath);
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
            await using var stream = File.OpenRead(filePath);
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
            await using (var stream = File.Create(tempPath))
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

    public async ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
    {
        if (branch is null)
            throw new ArgumentNullException(nameof(branch));

        var encodedId = EncodeKey(branch.BranchId);
        var filePath = Path.Combine(_branchesPath, $"{encodedId}.json");
        var tempPath = $"{filePath}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
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
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.SessionBranch, ct);
        }
        catch
        {
            return null;
        }
    }

    public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
    {
        var results = new List<SessionBranch>();

        try
        {
            var files = Directory.EnumerateFiles(_branchesPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
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

        return ValueTask.FromResult<IReadOnlyList<SessionBranch>>(results);
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
}

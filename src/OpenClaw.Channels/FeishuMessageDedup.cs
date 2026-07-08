using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Channels;

/// <summary>
/// Two-layer deduplication for Feishu <c>message_id</c>.
/// <list type="bullet">
///   <item>Layer 1: In-memory TTL cache (5 min, 2 000 entries) — fast in-flight protection
///         against Feishu's event retry on unacknowledged frames.</item>
///   <item>Layer 2: Persistent disk storage (24 h TTL, 10 000 entries/namespace) — survives
///         process restarts and WebSocket reconnects where Feishu replays the last event.</item>
/// </list>
/// Namespace should be set to the Feishu <c>app_id</c> so that multiple configured accounts
/// each maintain independent dedup stores.
/// </summary>
internal sealed class FeishuMessageDedup : IDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>In-memory entry TTL: 5 minutes.</summary>
    private const long MemoryTtlMs = 5L * 60 * 1_000;

    /// <summary>Disk entry TTL: 24 hours.</summary>
    private const long DiskTtlMs = 24L * 60 * 60 * 1_000;

    /// <summary>Maximum in-memory entries before lazy eviction runs.</summary>
    private const int MemoryMaxSize = 2_000;

    /// <summary>Maximum entries per namespace JSON file (oldest evicted when exceeded).</summary>
    private const int DiskMaxEntries = 10_000;

    // ── State ────────────────────────────────────────────────────────────────

    // Key: "{namespace}:{messageId}", Value: expiry Unix-ms
    private readonly ConcurrentDictionary<string, long> _memory = new(StringComparer.Ordinal);

    // One mutex for all disk I/O — writes are infrequent, so a single semaphore is fine.
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    private readonly string _stateDir;

    // ── Constructor ──────────────────────────────────────────────────────────

    public FeishuMessageDedup(string? stateDir = null)
    {
        _stateDir = !string.IsNullOrWhiteSpace(stateDir)
            ? stateDir
            : ResolveDefaultStateDir();
    }

    private static string ResolveDefaultStateDir()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR")?.Trim();
        if (!string.IsNullOrEmpty(env))
            return env;

        // ~/.openclaw (cross-platform)
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw");
    }

    private string ResolveDiskPath(string ns)
    {
        // Sanitize namespace to safe filename characters
        var safe = string.Concat(ns.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        return Path.Combine(_stateDir, "feishu", "dedup", $"{safe}.json");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-loads non-expired disk entries into the in-memory cache.
    /// Call once at channel startup so that replays of already-processed messages
    /// are caught immediately without a disk round-trip.
    /// </summary>
    public void Warmup(string ns, ILogger? logger = null)
    {
        try
        {
            var entries = ReadDiskEntries(ResolveDiskPath(ns));
            var now = NowMs();
            var loaded = 0;
            foreach (var (id, expMs) in entries)
            {
                if (expMs > now)
                {
                    _memory.TryAdd(MemKey(ns, id), expMs);
                    loaded++;
                }
            }
            logger?.LogDebug(
                "Feishu dedup warmup: loaded {Count}/{Total} valid entries from disk (ns={Ns}).",
                loaded, entries.Count, ns);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Feishu dedup warmup failed for ns={Ns}; continuing without pre-load.", ns);
        }
    }

    /// <summary>
    /// Attempts to claim <paramref name="messageId"/> for processing.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the message is new and should be processed;
    /// <c>false</c> if it is a duplicate (in-flight or already processed).
    /// </returns>
    /// <remarks>Thread-safe. On disk I/O errors the message is allowed through
    /// (fail-open) to avoid silently dropping legitimate messages.</remarks>
    public async ValueTask<bool> TryClaimAsync(
        string messageId,
        string ns,
        ILogger? logger,
        CancellationToken ct)
    {
        var key = MemKey(ns, messageId);
        var now = NowMs();

        // ── Layer 1: memory fast-path ──────────────────────────────────────
        if (_memory.TryGetValue(key, out var memExpMs) && memExpMs > now)
        {
            logger?.LogDebug(
                "Feishu dedup: in-flight duplicate {MessageId} (ns={Ns}) suppressed.", messageId, ns);
            return false;
        }

        // ── Layer 2: disk check ────────────────────────────────────────────
        try
        {
            if (await CheckDiskAsync(messageId, ns, ct))
            {
                // Promote to memory to avoid repeated disk reads for the same id.
                SetMemory(key, now + MemoryTtlMs);
                logger?.LogDebug(
                    "Feishu dedup: persisted duplicate {MessageId} (ns={Ns}) suppressed.", messageId, ns);
                return false;
            }
        }
        catch (Exception ex)
        {
            // Disk error — fail-open: prefer to process the message rather than silently drop it.
            logger?.LogWarning(ex,
                "Feishu dedup: disk check failed for {MessageId}; message will be processed.", messageId);
        }

        // ── Claim: mark in memory, then persist ────────────────────────────
        SetMemory(key, now + MemoryTtlMs);

        try
        {
            await WriteDiskAsync(messageId, ns, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Feishu dedup: disk write failed for {MessageId}; will process but may replay after restart.", messageId);
        }

        return true;
    }

    // ── Memory helpers ───────────────────────────────────────────────────────

    private void SetMemory(string key, long expMs)
    {
        _memory[key] = expMs;

        // Lazy eviction: run a sweep when the dictionary exceeds the size limit.
        if (_memory.Count > MemoryMaxSize)
            EvictExpiredMemory();
    }

    private void EvictExpiredMemory()
    {
        var now = NowMs();
        var toRemove = _memory
            .Where(kv => kv.Value <= now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _memory.TryRemove(key, out _);
    }

    // ── Disk helpers ─────────────────────────────────────────────────────────

    // Disk JSON format: { "messageId": expiryUnixMs, ... }

    private async ValueTask<bool> CheckDiskAsync(string messageId, string ns, CancellationToken ct)
    {
        await _diskLock.WaitAsync(ct);
        try
        {
            var entries = ReadDiskEntries(ResolveDiskPath(ns));
            return entries.TryGetValue(messageId, out var expMs) && expMs > NowMs();
        }
        finally
        {
            _diskLock.Release();
        }
    }

    private async ValueTask WriteDiskAsync(string messageId, string ns, CancellationToken ct)
    {
        await _diskLock.WaitAsync(ct);
        try
        {
            var path = ResolveDiskPath(ns);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Check disk space before write (10MB threshold)
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.AvailableFreeSpace < 10 * 1024 * 1024)
                throw new IOException($"Insufficient disk space for dedup store: {drive.AvailableFreeSpace / 1024 / 1024}MB available");

            var entries = ReadDiskEntries(path);
            var now = NowMs();

            // Remove expired entries to keep the file compact.
            foreach (var id in entries.Keys.Where(k => entries[k] <= now).ToList())
                entries.Remove(id);

            // Evict the entry with the smallest expiry when over the size limit.
            while (entries.Count >= DiskMaxEntries)
            {
                var oldest = entries.MinBy(kv => kv.Value).Key;
                entries.Remove(oldest);
            }

            entries[messageId] = now + DiskTtlMs;

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(entries, DedupSerializerContext.Default.DictionaryStringInt64),
                ct);
        }
        finally
        {
            _diskLock.Release();
        }
    }

    private static Dictionary<string, long> ReadDiskEntries(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, DedupSerializerContext.Default.DictionaryStringInt64)
                   ?? [];
        }
        catch
        {
            // Corrupted or unreadable file — treat as empty.
            return [];
        }
    }

    // ── Utilities ────────────────────────────────────────────────────────────

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string MemKey(string ns, string id) => string.Concat(ns, ":", id);

    public void Dispose() => _diskLock.Dispose();
}

[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class DedupSerializerContext : JsonSerializerContext { }

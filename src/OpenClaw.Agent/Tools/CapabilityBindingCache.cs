using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Session-scoped cache of resolved capability bindings (issue #232). Entries
/// are keyed by (session id, SHA-256 of the normalised intent); they expire
/// lazily after a configurable TTL (default 300s) and are cleared wholesale
/// when the workspace MCP config reloads. Only successful resolutions are
/// stored — failures are never cached, so the next execution retries.
/// </summary>
public sealed class CapabilityBindingCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);

    private sealed class CacheEntry(string server, string tool, DateTimeOffset storedAt)
    {
        public string Server { get; } = server;
        public string Tool { get; } = tool;
        public DateTimeOffset StoredAt { get; } = storedAt;
    }

    private readonly ConcurrentDictionary<(string SessionId, string IntentKey), CacheEntry> _entries = new();
    private readonly TimeSpan _ttl;

    public CapabilityBindingCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// SHA-256 hex of the normalised intent: task_description + keywords +
    /// selection policy. Same fields, same key; any field change, new key.
    /// </summary>
    public static string ComputeIntentKey(string taskDescription, string? keywords, string selectionPolicy)
    {
        var raw = string.Concat(taskDescription, "\n", keywords ?? string.Empty, "\n", selectionPolicy);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public bool TryGet(string sessionId, string intentKey, out string server, out string tool)
    {
        if (_entries.TryGetValue((sessionId, intentKey), out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.StoredAt <= _ttl)
            {
                server = entry.Server;
                tool = entry.Tool;
                return true;
            }
            _entries.TryRemove((sessionId, intentKey), out _);
        }

        server = string.Empty;
        tool = string.Empty;
        return false;
    }

    public void Set(string sessionId, string intentKey, string server, string tool)
        => _entries[(sessionId, intentKey)] = new CacheEntry(server, tool, DateTimeOffset.UtcNow);

    public void Clear() => _entries.Clear();
}

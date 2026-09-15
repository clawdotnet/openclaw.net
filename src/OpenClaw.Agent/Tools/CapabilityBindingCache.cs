using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Skills.Meta;
namespace OpenClaw.Agent.Tools;

public sealed class CapabilityBindingCache(TimeSpan? ttl = null, TimeProvider? clock = null, int capacity = 1024) : ICapabilityInvalidationSink
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    private sealed record Entry(object Value, DateTimeOffset At, bool Persistent);
    private readonly Dictionary<(string Scope, string Key), Entry> _entries = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private long _generation;
    public int Count { get { lock (_gate) return _entries.Count; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public static string ComputeIntentKey(string taskDescription, string? keywords, string selectionPolicy)
    {
        // Length-prefixing avoids delimiter collisions. Keep text unchanged; normalization must not change intent semantics.
        var words = (keywords ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var parts = new[] { taskDescription, string.Join(",", words), selectionPolicy.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(parts.Select(p => $"{p.Length}:{p}")))));
    }
    public bool TryGetValue<T>(string scope, string key, out T? value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((scope, key), out var entry) && (entry.Persistent || _clock.GetUtcNow() - entry.At < _ttl) && entry.Value is T match)
            { value = match; return true; }
            _entries.Remove((scope, key)); value = default; return false;
        }
    }
    public bool Store(string scope, string key, object value, long generation, bool persistent = false)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            foreach (var expired in _entries.Where(x => !x.Value.Persistent && _clock.GetUtcNow() - x.Value.At >= _ttl).Select(x => x.Key).ToArray()) _entries.Remove(expired);
            if (_entries.Count >= Math.Max(1, capacity)) _entries.Remove(_entries.MinBy(x => x.Value.At).Key);
            _entries[(scope, key)] = new(value, _clock.GetUtcNow(), persistent); return true;
        }
    }
    public bool TryGet(string sessionId, string key, out string server, out string tool)
    {
        if (TryGetValue<(string Server, string Tool)>(sessionId, key, out var entry)) { server = entry.Server; tool = entry.Tool; return true; }
        server = tool = ""; return false;
    }
    public void Set(string sessionId, string key, string server, string tool) => Store(sessionId, key, (server, tool), Generation);
    public void Clear() { lock (_gate) { _generation++; _entries.Clear(); } }
    public void Invalidate(CapabilityChange change) => Clear(); // Conservative full invalidation is intentional.
}

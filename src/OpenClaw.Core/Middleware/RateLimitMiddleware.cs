using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Middleware;

/// <summary>
/// Per-session rate limiting middleware. Tracks message timestamps per session
/// and rejects messages that exceed the configured rate.
/// </summary>
public sealed class RateLimitMiddleware : IMessageMiddleware
{
    private sealed class RateWindow
    {
        public Queue<DateTimeOffset> Entries { get; } = new();
        public DateTimeOffset LastSeenAt { get; set; }
    }

    private readonly int _maxMessagesPerMinute;
    private readonly ILogger? _logger;
    private readonly TimeSpan _idleTtl;
    private readonly int _cleanupEvery;
    private readonly ConcurrentDictionary<(string ChannelId, string SenderId), RateWindow> _windows = new();
    private long _requestCount;

    public string Name => "RateLimit";

    public RateLimitMiddleware(
        int maxMessagesPerMinute,
        ILogger? logger = null,
        TimeSpan? idleTtl = null,
        int cleanupEvery = 256)
    {
        _maxMessagesPerMinute = maxMessagesPerMinute > 0 ? maxMessagesPerMinute : int.MaxValue;
        _logger = logger;
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(10);
        _cleanupEvery = Math.Max(1, cleanupEvery);
    }

    public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
    {
        var key = (context.ChannelId, context.SenderId);
        var window = _windows.GetOrAdd(key, _ => new RateWindow());
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);

        lock (window)
        {
            // Evict old entries
            while (window.Entries.Count > 0 && window.Entries.Peek() < cutoff)
                window.Entries.Dequeue();

            if (window.Entries.Count >= _maxMessagesPerMinute)
            {
                _logger?.LogWarning("Rate limit exceeded for {Key} ({Count}/{Max} messages/min)",
                    $"{key.ChannelId}:{key.SenderId}", window.Entries.Count, _maxMessagesPerMinute);
                context.ShortCircuit("You're sending messages too quickly. Please wait a moment and try again.");
                return ValueTask.CompletedTask;
            }

            window.Entries.Enqueue(now);
            window.LastSeenAt = now;
        }

        if (Interlocked.Increment(ref _requestCount) % _cleanupEvery == 0)
            CleanupStaleWindows(now, cutoff);

        return next();
    }

    private void CleanupStaleWindows(DateTimeOffset now, DateTimeOffset rateWindowCutoff)
    {
        var idleCutoff = now - _idleTtl;

        foreach (var kvp in _windows)
        {
            var remove = false;
            var window = kvp.Value;

            lock (window)
            {
                while (window.Entries.Count > 0 && window.Entries.Peek() < rateWindowCutoff)
                    window.Entries.Dequeue();

                if (window.Entries.Count == 0 && window.LastSeenAt < idleCutoff)
                    remove = true;
            }

            if (remove)
                _windows.TryRemove(kvp.Key, out _);
        }
    }
}

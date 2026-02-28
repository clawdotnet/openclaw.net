using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Security;

namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Optional governance hook for SK-backed tools.
/// Enforces allow/deny patterns and per-sender per-tool rate limits.
/// </summary>
public sealed class SemanticKernelPolicyHook : IToolHookWithContext
{
    private readonly SemanticKernelPolicyOptions _options;

    // Key: sender|tool|minute
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private long _lastCleanupMinute;

    public SemanticKernelPolicyHook(IOptions<SemanticKernelPolicyOptions> options)
        => _options = options.Value;

    public string Name => "SemanticKernelPolicy";

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
        => new(true); // Prefer context-aware overload.

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        // Only govern SK-related tools.
        if (!context.ToolName.StartsWith("sk_", StringComparison.Ordinal) &&
            !string.Equals(context.ToolName, "semantic_kernel", StringComparison.Ordinal))
        {
            return new(true);
        }

        if (!GlobMatcher.IsAllowed(_options.AllowedTools, _options.DeniedTools, context.ToolName, StringComparison.Ordinal))
            return new(false);

        var limit = GetLimitForTool(context.ToolName);
        if (limit <= 0)
            return new(true);

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        CleanupIfNeeded(minute);

        var key = $"{context.SenderId}|{context.ToolName}|{minute}";
        var next = _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (next > limit)
            return new(false);

        return new(true);
    }

    public ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    private int GetLimitForTool(string toolName)
    {
        var best = 0;
        foreach (var kvp in _options.PerSenderPerToolPerMinute)
        {
            if (GlobMatcher.IsMatch(kvp.Key, toolName, StringComparison.Ordinal))
                best = Math.Max(best, kvp.Value);
        }
        return best;
    }

    private void CleanupIfNeeded(long currentMinute)
    {
        var last = Interlocked.Read(ref _lastCleanupMinute);
        if (last == currentMinute)
            return;

        if (Interlocked.CompareExchange(ref _lastCleanupMinute, currentMinute, last) != last)
            return;

        foreach (var key in _counts.Keys)
        {
            // key ends with |minute
            var idx = key.LastIndexOf('|');
            if (idx <= 0)
                continue;

            if (!long.TryParse(key[(idx + 1)..], out var minute))
                continue;

            // Keep current minute and previous minute to reduce churn.
            if (minute < currentMinute - 1)
                _counts.TryRemove(key, out _);
        }
    }
}

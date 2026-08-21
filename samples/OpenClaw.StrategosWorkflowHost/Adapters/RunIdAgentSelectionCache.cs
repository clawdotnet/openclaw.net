using System.Collections.Concurrent;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// In-memory sidecar-local record of which agent id the selector chose for each
/// (runId, stepName) pair. The cache is what lets <see cref="GatewayEventReceiver"/>
/// correlate a later outcome event back to the agent that produced it: the gateway
/// only knows runId and stepName, so the sidecar has to remember the agentId itself.
///
/// Capacity-driven FIFO eviction is intentional: the cache is bounded memory, and
/// when it overflows we drop the oldest selections rather than block new ones.
/// Thompson Sampling handles the "selection without recorded outcome" case
/// gracefully (the belief stays at its prior), so eviction is safe.
/// </summary>
public sealed class RunIdAgentSelectionCache
{
    private readonly ConcurrentDictionary<string, CachedSelection> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _evictionLock = new();
    private readonly int _capacity;

    public RunIdAgentSelectionCache(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Records <paramref name="agentId"/> as the picked agent for
    /// (<paramref name="runId"/>, <paramref name="stepName"/>) and returns the
    /// stored value. Re-setting the same key overwrites the prior entry; FIFO
    /// eviction uses the *most recent* insertion time, so overwrites do not
    /// reset eviction order until a different key fills the gap.
    /// </summary>
    public CachedSelection Set(string runId, string stepName, string agentId, string taskCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(taskCategory);

        var key = GetKey(runId, stepName);
        var selection = new CachedSelection(agentId, taskCategory, DateTimeOffset.UtcNow);

        if (_entries.TryAdd(key, selection))
        {
            lock (_evictionLock)
            {
                _insertionOrder.Enqueue(key);
                EvictIfOverCapacity();
            }
        }
        else
        {
            _entries[key] = selection;
        }

        return selection;
    }

    /// <summary>
    /// Returns the cached selection for (<paramref name="runId"/>,
    /// <paramref name="stepName"/>), or null if no selection was recorded (cache
    /// miss or already evicted).
    /// </summary>
    public CachedSelection? TryGet(string runId, string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

        return _entries.TryGetValue(GetKey(runId, stepName), out var selection)
            ? selection
            : null;
    }

    private void EvictIfOverCapacity()
    {
        while (_insertionOrder.Count > _capacity)
        {
            var oldest = _insertionOrder.Dequeue();
            _entries.TryRemove(oldest, out _);
        }
    }

    private static string GetKey(string runId, string stepName)
        => $"{runId}{stepName}";
}

public readonly record struct CachedSelection(string AgentId, string TaskCategory, DateTimeOffset SelectedAt);

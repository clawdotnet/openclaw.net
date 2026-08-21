using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

using Strategos.Selection;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// Translates <see cref="RuntimeEventEntry"/> records the gateway pushes over
/// /runtime-events into <see cref="Strategos.Selection.AgentOutcome"/> updates
/// for <see cref="Strategos.Abstractions.IAgentSelector.RecordOutcomeAsync"/>.
///
/// Pure function: takes an entry, returns the (agentId, taskCategory, outcome)
/// triple — or null when the entry should be ignored. The class is held as a
/// singleton because the only state is the injected cache; all branching
/// happens on the entry payload itself.
/// </summary>
public sealed class AgentOutcomeMapper
{
    private static readonly HashSet<string> CompletedActions = new(StringComparer.Ordinal)
    {
        "run_completed",
        "run_failed",
    };

    private readonly RunIdAgentSelectionCache _cache;
    private readonly ILogger<AgentOutcomeMapper> _logger;

    public AgentOutcomeMapper(RunIdAgentSelectionCache cache, ILogger<AgentOutcomeMapper> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Maps <paramref name="entry"/> to a recorded outcome. Returns null when
    /// the entry should be silently skipped (wrong component, missing
    /// metadata, cache miss, or a "neutral" action like run_started /
    /// response_sent that carries no pass/fail signal).
    /// </summary>
    public MappedOutcome? Map(RuntimeEventEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.Equals(entry.Component, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var metadata = entry.Metadata;
        if (metadata is null
            || !metadata.TryGetValue("runId", out var runId)
            || !metadata.TryGetValue("stepName", out var stepName))
        {
            _logger.LogDebug(
                "Skipping runtime event {EventId}: missing runId or stepName in metadata.",
                entry.Id);
            return null;
        }

        if (!CompletedActions.Contains(entry.Action))
        {
            // run_started / response_sent / anything else — no pass/fail signal.
            return null;
        }

        var cached = _cache.TryGet(runId, stepName);
        if (cached is null)
        {
            _logger.LogDebug(
                "Skipping runtime event {EventId}: no cached selection for runId={RunId} stepName={StepName}.",
                entry.Id, runId, stepName);
            return null;
        }

        var success = string.Equals(entry.Action, "run_completed", StringComparison.OrdinalIgnoreCase);
        var outcome = success
            ? AgentOutcome.Succeeded()
            : AgentOutcome.Failed();

        return new MappedOutcome(cached.Value.AgentId, cached.Value.TaskCategory, outcome);
    }
}

public readonly record struct MappedOutcome(string AgentId, string TaskCategory, AgentOutcome Outcome);
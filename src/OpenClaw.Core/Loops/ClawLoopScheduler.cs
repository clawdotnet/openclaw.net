using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NCrontab;
using TickerQ.Utilities;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Manages /loop command lifecycle using an internal registry of loop entries.
/// The AgentLoopJob TickerFunction ticks every minute and polls this scheduler
/// for due entries.
///
/// One loop per session (idempotent — new registration replaces old).
/// Implements ILoopControlService for termination signal handling.
/// </summary>
public sealed class ClawLoopScheduler : ILoopControlService
{
    private readonly ILogger<ClawLoopScheduler> _logger;

    // sessionId → LoopEntry (idempotent: one entry per session)
    private readonly ConcurrentDictionary<string, LoopEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ClawLoopScheduler(ILogger<ClawLoopScheduler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Schedules (or overwrites) a recurring loop for the given session.
    /// </summary>
    public Task ScheduleLoopAsync(string sessionId, string cronExpression, string prompt, CancellationToken ct)
    {
        var schedule = ParseCronExpression(cronExpression);
        var entry = new LoopEntry(sessionId, prompt, cronExpression, schedule);
        _entries.AddOrUpdate(sessionId, _ => entry, (_, _) => entry);
        _logger.LogInformation("Loop scheduled for session {SessionId} with cron '{Cron}'", sessionId, cronExpression);
        return Task.CompletedTask;
    }

    private static CrontabSchedule ParseCronExpression(string cronExpression)
    {
        try
        {
            var fieldCount = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var options = new CrontabSchedule.ParseOptions { IncludingSeconds = fieldCount == 6 };
            return CrontabSchedule.Parse(cronExpression, options);
        }
        catch (Exception ex) when (ex is CrontabException or FormatException or ArgumentException)
        {
            throw new ArgumentException($"Invalid cron expression: {cronExpression}", nameof(cronExpression), ex);
        }
    }

    /// <summary>
    /// Cancels the loop for the given session. No-op if none exists.
    /// </summary>
    public Task CancelLoopAsync(string sessionId, CancellationToken ct)
    {
        if (_entries.TryRemove(sessionId, out _))
            _logger.LogInformation("Loop canceled for session {SessionId}", sessionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns loop status info or null if no active loop.
    /// </summary>
    public Task<string?> GetLoopStatusAsync(string sessionId, CancellationToken ct)
    {
        if (_entries.TryGetValue(sessionId, out var entry))
            return Task.FromResult<string?>(
                $"Loop active — cron: {entry.CronExpression}, prompt: \"{entry.Prompt}\", " +
                $"scheduled at: {entry.ScheduledAt:O}");
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    Task ILoopControlService.SignalCompleteAsync(string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("Loop termination signal received for session {SessionId}", sessionId);
        return CancelLoopAsync(sessionId, ct);
    }

    /// <summary>
    /// Returns loop entries that are due for execution at the given time.
    /// Called by AgentLoopJob on each TickerQ tick.
    /// </summary>
    internal IReadOnlyList<LoopEntry> GetDueEntries(DateTimeOffset now)
    {
        var results = new List<LoopEntry>();
        foreach (var (_, entry) in _entries)
        {
            if (entry.IsDue(now))
                results.Add(entry);
        }
        return results;
    }

    /// <summary>
    /// Converts a user-facing interval (e.g. "5m", "30s", "2h") to a 6-field cron expression.
    /// </summary>
    public static string IntervalToCron(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
            throw new ArgumentException("Interval must not be empty.", nameof(interval));

        var unit = interval[^1];
        if (!int.TryParse(interval.AsSpan(0, interval.Length - 1), out var val) || val <= 0)
            throw new ArgumentException($"Invalid interval: {interval}", nameof(interval));

        return unit switch
        {
            's' when val >= 60 => $"*/{val / 60} * * * *",
            's' => $"*/{val} * * * * *",
            'm' => $"*/{val} * * * *",
            'h' => $"0 */{val} * * *",
            _ => throw new ArgumentException($"Unknown interval unit: {unit}", nameof(interval))
        };
    }
}

/// <summary>
/// Represents a scheduled loop entry for a session.
/// </summary>
public sealed class LoopEntry
{
    public string SessionId { get; }
    public string Prompt { get; }
    public string CronExpression { get; }
    public DateTimeOffset ScheduledAt { get; }

    private readonly object _gate = new();
    private readonly CrontabSchedule _schedule;
    private DateTimeOffset _nextOccurrence;

    public LoopEntry(string sessionId, string prompt, string cronExpression, CrontabSchedule schedule)
    {
        SessionId = sessionId;
        Prompt = prompt;
        CronExpression = cronExpression;
        _schedule = schedule;
        ScheduledAt = DateTimeOffset.UtcNow;
        _nextOccurrence = _schedule.GetNextOccurrence(ScheduledAt.UtcDateTime);
    }

    /// <summary>
    /// Returns true if the loop is due at the given time.
    /// Updates the next occurrence marker.
    /// </summary>
    public bool IsDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now.UtcDateTime < _nextOccurrence)
                return false;

            _nextOccurrence = _schedule.GetNextOccurrence(now.UtcDateTime);
            return true;
        }
    }
}

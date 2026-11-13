using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NCrontab;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Manages /loop command lifecycle using an internal registry of loop entries.
/// The AgentLoopJob background worker polls this scheduler for due entries.
///
/// One loop per session (idempotent — new registration replaces old).
/// Implements ILoopControlService for termination signal handling.
/// </summary>
public sealed class ClawLoopScheduler : ILoopControlService
{
    private readonly ILogger<ClawLoopScheduler> _logger;
    private readonly TimeProvider _clock;

    // sessionId → LoopEntry (idempotent: one entry per session)
    private readonly ConcurrentDictionary<string, LoopEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ClawLoopScheduler(ILogger<ClawLoopScheduler> logger, TimeProvider? clock = null)
    {
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Schedules (or overwrites) a recurring loop for the given session.
    /// </summary>
    public Task ScheduleLoopAsync(string sessionId, string cronExpression, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var schedule = ParseCronExpression(cronExpression);
        var entry = new LoopEntry(sessionId, prompt, cronExpression, schedule, _clock.GetUtcNow());
        _entries.AddOrUpdate(sessionId, _ => entry, (_, _) => entry);
        _logger.LogInformation("Loop scheduled for session {SessionId} with cron '{Cron}'", sessionId, cronExpression);
        return Task.CompletedTask;
    }

    /// <summary>Schedules a fixed duration from registration, without rounding to calendar boundaries.</summary>
    public Task ScheduleIntervalLoopAsync(string sessionId, string interval, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var duration = ParseInterval(interval);
        var entry = new LoopEntry(sessionId, prompt, duration, _clock.GetUtcNow());
        _entries.AddOrUpdate(sessionId, _ => entry, (_, _) => entry);
        _logger.LogInformation("Loop scheduled for session {SessionId} every {Interval}", sessionId, duration);
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
                $"Loop active — {entry.ScheduleDescription}, prompt: \"{entry.Prompt}\", " +
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
    /// Called by AgentLoopJob on each hosted polling tick.
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

    internal IReadOnlyList<LoopEntry> GetDueEntries() => GetDueEntries(_clock.GetUtcNow());

    public static TimeSpan ParseInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
            throw new ArgumentException("Interval must not be empty.", nameof(interval));
        interval = interval.Trim();
        if (!int.TryParse(interval.AsSpan(0, interval.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"Invalid interval: {interval}", nameof(interval));
        var seconds = char.ToLowerInvariant(interval[^1]) switch
        {
            's' => (long)value,
            'm' => value * 60L,
            'h' => value * 3600L,
            _ => throw new ArgumentException($"Unknown interval unit: {interval[^1]}", nameof(interval))
        };
        if (seconds > 365L * 24 * 3600)
            throw new ArgumentException("Loop intervals must not exceed 365 days.", nameof(interval));
        if (seconds < 60)
            throw new ArgumentException("Loop intervals must be at least 60 seconds.", nameof(interval));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Converts only intervals that cron can represent exactly. Duration scheduling should use
    /// ScheduleIntervalLoopAsync instead, which also anchors the first run to registration time.
    /// </summary>
    public static string IntervalToCron(string interval)
    {
        var seconds = (long)ParseInterval(interval).TotalSeconds;
        if (seconds < 60 && 60 % seconds == 0)
            return $"*/{seconds} * * * * *";
        if (seconds % 60 == 0 && seconds < 3600 && 3600 % seconds == 0)
            return $"*/{seconds / 60} * * * *";
        if (seconds % 3600 == 0 && seconds <= 86400 && 86400 % seconds == 0)
            return $"0 */{seconds / 3600} * * *";
        throw new ArgumentException("This duration cannot be represented exactly by cron. Use ScheduleIntervalLoopAsync.", nameof(interval));
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
    public TimeSpan? Interval { get; }
    public string ScheduleDescription => Interval is { } interval ? $"interval: {interval}" : $"cron: {CronExpression}";
    public DateTimeOffset ScheduledAt { get; }

    private readonly object _gate = new();
    private readonly CrontabSchedule? _schedule;
    private DateTimeOffset _nextOccurrence;

    public LoopEntry(string sessionId, string prompt, string cronExpression, CrontabSchedule schedule, DateTimeOffset? scheduledAt = null)
    {
        SessionId = sessionId;
        Prompt = prompt;
        CronExpression = cronExpression;
        _schedule = schedule;
        ScheduledAt = scheduledAt ?? DateTimeOffset.UtcNow;
        _nextOccurrence = _schedule.GetNextOccurrence(ScheduledAt.UtcDateTime);
    }

    public LoopEntry(string sessionId, string prompt, TimeSpan interval, DateTimeOffset scheduledAt)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        SessionId = sessionId;
        Prompt = prompt;
        CronExpression = "";
        Interval = interval;
        ScheduledAt = scheduledAt;
        _nextOccurrence = scheduledAt.Add(interval);
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

            // Skip missed runs, preserving the original cadence instead of drifting with each poll.
            _nextOccurrence = Interval is { } interval
                ? _nextOccurrence.AddTicks(((now - _nextOccurrence).Ticks / interval.Ticks + 1) * interval.Ticks)
                : _schedule!.GetNextOccurrence(now.UtcDateTime);
            return true;
        }
    }
}

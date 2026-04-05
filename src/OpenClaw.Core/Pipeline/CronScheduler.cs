using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

/// <summary>
/// A simple background service that checks registered cron jobs every minute
/// and publishes an InboundMessage to the pipeline.
/// </summary>
public sealed class CronScheduler : BackgroundService
{
    private static readonly TimeSpan MaxRunningDuration = TimeSpan.FromHours(6);

    private readonly ICronJobSource _jobSource;
    private readonly ILogger<CronScheduler> _logger;
    private readonly ChannelWriter<InboundMessage> _pipelineChannel;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _runningJobs = new(StringComparer.OrdinalIgnoreCase);

    public CronScheduler(ICronJobSource jobSource, ILogger<CronScheduler> logger, ChannelWriter<InboundMessage> pipelineChannel)
    {
        _jobSource = jobSource;
        _logger = logger;
        _pipelineChannel = pipelineChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialJobs = _jobSource.GetJobs();
        if (initialJobs.Count == 0)
        {
            _logger.LogInformation("Cron Scheduler started with no jobs. Waiting for live cron registrations.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _logger.LogInformation("Cron Scheduler started. Monitoring {Count} initial jobs.", initialJobs.Count);

        // Optional: run selected jobs immediately once on startup (useful for testing / boot-time reports)
        foreach (var job in initialJobs)
        {
            if (!job.RunOnStartup)
                continue;

            try
            {
                var now = DateTimeOffset.UtcNow;
                _logger.LogInformation("Triggering cron job '{JobName}' on startup at {Time}", job.Name, now);
                await EnqueueJobIfNotRunningAsync(job, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to run cron job '{JobName}' on startup", job.Name);
            }
        }

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            CleanupStaleRunningJobs(DateTimeOffset.UtcNow);
            var jobs = _jobSource.GetJobs();
            if (jobs.Count == 0)
                continue;

            var utcNow = DateTimeOffset.UtcNow;

            // Re-evaluate jobs at the top of the minute
            foreach (var job in jobs)
            {
                // Convert to job-specific timezone if configured, otherwise use UTC
                var now = utcNow;
                if (!string.IsNullOrWhiteSpace(job.Timezone))
                {
                    try
                    {
                        var tz = TimeZoneInfo.FindSystemTimeZoneById(job.Timezone);
                        now = TimeZoneInfo.ConvertTime(utcNow, tz);
                    }
                    catch (TimeZoneNotFoundException)
                    {
                        _logger.LogWarning("Cron job '{JobName}' has invalid timezone '{Timezone}', falling back to UTC.",
                            job.Name, job.Timezone);
                    }
                }

                if (IsTime(job.CronExpression, now))
                {
                    _logger.LogInformation("Triggering cron job '{JobName}' at {Time}", job.Name, now);
                    await EnqueueJobIfNotRunningAsync(job, stoppingToken);
                }
            }
        }
    }

    public void MarkJobCompleted(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            return;

        _runningJobs.TryRemove(jobName, out _);
    }

    private async ValueTask EnqueueJobIfNotRunningAsync(CronJobConfig job, CancellationToken ct)
    {
        var jobName = string.IsNullOrWhiteSpace(job.Name) ? "unnamed" : job.Name;
        var now = DateTimeOffset.UtcNow;
        if (_runningJobs.TryGetValue(jobName, out var runningSince))
        {
            if ((now - runningSince) <= MaxRunningDuration)
            {
                _logger.LogWarning("Skipping cron job '{JobName}' because a previous invocation is still running.", jobName);
                return;
            }

            _logger.LogWarning("Reaping stale running state for cron job '{JobName}' after {Duration}.", jobName, now - runningSince);
            _runningJobs.TryRemove(jobName, out _);
        }

        if (!_runningJobs.TryAdd(jobName, now))
        {
            _logger.LogWarning("Skipping cron job '{JobName}' because a previous invocation is still running.", jobName);
            return;
        }

        try
        {
            await EnqueueJobAsync(job, ct);
        }
        catch
        {
            _runningJobs.TryRemove(jobName, out _);
            throw;
        }
    }

    private async ValueTask EnqueueJobAsync(CronJobConfig job, CancellationToken ct)
    {
        var sessionId = job.SessionId ?? $"cron:{job.Name}";
        var channelId = job.ChannelId ?? "cron";

        // If a delivery RecipientId is explicitly set, send responses to that recipient.
        // Otherwise, set a stable "pseudo recipient" so the cron channel can bucket outputs per job/session.
        var senderId = job.RecipientId ?? sessionId ?? job.Name ?? "system";

        var msg = new InboundMessage
        {
            IsSystem = true,
            SessionId = sessionId,
            CronJobName = job.Name,
            ChannelId = channelId,
            SenderId = senderId,
            Subject = job.Subject ?? (string.IsNullOrWhiteSpace(job.Name) ? null : $"OpenClaw Cron: {job.Name}"),
            Text = job.Prompt
        };

        await _pipelineChannel.WriteAsync(msg, ct);
    }

    /// <summary>
    /// Evaluates a standard 5-field cron expression against a given time.
    /// (Minutes, Hours, Day of Month, Month, Day of Week)
    /// </summary>
    public static bool IsTime(string expression, DateTimeOffset time)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        expression = NormalizeExpression(expression);

        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        var minMatch = MatchField(parts[0], time.Minute, 0, 59, time);
        var hourMatch = MatchField(parts[1], time.Hour, 0, 23, time);
        var domMatch = MatchField(parts[2], time.Day, 1, 31, time);
        var monthMatch = MatchField(parts[3], time.Month, 1, 12, time);
        var dowMatch = MatchField(parts[4], (int)time.DayOfWeek, 0, 6, time);

        return minMatch && hourMatch && domMatch && monthMatch && dowMatch;
    }

    private static string NormalizeExpression(string expression)
    {
        return expression.Trim().ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            _ => expression
        };
    }

    private static bool MatchField(string field, int value, int minValue, int maxValue, DateTimeOffset time)
    {
        if (field == "*") return true;

        if (field == "L")
            return value == DateTime.DaysInMonth(time.Year, time.Month);

        if (int.TryParse(field, out var exact))
            return exact == value;

        if (field.Contains(','))
            return field.Split(',').Any(option => MatchField(option, value, minValue, maxValue, time));

        if (field.Contains('/'))
        {
            var stepParts = field.Split('/');
            if (stepParts.Length == 2 && int.TryParse(stepParts[1], out var step) && step > 0)
            {
                var range = stepParts[0];
                if (range == "*")
                    return (value - minValue) % step == 0;

                if (TryParseRange(range, out var start, out var end))
                {
                    if (!IsValueInRange(value, start, end))
                        return false;

                    return (value - start) % step == 0;
                }
            }
        }

        if (TryParseRange(field, out var rangeStart, out var rangeEnd))
            return IsValueInRange(value, rangeStart, rangeEnd);

        return false;
    }

    private static bool TryParseRange(string field, out int start, out int end)
    {
        start = 0;
        end = 0;
        var rangeParts = field.Split('-');
        if (rangeParts.Length != 2)
            return false;

        return int.TryParse(rangeParts[0], out start)
            && int.TryParse(rangeParts[1], out end);
    }

    private void CleanupStaleRunningJobs(DateTimeOffset nowUtc)
    {
        foreach (var kvp in _runningJobs)
        {
            if ((nowUtc - kvp.Value) <= MaxRunningDuration)
                continue;

            if (_runningJobs.TryRemove(kvp.Key, out _))
            {
                _logger.LogWarning(
                    "Removed stale running marker for cron job '{JobName}' after {Duration}.",
                    kvp.Key,
                    nowUtc - kvp.Value);
            }
        }
    }

    private static bool IsValueInRange(int value, int start, int end)
    {
        if (start <= end)
            return value >= start && value <= end;

        return value >= start || value <= end;
    }
}

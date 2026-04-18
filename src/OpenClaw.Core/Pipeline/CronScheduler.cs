using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NCrontab;
using OpenClaw.Core.Models;
using TickerQ.Utilities;

namespace OpenClaw.Core.Pipeline;

/// <summary>
/// Dispatches configured cron jobs when invoked by the host scheduler.
/// </summary>
public sealed class CronScheduler
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

    public async Task RunStartupJobsAsync(CancellationToken stoppingToken)
    {
        var initialJobs = _jobSource.GetJobs();
        if (initialJobs.Count == 0)
        {
            _logger.LogInformation("Cron Scheduler started with no jobs. Waiting for live cron registrations.");
        }

        _logger.LogInformation("Cron Scheduler started. Monitoring {Count} initial jobs.", initialJobs.Count);

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
    }

    public async Task RunTickAsync(CancellationToken stoppingToken)
    {
        CleanupStaleRunningJobs(DateTimeOffset.UtcNow);
        var jobs = _jobSource.GetJobs();
        if (jobs.Count == 0)
            return;

        var utcNow = DateTimeOffset.UtcNow;

        foreach (var job in jobs)
        {
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

            if (!IsTime(job.CronExpression, now))
                continue;

            _logger.LogInformation("Triggering cron job '{JobName}' at {Time}", job.Name, now);
            await EnqueueJobIfNotRunningAsync(job, stoppingToken);
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
    /// Evaluates a cron expression against a given time using TickerQ parsing semantics.
    /// </summary>
    public static bool IsTime(string expression, DateTimeOffset time)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var normalizedExpression = NormalizeExpression(expression, time);
        if (!CronExpression.TryParse(normalizedExpression, out var cronExpression))
            return false;

        var schedule = CrontabSchedule.Parse(cronExpression.Value, new CrontabSchedule.ParseOptions
        {
            IncludingSeconds = true
        });

        var localTime = DateTime.SpecifyKind(time.DateTime, DateTimeKind.Unspecified);
        var previousSecond = localTime.AddSeconds(-1);
        var nextOccurrence = schedule.GetNextOccurrence(previousSecond);

        return nextOccurrence == localTime;
    }

    private static string NormalizeExpression(string expression, DateTimeOffset time)
    {
        var normalized = expression.Trim().ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            _ => expression
        };

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dayOfMonthIndex = parts.Length switch
        {
            5 => 2,
            6 => 3,
            _ => -1
        };

        if (dayOfMonthIndex >= 0 && string.Equals(parts[dayOfMonthIndex], "l", StringComparison.OrdinalIgnoreCase))
            parts[dayOfMonthIndex] = DateTime.DaysInMonth(time.Year, time.Month).ToString();

        return string.Join(' ', parts);
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
}

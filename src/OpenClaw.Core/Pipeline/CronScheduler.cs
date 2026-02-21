using System.Collections.Concurrent;
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
    private readonly GatewayConfig _config;
    private readonly ILogger<CronScheduler> _logger;
    private readonly ChannelWriter<InboundMessage> _pipelineChannel;
    
    // Quick cache for "is it this minute?" evaluations
    private readonly ConcurrentDictionary<string, bool> _cronCache = new();

    public CronScheduler(GatewayConfig config, ILogger<CronScheduler> logger, ChannelWriter<InboundMessage> pipelineChannel)
    {
        _config = config;
        _logger = logger;
        _pipelineChannel = pipelineChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.Cron?.Enabled != true || _config.Cron.Jobs is null || _config.Cron.Jobs.Count == 0)
        {
            _logger.LogInformation("Cron Scheduler disabled or no jobs defined. Exiting background loop.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _logger.LogInformation("Cron Scheduler started. Monitoring {Count} jobs.", _config.Cron.Jobs.Count);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            
            // Re-evaluate jobs at the top of the minute
            foreach (var job in _config.Cron.Jobs)
            {
                if (IsTime(job.CronExpression, now))
                {
                    _logger.LogInformation("Triggering cron job '{JobName}' at {Time}", job.Name, now);
                    
                    var msg = new InboundMessage
                    {
                        SessionId = job.SessionId ?? $"cron:{job.Name}",
                        ChannelId = job.ChannelId ?? "cron",
                        SenderId = "system",
                        Text = job.Prompt
                    };

                    await _pipelineChannel.WriteAsync(msg, stoppingToken);
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a standard 5-field cron expression against a given time.
    /// (Minutes, Hours, Day of Month, Month, Day of Week)
    /// </summary>
    private static bool IsTime(string expression, DateTimeOffset time)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        var minMatch = MatchField(parts[0], time.Minute);
        var hourMatch = MatchField(parts[1], time.Hour);
        var domMatch = MatchField(parts[2], time.Day);
        var monthMatch = MatchField(parts[3], time.Month);
        var dowMatch = MatchField(parts[4], (int)time.DayOfWeek);

        return minMatch && hourMatch && domMatch && monthMatch && dowMatch;
    }

    private static bool MatchField(string field, int value)
    {
        if (field == "*") return true;

        if (int.TryParse(field, out var exact))
            return exact == value;

        if (field.Contains('/'))
        {
            var stepParts = field.Split('/');
            if (stepParts.Length == 2 && stepParts[0] == "*" && int.TryParse(stepParts[1], out var step))
                return value % step == 0;
        }

        if (field.Contains(','))
        {
            var options = field.Split(',');
            foreach (var opt in options)
            {
                if (int.TryParse(opt, out var parsed) && parsed == value)
                    return true;
            }
        }

        if (field.Contains('-'))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length == 2 && 
                int.TryParse(rangeParts[0], out var start) && 
                int.TryParse(rangeParts[1], out var end))
            {
                return value >= start && value <= end;
            }
        }

        return false;
    }
}

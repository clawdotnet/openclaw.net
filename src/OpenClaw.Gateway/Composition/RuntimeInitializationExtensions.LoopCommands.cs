using OpenClaw.Core.Loops;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
    private static void WireLoopCommandCallback(WebApplication app, RuntimeServices services)
    {
        var scheduler = app.Services.GetRequiredService<ClawLoopScheduler>();
        services.CommandProcessor.SetLoopCallback(async (session, text, ct) =>
        {
            var cmd = LoopCommandParser.TryParse(text);
            if (cmd is null || cmd.Action == LoopAction.Invalid)
                return "Usage: /loop <interval> <prompt>  — e.g. /loop 5m check build status\n       /loop cancel  — cancel active loop\n       /loop status  — show loop status";

            return cmd.Action switch
            {
                LoopAction.Cancel =>
                    await CancelLoopAsync(scheduler, session.Id, ct),
                LoopAction.Status =>
                    await GetLoopStatusAsync(scheduler, session.Id, ct),
                LoopAction.Schedule when cmd.Interval is not null && cmd.Prompt is not null =>
                    await ScheduleLoopAsync(scheduler, session.Id, cmd.Interval, cmd.Prompt, ct),
                _ => "Invalid /loop syntax."
            };
        });
    }

    private static async Task<string> ScheduleLoopAsync(ClawLoopScheduler scheduler, string sessionId, string interval, string prompt, CancellationToken ct)
    {
        try
        {
            var cron = ClawLoopScheduler.IntervalToCron(interval);
            await scheduler.ScheduleLoopAsync(sessionId, cron, prompt, ct);
            return $"Loop started — interval: {interval}, prompt: \"{prompt}\"";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> CancelLoopAsync(ClawLoopScheduler scheduler, string sessionId, CancellationToken ct)
    {
        await scheduler.CancelLoopAsync(sessionId, ct);
        return "Loop canceled.";
    }

    private static async Task<string> GetLoopStatusAsync(ClawLoopScheduler scheduler, string sessionId, CancellationToken ct)
    {
        var status = await scheduler.GetLoopStatusAsync(sessionId, ct);
        return status ?? "No active loop for this session.";
    }
}

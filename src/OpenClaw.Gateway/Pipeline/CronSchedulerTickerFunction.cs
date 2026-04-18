using OpenClaw.Core.Pipeline;
using TickerQ.Utilities.Base;

namespace OpenClaw.Gateway.Pipeline;

internal sealed class CronSchedulerTickerFunction
{
    private readonly CronScheduler _scheduler;

    public CronSchedulerTickerFunction(CronScheduler scheduler)
        => _scheduler = scheduler;

    [TickerFunction("openclaw-cron-scan", cronExpression: "* * * * *")]
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
        => _scheduler.RunTickAsync(cancellationToken);
}

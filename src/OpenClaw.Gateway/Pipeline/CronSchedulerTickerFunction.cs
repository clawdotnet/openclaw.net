using OpenClaw.Core.Pipeline;
using TickerQ.Utilities.Base;

namespace OpenClaw.Gateway.Pipeline;

internal sealed class CronSchedulerTickerFunction
{
    private readonly CronScheduler _scheduler;
    private readonly GatewayAutomationService _automations;
    private readonly MessagePipeline _pipeline;

    public CronSchedulerTickerFunction(CronScheduler scheduler, GatewayAutomationService automations, MessagePipeline pipeline)
    {
        _scheduler = scheduler;
        _automations = automations;
        _pipeline = pipeline;
    }

    [TickerFunction("openclaw-cron-scan", cronExpression: "* * * * *")]
    public async Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        await _scheduler.RunTickAsync(cancellationToken);
        await _automations.RunMaintenanceAsync(_pipeline, cancellationToken);
    }
}

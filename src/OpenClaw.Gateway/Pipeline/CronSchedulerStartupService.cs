using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Pipeline;

internal sealed class CronSchedulerStartupService : IHostedService
{
    private readonly CronScheduler _scheduler;

    public CronSchedulerStartupService(CronScheduler scheduler)
        => _scheduler = scheduler;

    public Task StartAsync(CancellationToken cancellationToken)
        => _scheduler.RunStartupJobsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

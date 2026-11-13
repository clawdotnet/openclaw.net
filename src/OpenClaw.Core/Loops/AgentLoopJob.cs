using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Lightweight in-process worker that dispatches due loop prompts.
/// Cuts OTel context on each iteration to prevent span nesting across iterations.
/// </summary>
public sealed class AgentLoopJob : BackgroundService
{
    private readonly ClawLoopScheduler _scheduler;
    private readonly IAgentLoopDispatcher _dispatcher;
    private readonly ILogger<AgentLoopJob> _logger;

    public AgentLoopJob(
        ClawLoopScheduler scheduler,
        IAgentLoopDispatcher dispatcher,
        ILogger<AgentLoopJob> logger)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await DispatchDueAsync(stoppingToken);
    }

    internal async Task DispatchDueAsync(CancellationToken ct)
    {
        // Cut OTel context to prevent nested span chains across loop iterations
        Activity.Current = null;

        try
        {
            var dueEntries = _scheduler.GetDueEntries();

            foreach (var entry in dueEntries)
            {
                if (ct.IsCancellationRequested)
                    break;

                _logger.LogInformation(
                    "Loop tick: dispatching prompt for session {SessionId}",
                    entry.SessionId);

                try
                {
                    await _dispatcher.DispatchAsync(entry.SessionId, entry.Prompt, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Loop dispatch failed for session {SessionId}",
                        entry.SessionId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }
}

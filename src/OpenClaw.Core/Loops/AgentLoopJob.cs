using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace OpenClaw.Core.Loops;

/// <summary>
/// TickerQ function that ticks every second and dispatches due loop prompts.
/// Polls ClawLoopScheduler.GetDueEntries() and delegates to IAgentLoopDispatcher.
/// Cuts OTel context on each tick to prevent span nesting across iterations.
/// </summary>
public sealed class AgentLoopJob
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

    [TickerFunction("AgentLoopExecutor", cronExpression: "* * * * * *")]
    public async Task ExecuteAsync(TickerFunctionContext context, CancellationToken ct)
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

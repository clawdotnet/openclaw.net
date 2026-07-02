using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Background;

internal sealed class BackgroundSessionRecoveryWorker
{
    private readonly IBackgroundSessionStore? _store;
    private readonly MessagePipeline _pipeline;
    private readonly GatewayConfig _config;
    private readonly ILogger<BackgroundSessionRecoveryWorker> _logger;

    public BackgroundSessionRecoveryWorker(
        IBackgroundSessionStore? store,
        MessagePipeline pipeline,
        GatewayConfig config,
        ILogger<BackgroundSessionRecoveryWorker> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _config = config;
        _logger = logger ?? NullLogger<BackgroundSessionRecoveryWorker>.Instance;
    }

    public async Task RecoverOnceAsync(CancellationToken ct)
    {
        if (!_config.BackgroundExecution.Enabled || !_config.BackgroundExecution.AutoResumeOnStartup)
            return;

        if (_store is null)
        {
            _logger.LogWarning("Background session recovery is enabled, but the configured memory store does not support runnable session listing.");
            return;
        }

        var limit = Math.Max(1, _config.BackgroundExecution.AutoResumeMaxConcurrent * 20);
        var sessions = await _store.ListBackgroundRunnableSessionsAsync(limit, ct);
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (session.BackgroundRun is null)
                continue;

            await _pipeline.InboundWriter.WriteAsync(new InboundMessage
            {
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                SessionId = session.Id,
                Text = "Resume the active background goal from the latest checkpoint.",
                Type = BackgroundMessageTypes.AutoResume,
                IsSystem = true,
                BackgroundRunId = session.BackgroundRun.RunId,
                BackgroundContinuationSequence = session.BackgroundRun.ContinuationSequence
            }, ct);

            if (_config.BackgroundExecution.AutoResumeStaggerSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_config.BackgroundExecution.AutoResumeStaggerSeconds), ct);
        }
    }
}

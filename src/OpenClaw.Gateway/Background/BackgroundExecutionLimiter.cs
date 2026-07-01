using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Background;

internal sealed class BackgroundExecutionLimiter : IAsyncDisposable
{
    private readonly SemaphoreSlim _permits;

    public BackgroundExecutionLimiter(GatewayConfig config)
    {
        var max = Math.Max(1, config.BackgroundExecution.MaxConcurrentBackgroundTurns);
        _permits = new SemaphoreSlim(max, max);
    }

    public async ValueTask<Releaser?> TryAcquireAsync(InboundMessage message, CancellationToken ct)
    {
        if (!IsBackgroundContinuation(message))
            return new Releaser(null);

        if (!await _permits.WaitAsync(TimeSpan.Zero, ct))
            return null;

        return new Releaser(_permits);
    }

    public static bool IsBackgroundContinuation(InboundMessage message)
        => string.Equals(message.Type, BackgroundMessageTypes.AutoContinue, StringComparison.Ordinal)
        || string.Equals(message.Type, BackgroundMessageTypes.AutoResume, StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        _permits.Dispose();
        return ValueTask.CompletedTask;
    }

    public readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim? _semaphore;

        internal Releaser(SemaphoreSlim? semaphore) => _semaphore = semaphore;

        public void Dispose() => _semaphore?.Release();
    }
}

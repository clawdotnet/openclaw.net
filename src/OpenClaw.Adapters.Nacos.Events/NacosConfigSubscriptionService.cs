using Microsoft.Extensions.Logging;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>Background subscription setup with bounded attempts and retry. SDK long polling owns reconnect after registration.</summary>
public sealed class NacosConfigSubscriptionService(
    INacosConfigService config, NacosOptions options, ICapabilityInvalidationSink invalidation,
    ILogger<NacosConfigSubscriptionService> logger) : ICapabilityChangeSource
{
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private IDisposable? _handle;
    private bool _disposed;
    private volatile string _status = "disabled";
    public string ProviderId => "nacos";
    // Active means listener registered, not a guarantee that the remote service is healthy.
    public string Status => _status;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null || !options.Enabled || string.IsNullOrWhiteSpace(options.ServerAddr))
                return Task.CompletedTask;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _status = "starting";
            _worker = SubscribeAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    private async Task SubscribeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.LongPollingTimeoutMs, 100, 60_000)));
                await config.GetConfigAsync(options.DataId, options.Group, attempt.Token);
                _handle = await config.AddListenerAsync(options.DataId, options.Group, OnChange, attempt.Token);
                _status = "active";
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _status = "degraded";
                logger.LogWarning(ex, "Nacos subscription unavailable; retrying. TTL and explicit reload remain available.");
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(options.ReconnectDelayMs, 10, 60_000)), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private void OnChange(NacosConfig changed)
    {
        if (_lifetime?.IsCancellationRequested != false) return;
        invalidation.Invalidate(new CapabilityChange(ProviderId, changed.Group + "/" + changed.DataId, Guid.NewGuid().ToString("N")));
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
            worker = _worker;
        }
        if (worker is not null) await worker;
        _handle?.Dispose();
        _handle = null;
        _lifetime?.Dispose();
        _status = "stopped";
        if (config is IAsyncDisposable disposable) await disposable.DisposeAsync();
        else if (config is IDisposable syncDisposable) syncDisposable.Dispose();
    }
}

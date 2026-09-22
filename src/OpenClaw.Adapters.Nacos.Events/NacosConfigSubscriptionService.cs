using Microsoft.Extensions.Logging;
using OpenClaw.Core.Skills.Meta;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>Background subscription setup with time-limited setup attempts and indefinite retries. SDK long polling owns reconnect after registration.</summary>
public sealed class NacosConfigSubscriptionService(
    INacosConfigService config, NacosOptions options, ICapabilityInvalidationSink invalidation,
    ILogger<NacosConfigSubscriptionService> logger) : ICapabilityChangeSource
{
    private readonly object _gate = new();
    private readonly object _changeGate = new();
    private string? _lastContentHash;
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
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            IDisposable? pendingHandle = null;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.LongPollingTimeoutMs, 100, 60_000)));
                pendingHandle = await config.AddListenerAsync(options.DataId, options.Group, OnChange, attempt.Token);
                var snapshot = await config.GetConfigAsync(options.DataId, options.Group, attempt.Token);
                attempt.Token.ThrowIfCancellationRequested();
                // Reconcile even an absent snapshot: bindings may predate registration or a deletion.
                OnChange(snapshot ?? new NacosConfig(options.DataId, options.Group, ""));
                _handle = pendingHandle;
                pendingHandle = null;
                _status = "active";
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _status = "degraded";
                logger.LogWarning(ex, "Nacos subscription unavailable; retrying. TTL and explicit reload remain available.");
            }
            finally { pendingHandle?.Dispose(); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(60_000, Math.Clamp(options.ReconnectDelayMs, 10, 60_000) * Math.Pow(2, Math.Min(failures++, 10)))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private void OnChange(NacosConfig changed)
    {
        if (changed.DataId != options.DataId || changed.Group != options.Group) return;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changed.Content)));
        lock (_changeGate)
        {
            if (_lifetime?.IsCancellationRequested != false || hash == _lastContentHash) return;
            // The SDK can deliver its initial snapshot after GetConfigAsync has
            // reconciled the same content. Do not invalidate a binding in flight
            // twice for that one revision. Retain only a digest, never config data.
            invalidation.Invalidate(new CapabilityChange(ProviderId, changed.Group + "/" + changed.DataId, Guid.NewGuid().ToString("N")));
            _lastContentHash = hash;
        }
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

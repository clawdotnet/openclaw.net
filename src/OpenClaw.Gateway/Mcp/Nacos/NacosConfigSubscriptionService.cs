using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Mcp.Nacos;

/// <summary>
/// Owns the Nacos long-poll listener lifecycle (issue #238). On
/// <see cref="StartAsync"/> it reads the initial config for parity, then
/// subscribes to <c>(DataId, Group)</c>; every change event funnels into
/// <see cref="IMcpWorkspaceReloadTrigger.TriggerReload"/> so the established
/// workspace reload path (registry reload + tool changes + binding-cache and
/// runtime added-server cache clears) runs unchanged.
/// <para>
/// Graceful degradation: when <c>NacosOptions.ServerAddr</c> is empty/whitespace
/// or <c>Enabled</c> is false, <see cref="StartAsync"/> subscribes nothing and the
/// existing TTL/reload fallback (issue #232) remains the only invalidation path.
/// </para>
/// </summary>
public sealed class NacosConfigSubscriptionService : IAsyncDisposable
{
    private readonly INacosConfigService _config;
    private readonly NacosOptions _options;
    private readonly IMcpWorkspaceReloadTrigger _reloadTrigger;
    private readonly ILogger<NacosConfigSubscriptionService> _logger;
    private IDisposable? _handle;
    private bool _started;

    public NacosConfigSubscriptionService(
        INacosConfigService config,
        NacosOptions options,
        IMcpWorkspaceReloadTrigger reloadTrigger,
        ILogger<NacosConfigSubscriptionService> logger)
    {
        _config = config;
        _options = options;
        _reloadTrigger = reloadTrigger;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return;
        _started = true;

        if (string.IsNullOrWhiteSpace(_options.ServerAddr) || !_options.Enabled)
        {
            _logger.LogInformation("Nacos event subscription disabled: NacosOptions.ServerAddr not configured or Enabled=false. TTL/reload fallback remains active.");
            return;
        }

        try
        {
            // Initial read for parity; a missing entry is non-fatal because the
            // subscription delivers it on first publish.
            var initial = await _config.GetConfigAsync(_options.DataId, _options.Group, ct);
            if (initial is not null)
                _logger.LogInformation("Nacos initial config loaded for {DataId}/{Group} ({Length} chars).", _options.DataId, _options.Group, initial.Content.Length);
            else
                _logger.LogWarning("Nacos initial config missing for {DataId}/{Group}; subscribing for future updates.", _options.DataId, _options.Group);

            _handle = _config.AddListener(_options.DataId, _options.Group, OnChange);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _started = false;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nacos subscription setup failed; falling back to TTL/reload invalidation.");
            _handle?.Dispose();
            _handle = null;
        }
    }

    private void OnChange(NacosConfig config)
    {
        _logger.LogInformation("Nacos config change received for {DataId}/{Group}; triggering MCP workspace reload.", config.DataId, config.Group);
        _reloadTrigger.TriggerReload();
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle is not null)
        {
            _handle.Dispose();
            _handle = null;
        }
        if (_config is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_config is IDisposable disposable)
            disposable.Dispose();
    }
}
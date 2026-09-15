using Microsoft.Extensions.Logging;
using OpenClaw.Core.Skills.Meta;
using RedNb.Nacos.Config;

namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>
/// Production <see cref="INacosConfigService"/> backed by the RedNb.Nacos SDK
/// (RedNb.Nacos.All 2.0.0, issue #238). The SDK client is constructed by the
/// DI extension <c>RedNb.Nacos.DependencyInjection.AddNacosConfig</c> and injected
/// as <see cref="IConfigService"/>; this adapter only translates between the SDK
/// surface (<see cref="IConfigService"/>, <see cref="ConfigInfo"/>) and the gateway
/// abstraction, keeping every Nacos-specific call site in one file.
/// </summary>
public sealed class RedNbNacosConfigService : INacosConfigService, IAsyncDisposable
{
    private readonly IConfigService _client;
    private readonly NacosOptions _options;
    private readonly ILogger<RedNbNacosConfigService> _logger;
    private int _disposed;

    public RedNbNacosConfigService(IConfigService client, NacosOptions options, ILogger<RedNbNacosConfigService> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <summary>Maps an SDK <see cref="ConfigInfo"/> to the gateway record. Pure translation.</summary>
    internal static NacosConfig Translate(ConfigInfo info)
        => new(info.DataId, info.Group, info.Content ?? string.Empty);

    public async Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct)
    {
        var raw = await _client.GetConfigAsync(dataId, group, _options.LongPollingTimeoutMs, ct);
        return string.IsNullOrEmpty(raw) ? null : new NacosConfig(dataId, group, raw);
    }

    public async Task<IDisposable> AddListenerAsync(string dataId, string group, Action<NacosConfig> onChange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var listener = new AdapterListener(info => onChange(Translate(info)));
        var handle = new Handle(() => _client.RemoveListener(dataId, group, listener));
        try
        {
            await _client.AddListenerAsync(dataId, group, listener, ct);
            ct.ThrowIfCancellationRequested();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { await _client.ShutdownAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Nacos ShutdownAsync failed."); }
        try { await _client.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Nacos config client dispose failed."); }
    }

    private sealed class AdapterListener(Action<ConfigInfo> onChange) : IConfigChangeListener
    {
        public void OnReceiveConfigInfo(ConfigInfo configInfo) => onChange(configInfo);
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                onDispose();
        }
    }
}
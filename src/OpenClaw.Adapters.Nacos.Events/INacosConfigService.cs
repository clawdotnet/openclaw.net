namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>Optional SDK boundary. Failures propagate to the subscription lifecycle.</summary>
public interface INacosConfigService
{
    Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct);
    /// <summary>Completes only after registration; disposing the handle removes the listener.</summary>
    Task<IDisposable> AddListenerAsync(string dataId, string group, Action<NacosConfig> onChange, CancellationToken ct);
}

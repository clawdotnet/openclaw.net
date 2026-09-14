namespace OpenClaw.Gateway.Mcp.Nacos;

/// <summary>
/// Gateway-side abstraction over a Nacos config client (issue #238). The real
/// implementation (<see cref="RedNbNacosConfigService"/>) wraps
/// <c>RedNb.Nacos.INacosConfigService</c>; tests inject <see cref="FakeNacosConfigService"/>
/// to simulate publish events without a live Nacos server.
/// </summary>
public interface INacosConfigService
{
    /// <summary>
    /// Read the current content of <paramref name="dataId"/>/<paramref name="group"/>.
    /// Returns <c>null</c> when the entry does not exist or the SDK reports an error
    /// (callers log the failure path; missing entries are non-fatal because the
    /// subscription delivers them on first publish).
    /// </summary>
    Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct);

    /// <summary>
    /// Subscribe to long-polling change events. The returned handle is the
    /// listener's lifetime — disposing it unsubscribes and the callback stops
    /// firing. The implementation must guarantee that <paramref name="onChange"/>
    /// runs at most once per publish per active handle.
    /// </summary>
    IDisposable AddListener(string dataId, string group, Action<NacosConfig> onChange);
}
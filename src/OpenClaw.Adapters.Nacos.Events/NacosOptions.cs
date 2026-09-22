namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>
/// Configuration block that enables Nacos config event subscription on the Gateway
/// (issue #238). Bound from <c>GatewayConfig.AdapterSettings["nacos"]</c>; the subscription service is
/// a no-op when <see cref="ServerAddr"/> is empty / whitespace, which keeps the
/// TTL + reload fallback from <c>#232</c> active in deployments that do not run Nacos.
/// </summary>
public sealed class NacosOptions
{
    /// <summary>
    /// Nacos server address (e.g. <c>127.0.0.1:8848</c>). When empty the
    /// subscription service degrades to the existing TTL/reload fallback
    /// and emits no listener requests.
    /// </summary>
    public string? ServerAddr { get; set; }

    /// <summary>
    /// Nacos dataId the Gateway subscribes to. Defaults to the workspace
    /// mcp.json shape so existing operators do not need to publish a
    /// second config entry. Override only when the deployment uses a
    /// different dataId convention.
    /// </summary>
    public string DataId { get; set; } = "openclaw-mcp.json";

    /// <summary>Nacos group; default matches Nacos' built-in DEFAULT_GROUP.</summary>
    public string Group { get; set; } = "DEFAULT_GROUP";

    /// <summary>Optional Nacos username. Supports env: and raw: secret references.</summary>
    public string? Username { get; set; }

    /// <summary>Optional Nacos password. Supports env: and raw: secret references.</summary>
    public string? Password { get; set; }

    /// <summary>LongPolling timeout (milliseconds) handed to the SDK.</summary>
    public int LongPollingTimeoutMs { get; set; } = 10_000;

    /// <summary>Opt-out switch; <c>false</c> disables subscription without un-configuring the block.</summary>
    public bool Enabled { get; set; } = true;

    public int ReconnectDelayMs { get; set; } = 5_000;
}
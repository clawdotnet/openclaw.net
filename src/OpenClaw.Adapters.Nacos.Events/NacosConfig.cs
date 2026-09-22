namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>
/// Immutable snapshot of one Nacos config blob, scoped to its (dataId, group) tuple.
/// The <see cref="Content"/> carries the raw payload as published to Nacos — for the
/// mcp.json dataId this is the JSON object that <see cref="McpConfigStore"/> parses
/// into <see cref="McpServerConfig"/> entries.
/// </summary>
public sealed record NacosConfig(string DataId, string Group, string Content);
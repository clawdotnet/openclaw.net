namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Governance options for SK-backed tools, enforced via a tool hook.
/// </summary>
public sealed class SemanticKernelPolicyOptions
{
    /// <summary>Allowlist patterns for tool names. Default: allow SK tools.</summary>
    public string[] AllowedTools { get; set; } = ["sk_*", "semantic_kernel"]; 

    /// <summary>Denylist patterns for tool names. Deny wins.</summary>
    public string[] DeniedTools { get; set; } = [];

    /// <summary>
    /// Per-sender per-tool per-minute limits, keyed by tool name glob (e.g. "sk_*" => 30).
    /// 0 or missing => unlimited.
    /// </summary>
    public Dictionary<string, int> PerSenderPerToolPerMinute { get; set; } = new(StringComparer.Ordinal);
}

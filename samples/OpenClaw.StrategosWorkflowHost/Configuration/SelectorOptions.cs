namespace OpenClaw.StrategosWorkflowHost.Configuration;

/// <summary>
/// Configuration for the Thompson Sampling selector wrapper. All fields default to
/// "off" so the sidecar keeps shipping with zero selector footprint; an operator
/// enables the loop by setting <see cref="Enabled"/> to true and providing
/// <see cref="InnerClients"/>.
/// </summary>
public sealed class SelectorOptions
{
    /// <summary>Configuration section the selector options bind from.</summary>
    public const string SectionName = "Strategos:Selector";

    /// <summary>Nested webhook sub-section (token + optional URL).</summary>
    public const string WebhookSectionName = "Strategos:Selector:Webhook";

    /// <summary>When false, the selector decorator is bypassed and chat calls go
    /// straight to the configured LlmMode client. Defaults to false so the sidecar
    /// behaves exactly as it did before this feature shipped.</summary>
    public bool Enabled { get; set; }

    /// <summary>Agent ids exposed to Thompson Sampling. The decorator routes a
    /// picked id to <see cref="InnerClients"/>[id]. In Mock mode this is typically
    /// <c>["mock"]</c> so the selector runs but picks the only available client.</summary>
    public string[] AvailableAgents { get; set; } = Array.Empty<string>();

    /// <summary>Default task category recorded against every selection. Mirrors
    /// <see cref="Strategos.Selection.TaskCategory"/> string names ("General",
    /// "CodeGeneration", ...). Defaults to "General".</summary>
    public string TaskCategory { get; set; } = "General";

    /// <summary>Inner chat clients keyed by agent id. The decorator resolves
    /// <c>InnerClients[selectedAgentId]</c> on every call. Required when
    /// <see cref="Enabled"/> is true.</summary>
    public Dictionary<string, Microsoft.Extensions.AI.IChatClient> InnerClients { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Max cached (runId, stepName) selections before FIFO eviction.
    /// Defaults to 10 000 — enough for a day of medium traffic.</summary>
    public int CacheSize { get; set; } = 10_000;

    /// <summary>Webhook receiver sub-options.</summary>
    public SelectorWebhookOptions Webhook { get; set; } = new();
}

public sealed class SelectorWebhookOptions
{
    /// <summary>Bearer token source accepted on POST /runtime-events. Resolved
    /// through <see cref="OpenClaw.Core.Security.SecretResolver"/>; supports
    /// <c>env:VAR</c>, <c>raw:LITERAL</c>, and bare env-var-name forms. Null/blank
    /// disables the receiver.</summary>
    public string? TokenSecret { get; set; }
}

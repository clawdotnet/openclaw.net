namespace OpenClaw.Core.Models;

public sealed class DynamicTurnRoutingConfig
{
    public bool Enabled { get; set; }
    public JevRoutingConfig Jev { get; set; } = new();
    public string BundlePath { get; set; } = "";
    public DynamicTurnRoutingAssetsConfig Assets { get; set; } = new();
    public DynamicTurnRoutingPolicyConfig Policy { get; set; } = new();
}

/// <summary>Optional hosted decision routing. Independent of Enabled, which controls the ONNX baseline.</summary>
public sealed class JevRoutingConfig
{
    public string Mode { get; set; } = "disabled";
    public string Endpoint { get; set; } = "https://api.typesafe.ai/v1/systemone";
    public string ApiKeyRef { get; set; } = "env:TYPESAFE_API_KEY";
    public string Model { get; set; } = "jev-1.13.0";
    public int TimeoutMs { get; set; } = 1500;
    public int MaxStateChars { get; set; } = 12000;
    public int HistoryMessages { get; set; } = 4;
    public int MaxConcurrentRequests { get; set; } = 8;
    public int CircuitFailureThreshold { get; set; } = 3;
    public int CircuitBreakSeconds { get; set; } = 30;
    public double MinConfidence { get; set; } = 0.80;
    public double DowngradeMinConfidence { get; set; } = 0.95;
    public double MinProbabilityMargin { get; set; } = 0.15;
    public double HighRiskThreshold { get; set; } = 0.20;
    public decimal InputUsdPerMillionTokens { get; set; } = 0.042m;
    // Relative to Memory.StoragePath. Empty disables the journal.
    public string DiagnosticsPath { get; set; } = "routing/jev-decisions.jsonl";
}

public sealed class DynamicTurnRoutingAssetsConfig
{
    public string ClassifierModelPath { get; set; } = "";
    public string EmbeddingModelPath { get; set; } = "";
    public string TokenizerPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string RuntimeConfigPath { get; set; } = "";
    public int Dimensions { get; set; } = 384;
}

public sealed class DynamicTurnRoutingPolicyConfig
{
    public DynamicTurnRoutingTierMap Tiers { get; set; } = new();
    public bool EnableDiagnostics { get; set; }
    public bool EnableStickyTier { get; set; } = true;
    public bool EnableMarginUpgrade { get; set; } = true;
    public bool EnableR1Rescue { get; set; } = true;
    public bool EnableUnderRoutingSafety { get; set; } = true;
    public float MarginUpgradeThreshold { get; set; } = 0.15f;
    public float R1RescueThreshold { get; set; } = 0.20f;
    public float UnderRoutingSafetyThreshold { get; set; } = 0.45f;
    public int DeepConversationTurnIndexThreshold { get; set; } = 4;
}

public sealed class DynamicTurnRoutingTierMap
{
    public DynamicTurnRoutingTierTarget T0 { get; set; } = new();
    public DynamicTurnRoutingTierTarget T1 { get; set; } = new();
    public DynamicTurnRoutingTierTarget T2 { get; set; } = new();
    public DynamicTurnRoutingTierTarget T3 { get; set; } = new();
}

public sealed class DynamicTurnRoutingTierTarget
{
    public string ModelProfileId { get; set; } = "";
    public string DirectModelFallbackProfileId { get; set; } = "";
    public string[] AllowedTools { get; set; } = [];
    public string[] PreferredTags { get; set; } = [];
    public string ReasoningLevel { get; set; } = "";
    public string ResponsePolicy { get; set; } = "";
    public string ImageCapableModelProfileId { get; set; } = "";
    public CacheContinuitySafeguardsConfig CacheContinuitySafeguards { get; set; } = new();
    public string PromptMode { get; set; } = "full";
    public bool DisableTools { get; set; }
}

public sealed class CacheContinuitySafeguardsConfig
{
    public bool Enabled { get; set; }
    public int MaxConversationTurns { get; set; } = 64;
    public bool ResetOnProfileSwitch { get; set; } = true;
}

public static class DynamicTurnRoutingTierTargets
{
    private static readonly CacheContinuitySafeguardsConfig DefaultSafeguards = new();
    private static readonly DynamicTurnRoutingTierTarget DefaultTier = new();

    public static bool HasAnyConfigured(DynamicTurnRoutingTierMap tiers)
        => IsConfigured(tiers.T0)
        || IsConfigured(tiers.T1)
        || IsConfigured(tiers.T2)
        || IsConfigured(tiers.T3);

    public static bool IsConfigured(DynamicTurnRoutingTierTarget tier)
        => !string.IsNullOrWhiteSpace(tier.ModelProfileId)
        || !string.IsNullOrWhiteSpace(tier.DirectModelFallbackProfileId)
        || tier.AllowedTools.Length > 0
        || tier.PreferredTags.Length > 0
        || !string.IsNullOrWhiteSpace(tier.ReasoningLevel)
        || !string.IsNullOrWhiteSpace(tier.ResponsePolicy)
        || !string.IsNullOrWhiteSpace(tier.ImageCapableModelProfileId)
        || tier.CacheContinuitySafeguards.Enabled != DefaultSafeguards.Enabled
        || tier.CacheContinuitySafeguards.MaxConversationTurns != DefaultSafeguards.MaxConversationTurns
        || tier.CacheContinuitySafeguards.ResetOnProfileSwitch != DefaultSafeguards.ResetOnProfileSwitch
        || !string.Equals(tier.PromptMode, DefaultTier.PromptMode, StringComparison.OrdinalIgnoreCase)
        || tier.DisableTools;
}

public sealed class ResolvedDynamicTurnRoutingConfig
{
    public bool Enabled { get; init; }
    public string Source { get; init; } = "disabled";
    public ResolvedDynamicTurnRoutingAssets Assets { get; init; } = new();
    public DynamicTurnRoutingPolicyConfig Policy { get; init; } = new();
    public DynamicTurnRoutingTierMap Tiers { get; init; } = new();
}

public sealed class ResolvedDynamicTurnRoutingAssets
{
    public string ClassifierModelPath { get; init; } = "";
    public string EmbeddingModelPath { get; init; } = "";
    public string TokenizerPath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public string RuntimeConfigPath { get; init; } = "";
    public int EmbeddingDimensions { get; init; } = 384;
}

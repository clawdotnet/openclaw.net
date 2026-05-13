namespace OpenClaw.Core.Models;

public static class SessionResponseModes
{
    public const string Default = "default";
    public const string ConciseOps = "concise_ops";
    public const string Full = "full";
}

public sealed class LocalModelPresetDefinition
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Provider { get; init; } = "ollama";
    public string DefaultBaseUrl { get; init; } = "http://127.0.0.1:11434";
    public string? PackageId { get; init; }
    public string? ModelId { get; init; }
    public bool Installable { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required ModelCapabilities Capabilities { get; init; }
    public int RecommendedContextTokens { get; init; }
    public int RecommendedOutputTokens { get; init; }
    public IReadOnlyList<string> CompatibilityNotes { get; init; } = [];
    public IReadOnlyList<string> DoctorExpectations { get; init; } = [];
}

public sealed class LocalModelPresetListResponse
{
    public IReadOnlyList<LocalModelPresetDefinition> Items { get; init; } = [];
}

public sealed class LocalModelRuntimeDefaults
{
    public string Backend { get; init; } = "llama.cpp";
    public string Threads { get; init; } = "auto";
    public string GpuLayers { get; init; } = "auto";
    public int ContextSize { get; init; } = 4096;
    public bool EnableJinja { get; init; }
    public string? ChatTemplate { get; init; }
    public string? ChatTemplateFileName { get; init; }
    public string? MultimodalProjectorFileName { get; init; }
    public string? DraftModelFileName { get; init; }
    public string ReasoningMode { get; init; } = "auto";
    public int? ReasoningBudget { get; init; }
}

public static class LocalModelPackageFileRoles
{
    public const string Model = "model";
    public const string MultimodalProjector = "mmproj";
    public const string DraftModel = "draft";
}

public sealed class LocalModelPackageFileDefinition
{
    public required string Role { get; init; }
    public required string FileName { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ExpectedSha256 { get; init; }
    public bool Required { get; init; } = true;
    public bool InstallByDefault { get; init; } = true;
}

public sealed class LocalModelPackageDefinition
{
    public required string Id { get; init; }
    public required string PresetId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Provider { get; init; } = "embedded";
    public string ModelId { get; init; } = "";
    public string Family { get; init; } = "";
    public string Format { get; init; } = "gguf";
    public string Quantization { get; init; } = "";
    public string FileName { get; init; } = "model.gguf";
    public string? DownloadUrl { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ModelPageUrl { get; init; }
    public string? LicenseUrl { get; init; }
    public bool RequiresLicenseAcceptance { get; init; }
    public bool RequiresDownloadToken { get; init; }
    public bool Experimental { get; init; }
    public int MinRamGb { get; init; }
    public int RecommendedRamGb { get; init; }
    public int ContextWindow { get; init; } = 4096;
    public int MaxOutputTokens { get; init; } = 1024;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<LocalModelPackageFileDefinition> Files { get; init; } = [];
    public required ModelCapabilities Capabilities { get; init; }
    public LocalModelRuntimeDefaults Runtime { get; init; } = new();
}

public sealed class LocalModelInstallFileManifest
{
    public required string Role { get; init; }
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public string? Source { get; init; }
}

public sealed class LocalModelInstallManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string PackageId { get; init; }
    public required string PresetId { get; init; }
    public required string ModelId { get; init; }
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public string? Source { get; init; }
    public string? LicenseUrl { get; init; }
    public bool LicenseAccepted { get; init; }
    public DateTimeOffset InstalledAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<LocalModelInstallFileManifest> Files { get; init; } = [];
}

public sealed class LocalModelPackageFileStatus
{
    public required string Role { get; init; }
    public required string FileName { get; init; }
    public bool Required { get; init; }
    public bool Installed { get; init; }
    public bool Verified { get; init; }
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public string? Issue { get; init; }
}

public sealed class LocalModelPackageStatus
{
    public required string PackageId { get; init; }
    public required string PresetId { get; init; }
    public required string ModelId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool Installed { get; init; }
    public bool Verified { get; init; }
    public string? ModelPath { get; init; }
    public string? Sha256 { get; init; }
    public string? Issue { get; init; }
    public IReadOnlyList<LocalModelPackageFileStatus> Files { get; init; } = [];
}

public static class MaintenanceFindingSeverities
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

public static class MaintenanceFindingCategories
{
    public const string Storage = "storage";
    public const string PromptBudget = "prompt_budget";
    public const string Drift = "drift";
    public const string Reliability = "reliability";
}

public sealed class MaintenanceFinding
{
    public required string Id { get; init; }
    public string Category { get; init; } = MaintenanceFindingCategories.Storage;
    public string Severity { get; init; } = MaintenanceFindingSeverities.Info;
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string? Recommendation { get; init; }
    public string? RecommendedCommand { get; init; }
    public long NumericValue { get; init; }
}

public sealed class MaintenancePromptBudgetSnapshot
{
    public long RecentTurnsAnalyzed { get; init; }
    public long P50InputTokens { get; init; }
    public long P95InputTokens { get; init; }
    public long SystemPromptTokens { get; init; }
    public long SkillsTokens { get; init; }
    public long HistoryTokens { get; init; }
    public long ToolOutputsTokens { get; init; }
    public long UserInputTokens { get; init; }
    public int AgentsFileBytes { get; init; }
    public int SoulFileBytes { get; init; }
    public int LoadedSkillCount { get; init; }
}

public sealed class MaintenanceStorageSnapshot
{
    public long MemoryBytes { get; init; }
    public long ArchiveBytes { get; init; }
    public int OrphanedSessionMetadataEntries { get; init; }
    public int ModelEvaluationArtifacts { get; init; }
    public int PromptCacheTraceArtifacts { get; init; }
}

public sealed class MaintenanceDriftSnapshot
{
    public long ProviderRetries { get; init; }
    public long ProviderErrors { get; init; }
    public int DegradedAutomations { get; init; }
    public int QuarantinedAutomations { get; init; }
    public long RetentionFailures { get; init; }
    public int ChannelDriftCount { get; init; }
    public int PluginWarningCount { get; init; }
    public int PluginErrorCount { get; init; }
    public long PromptP95Delta { get; init; }
}

public sealed class MaintenanceReportResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string OverallStatus { get; init; } = SetupCheckStates.Pass;
    public MaintenanceStorageSnapshot Storage { get; init; } = new();
    public MaintenancePromptBudgetSnapshot PromptBudget { get; init; } = new();
    public MaintenanceDriftSnapshot Drift { get; init; } = new();
    public IReadOnlyList<MaintenanceFinding> Findings { get; init; } = [];
    public ReliabilitySnapshot Reliability { get; init; } = new();
}

public sealed class MaintenanceFixRequest
{
    public bool DryRun { get; init; } = true;
    public string Apply { get; init; } = "all";
}

public sealed class MaintenanceFixAction
{
    public required string Id { get; init; }
    public bool Applied { get; init; }
    public string Summary { get; init; } = "";
    public long NumericValue { get; init; }
}

public sealed class MaintenanceFixResponse
{
    public bool DryRun { get; init; } = true;
    public bool Success { get; init; }
    public IReadOnlyList<MaintenanceFixAction> Actions { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ReliabilitySnapshot Reliability { get; init; } = new();
}

public static class ReliabilityStates
{
    public const string Healthy = "healthy";
    public const string Watch = "watch";
    public const string ActionNeeded = "action_needed";
}

public sealed class ReliabilityFactor
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public int Weight { get; init; }
    public int Score { get; init; }
    public string Status { get; init; } = ReliabilityStates.Healthy;
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public sealed class ReliabilityRecommendation
{
    public required string Id { get; init; }
    public string Summary { get; init; } = "";
    public string Command { get; init; } = "";
    public int Priority { get; init; }
}

public sealed class ReliabilitySnapshot
{
    public int Score { get; init; }
    public string Status { get; init; } = ReliabilityStates.Healthy;
    public IReadOnlyList<ReliabilityFactor> Factors { get; init; } = [];
    public IReadOnlyList<ReliabilityRecommendation> Recommendations { get; init; } = [];
}

public sealed class MaintenanceHistorySnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public MaintenanceReportResponse Report { get; init; } = new();
}

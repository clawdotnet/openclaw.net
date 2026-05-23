using OpenClaw.Core.Models;

namespace OpenClaw.Testing;

public static class HarnessRegressionScenarioStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Warning = "warning";
    public const string NotApplicable = "not_applicable";
}

public static class HarnessRegressionCategory
{
    public const string Onboarding = "onboarding";
    public const string Security = "security";
    public const string Approvals = "approvals";
    public const string Memory = "memory";
    public const string Providers = "providers";
    public const string Tools = "tools";
    public const string Mcp = "mcp";
    public const string OpenAiCompat = "openai_compat";
    public const string Sessions = "sessions";
    public const string Harness = "harness";
    public const string Deployment = "deployment";
    public const string Docs = "docs";
}

public static class HarnessRegressionSeverity
{
    public const string Info = "info";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";
}

public sealed class HarnessRegressionOptions
{
    public string? ConfigPath { get; init; }
    public string? Category { get; init; }
    public bool Offline { get; init; } = true;
    public bool Strict { get; init; }
    public string? ProposalId { get; init; }
    public string? OutputPath { get; init; }
}

public sealed class HarnessRegressionContext
{
    public required string ConfigPath { get; init; }
    public bool ConfigPathExplicit { get; init; }
    public bool ConfigExists { get; init; }
    public GatewayConfig? Config { get; init; }
    public string? ConfigLoadError { get; init; }
    public bool Offline { get; init; }
    public bool Strict { get; init; }
    public string? ProposalId { get; init; }
    public required string TempWorkspacePath { get; init; }
    public TextWriter? Logger { get; init; }
}

public sealed class HarnessRegressionReport
{
    public string Id { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public string? ConfigPath { get; init; }
    public string? ProposalId { get; init; }
    public bool Offline { get; init; }
    public bool Strict { get; init; }
    public string OverallStatus { get; init; } = HarnessRegressionScenarioStatus.Passed;
    public IReadOnlyList<HarnessRegressionScenarioResult> Results { get; init; } = [];
    public HarnessRegressionSummary Summary { get; init; } = new();
    public IReadOnlyList<HarnessRegressionRecommendation> Recommendations { get; init; } = [];
}

public sealed class HarnessRegressionScenarioResult
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = HarnessRegressionCategory.Harness;
    public string Status { get; init; } = HarnessRegressionScenarioStatus.NotApplicable;
    public string Severity { get; init; } = HarnessRegressionSeverity.Info;
    public bool Required { get; init; } = true;
    public string Summary { get; init; } = "";
    public string? Details { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public string? EvidenceBundleId { get; init; }
    public string? RelatedContractId { get; init; }
}

public sealed class HarnessRegressionSummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Warning { get; init; }
    public int NotApplicable { get; init; }
}

public sealed class HarnessRegressionRecommendation
{
    public string Id { get; init; } = "";
    public string Severity { get; init; } = HarnessRegressionSeverity.Info;
    public string Summary { get; init; } = "";
    public string? Command { get; init; }
    public string? Details { get; init; }
}

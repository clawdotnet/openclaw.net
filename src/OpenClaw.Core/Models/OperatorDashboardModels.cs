namespace OpenClaw.Core.Models;

public sealed class OperatorDashboardSnapshot
{
    public required DashboardSessionSummary Sessions { get; init; }
    public required DashboardApprovalSummary Approvals { get; init; }
    public required DashboardMemorySummary Memory { get; init; }
    public required DashboardAutomationSummary Automations { get; init; }
    public required DashboardLearningSummary Learning { get; init; }
    public required DashboardDelegationSummary Delegation { get; init; }
    public required DashboardChannelSummary Channels { get; init; }
    public required DashboardPluginSummary Plugins { get; init; }
    public ReliabilitySnapshot Reliability { get; init; } = new();
}

public sealed class DashboardNamedMetric
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Count { get; init; }
}

public sealed class DashboardSessionSummary
{
    public int Active { get; init; }
    public int Persisted { get; init; }
    public int UniqueTotal { get; init; }
    public int Last24Hours { get; init; }
    public int Last7Days { get; init; }
    public int Starred { get; init; }
    public IReadOnlyList<DashboardNamedMetric> Channels { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> States { get; init; } = [];
}

public sealed class DashboardApprovalSummary
{
    public int Pending { get; init; }
    public int DecisionsLast24Hours { get; init; }
    public int ApprovedLast24Hours { get; init; }
    public int RejectedLast24Hours { get; init; }
    public IReadOnlyList<DashboardNamedMetric> PendingByTool { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> PendingByChannel { get; init; } = [];
}

public sealed class DashboardMemorySummary
{
    public int ListedNotes { get; init; }
    public bool CatalogTruncated { get; init; }
    public IReadOnlyList<DashboardNamedMetric> ByClass { get; init; } = [];
    public IReadOnlyList<MemoryNoteItem> RecentNotes { get; init; } = [];
    public IReadOnlyList<RuntimeEventEntry> RecentActivity { get; init; } = [];
}

public sealed class DashboardAutomationItem
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public bool IsDraft { get; init; }
    public string DeliveryChannelId { get; init; } = "cron";
    public string? TemplateKey { get; init; }
    public string Outcome { get; init; } = "never";
    public DateTimeOffset? LastRunAtUtc { get; init; }
}

public sealed class DashboardAutomationSummary
{
    public int Total { get; init; }
    public int Enabled { get; init; }
    public int Drafts { get; init; }
    public int NeverRun { get; init; }
    public int QueuedOrRunning { get; init; }
    public int Failing { get; init; }
    public IReadOnlyList<DashboardAutomationItem> Items { get; init; } = [];
    public IReadOnlyList<AutomationTemplate> Templates { get; init; } = [];
}

public sealed class DashboardLearningSummary
{
    public int Pending { get; init; }
    public int Approved { get; init; }
    public int Rejected { get; init; }
    public int RolledBack { get; init; }
    public IReadOnlyList<DashboardNamedMetric> PendingByKind { get; init; } = [];
    public IReadOnlyList<LearningProposal> Recent { get; init; } = [];
}

public sealed class DashboardDelegationSummary
{
    public bool Enabled { get; init; }
    public int MaxDepth { get; init; }
    public int Last24Hours { get; init; }
    public IReadOnlyList<string> Profiles { get; init; } = [];
}

public sealed class DashboardChannelSummary
{
    public int Ready { get; init; }
    public int Degraded { get; init; }
    public int Misconfigured { get; init; }
    public IReadOnlyList<ChannelReadinessDto> Items { get; init; } = [];
}

public sealed class DashboardPluginSummary
{
    public int Total { get; init; }
    public int Loaded { get; init; }
    public int Disabled { get; init; }
    public int Quarantined { get; init; }
    public int NeedsReview { get; init; }
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<DashboardNamedMetric> TrustLevels { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> CompatibilityStatuses { get; init; } = [];
}

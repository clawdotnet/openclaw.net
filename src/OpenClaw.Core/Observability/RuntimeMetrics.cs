using System.Threading;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Lightweight runtime metrics counters.
/// All operations are lock-free using <see cref="Interlocked"/>.
/// Exposed via the health endpoint for monitoring/alerting.
/// </summary>
public sealed class RuntimeMetrics
{
    // ── Counters (monotonically increasing) ───────────────────────────────
    private long _totalRequests;
    private long _totalLlmCalls;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalToolCalls;
    private long _totalToolFailures;
    private long _totalToolTimeouts;
    private long _totalLlmRetries;
    private long _totalLlmErrors;
    private long _approvalDecisionsRecorded;
    private long _approvalDecisionsRejected;
    private long _sessionEvictions;
    private long _sessionCapacityRejects;
    private long _estimatedTokenAdmissionRejects;
    private long _browserCancellationResets;
    private long _pluginBridgeAuthFailures;
    private long _pluginBridgeRestartAttempts;
    private long _pluginBridgeRestartFailures;
    private long _processStarts;
    private long _processCompletions;
    private long _processFailures;
    private long _processKills;
    private long _processTimeouts;
    private long _processHistoryEvictions;
    private long _sandboxLeaseCreates;
    private long _sandboxLeaseReuses;
    private long _sandboxLeaseRecoveries;
    private long _retentionSweepRuns;
    private long _retentionSweepFailures;
    private long _retentionArchivedItems;
    private long _retentionDeletedItems;
    private long _retentionSkippedProtectedSessions;
    private long _operatorAuditWriteFailures;
    private long _runtimeEventWriteFailures;
    private long _sessionCacheHits;
    private long _sessionCacheMisses;
    private long _memoryRecallSearches;
    private long _memoryRecallHits;
    private long _memoryCompactions;
    private long _promptCacheReads;
    private long _promptCacheWrites;
    private long _promptCacheWarmRuns;
    private long _promptCacheWarmSkips;
    private long _promptCacheWarmFailures;
    private long _pulseRuns;
    private long _pulseSkips;
    private long _pulseAlerts;
    private long _pulseOkSuppressed;
    private long _pulseErrors;

    // ── Gauges ────────────────────────────────────────────────────────────
    private int _activeSessions;
    private int _circuitBreakerState; // 0=Closed, 1=Open, 2=HalfOpen
    private int _retainedProcesses;
    private long _retentionLastRunAtUnixSeconds;
    private long _retentionLastRunDurationMs;
    private int _retentionLastRunSucceeded;
    private long _pulseLastRunDurationMs;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long TotalLlmCalls => Interlocked.Read(ref _totalLlmCalls);
    public long TotalInputTokens => Interlocked.Read(ref _totalInputTokens);
    public long TotalOutputTokens => Interlocked.Read(ref _totalOutputTokens);
    public long TotalToolCalls => Interlocked.Read(ref _totalToolCalls);
    public long TotalToolFailures => Interlocked.Read(ref _totalToolFailures);
    public long TotalToolTimeouts => Interlocked.Read(ref _totalToolTimeouts);
    public long TotalLlmRetries => Interlocked.Read(ref _totalLlmRetries);
    public long TotalLlmErrors => Interlocked.Read(ref _totalLlmErrors);
    public long ApprovalDecisionsRecorded => Interlocked.Read(ref _approvalDecisionsRecorded);
    public long ApprovalDecisionsRejected => Interlocked.Read(ref _approvalDecisionsRejected);
    public long SessionEvictions => Interlocked.Read(ref _sessionEvictions);
    public long SessionCapacityRejects => Interlocked.Read(ref _sessionCapacityRejects);
    public long EstimatedTokenAdmissionRejects => Interlocked.Read(ref _estimatedTokenAdmissionRejects);
    public long BrowserCancellationResets => Interlocked.Read(ref _browserCancellationResets);
    public long PluginBridgeAuthFailures => Interlocked.Read(ref _pluginBridgeAuthFailures);
    public long PluginBridgeRestartAttempts => Interlocked.Read(ref _pluginBridgeRestartAttempts);
    public long PluginBridgeRestartFailures => Interlocked.Read(ref _pluginBridgeRestartFailures);
    public long ProcessStarts => Interlocked.Read(ref _processStarts);
    public long ProcessCompletions => Interlocked.Read(ref _processCompletions);
    public long ProcessFailures => Interlocked.Read(ref _processFailures);
    public long ProcessKills => Interlocked.Read(ref _processKills);
    public long ProcessTimeouts => Interlocked.Read(ref _processTimeouts);
    public long ProcessHistoryEvictions => Interlocked.Read(ref _processHistoryEvictions);
    public long SandboxLeaseCreates => Interlocked.Read(ref _sandboxLeaseCreates);
    public long SandboxLeaseReuses => Interlocked.Read(ref _sandboxLeaseReuses);
    public long SandboxLeaseRecoveries => Interlocked.Read(ref _sandboxLeaseRecoveries);
    public long RetentionSweepRuns => Interlocked.Read(ref _retentionSweepRuns);
    public long RetentionSweepFailures => Interlocked.Read(ref _retentionSweepFailures);
    public long RetentionArchivedItems => Interlocked.Read(ref _retentionArchivedItems);
    public long RetentionDeletedItems => Interlocked.Read(ref _retentionDeletedItems);
    public long RetentionSkippedProtectedSessions => Interlocked.Read(ref _retentionSkippedProtectedSessions);
    public long OperatorAuditWriteFailures => Interlocked.Read(ref _operatorAuditWriteFailures);
    public long RuntimeEventWriteFailures => Interlocked.Read(ref _runtimeEventWriteFailures);
    public long SessionCacheHits => Interlocked.Read(ref _sessionCacheHits);
    public long SessionCacheMisses => Interlocked.Read(ref _sessionCacheMisses);
    public long MemoryRecallSearches => Interlocked.Read(ref _memoryRecallSearches);
    public long MemoryRecallHits => Interlocked.Read(ref _memoryRecallHits);
    public long MemoryCompactions => Interlocked.Read(ref _memoryCompactions);
    public long PromptCacheReads => Interlocked.Read(ref _promptCacheReads);
    public long PromptCacheWrites => Interlocked.Read(ref _promptCacheWrites);
    public long PromptCacheWarmRuns => Interlocked.Read(ref _promptCacheWarmRuns);
    public long PromptCacheWarmSkips => Interlocked.Read(ref _promptCacheWarmSkips);
    public long PromptCacheWarmFailures => Interlocked.Read(ref _promptCacheWarmFailures);
    public long PulseRuns => Interlocked.Read(ref _pulseRuns);
    public long PulseSkips => Interlocked.Read(ref _pulseSkips);
    public long PulseAlerts => Interlocked.Read(ref _pulseAlerts);
    public long PulseOkSuppressed => Interlocked.Read(ref _pulseOkSuppressed);
    public long PulseErrors => Interlocked.Read(ref _pulseErrors);
    public int ActiveSessions => Volatile.Read(ref _activeSessions);
    public int CircuitBreakerState => Volatile.Read(ref _circuitBreakerState);
    public int RetainedProcesses => Volatile.Read(ref _retainedProcesses);
    public long RetentionLastRunAtUnixSeconds => Interlocked.Read(ref _retentionLastRunAtUnixSeconds);
    public long RetentionLastRunDurationMs => Interlocked.Read(ref _retentionLastRunDurationMs);
    public int RetentionLastRunSucceeded => Volatile.Read(ref _retentionLastRunSucceeded);
    public long PulseLastRunDurationMs => Interlocked.Read(ref _pulseLastRunDurationMs);

    public void IncrementRequests() => Interlocked.Increment(ref _totalRequests);
    public void IncrementLlmCalls() => Interlocked.Increment(ref _totalLlmCalls);
    public void AddInputTokens(long n) => Interlocked.Add(ref _totalInputTokens, n);
    public void AddOutputTokens(long n) => Interlocked.Add(ref _totalOutputTokens, n);
    public void IncrementToolCalls() => Interlocked.Increment(ref _totalToolCalls);
    public void IncrementToolFailures() => Interlocked.Increment(ref _totalToolFailures);
    public void IncrementToolTimeouts() => Interlocked.Increment(ref _totalToolTimeouts);
    public void IncrementLlmRetries() => Interlocked.Increment(ref _totalLlmRetries);
    public void IncrementLlmErrors() => Interlocked.Increment(ref _totalLlmErrors);
    public void IncrementApprovalDecisionsRecorded() => Interlocked.Increment(ref _approvalDecisionsRecorded);
    public void IncrementApprovalDecisionsRejected() => Interlocked.Increment(ref _approvalDecisionsRejected);
    public void IncrementSessionEvictions() => Interlocked.Increment(ref _sessionEvictions);
    public void IncrementSessionCapacityRejects() => Interlocked.Increment(ref _sessionCapacityRejects);
    public void IncrementEstimatedTokenAdmissionRejects() => Interlocked.Increment(ref _estimatedTokenAdmissionRejects);
    public void IncrementBrowserCancellationResets() => Interlocked.Increment(ref _browserCancellationResets);
    public void IncrementPluginBridgeAuthFailures() => Interlocked.Increment(ref _pluginBridgeAuthFailures);
    public void IncrementPluginBridgeRestartAttempts() => Interlocked.Increment(ref _pluginBridgeRestartAttempts);
    public void IncrementPluginBridgeRestartFailures() => Interlocked.Increment(ref _pluginBridgeRestartFailures);
    public void IncrementProcessStarts() => Interlocked.Increment(ref _processStarts);
    public void IncrementProcessCompletions() => Interlocked.Increment(ref _processCompletions);
    public void IncrementProcessFailures() => Interlocked.Increment(ref _processFailures);
    public void IncrementProcessKills() => Interlocked.Increment(ref _processKills);
    public void IncrementProcessTimeouts() => Interlocked.Increment(ref _processTimeouts);
    public void IncrementProcessHistoryEvictions() => Interlocked.Increment(ref _processHistoryEvictions);
    public void IncrementSandboxLeaseCreates() => Interlocked.Increment(ref _sandboxLeaseCreates);
    public void IncrementSandboxLeaseReuses() => Interlocked.Increment(ref _sandboxLeaseReuses);
    public void IncrementSandboxLeaseRecoveries() => Interlocked.Increment(ref _sandboxLeaseRecoveries);
    public void IncrementRetentionSweepRuns() => Interlocked.Increment(ref _retentionSweepRuns);
    public void IncrementRetentionSweepFailures() => Interlocked.Increment(ref _retentionSweepFailures);
    public void AddRetentionArchivedItems(long n) => Interlocked.Add(ref _retentionArchivedItems, n);
    public void AddRetentionDeletedItems(long n) => Interlocked.Add(ref _retentionDeletedItems, n);
    public void AddRetentionSkippedProtectedSessions(long n) => Interlocked.Add(ref _retentionSkippedProtectedSessions, n);
    public void IncrementOperatorAuditWriteFailures() => Interlocked.Increment(ref _operatorAuditWriteFailures);
    public void IncrementRuntimeEventWriteFailures() => Interlocked.Increment(ref _runtimeEventWriteFailures);
    public void IncrementSessionCacheHits() => Interlocked.Increment(ref _sessionCacheHits);
    public void IncrementSessionCacheMisses() => Interlocked.Increment(ref _sessionCacheMisses);
    public void IncrementMemoryRecallSearches() => Interlocked.Increment(ref _memoryRecallSearches);
    public void AddMemoryRecallHits(long n) => Interlocked.Add(ref _memoryRecallHits, n);
    public void IncrementMemoryCompactions() => Interlocked.Increment(ref _memoryCompactions);
    public void AddPromptCacheReads(long n) => Interlocked.Add(ref _promptCacheReads, n);
    public void AddPromptCacheWrites(long n) => Interlocked.Add(ref _promptCacheWrites, n);
    public void IncrementPromptCacheWarmRuns() => Interlocked.Increment(ref _promptCacheWarmRuns);
    public void IncrementPromptCacheWarmSkips() => Interlocked.Increment(ref _promptCacheWarmSkips);
    public void IncrementPromptCacheWarmFailures() => Interlocked.Increment(ref _promptCacheWarmFailures);
    public void IncrementPulseRuns() => Interlocked.Increment(ref _pulseRuns);
    public void IncrementPulseSkips() => Interlocked.Increment(ref _pulseSkips);
    public void IncrementPulseAlerts() => Interlocked.Increment(ref _pulseAlerts);
    public void IncrementPulseOkSuppressed() => Interlocked.Increment(ref _pulseOkSuppressed);
    public void IncrementPulseErrors() => Interlocked.Increment(ref _pulseErrors);
    public void SetPulseLastRunDuration(long durationMs) => Interlocked.Exchange(ref _pulseLastRunDurationMs, Math.Max(0, durationMs));
    public void SetActiveSessions(int count) => Volatile.Write(ref _activeSessions, count);
    public void SetCircuitBreakerState(int state) => Volatile.Write(ref _circuitBreakerState, state);
    public void SetRetainedProcesses(int count) => Volatile.Write(ref _retainedProcesses, count);
    public void SetRetentionLastRun(DateTimeOffset runAtUtc, long durationMs, bool succeeded)
    {
        Interlocked.Exchange(ref _retentionLastRunAtUnixSeconds, runAtUtc.ToUnixTimeSeconds());
        Interlocked.Exchange(ref _retentionLastRunDurationMs, durationMs);
        Volatile.Write(ref _retentionLastRunSucceeded, succeeded ? 1 : 0);
    }

    /// <summary>
    /// Snapshot for JSON serialization. Uses a struct to avoid allocations in the AOT path.
    /// </summary>
    public MetricsSnapshot Snapshot() => new()
    {
        TotalRequests = TotalRequests,
        TotalLlmCalls = TotalLlmCalls,
        TotalInputTokens = TotalInputTokens,
        TotalOutputTokens = TotalOutputTokens,
        TotalToolCalls = TotalToolCalls,
        TotalToolFailures = TotalToolFailures,
        TotalToolTimeouts = TotalToolTimeouts,
        TotalLlmRetries = TotalLlmRetries,
        TotalLlmErrors = TotalLlmErrors,
        ApprovalDecisionsRecorded = ApprovalDecisionsRecorded,
        ApprovalDecisionsRejected = ApprovalDecisionsRejected,
        SessionEvictions = SessionEvictions,
        SessionCapacityRejects = SessionCapacityRejects,
        EstimatedTokenAdmissionRejects = EstimatedTokenAdmissionRejects,
        BrowserCancellationResets = BrowserCancellationResets,
        PluginBridgeAuthFailures = PluginBridgeAuthFailures,
        PluginBridgeRestartAttempts = PluginBridgeRestartAttempts,
        PluginBridgeRestartFailures = PluginBridgeRestartFailures,
        ProcessStarts = ProcessStarts,
        ProcessCompletions = ProcessCompletions,
        ProcessFailures = ProcessFailures,
        ProcessKills = ProcessKills,
        ProcessTimeouts = ProcessTimeouts,
        ProcessHistoryEvictions = ProcessHistoryEvictions,
        SandboxLeaseCreates = SandboxLeaseCreates,
        SandboxLeaseReuses = SandboxLeaseReuses,
        SandboxLeaseRecoveries = SandboxLeaseRecoveries,
        RetentionSweepRuns = RetentionSweepRuns,
        RetentionSweepFailures = RetentionSweepFailures,
        RetentionArchivedItems = RetentionArchivedItems,
        RetentionDeletedItems = RetentionDeletedItems,
        RetentionSkippedProtectedSessions = RetentionSkippedProtectedSessions,
        OperatorAuditWriteFailures = OperatorAuditWriteFailures,
        RuntimeEventWriteFailures = RuntimeEventWriteFailures,
        SessionCacheHits = SessionCacheHits,
        SessionCacheMisses = SessionCacheMisses,
        MemoryRecallSearches = MemoryRecallSearches,
        MemoryRecallHits = MemoryRecallHits,
        MemoryCompactions = MemoryCompactions,
        PromptCacheReads = PromptCacheReads,
        PromptCacheWrites = PromptCacheWrites,
        PromptCacheWarmRuns = PromptCacheWarmRuns,
        PromptCacheWarmSkips = PromptCacheWarmSkips,
        PromptCacheWarmFailures = PromptCacheWarmFailures,
        PulseRuns = PulseRuns,
        PulseSkips = PulseSkips,
        PulseAlerts = PulseAlerts,
        PulseOkSuppressed = PulseOkSuppressed,
        PulseErrors = PulseErrors,
        RetentionLastRunAtUnixSeconds = RetentionLastRunAtUnixSeconds,
        RetentionLastRunDurationMs = RetentionLastRunDurationMs,
        RetentionLastRunSucceeded = RetentionLastRunSucceeded,
        PulseLastRunDurationMs = PulseLastRunDurationMs,
        ActiveSessions = ActiveSessions,
        CircuitBreakerState = CircuitBreakerState,
        RetainedProcesses = RetainedProcesses
    };
}

public struct MetricsSnapshot
{
    public long TotalRequests { get; set; }
    public long TotalLlmCalls { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalToolCalls { get; set; }
    public long TotalToolFailures { get; set; }
    public long TotalToolTimeouts { get; set; }
    public long TotalLlmRetries { get; set; }
    public long TotalLlmErrors { get; set; }
    public long ApprovalDecisionsRecorded { get; set; }
    public long ApprovalDecisionsRejected { get; set; }
    public long SessionEvictions { get; set; }
    public long SessionCapacityRejects { get; set; }
    public long EstimatedTokenAdmissionRejects { get; set; }
    public long BrowserCancellationResets { get; set; }
    public long PluginBridgeAuthFailures { get; set; }
    public long PluginBridgeRestartAttempts { get; set; }
    public long PluginBridgeRestartFailures { get; set; }
    public long ProcessStarts { get; set; }
    public long ProcessCompletions { get; set; }
    public long ProcessFailures { get; set; }
    public long ProcessKills { get; set; }
    public long ProcessTimeouts { get; set; }
    public long ProcessHistoryEvictions { get; set; }
    public long SandboxLeaseCreates { get; set; }
    public long SandboxLeaseReuses { get; set; }
    public long SandboxLeaseRecoveries { get; set; }
    public long RetentionSweepRuns { get; set; }
    public long RetentionSweepFailures { get; set; }
    public long RetentionArchivedItems { get; set; }
    public long RetentionDeletedItems { get; set; }
    public long RetentionSkippedProtectedSessions { get; set; }
    public long OperatorAuditWriteFailures { get; set; }
    public long RuntimeEventWriteFailures { get; set; }
    public long SessionCacheHits { get; set; }
    public long SessionCacheMisses { get; set; }
    public long MemoryRecallSearches { get; set; }
    public long MemoryRecallHits { get; set; }
    public long MemoryCompactions { get; set; }
    public long PromptCacheReads { get; set; }
    public long PromptCacheWrites { get; set; }
    public long PromptCacheWarmRuns { get; set; }
    public long PromptCacheWarmSkips { get; set; }
    public long PromptCacheWarmFailures { get; set; }
    public long PulseRuns { get; set; }
    public long PulseSkips { get; set; }
    public long PulseAlerts { get; set; }
    public long PulseOkSuppressed { get; set; }
    public long PulseErrors { get; set; }
    public long RetentionLastRunAtUnixSeconds { get; set; }
    public long RetentionLastRunDurationMs { get; set; }
    public int RetentionLastRunSucceeded { get; set; }
    public long PulseLastRunDurationMs { get; set; }
    public int ActiveSessions { get; set; }
    public int CircuitBreakerState { get; set; }
    public int RetainedProcesses { get; set; }
}

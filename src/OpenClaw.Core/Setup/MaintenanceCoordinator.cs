using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;

namespace OpenClaw.Core.Setup;

public sealed class MaintenanceScanInputs
{
    public string? ConfigPath { get; init; }
    public SetupStatusResponse? SetupStatus { get; init; }
    public ModelSelectionDoctorResponse? ModelDoctor { get; init; }
    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns { get; init; } = [];
    public IReadOnlyList<ProviderRouteHealthSnapshot> ProviderRoutes { get; init; } = [];
    public IReadOnlyList<AutomationRunState> AutomationRunStates { get; init; } = [];
    public MetricsSnapshot? RuntimeMetrics { get; init; }
    public IReadOnlyList<SkillDefinition> LoadedSkills { get; init; } = [];
    public int ChannelDriftCount { get; init; }
    public int PluginWarningCount { get; init; }
    public int PluginErrorCount { get; init; }
}

public static class MaintenanceCoordinator
{
    private const int MaintenanceHistoryRetention = 20;
    private const int MaxModelEvaluationGroupsToKeep = 20;
    private const int PromptSizeWarningBytes = 12_000;
    private static readonly HashSet<string> ProtectedRetentionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "keep",
        "pinned",
        "retain",
        "retention-exempt"
    };

    public static async Task<MaintenanceReportResponse> ScanAsync(
        GatewayConfig config,
        MaintenanceScanInputs? inputs,
        CancellationToken ct)
    {
        inputs ??= new MaintenanceScanInputs();
        var memoryRoot = Path.GetFullPath(config.Memory.StoragePath);
        Directory.CreateDirectory(memoryRoot);
        var adminRoot = Path.Combine(memoryRoot, "admin");
        Directory.CreateDirectory(adminRoot);

        var modelDoctor = inputs.ModelDoctor ?? ModelDoctorEvaluator.Build(config);
        var automationStates = inputs.AutomationRunStates.Count > 0
            ? inputs.AutomationRunStates
            : await LoadAutomationRunStatesAsync(config, ct);
        var sessionIds = await LoadPersistedSessionIdsAsync(config, ct);
        var metadata = LoadSessionMetadata(memoryRoot);
        var orphanedMetadata = metadata.Count(item => !sessionIds.Contains(item.SessionId));
        var storage = new MaintenanceStorageSnapshot
        {
            MemoryBytes = GetDirectorySize(memoryRoot),
            ArchiveBytes = GetDirectorySize(PathFor(config.Memory.Retention.ArchivePath, memoryRoot)),
            OrphanedSessionMetadataEntries = orphanedMetadata,
            ModelEvaluationArtifacts = CountModelEvaluationArtifacts(adminRoot),
            PromptCacheTraceArtifacts = CountPromptCacheTraceArtifacts(config, memoryRoot)
        };

        var promptBudget = BuildPromptBudgetSnapshot(
            inputs.RecentTurns,
            inputs.LoadedSkills,
            ResolveWorkspacePromptPath(config.Tooling.WorkspaceRoot, "AGENTS.md"),
            ResolveWorkspacePromptPath(config.Tooling.WorkspaceRoot, "SOUL.md"));

        var previous = LoadPreviousSnapshot(memoryRoot);
        var drift = BuildDriftSnapshot(inputs, automationStates, promptBudget, previous);
        var findings = BuildFindings(inputs, modelDoctor, storage, promptBudget, drift);
        var partial = new MaintenanceReportResponse
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            OverallStatus = DetermineOverallStatus(findings),
            Storage = storage,
            PromptBudget = promptBudget,
            Drift = drift,
            Findings = findings
        };

        var reliability = ReliabilityScorer.Build(config, inputs, partial, modelDoctor, automationStates);
        var report = new MaintenanceReportResponse
        {
            GeneratedAtUtc = partial.GeneratedAtUtc,
            OverallStatus = partial.OverallStatus,
            Storage = partial.Storage,
            PromptBudget = partial.PromptBudget,
            Drift = partial.Drift,
            Findings = partial.Findings,
            Reliability = reliability
        };

        SaveSnapshot(memoryRoot, report);
        return report;
    }

    public static async Task<MaintenanceFixResponse> FixAsync(
        GatewayConfig config,
        MaintenanceFixRequest request,
        MaintenanceScanInputs? inputs,
        CancellationToken ct)
    {
        inputs ??= new MaintenanceScanInputs();
        var memoryRoot = Path.GetFullPath(config.Memory.StoragePath);
        Directory.CreateDirectory(memoryRoot);
        var actions = new List<MaintenanceFixAction>();
        var warnings = new List<string>();
        var applyMode = NormalizeApply(request.Apply);

        if (applyMode is "all" or "retention")
        {
            try
            {
                var retentionAction = await RunRetentionFixAsync(config, request.DryRun, ct);
                actions.Add(retentionAction);
            }
            catch (Exception ex)
            {
                warnings.Add($"Retention sweep could not run: {ex.Message}");
            }
        }

        if (applyMode is "all" or "metadata")
        {
            try
            {
                var metadataAction = await PruneOrphanedMetadataAsync(config, request.DryRun, ct);
                actions.Add(metadataAction);
            }
            catch (Exception ex)
            {
                warnings.Add($"Metadata pruning could not run: {ex.Message}");
            }
        }

        if (applyMode is "all" or "artifacts")
        {
            try
            {
                actions.Add(PruneModelEvaluationArtifacts(config, request.DryRun));
            }
            catch (Exception ex)
            {
                warnings.Add($"Model evaluation artifact pruning could not run: {ex.Message}");
            }

            try
            {
                var traceAction = PrunePromptCacheTraceArtifacts(config, request.DryRun);
                if (traceAction is not null)
                    actions.Add(traceAction);
            }
            catch (Exception ex)
            {
                warnings.Add($"Prompt cache trace artifact pruning could not run: {ex.Message}");
            }
        }

        var report = await ScanAsync(config, inputs, ct);
        return new MaintenanceFixResponse
        {
            DryRun = request.DryRun,
            Success = warnings.Count == 0,
            Actions = actions,
            Warnings = warnings,
            Reliability = report.Reliability
        };
    }

    private static string NormalizeApply(string? apply)
    {
        var normalized = (apply ?? "all").Trim().ToLowerInvariant();
        return normalized is "all" or "retention" or "metadata" or "artifacts"
            ? normalized
            : "all";
    }

    private static MaintenancePromptBudgetSnapshot BuildPromptBudgetSnapshot(
        IReadOnlyList<ProviderTurnUsageEntry> recentTurns,
        IReadOnlyList<SkillDefinition> loadedSkills,
        string agentsPath,
        string soulPath)
    {
        var sortedInputs = recentTurns
            .Select(static item => item.InputTokens)
            .OrderBy(static item => item)
            .ToArray();
        var median = Percentile(sortedInputs, 0.50);
        var p95 = Percentile(sortedInputs, 0.95);

        var components = recentTurns.Count == 0
            ? new InputTokenComponentEstimate()
            : new InputTokenComponentEstimate
            {
                SystemPrompt = (long)Math.Round(recentTurns.Average(static item => (double)item.EstimatedInputTokensByComponent.SystemPrompt)),
                Skills = (long)Math.Round(recentTurns.Average(static item => (double)item.EstimatedInputTokensByComponent.Skills)),
                History = (long)Math.Round(recentTurns.Average(static item => (double)item.EstimatedInputTokensByComponent.History)),
                ToolOutputs = (long)Math.Round(recentTurns.Average(static item => (double)item.EstimatedInputTokensByComponent.ToolOutputs)),
                UserInput = (long)Math.Round(recentTurns.Average(static item => (double)item.EstimatedInputTokensByComponent.UserInput))
            };

        return new MaintenancePromptBudgetSnapshot
        {
            RecentTurnsAnalyzed = recentTurns.Count,
            P50InputTokens = median,
            P95InputTokens = p95,
            SystemPromptTokens = components.SystemPrompt,
            SkillsTokens = components.Skills,
            HistoryTokens = components.History,
            ToolOutputsTokens = components.ToolOutputs,
            UserInputTokens = components.UserInput,
            AgentsFileBytes = File.Exists(agentsPath) ? (int)Math.Min(int.MaxValue, new FileInfo(agentsPath).Length) : 0,
            SoulFileBytes = File.Exists(soulPath) ? (int)Math.Min(int.MaxValue, new FileInfo(soulPath).Length) : 0,
            LoadedSkillCount = loadedSkills.Count
        };
    }

    private static MaintenanceDriftSnapshot BuildDriftSnapshot(
        MaintenanceScanInputs inputs,
        IReadOnlyList<AutomationRunState> automationStates,
        MaintenancePromptBudgetSnapshot promptBudget,
        MaintenanceHistorySnapshot? previous)
    {
        var providerRetries = inputs.ProviderRoutes.Sum(static item => item.Retries);
        var providerErrors = inputs.ProviderRoutes.Sum(static item => item.Errors);
        var degradedAutomations = automationStates.Count(static item =>
            string.Equals(item.HealthState, AutomationHealthStates.Degraded, StringComparison.OrdinalIgnoreCase));
        var quarantinedAutomations = automationStates.Count(static item =>
            string.Equals(item.HealthState, AutomationHealthStates.Quarantined, StringComparison.OrdinalIgnoreCase));
        var promptDelta = previous is null
            ? 0
            : promptBudget.P95InputTokens - previous.Report.PromptBudget.P95InputTokens;

        return new MaintenanceDriftSnapshot
        {
            ProviderRetries = providerRetries,
            ProviderErrors = providerErrors,
            DegradedAutomations = degradedAutomations,
            QuarantinedAutomations = quarantinedAutomations,
            RetentionFailures = inputs.RuntimeMetrics?.RetentionSweepFailures ?? 0,
            ChannelDriftCount = inputs.ChannelDriftCount,
            PluginWarningCount = inputs.PluginWarningCount,
            PluginErrorCount = inputs.PluginErrorCount,
            PromptP95Delta = promptDelta
        };
    }

    private static IReadOnlyList<MaintenanceFinding> BuildFindings(
        MaintenanceScanInputs inputs,
        ModelSelectionDoctorResponse modelDoctor,
        MaintenanceStorageSnapshot storage,
        MaintenancePromptBudgetSnapshot promptBudget,
        MaintenanceDriftSnapshot drift)
    {
        var findings = new List<MaintenanceFinding>();

        if (storage.OrphanedSessionMetadataEntries > 0)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "orphaned-session-metadata",
                Category = MaintenanceFindingCategories.Storage,
                Severity = MaintenanceFindingSeverities.Warn,
                Summary = $"{storage.OrphanedSessionMetadataEntries} orphaned session metadata entries can be pruned.",
                Recommendation = "Run a maintenance fix to remove stale admin metadata.",
                RecommendedCommand = BuildConfigAwareCommand("openclaw maintenance fix --dry-run", inputs.ConfigPath),
                NumericValue = storage.OrphanedSessionMetadataEntries
            });
        }

        if (storage.ModelEvaluationArtifacts > MaxModelEvaluationGroupsToKeep * 2)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "model-evaluation-artifacts",
                Category = MaintenanceFindingCategories.Storage,
                Severity = MaintenanceFindingSeverities.Warn,
                Summary = $"{storage.ModelEvaluationArtifacts} model evaluation artifacts are stored under memory/admin/model-evaluations.",
                Recommendation = "Prune older evaluation artifacts if you no longer need historical comparison runs.",
                RecommendedCommand = BuildConfigAwareCommand("openclaw maintenance fix --dry-run --apply artifacts", inputs.ConfigPath),
                NumericValue = storage.ModelEvaluationArtifacts
            });
        }

        if (storage.PromptCacheTraceArtifacts > 0)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "prompt-cache-traces",
                Category = MaintenanceFindingCategories.Storage,
                Severity = MaintenanceFindingSeverities.Info,
                Summary = $"{storage.PromptCacheTraceArtifacts} prompt-cache trace artifact(s) are consuming managed storage.",
                Recommendation = "Remove trace artifacts once prompt-cache investigation is complete.",
                RecommendedCommand = BuildConfigAwareCommand("openclaw maintenance fix --dry-run --apply artifacts", inputs.ConfigPath),
                NumericValue = storage.PromptCacheTraceArtifacts
            });
        }

        if (promptBudget.AgentsFileBytes > PromptSizeWarningBytes)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "agents-file-size",
                Category = MaintenanceFindingCategories.PromptBudget,
                Severity = MaintenanceFindingSeverities.Warn,
                Summary = $"AGENTS.md is {promptBudget.AgentsFileBytes:N0} bytes and may be inflating every turn.",
                Recommendation = "Trim AGENTS.md or move infrequently needed guidance into conditional skills.",
                RecommendedCommand = "openclaw maintenance scan",
                NumericValue = promptBudget.AgentsFileBytes
            });
        }

        if (promptBudget.SoulFileBytes > PromptSizeWarningBytes)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "soul-file-size",
                Category = MaintenanceFindingCategories.PromptBudget,
                Severity = MaintenanceFindingSeverities.Warn,
                Summary = $"SOUL.md is {promptBudget.SoulFileBytes:N0} bytes and may be reducing local-model headroom.",
                Recommendation = "Keep SOUL.md concise, especially for local models with tight context budgets.",
                RecommendedCommand = "openclaw maintenance scan",
                NumericValue = promptBudget.SoulFileBytes
            });
        }

        if (promptBudget.RecentTurnsAnalyzed > 0 && drift.PromptP95Delta > 4_000)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "prompt-growth",
                Category = MaintenanceFindingCategories.Drift,
                Severity = MaintenanceFindingSeverities.Warn,
                Summary = $"Recent prompt p95 grew by {drift.PromptP95Delta:N0} tokens since the last maintenance scan.",
                Recommendation = "Review prompt contributors before latency and local-model reliability degrade further.",
                RecommendedCommand = "openclaw models doctor",
                NumericValue = drift.PromptP95Delta
            });
        }

        if (drift.ProviderErrors > 0 || drift.ProviderRetries >= 5)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "provider-instability",
                Category = MaintenanceFindingCategories.Drift,
                Severity = drift.ProviderErrors > 0 ? MaintenanceFindingSeverities.Warn : MaintenanceFindingSeverities.Info,
                Summary = $"Provider routes reported {drift.ProviderErrors:N0} errors and {drift.ProviderRetries:N0} retries.",
                Recommendation = "Run model doctor and inspect provider health before changing model routing.",
                RecommendedCommand = "openclaw models doctor",
                NumericValue = drift.ProviderErrors + drift.ProviderRetries
            });
        }

        if (drift.QuarantinedAutomations > 0 || drift.DegradedAutomations > 0)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "automation-health",
                Category = MaintenanceFindingCategories.Reliability,
                Severity = drift.QuarantinedAutomations > 0 ? MaintenanceFindingSeverities.Fail : MaintenanceFindingSeverities.Warn,
                Summary = $"{drift.DegradedAutomations:N0} degraded and {drift.QuarantinedAutomations:N0} quarantined automations need attention.",
                Recommendation = "Review automation health and replay or clear quarantine after fixing the underlying issue.",
                RecommendedCommand = "openclaw maintenance scan",
                NumericValue = drift.DegradedAutomations + drift.QuarantinedAutomations
            });
        }

        if (drift.RetentionFailures > 0)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "retention-failures",
                Category = MaintenanceFindingCategories.Reliability,
                Severity = MaintenanceFindingSeverities.Fail,
                Summary = $"Retention reported {drift.RetentionFailures:N0} recent failures.",
                Recommendation = "Run a dry-run maintenance fix and inspect storage permissions before enabling more cleanup.",
                RecommendedCommand = BuildConfigAwareCommand("openclaw maintenance fix --dry-run --apply retention", inputs.ConfigPath),
                NumericValue = drift.RetentionFailures
            });
        }

        if (modelDoctor.Errors.Count > 0 || modelDoctor.Warnings.Count > 0)
        {
            findings.Add(new MaintenanceFinding
            {
                Id = "model-doctor",
                Category = MaintenanceFindingCategories.Reliability,
                Severity = modelDoctor.Errors.Count > 0 ? MaintenanceFindingSeverities.Fail : MaintenanceFindingSeverities.Warn,
                Summary = $"Model doctor reported {modelDoctor.Errors.Count} error(s) and {modelDoctor.Warnings.Count} warning(s).",
                Recommendation = "Resolve model doctor findings before relying on local-first routing.",
                RecommendedCommand = "openclaw models doctor",
                NumericValue = modelDoctor.Errors.Count + modelDoctor.Warnings.Count
            });
        }

        return findings
            .OrderByDescending(static item => SeverityRank(item.Severity))
            .ThenByDescending(static item => item.NumericValue)
            .ToArray();
    }

    private static string DetermineOverallStatus(IReadOnlyList<MaintenanceFinding> findings)
    {
        if (findings.Any(static item => string.Equals(item.Severity, MaintenanceFindingSeverities.Fail, StringComparison.OrdinalIgnoreCase)))
            return SetupCheckStates.Fail;
        if (findings.Any(static item => string.Equals(item.Severity, MaintenanceFindingSeverities.Warn, StringComparison.OrdinalIgnoreCase)))
            return SetupCheckStates.Warn;
        return SetupCheckStates.Pass;
    }

    private static int SeverityRank(string severity)
        => severity switch
        {
            MaintenanceFindingSeverities.Fail => 3,
            MaintenanceFindingSeverities.Warn => 2,
            _ => 1
        };

    private static string BuildConfigAwareCommand(string command, string? configPath)
        => string.IsNullOrWhiteSpace(configPath)
            ? command
            : $"{command} --config {GatewaySetupPaths.QuoteIfNeeded(configPath)}";

    private static long Percentile(long[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0;

        var index = (int)Math.Ceiling((sortedValues.Length - 1) * percentile);
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static async ValueTask<IReadOnlyList<AutomationRunState>> LoadAutomationRunStatesAsync(GatewayConfig config, CancellationToken ct)
    {
        var store = CreateAutomationStore(config);
        try
        {
            var automations = await store.ListAutomationsAsync(ct);
            var states = new List<AutomationRunState>(automations.Count);
            foreach (var automation in automations)
            {
                var state = await store.GetRunStateAsync(automation.Id, ct);
                if (state is not null)
                    states.Add(state);
            }

            return states;
        }
        finally
        {
            DisposeIfNeeded(store);
        }
    }

    private static async ValueTask<HashSet<string>> LoadPersistedSessionIdsAsync(GatewayConfig config, CancellationToken ct)
    {
        var store = CreateMemoryStore(config);
        try
        {
            if (store is not ISessionAdminStore adminStore)
                return [];

            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= 100; page++)
            {
                var result = await adminStore.ListSessionsAsync(page, 500, new SessionListQuery(), ct);
                foreach (var item in result.Items)
                    sessionIds.Add(item.Id);

                if (!result.HasMore)
                    break;
            }

            return sessionIds;
        }
        finally
        {
            await DisposeIfNeededAsync(store);
        }
    }

    private static List<SessionMetadataSnapshot> LoadSessionMetadata(string memoryRoot)
    {
        var path = Path.Combine(memoryRoot, "admin", "session-metadata.json");
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListSessionMetadataSnapshot) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveSessionMetadata(string memoryRoot, IReadOnlyList<SessionMetadataSnapshot> metadata)
    {
        var path = Path.Combine(memoryRoot, "admin", "session-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata.ToList(), CoreJsonContext.Default.ListSessionMetadataSnapshot));
    }

    private static MaintenanceHistorySnapshot? LoadPreviousSnapshot(string memoryRoot)
    {
        var path = Path.Combine(memoryRoot, "admin", "maintenance-history.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListMaintenanceHistorySnapshot) ?? [];
            return items.OrderByDescending(static item => item.GeneratedAtUtc).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSnapshot(string memoryRoot, MaintenanceReportResponse report)
    {
        var path = Path.Combine(memoryRoot, "admin", "maintenance-history.json");
        var items = new List<MaintenanceHistorySnapshot>();
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.ListMaintenanceHistorySnapshot) ?? [];
                items.AddRange(existing);
            }
            catch
            {
            }
        }

        items.Add(new MaintenanceHistorySnapshot { GeneratedAtUtc = report.GeneratedAtUtc, Report = report });
        items = items
            .OrderByDescending(static item => item.GeneratedAtUtc)
            .Take(MaintenanceHistoryRetention)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(items, CoreJsonContext.Default.ListMaintenanceHistorySnapshot));
    }

    private static long GetDirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return 0;

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                try { return new FileInfo(file).Length; }
                catch { return 0L; }
            })
            .Sum();
    }

    private static int CountModelEvaluationArtifacts(string adminRoot)
    {
        var path = Path.Combine(adminRoot, "model-evaluations");
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private static int CountPromptCacheTraceArtifacts(GatewayConfig config, string memoryRoot)
    {
        var path = ResolvePromptCacheTracePath(config, memoryRoot);
        if (!IsUnderRoot(path, memoryRoot))
            return 0;
        if (File.Exists(path))
            return 1;
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        return 0;
    }

    private static string ResolveWorkspacePromptPath(string? workspaceRoot, string fileName)
    {
        var workspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);
        return Path.Combine(workspace, fileName);
    }

    private static string ResolvePromptCacheTracePath(GatewayConfig config, string memoryRoot)
    {
        var raw = config.Llm.PromptCaching.TraceFilePath
            ?? config.Diagnostics.CacheTrace.FilePath
            ?? Path.Combine(memoryRoot, "logs", "cache-trace.jsonl");
        return PathFor(raw, memoryRoot);
    }

    private static string PathFor(string? configuredPath, string basePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(basePath);

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(basePath, configuredPath));
    }

    private static bool IsUnderRoot(string path, string root)
        => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
           || string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.Ordinal);

    private static async Task<MaintenanceFixAction> RunRetentionFixAsync(GatewayConfig config, bool dryRun, CancellationToken ct)
    {
        var store = CreateMemoryStore(config);
        try
        {
            if (store is not IMemoryRetentionStore retentionStore)
            {
                return new MaintenanceFixAction
                {
                    Id = "retention",
                    Applied = false,
                    Summary = "Current memory store does not support retention sweeps."
                };
            }

            var metadata = LoadSessionMetadata(Path.GetFullPath(config.Memory.StoragePath));
            var protectedSessions = metadata
                .Where(static item => item.Starred || item.Tags.Any(static tag => ProtectedRetentionTags.Contains(tag)))
                .Select(static item => item.SessionId)
                .ToHashSet(StringComparer.Ordinal);
            var request = new RetentionSweepRequest
            {
                DryRun = dryRun,
                NowUtc = DateTimeOffset.UtcNow,
                SessionExpiresBeforeUtc = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, config.Memory.Retention.SessionTtlDays)),
                BranchExpiresBeforeUtc = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, config.Memory.Retention.BranchTtlDays)),
                ArchivePath = PathFor(config.Memory.Retention.ArchivePath, config.Memory.StoragePath),
                ArchiveEnabled = config.Memory.Retention.ArchiveEnabled,
                ArchiveRetentionDays = Math.Max(1, config.Memory.Retention.ArchiveRetentionDays),
                MaxItems = Math.Max(10, config.Memory.Retention.MaxItemsPerSweep)
            };

            var result = await retentionStore.SweepAsync(request, protectedSessions, ct);
            return new MaintenanceFixAction
            {
                Id = "retention",
                Applied = !dryRun,
                Summary = dryRun
                    ? $"Retention dry-run found {result.TotalEligible} eligible item(s)."
                    : $"Retention archived {result.TotalArchived} and deleted {result.TotalDeleted} item(s).",
                NumericValue = result.TotalEligible
            };
        }
        finally
        {
            await DisposeIfNeededAsync(store);
        }
    }

    private static async Task<MaintenanceFixAction> PruneOrphanedMetadataAsync(GatewayConfig config, bool dryRun, CancellationToken ct)
    {
        var memoryRoot = Path.GetFullPath(config.Memory.StoragePath);
        var metadata = LoadSessionMetadata(memoryRoot);
        var sessionIds = await LoadPersistedSessionIdsAsync(config, ct);
        var orphaned = metadata.Where(item => !sessionIds.Contains(item.SessionId)).ToArray();

        if (!dryRun && orphaned.Length > 0)
        {
            var retained = metadata.Where(item => sessionIds.Contains(item.SessionId)).ToArray();
            SaveSessionMetadata(memoryRoot, retained);
        }

        return new MaintenanceFixAction
        {
            Id = "metadata",
            Applied = !dryRun && orphaned.Length > 0,
            Summary = orphaned.Length == 0
                ? "No orphaned session metadata entries were found."
                : dryRun
                    ? $"Would remove {orphaned.Length} orphaned session metadata entr{(orphaned.Length == 1 ? "y" : "ies")}."
                    : $"Removed {orphaned.Length} orphaned session metadata entr{(orphaned.Length == 1 ? "y" : "ies")}.",
            NumericValue = orphaned.Length
        };
    }

    private static MaintenanceFixAction PruneModelEvaluationArtifacts(GatewayConfig config, bool dryRun)
    {
        var path = Path.Combine(Path.GetFullPath(config.Memory.StoragePath), "admin", "model-evaluations");
        if (!Directory.Exists(path))
        {
            return new MaintenanceFixAction
            {
                Id = "model-evaluations",
                Applied = false,
                Summary = "No model evaluation artifacts were found."
            };
        }

        var groups = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .GroupBy(static file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Max(File.GetLastWriteTimeUtc))
            .ToList();
        var removable = groups.Skip(MaxModelEvaluationGroupsToKeep).SelectMany(static group => group).ToArray();

        if (!dryRun)
        {
            foreach (var file in removable)
            {
                try { File.Delete(file); } catch { }
            }
        }

        return new MaintenanceFixAction
        {
            Id = "model-evaluations",
            Applied = !dryRun && removable.Length > 0,
            Summary = removable.Length == 0
                ? "Model evaluation artifacts are already within the retention window."
                : dryRun
                    ? $"Would remove {removable.Length} old model evaluation artifact(s)."
                    : $"Removed {removable.Length} old model evaluation artifact(s).",
            NumericValue = removable.Length
        };
    }

    private static MaintenanceFixAction? PrunePromptCacheTraceArtifacts(GatewayConfig config, bool dryRun)
    {
        var memoryRoot = Path.GetFullPath(config.Memory.StoragePath);
        var path = ResolvePromptCacheTracePath(config, memoryRoot);
        if (!IsUnderRoot(path, memoryRoot))
            return null;

        var removable = File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
                : [];

        if (!dryRun)
        {
            foreach (var file in removable)
            {
                try { File.Delete(file); } catch { }
            }
        }

        return new MaintenanceFixAction
        {
            Id = "prompt-cache-traces",
            Applied = !dryRun && removable.Length > 0,
            Summary = removable.Length == 0
                ? "No managed prompt-cache trace artifacts were found."
                : dryRun
                    ? $"Would remove {removable.Length} managed prompt-cache trace artifact(s)."
                    : $"Removed {removable.Length} managed prompt-cache trace artifact(s).",
            NumericValue = removable.Length
        };
    }

    private static IMemoryStore CreateMemoryStore(GatewayConfig config)
    {
        if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var dbPath = Path.IsPathRooted(config.Memory.Sqlite.DbPath)
                ? config.Memory.Sqlite.DbPath
                : Path.Combine(Path.GetFullPath(config.Memory.StoragePath), config.Memory.Sqlite.DbPath);
            return new SqliteMemoryStore(Path.GetFullPath(dbPath), config.Memory.Sqlite.EnableFts, null, enableVectors: false);
        }

        return new FileMemoryStore(Path.GetFullPath(config.Memory.StoragePath));
    }

    private static IAutomationStore CreateAutomationStore(GatewayConfig config)
    {
        if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var dbPath = Path.IsPathRooted(config.Memory.Sqlite.DbPath)
                ? config.Memory.Sqlite.DbPath
                : Path.Combine(Path.GetFullPath(config.Memory.StoragePath), config.Memory.Sqlite.DbPath);
            return new SqliteFeatureStore(Path.GetFullPath(dbPath));
        }

        return new FileFeatureStore(Path.GetFullPath(config.Memory.StoragePath));
    }

    private static async ValueTask DisposeIfNeededAsync(object store)
    {
        switch (store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static void DisposeIfNeeded(object store)
    {
        if (store is IDisposable disposable)
            disposable.Dispose();
    }
}

public static class ReliabilityScorer
{
    public static ReliabilitySnapshot Build(
        GatewayConfig config,
        MaintenanceScanInputs? inputs,
        MaintenanceReportResponse? maintenanceReport,
        ModelSelectionDoctorResponse? modelDoctor = null,
        IReadOnlyList<AutomationRunState>? automationStates = null)
    {
        inputs ??= new MaintenanceScanInputs();
        modelDoctor ??= inputs.ModelDoctor ?? ModelDoctorEvaluator.Build(config);
        automationStates ??= inputs.AutomationRunStates;

        var factors = new List<ReliabilityFactor>();
        var recommendations = new List<ReliabilityRecommendation>();

        factors.Add(BuildReadinessFactor(config, inputs.SetupStatus, recommendations, inputs.ConfigPath));
        factors.Add(BuildModelFactor(modelDoctor, inputs.ProviderRoutes, recommendations));
        factors.Add(BuildAutomationFactor(automationStates, recommendations));
        factors.Add(BuildMaintenanceFactor(maintenanceReport, recommendations, inputs.ConfigPath));
        factors.Add(BuildOperatorFactor(inputs, recommendations));

        var score = factors.Sum(static item => item.Score);
        return new ReliabilitySnapshot
        {
            Score = score,
            Status = score >= 90
                ? ReliabilityStates.Healthy
                : score >= 70
                    ? ReliabilityStates.Watch
                    : ReliabilityStates.ActionNeeded,
            Factors = factors,
            Recommendations = recommendations
                .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.OrderByDescending(item => item.Priority).First())
                .OrderByDescending(static item => item.Priority)
                .Take(6)
                .ToArray()
        };
    }

    private static ReliabilityFactor BuildReadinessFactor(
        GatewayConfig config,
        SetupStatusResponse? setupStatus,
        List<ReliabilityRecommendation> recommendations,
        string? configPath)
    {
        var weight = 25;
        var findings = new List<string>();
        var score = weight;
        var publicBind = setupStatus?.PublicBind ?? IsNonLoopbackBind(config.BindAddress);
        var workspacePath = setupStatus?.WorkspacePath ?? config.Tooling.WorkspaceRoot;
        var workspaceExists = setupStatus?.WorkspaceExists ?? (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath));
        var providerConfigured = setupStatus?.ProviderConfigured ?? ProviderSmokeProbe.IsProviderConfigured(config.Llm);

        if (publicBind && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            score -= 10;
            findings.Add("Public bind is missing an auth token.");
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "verify-provider",
                Summary = "Re-run setup verification with provider requirements.",
                Command = BuildConfigAwareCommand("openclaw setup verify --require-provider", configPath),
                Priority = 100
            });
        }

        if (!workspaceExists)
        {
            score -= 8;
            findings.Add("Configured workspace path does not exist.");
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "verify-workspace",
                Summary = "Confirm the configured workspace and setup posture.",
                Command = BuildConfigAwareCommand("openclaw setup status", configPath),
                Priority = 85
            });
        }

        if (!providerConfigured)
        {
            score -= 7;
            findings.Add("No provider is configured.");
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "require-provider",
                Summary = "Finish provider configuration before relying on the gateway.",
                Command = BuildConfigAwareCommand("openclaw setup verify --require-provider", configPath),
                Priority = 95
            });
        }

        return BuildFactor("readiness", "Readiness & posture", weight, score, findings);
    }

    private static ReliabilityFactor BuildModelFactor(
        ModelSelectionDoctorResponse modelDoctor,
        IReadOnlyList<ProviderRouteHealthSnapshot> routes,
        List<ReliabilityRecommendation> recommendations)
    {
        var weight = 25;
        var findings = new List<string>();
        var score = weight;

        if (modelDoctor.Errors.Count > 0)
        {
            score -= Math.Min(15, modelDoctor.Errors.Count * 5);
            findings.Add($"{modelDoctor.Errors.Count} model-doctor error(s) are unresolved.");
        }

        if (modelDoctor.Warnings.Count > 0)
        {
            score -= Math.Min(8, modelDoctor.Warnings.Count * 2);
            findings.Add($"{modelDoctor.Warnings.Count} model-doctor warning(s) are active.");
        }

        var compatibilityCount = modelDoctor.Profiles.Count(static item => item.UsesCompatibilityTransport);
        if (compatibilityCount > 0)
        {
            score -= Math.Min(6, compatibilityCount * 2);
            findings.Add($"{compatibilityCount} profile(s) still rely on compatibility transport.");
        }

        var routeErrors = routes.Sum(static item => item.Errors);
        if (routeErrors > 0)
        {
            score -= Math.Min(6, (int)routeErrors);
            findings.Add($"{routeErrors} recent provider route error(s) were recorded.");
        }

        if (findings.Count > 0)
        {
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "model-doctor",
                Summary = "Resolve model and provider routing issues.",
                Command = "openclaw models doctor",
                Priority = 90
            });
        }

        return BuildFactor("model_health", "Model & provider health", weight, score, findings);
    }

    private static ReliabilityFactor BuildAutomationFactor(
        IReadOnlyList<AutomationRunState> automationStates,
        List<ReliabilityRecommendation> recommendations)
    {
        var weight = 20;
        var findings = new List<string>();
        var score = weight;
        var degraded = automationStates.Count(static item => string.Equals(item.HealthState, AutomationHealthStates.Degraded, StringComparison.OrdinalIgnoreCase));
        var quarantined = automationStates.Count(static item => string.Equals(item.HealthState, AutomationHealthStates.Quarantined, StringComparison.OrdinalIgnoreCase));

        if (degraded > 0)
        {
            score -= Math.Min(8, degraded * 2);
            findings.Add($"{degraded} automation(s) are degraded.");
        }

        if (quarantined > 0)
        {
            score -= Math.Min(12, quarantined * 4);
            findings.Add($"{quarantined} automation(s) are quarantined.");
        }

        if (findings.Count > 0)
        {
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "maintenance-scan",
                Summary = "Review automation health before trusting scheduled runs.",
                Command = "openclaw maintenance scan",
                Priority = quarantined > 0 ? 88 : 70
            });
        }

        return BuildFactor("automation_health", "Automation health", weight, score, findings);
    }

    private static ReliabilityFactor BuildMaintenanceFactor(
        MaintenanceReportResponse? maintenanceReport,
        List<ReliabilityRecommendation> recommendations,
        string? configPath)
    {
        var weight = 20;
        var findings = new List<string>();
        var score = weight;

        if (maintenanceReport is not null)
        {
            var failCount = maintenanceReport.Findings.Count(static item => string.Equals(item.Severity, MaintenanceFindingSeverities.Fail, StringComparison.OrdinalIgnoreCase));
            var warnCount = maintenanceReport.Findings.Count(static item => string.Equals(item.Severity, MaintenanceFindingSeverities.Warn, StringComparison.OrdinalIgnoreCase));
            score -= Math.Min(12, failCount * 4);
            score -= Math.Min(8, warnCount * 2);

            if (maintenanceReport.Storage.OrphanedSessionMetadataEntries > 0)
                findings.Add($"{maintenanceReport.Storage.OrphanedSessionMetadataEntries} orphaned metadata entries remain.");
            if (maintenanceReport.Drift.RetentionFailures > 0)
                findings.Add($"{maintenanceReport.Drift.RetentionFailures} retention failure(s) were observed.");
            if (maintenanceReport.Drift.PromptP95Delta > 0)
                findings.Add($"Prompt p95 drift is +{maintenanceReport.Drift.PromptP95Delta:N0} tokens.");
        }

        if (findings.Count > 0)
        {
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "maintenance-fix",
                Summary = "Run a dry-run maintenance fix and apply only the safe cleanup you want.",
                Command = BuildConfigAwareCommand("openclaw maintenance fix --dry-run", configPath),
                Priority = 80
            });
        }

        return BuildFactor("maintenance_drift", "Maintenance & drift", weight, score, findings);
    }

    private static ReliabilityFactor BuildOperatorFactor(
        MaintenanceScanInputs inputs,
        List<ReliabilityRecommendation> recommendations)
    {
        var weight = 10;
        var findings = new List<string>();
        var score = weight;

        if (inputs.PluginErrorCount > 0)
        {
            score -= Math.Min(6, inputs.PluginErrorCount * 2);
            findings.Add($"{inputs.PluginErrorCount} plugin error(s) need operator review.");
        }

        if (inputs.PluginWarningCount > 0)
        {
            score -= Math.Min(4, inputs.PluginWarningCount);
            findings.Add($"{inputs.PluginWarningCount} plugin warning(s) are active.");
        }

        if (inputs.ChannelDriftCount > 0)
            findings.Add($"{inputs.ChannelDriftCount} channel(s) are not fully ready.");

        if (findings.Count > 0)
        {
            recommendations.Add(new ReliabilityRecommendation
            {
                Id = "setup-status",
                Summary = "Review setup posture and runtime hygiene before widening scope.",
                Command = BuildConfigAwareCommand("openclaw setup status", inputs.ConfigPath),
                Priority = 60
            });
        }

        return BuildFactor("operator_hygiene", "Operator & runtime hygiene", weight, score, findings);
    }

    private static ReliabilityFactor BuildFactor(string id, string label, int weight, int score, IReadOnlyList<string> findings)
    {
        var boundedScore = Math.Clamp(score, 0, weight);
        var status = boundedScore >= (int)Math.Ceiling(weight * 0.9)
            ? ReliabilityStates.Healthy
            : boundedScore >= (int)Math.Ceiling(weight * 0.6)
                ? ReliabilityStates.Watch
                : ReliabilityStates.ActionNeeded;
        return new ReliabilityFactor
        {
            Id = id,
            Label = label,
            Weight = weight,
            Score = boundedScore,
            Status = status,
            Findings = findings
        };
    }

    private static string BuildConfigAwareCommand(string command, string? configPath)
        => string.IsNullOrWhiteSpace(configPath)
            ? command
            : $"{command} --config {GatewaySetupPaths.QuoteIfNeeded(configPath)}";

    private static bool IsNonLoopbackBind(string bindAddress)
    {
        var normalized = (bindAddress ?? string.Empty).Trim();
        return normalized.Length > 0 &&
               !string.Equals(normalized, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalized, "::1", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalized, "[::1]", StringComparison.OrdinalIgnoreCase);
    }
}

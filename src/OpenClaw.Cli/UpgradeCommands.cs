using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;

namespace OpenClaw.Cli;

internal static class UpgradeCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        var rest = args[1..];
        return subcommand switch
        {
            "check" => await RunCheckAsync(rest, output, error, currentDirectory),
            _ => UnknownSubcommand(subcommand, output, error)
        };
    }

    private static async Task<int> RunCheckAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var configPath = ResolveConfigPath(parsed);
        GatewayConfig config;
        try
        {
            config = GatewayConfigFile.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }

        var workspacePath = ResolveWorkspacePath(config);
        var offline = parsed.HasFlag("--offline");
        var verification = await EvaluateVerificationAsync(config, workspacePath, offline);
        var plugins = EvaluatePlugins(config, workspacePath);
        var skills = EvaluateSkills(config, workspacePath);
        var migrationImpact = EvaluateMigrationImpact(configPath, config);

        var overall = AggregateStatus(
            verification.Status,
            plugins.Status,
            skills.Status,
            migrationImpact.Status);

        output.WriteLine("OpenClaw upgrade preflight");
        output.WriteLine($"Config: {configPath}");
        output.WriteLine($"Workspace: {workspacePath ?? "not configured"}");
        output.WriteLine($"Overall result: {overall}");
        output.WriteLine();
        output.WriteLine("Checks:");
        WriteCheck(output, verification);
        WriteCheck(output, plugins);
        WriteCheck(output, skills);
        WriteCheck(output, migrationImpact);

        var nextActions = verification.NextActions
            .Concat(plugins.NextActions)
            .Concat(skills.NextActions)
            .Concat(migrationImpact.NextActions)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (nextActions.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Recommended next actions:");
            foreach (var action in nextActions)
                output.WriteLine($"- {action}");
        }

        if (string.Equals(overall, SetupCheckStates.Fail, StringComparison.Ordinal))
        {
            output.WriteLine();
            output.WriteLine("Upgrade preflight failed. Resolve the blocking issues before upgrading.");
            return 1;
        }

        return 0;
    }

    private static async Task<UpgradeCheckSummary> EvaluateVerificationAsync(GatewayConfig config, string? workspacePath, bool offline)
    {
        var localState = LocalSetupStateLoader.Load(config.Memory.StoragePath);
        var verification = await SetupVerificationService.VerifyAsync(new SetupVerificationRequest
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            Policy = localState.Policy,
            OperatorAccountCount = localState.OperatorAccountCount,
            Offline = offline,
            RequireProvider = !offline,
            WorkspacePath = workspacePath,
            ModelDoctor = ModelDoctorEvaluator.Build(config)
        }, CancellationToken.None);

        var detail = new List<string>();
        foreach (var check in verification.Checks.Where(static item => !string.Equals(item.Status, SetupCheckStates.Pass, StringComparison.Ordinal)))
            detail.Add($"[{check.Status}] {check.Label}: {check.Summary}");

        var summary = $"Setup verification reported {verification.Checks.Count} checks with overall status '{verification.OverallStatus}'.";
        if (offline)
            summary += " Provider smoke was skipped because offline mode is enabled.";

        return new UpgradeCheckSummary(
            "Config and provider readiness",
            verification.OverallStatus,
            summary,
            detail,
            verification.RecommendedNextActions);
    }

    private static UpgradeCheckSummary EvaluatePlugins(GatewayConfig config, string? workspacePath)
    {
        var result = PluginDiscovery.DiscoverWithDiagnostics(config.Plugins, workspacePath);
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;

        foreach (var report in result.Reports)
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                var severity = NormalizeSeverity(diagnostic.Severity);
                status = AggregateStatus(status, severity);
                details.Add($"[{severity}] {report.PluginId}: {diagnostic.Message}");
            }
        }

        foreach (var plugin in result.Plugins)
        {
            var manifestPath = Path.Combine(plugin.RootPath, "openclaw.plugin.json");
            var packageJsonPath = Path.Combine(plugin.RootPath, "package.json");
            if (File.Exists(manifestPath) || File.Exists(packageJsonPath))
            {
                var inspection = PluginCommands.InspectCandidate(plugin.RootPath, plugin.RootPath, sourceIsNpm: false);
                if (!inspection.Success)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {plugin.Manifest.Id}: {inspection.ErrorMessage}");
                    continue;
                }

                if (!inspection.CanInstall)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {inspection.PluginId}: compatibility inspection reported blocking errors.");
                    foreach (var diagnostic in inspection.Diagnostics.Where(static item => string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase)))
                        details.Add($"[{SetupCheckStates.Fail}] {inspection.PluginId}: [{diagnostic.Code}] {diagnostic.Message}");
                    nextActions.Add($"Review plugin '{inspection.PluginId}' and resolve its compatibility errors before upgrading.");
                    continue;
                }

                if (inspection.WarningCount > 0 || inspection.Warnings.Count > 0 || string.Equals(inspection.TrustLevel, "untrusted", StringComparison.Ordinal))
                {
                    status = AggregateStatus(status, SetupCheckStates.Warn);
                    details.Add($"[{SetupCheckStates.Warn}] {inspection.PluginId}: trust={inspection.TrustLevel}, warnings={inspection.WarningCount + inspection.Warnings.Count}.");
                }
            }
            else
            {
                status = AggregateStatus(status, SetupCheckStates.Warn);
                details.Add($"[{SetupCheckStates.Warn}] {plugin.Manifest.Id}: entry-only plugin without a structured manifest-backed compatibility surface.");
            }
        }

        if (details.Count == 0)
            details.Add("No blocking plugin compatibility issues were detected.");

        if (string.Equals(status, SetupCheckStates.Warn, StringComparison.Ordinal))
            nextActions.Add("Review entry-only or warning-level plugins before upgrading the gateway/runtime.");

        var summary = result.Plugins.Count == 0 && result.Reports.Count == 0
            ? "No workspace, global, or configured plugins were discovered."
            : $"Inspected {result.Plugins.Count} plugin(s) with {result.Reports.Count} discovery diagnostic report(s).";

        return new UpgradeCheckSummary("Plugin compatibility", status, summary, details, nextActions);
    }

    private static UpgradeCheckSummary EvaluateSkills(GatewayConfig config, string? workspacePath)
    {
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;
        var totalInspected = 0;

        foreach (var root in EnumerateSkillRoots(config, workspacePath))
        {
            foreach (var inspection in SkillInspector.InspectInstalledRoot(root.Path, root.Source))
            {
                totalInspected++;
                if (!inspection.Success || inspection.Definition is null)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {root.Path}: {inspection.ErrorMessage ?? "Failed to inspect skill."}");
                    continue;
                }

                var requirementIssues = EvaluateSkillRequirements(config, inspection.Definition);
                if (requirementIssues.Count > 0)
                {
                    status = AggregateStatus(status, SetupCheckStates.Warn);
                    foreach (var issue in requirementIssues)
                        details.Add($"[{SetupCheckStates.Warn}] {inspection.Definition.Name}: {issue}");
                }
            }
        }

        if (details.Count == 0)
            details.Add("No blocking skill compatibility issues were detected.");

        if (string.Equals(status, SetupCheckStates.Warn, StringComparison.Ordinal))
            nextActions.Add("Resolve missing skill requirements before upgrading so post-upgrade validation stays green.");

        var summary = totalInspected == 0
            ? "No managed, workspace, or extra skill directories were discovered."
            : $"Inspected {totalInspected} skill package(s) across the configured roots.";

        return new UpgradeCheckSummary("Skill compatibility", status, summary, details, nextActions);
    }

    private static UpgradeCheckSummary EvaluateMigrationImpact(string configPath, GatewayConfig config)
    {
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;

        if (configPath.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}OpenClaw.Gateway{Path.DirectorySeparatorChar}appsettings", StringComparison.OrdinalIgnoreCase))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Config points at a checked-in gateway appsettings file. Upgrades are safer when you use an external config generated by setup.");
            nextActions.Add("Move to an external config file generated by 'openclaw setup' before upgrading.");
        }

        if (!string.Equals(RuntimeModeResolver.Normalize(config.Runtime.Mode), "auto", StringComparison.Ordinal))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Runtime mode is pinned to '{RuntimeModeResolver.Normalize(config.Runtime.Mode)}', which narrows the supported upgrade lane.");
        }

        if (!string.Equals(RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator), RuntimeOrchestrator.Native, StringComparison.Ordinal))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Runtime orchestrator is '{RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator)}' instead of the default native path.");
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Dynamic native plugins are enabled, which increases upgrade compatibility risk.");
        }

        if (config.Plugins.Mcp.Enabled)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] MCP plugin bridges are enabled. Re-check bridge compatibility after the upgrade.");
        }

        if (config.Plugins.Load.Paths.Length > 0)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Custom plugin load paths are configured: {string.Join(", ", config.Plugins.Load.Paths)}");
        }

        if (config.Skills.Load.ExtraDirs.Length > 0)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Extra skill directories are configured: {string.Join(", ", config.Skills.Load.ExtraDirs)}");
        }

        if (details.Count == 0)
            details.Add("Config uses the default native runtime/orchestrator lane with no extra compatibility-surface overrides.");
        else
            nextActions.Add("Treat this install as an elevated-risk upgrade lane and rerun setup verification immediately after upgrading.");

        return new UpgradeCheckSummary(
            "Migration impact",
            status,
            "Estimates how much the current install relies on non-default or expanded compatibility surfaces.",
            details,
            nextActions);
    }

    private static IEnumerable<(string Path, SkillSource Source)> EnumerateSkillRoots(GatewayConfig config, string? workspacePath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (config.Skills.Load.IncludeManaged)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var managed = Path.Combine(home, ".openclaw", "skills");
            if (seen.Add(managed))
                yield return (managed, SkillSource.Managed);
        }

        if (config.Skills.Load.IncludeWorkspace && !string.IsNullOrWhiteSpace(workspacePath))
        {
            var workspaceSkills = Path.Combine(workspacePath, "skills");
            if (seen.Add(workspaceSkills))
                yield return (workspaceSkills, SkillSource.Workspace);
        }

        foreach (var extraDir in config.Skills.Load.ExtraDirs)
        {
            var resolved = ResolveConfiguredPath(extraDir);
            if (!string.IsNullOrWhiteSpace(resolved) && seen.Add(resolved))
                yield return (resolved, SkillSource.Extra);
        }
    }

    private static IReadOnlyList<string> EvaluateSkillRequirements(GatewayConfig config, SkillDefinition definition)
    {
        var issues = new List<string>();
        config.Skills.Entries.TryGetValue(definition.Metadata.SkillKey ?? definition.Name, out var entry);
        if (entry is null)
            config.Skills.Entries.TryGetValue(definition.Name, out entry);

        foreach (var env in definition.Metadata.RequireEnv)
        {
            var fromEntry = entry?.Env.ContainsKey(env) == true;
            var fromApiKey = string.Equals(definition.Metadata.PrimaryEnv, env, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry?.ApiKey);
            if (!fromEntry && !fromApiKey && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)))
                issues.Add($"required env '{env}' is not set.");
        }

        foreach (var binary in definition.Metadata.RequireBins)
        {
            if (!IsBinaryAvailable(binary))
                issues.Add($"required binary '{binary}' is not available on PATH.");
        }

        if (definition.Metadata.RequireAnyBins.Length > 0 &&
            !definition.Metadata.RequireAnyBins.Any(IsBinaryAvailable))
        {
            issues.Add($"none of the required fallback binaries are available on PATH: {string.Join(", ", definition.Metadata.RequireAnyBins)}");
        }

        foreach (var configKey in definition.Metadata.RequireConfig)
        {
            if (entry?.Config.TryGetValue(configKey, out var value) != true || string.IsNullOrWhiteSpace(value))
                issues.Add($"required skills.entries config '{configKey}' is not configured.");
        }

        return issues;
    }

    private static bool IsBinaryAvailable(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
            return false;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { binaryName, binaryName + ".exe", binaryName + ".cmd", binaryName + ".bat" }
            : new[] { binaryName };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                    return true;
            }
        }

        return false;
    }

    private static void WriteCheck(TextWriter output, UpgradeCheckSummary check)
    {
        output.WriteLine($"- [{check.Status}] {check.Label}: {check.Summary}");
        foreach (var detail in check.Details)
            output.WriteLine($"  {detail}");
    }

    private static string AggregateStatus(params string[] statuses)
    {
        var normalized = statuses.Select(NormalizeStatus).ToArray();
        if (normalized.Contains(SetupCheckStates.Fail, StringComparer.Ordinal))
            return SetupCheckStates.Fail;
        if (normalized.Contains(SetupCheckStates.Warn, StringComparer.Ordinal) || normalized.Contains(SetupCheckStates.Skip, StringComparer.Ordinal))
            return SetupCheckStates.Warn;
        return SetupCheckStates.Pass;
    }

    private static string NormalizeSeverity(string severity)
        => string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase)
            ? SetupCheckStates.Fail
            : SetupCheckStates.Warn;

    private static string NormalizeStatus(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            var value when value == SetupCheckStates.Fail => SetupCheckStates.Fail,
            var value when value == SetupCheckStates.Warn => SetupCheckStates.Warn,
            var value when value == SetupCheckStates.Skip => SetupCheckStates.Skip,
            _ => SetupCheckStates.Pass
        };

    private static string ResolveConfigPath(CliArgs parsed)
        => Path.GetFullPath(GatewaySetupPaths.ExpandPath(parsed.GetOption("--config") ?? GatewaySetupPaths.DefaultConfigPath));

    private static string? ResolveWorkspacePath(GatewayConfig config)
    {
        var workspace = config.Tooling.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspace))
            return null;

        var resolved = SecretResolver.Resolve(workspace) ?? workspace;
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }

    private static string? ResolveConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(path);
        expanded = GatewaySetupPaths.ExpandPath(expanded);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(expanded);
    }

    private static int UnknownSubcommand(string subcommand, TextWriter output, TextWriter error)
    {
        error.WriteLine($"Unknown upgrade subcommand: {subcommand}");
        PrintHelp(output);
        return 2;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw upgrade

            Usage:
              openclaw upgrade check [--config <path>] [--offline]

            Notes:
              - Runs preflight checks before an upgrade.
              - Combines setup verification, provider readiness, plugin compatibility,
                skill compatibility, and migration-risk heuristics into one report.
              - Returns a non-zero exit code when blocking issues are found.
            """);
    }

    private sealed record UpgradeCheckSummary(
        string Label,
        string Status,
        string Summary,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> NextActions);
}

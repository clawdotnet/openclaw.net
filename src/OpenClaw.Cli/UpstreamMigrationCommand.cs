using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Compatibility;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Cli;

internal static class UpstreamMigrationCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var sourcePath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--source") ?? currentDirectory));
        var targetConfigPath = parsed.GetOption("--target-config");
        if (string.IsNullOrWhiteSpace(targetConfigPath))
        {
            error.WriteLine("--target-config is required.");
            return 2;
        }

        if (!Directory.Exists(sourcePath))
        {
            error.WriteLine($"Source path not found: {sourcePath}");
            return 2;
        }

        var resolvedTargetConfigPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(targetConfigPath));
        var reportPath = parsed.GetOption("--report");
        var apply = parsed.HasFlag("--apply");

        var plan = BuildPlan(sourcePath, resolvedTargetConfigPath);
        var report = await ExecutePlanAsync(plan, apply, CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var resolvedReportPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(reportPath));
            var directory = Path.GetDirectoryName(resolvedReportPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                resolvedReportPath,
                JsonSerializer.Serialize(report, CoreJsonContext.Default.UpstreamMigrationReport),
                CancellationToken.None);
            output.WriteLine($"Wrote migration report: {resolvedReportPath}");
        }
        else
        {
            output.WriteLine(JsonSerializer.Serialize(report, CoreJsonContext.Default.UpstreamMigrationReport));
        }

        if (apply)
        {
            output.WriteLine($"Applied translated config: {resolvedTargetConfigPath}");
            if (!string.IsNullOrWhiteSpace(report.ManagedSkillRootPath))
                output.WriteLine($"Imported skills into: {report.ManagedSkillRootPath}");
            if (!string.IsNullOrWhiteSpace(report.PluginReviewPlanPath))
                output.WriteLine($"Wrote plugin review plan: {report.PluginReviewPlanPath}");
        }

        return 0;
    }

    private static MigrationPlan BuildPlan(string sourcePath, string targetConfigPath)
    {
        var warnings = new List<string>();
        var skippedSettings = new List<string>();
        var compatibility = new List<UpstreamMigrationCompatibilityItem>();
        var skills = DiscoverSkills(sourcePath, compatibility, warnings);
        var plugins = DiscoverPlugins(sourcePath, compatibility, warnings);
        var discoveredConfigPath = DiscoverConfigPath(sourcePath);
        var translatedConfig = TranslateConfig(sourcePath, discoveredConfigPath, compatibility, warnings, skippedSettings);
        var managedSkillRootPath = ResolveManagedSkillRoot(targetConfigPath);
        var pluginReviewPlanPath = BuildPluginReviewPlanPath(targetConfigPath);

        return new MigrationPlan(
            sourcePath,
            targetConfigPath,
            discoveredConfigPath,
            managedSkillRootPath,
            pluginReviewPlanPath,
            translatedConfig,
            compatibility,
            skills,
            plugins,
            warnings,
            skippedSettings);
    }

    private static async Task<UpstreamMigrationReport> ExecutePlanAsync(MigrationPlan plan, bool apply, CancellationToken ct)
    {
        if (apply)
        {
            await GatewayConfigFile.SaveAsync(plan.TranslatedConfig, plan.TargetConfigPath);
            await ImportSkillsAsync(plan.Skills, plan.ManagedSkillRootPath, ct);
            await WritePluginReviewPlanAsync(plan.PluginReviewPlanPath, plan.Plugins, ct);
        }

        return new UpstreamMigrationReport
        {
            SourcePath = plan.SourcePath,
            TargetConfigPath = plan.TargetConfigPath,
            DiscoveredConfigPath = plan.DiscoveredConfigPath,
            ManagedSkillRootPath = plan.ManagedSkillRootPath,
            PluginReviewPlanPath = plan.PluginReviewPlanPath,
            Applied = apply,
            Compatibility = plan.Compatibility.ToArray(),
            Skills = plan.Skills.ToArray(),
            Plugins = plan.Plugins.ToArray(),
            Warnings = plan.Warnings.ToArray(),
            SkippedSettings = plan.SkippedSettings.ToArray()
        };
    }

    private static GatewayConfig TranslateConfig(
        string sourcePath,
        string? discoveredConfigPath,
        List<UpstreamMigrationCompatibilityItem> compatibility,
        List<string> warnings,
        List<string> skippedSettings)
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                WorkspaceRoot = sourcePath,
                WorkspaceOnly = true,
                AllowedReadRoots = [sourcePath],
                AllowedWriteRoots = [sourcePath]
            }
        };

        if (string.IsNullOrWhiteSpace(discoveredConfigPath))
        {
            compatibility.Add(new UpstreamMigrationCompatibilityItem
            {
                Type = "config",
                Subject = "gateway-config",
                Status = "partial",
                Summary = "No upstream JSON config file was discovered. A default OpenClaw.NET config will be written instead.",
                Warnings = ["Provide provider/model/auth settings manually if the source tree did not include a compatible JSON config."]
            });
            return config;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(discoveredConfigPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            compatibility.Add(new UpstreamMigrationCompatibilityItem
            {
                Type = "config",
                Subject = discoveredConfigPath,
                Status = "unsupported",
                Summary = "The discovered upstream config could not be parsed as JSON.",
                Warnings = [ex.Message]
            });
            warnings.Add($"Config parse failed for {discoveredConfigPath}: {ex.Message}");
            return config;
        }

        var node = root?["OpenClaw"] as JsonObject ?? root;
        if (node is null)
        {
            compatibility.Add(new UpstreamMigrationCompatibilityItem
            {
                Type = "config",
                Subject = discoveredConfigPath,
                Status = "unsupported",
                Summary = "The discovered config did not contain a JSON object.",
                Warnings = ["Only JSON-based upstream configs are supported in migration v1."]
            });
            return config;
        }

        var mappedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bindAddress", "port", "authToken", "llm", "tooling", "memory", "plugins", "skills", "channels"
        };

        TryMapString(node, "bindAddress", value => config.BindAddress = value);
        TryMapInt(node, "port", value => config.Port = value);
        TryMapString(node, "authToken", value => config.AuthToken = value);
        if (node["llm"] is JsonObject llm)
        {
            TryMapString(llm, "provider", value => config.Llm.Provider = value);
            TryMapString(llm, "model", value => config.Llm.Model = value);
            TryMapString(llm, "apiKey", value => config.Llm.ApiKey = value);
            TryMapString(llm, "endpoint", value => config.Llm.Endpoint = value);
        }

        if (node["tooling"] is JsonObject tooling)
        {
            TryMapBool(tooling, "allowShell", value => config.Tooling.AllowShell = value);
            TryMapBool(tooling, "workspaceOnly", value => config.Tooling.WorkspaceOnly = value);
            TryMapString(tooling, "workspaceRoot", value =>
            {
                var resolved = Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(sourcePath, value));
                config.Tooling.WorkspaceRoot = resolved;
                config.Tooling.AllowedReadRoots = [resolved];
                config.Tooling.AllowedWriteRoots = [resolved];
            });
        }

        if (node["memory"] is JsonObject memory)
        {
            TryMapString(memory, "provider", value => config.Memory.Provider = value);
            TryMapString(memory, "storagePath", value =>
            {
                config.Memory.StoragePath = Path.IsPathRooted(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(discoveredConfigPath) ?? sourcePath, value));
            });
            TryMapString(memory, "projectId", value => config.Memory.ProjectId = value);
        }

        if (node["plugins"] is JsonObject plugins)
            TryMapBool(plugins, "enabled", value => config.Plugins.Enabled = value);

        if (node["skills"] is JsonObject skillsNode && skillsNode["load"] is JsonObject skillLoad)
        {
            TryMapBool(skillLoad, "includeManaged", value => config.Skills.Load.IncludeManaged = value);
            TryMapBool(skillLoad, "includeWorkspace", value => config.Skills.Load.IncludeWorkspace = value);
        }

        if (node["channels"] is JsonObject channels)
        {
            ApplyChannelEnabled(channels, "telegram", value => config.Channels.Telegram.Enabled = value);
            ApplyChannelEnabled(channels, "slack", value => config.Channels.Slack.Enabled = value);
            ApplyChannelEnabled(channels, "discord", value => config.Channels.Discord.Enabled = value);
            ApplyChannelEnabled(channels, "teams", value => config.Channels.Teams.Enabled = value);
            ApplyChannelEnabled(channels, "whatsapp", value => config.Channels.WhatsApp.Enabled = value);
            ApplyChannelEnabled(channels, "signal", value => config.Channels.Signal.Enabled = value);
        }

        foreach (var property in node)
        {
            if (mappedRootKeys.Contains(property.Key))
                continue;

            skippedSettings.Add($"{property.Key}: not translated by upstream migration v1");
        }

        compatibility.Add(new UpstreamMigrationCompatibilityItem
        {
            Type = "config",
            Subject = discoveredConfigPath,
            Status = skippedSettings.Count == 0 ? "supported" : "partial",
            Summary = skippedSettings.Count == 0
                ? "Discovered JSON config translated into an external OpenClaw.NET config."
                : "Discovered JSON config translated with some settings skipped.",
            Warnings = skippedSettings.ToArray()
        });

        return config;
    }

    private static List<UpstreamMigrationSkillItem> DiscoverSkills(
        string sourcePath,
        List<UpstreamMigrationCompatibilityItem> compatibility,
        List<string> warnings)
    {
        var results = new List<UpstreamMigrationSkillItem>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateFiles(sourcePath, "SKILL.md", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).Select(Path.GetDirectoryName).Where(static path => !string.IsNullOrWhiteSpace(path)).Cast<string>())
        {
            if (dir.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var inspection = SkillInspector.InspectPath(dir, SkillSource.Managed);
            if (!inspection.Success || inspection.Definition is null)
            {
                compatibility.Add(new UpstreamMigrationCompatibilityItem
                {
                    Type = "skill",
                    Subject = dir,
                    Status = "unsupported",
                    Summary = "Skill could not be parsed.",
                    Warnings = [inspection.ErrorMessage ?? "Failed to parse SKILL.md."]
                });
                continue;
            }

            var slug = Slugify(inspection.Definition.Name);
            if (!seenTargets.Add(slug))
            {
                warnings.Add($"Duplicate migrated skill slug '{slug}' from {dir}; keeping the first instance.");
                compatibility.Add(new UpstreamMigrationCompatibilityItem
                {
                    Type = "skill",
                    Subject = inspection.Definition.Name,
                    Status = "partial",
                    Summary = "Skill was discovered more than once; only the first instance will be applied.",
                    Warnings = [$"Duplicate source path skipped: {dir}"]
                });
                continue;
            }

            results.Add(new UpstreamMigrationSkillItem
            {
                Name = inspection.Definition.Name,
                SourcePath = dir,
                TargetSlug = slug,
                Status = "supported"
            });
            compatibility.Add(new UpstreamMigrationCompatibilityItem
            {
                Type = "skill",
                Subject = inspection.Definition.Name,
                Status = "supported",
                Summary = $"Skill will be imported into managed skills as '{slug}'."
            });
        }

        return results.OrderBy(static item => item.TargetSlug, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<UpstreamMigrationPluginItem> DiscoverPlugins(
        string sourcePath,
        List<UpstreamMigrationCompatibilityItem> compatibility,
        List<string> warnings)
    {
        var catalog = PublicCompatibilityCatalog.GetCatalog(kind: "npm-plugin");
        var results = new List<UpstreamMigrationPluginItem>();
        foreach (var manifestPath in Directory.EnumerateFiles(sourcePath, "openclaw.plugin.json", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            if (manifestPath.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var pluginRoot = Path.GetDirectoryName(manifestPath)!;
            var inspection = PluginCommands.InspectCandidate(pluginRoot, pluginRoot, sourceIsNpm: false);
            if (!inspection.Success)
            {
                compatibility.Add(new UpstreamMigrationCompatibilityItem
                {
                    Type = "plugin",
                    Subject = manifestPath,
                    Status = "unsupported",
                    Summary = "Plugin manifest could not be inspected.",
                    Warnings = [inspection.ErrorMessage ?? "Plugin inspection failed."]
                });
                continue;
            }

            var packageSpec = BuildPackageSpec(pluginRoot, inspection);
            var matchedCatalog = catalog.Items.FirstOrDefault(item =>
                string.Equals(item.PluginId, inspection.PluginId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.PackageName, packageSpec, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.PackageSpec) && string.Equals(item.PackageSpec, packageSpec, StringComparison.OrdinalIgnoreCase)));
            var status = inspection.CanInstall
                ? (matchedCatalog is null ? "partial" : MapCatalogStatus(matchedCatalog.CompatibilityStatus))
                : "unsupported";
            var guidance = new List<string>();
            if (!string.IsNullOrWhiteSpace(packageSpec))
                guidance.Add($"Install review command: openclaw plugins install {packageSpec} --dry-run");
            guidance.Add($"Trust level: {inspection.TrustLevel}");
            guidance.Add($"Declared surface: {inspection.DeclaredSurface}");
            guidance.AddRange(inspection.Warnings);
            if (matchedCatalog is not null)
                guidance.AddRange(matchedCatalog.Guidance);
            if (inspection.Diagnostics.Count > 0)
                guidance.AddRange(inspection.Diagnostics.Select(static item => $"{item.Code}: {item.Message}"));

            results.Add(new UpstreamMigrationPluginItem
            {
                Subject = string.IsNullOrWhiteSpace(inspection.PluginId) ? Path.GetFileName(pluginRoot) : inspection.PluginId,
                PackageSpec = packageSpec,
                Status = status,
                Guidance = guidance.Distinct(StringComparer.Ordinal).ToArray()
            });

            compatibility.Add(new UpstreamMigrationCompatibilityItem
            {
                Type = "plugin",
                Subject = string.IsNullOrWhiteSpace(inspection.PluginId) ? manifestPath : inspection.PluginId,
                Status = status,
                Summary = inspection.CanInstall
                    ? "Plugin manifest was discovered and converted into a pending review/install plan."
                    : "Plugin manifest was discovered but failed local compatibility inspection.",
                Warnings = guidance.ToArray()
            });
        }

        if (results.Count == 0)
            warnings.Add("No upstream plugin manifests were discovered under the source tree.");

        return results.OrderBy(static item => item.Subject, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? DiscoverConfigPath(string sourcePath)
    {
        var candidates = new[]
        {
            Path.Combine(sourcePath, "openclaw.json"),
            Path.Combine(sourcePath, "openclaw.config.json"),
            Path.Combine(sourcePath, "openclaw.settings.json"),
            Path.Combine(sourcePath, "config", "openclaw.json"),
            Path.Combine(sourcePath, "config", "openclaw.config.json"),
            Path.Combine(sourcePath, "config", "openclaw.settings.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return Directory.EnumerateFiles(sourcePath, "*.json", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 3
        })
        .FirstOrDefault(path =>
        {
            var fileName = Path.GetFileName(path);
            return fileName.Contains("openclaw", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("claw", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string ResolveManagedSkillRoot(string targetConfigPath)
    {
        var configDirectory = Path.GetDirectoryName(targetConfigPath) ?? Directory.GetCurrentDirectory();
        var parent = Directory.GetParent(configDirectory)?.FullName;
        return Path.Combine(parent ?? configDirectory, "skills");
    }

    private static string BuildPluginReviewPlanPath(string targetConfigPath)
    {
        var configDirectory = Path.GetDirectoryName(targetConfigPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(targetConfigPath);
        return Path.Combine(configDirectory, $"{fileName}.plugin-review-plan.json");
    }

    private static async Task ImportSkillsAsync(IReadOnlyList<UpstreamMigrationSkillItem> skills, string targetRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var skill in skills)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(targetRoot, skill.TargetSlug);
            CopyDirectory(skill.SourcePath, destination);
        }
        await Task.CompletedTask;
    }

    private static async Task WritePluginReviewPlanAsync(string path, IReadOnlyList<UpstreamMigrationPluginItem> plugins, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new JsonObject
        {
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["items"] = new JsonArray(
                plugins.Select(item => new JsonObject
                {
                    ["subject"] = item.Subject,
                    ["packageSpec"] = item.PackageSpec,
                    ["status"] = item.Status,
                    ["guidance"] = new JsonArray(item.Guidance.Select(static guidance => JsonValue.Create(guidance)).ToArray())
                }).ToArray())
        };
        await File.WriteAllTextAsync(path, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destination = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;

            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
        }
    }

    private static string BuildPackageSpec(string pluginRoot, PluginCommands.PluginInstallInspection inspection)
    {
        var packageJsonPath = Path.Combine(pluginRoot, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
                var name = packageJson?["name"]?.GetValue<string>();
                var version = packageJson?["version"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    return string.IsNullOrWhiteSpace(version) ? name! : $"{name}@{version}";
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(inspection.DisplayName) && !string.IsNullOrWhiteSpace(inspection.Version))
            return $"{inspection.DisplayName}@{inspection.Version}";

        return inspection.PluginId;
    }

    private static string MapCatalogStatus(string status)
        => string.Equals(status, "compatible", StringComparison.OrdinalIgnoreCase) ? "supported" : "unsupported";

    private static string Slugify(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private static void ApplyChannelEnabled(JsonObject root, string channelKey, Action<bool> apply)
    {
        if (root[channelKey] is JsonObject channel)
            TryMapBool(channel, "enabled", apply);
    }

    private static void TryMapString(JsonObject root, string key, Action<string> apply)
    {
        if (root[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            apply(text);
    }

    private static void TryMapBool(JsonObject root, string key, Action<bool> apply)
    {
        if (root[key] is JsonValue value && value.TryGetValue<bool>(out var flag))
            apply(flag);
    }

    private static void TryMapInt(JsonObject root, string key, Action<int> apply)
    {
        if (root[key] is JsonValue value && value.TryGetValue<int>(out var number))
            apply(number);
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw migrate upstream

            Usage:
              openclaw migrate upstream --source <path> --target-config <path> [--apply] [--report <path>]

            Notes:
              - Dry-run is the default and emits a structured migration report.
              - Apply mode writes translated config, imports managed skills, and writes a plugin review plan.
              - Memory, session history, and workspace state are not migrated in v1.
            """);
    }

    private sealed record MigrationPlan(
        string SourcePath,
        string TargetConfigPath,
        string? DiscoveredConfigPath,
        string ManagedSkillRootPath,
        string PluginReviewPlanPath,
        GatewayConfig TranslatedConfig,
        List<UpstreamMigrationCompatibilityItem> Compatibility,
        List<UpstreamMigrationSkillItem> Skills,
        List<UpstreamMigrationPluginItem> Plugins,
        List<string> Warnings,
        List<string> SkippedSettings);
}

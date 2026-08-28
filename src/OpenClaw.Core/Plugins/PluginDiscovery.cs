using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Plugins;

/// <summary>
/// Discovers OpenClaw plugins from the standard filesystem locations
/// and extra configured paths. Compatible with the OpenClaw TypeScript
/// plugin ecosystem discovery spec.
/// </summary>
public static class PluginDiscovery
{
    private const int MaxPluginScanDepth = 8;
    private const int MaxSymlinkResolutionDepth = 64;

    private const string ManifestFileName = "openclaw.plugin.json";
    private const string PackageJsonFileName = "package.json";

    /// <summary>
    /// Discover all plugins from standard locations + configured paths.
    /// Follows OpenClaw precedence: config paths → workspace → global → bundled.
    /// </summary>
    public static List<DiscoveredPlugin> Discover(PluginsConfig pluginsConfig, string? workspacePath = null)
        => DiscoverWithDiagnostics(pluginsConfig, workspacePath).Plugins;

    /// <summary>
    /// Discover plugins plus structured diagnostics for invalid plugin entries.
    /// </summary>
    public static PluginDiscoveryResult DiscoverWithDiagnostics(PluginsConfig pluginsConfig, string? workspacePath = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new PluginDiscoveryResult();

        // 1. Config paths
        foreach (var configPath in pluginsConfig.Load.Paths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configPath);
            if (expanded.StartsWith('~'))
                expanded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    expanded[1..].TrimStart('/').TrimStart('\\'));

            if (File.Exists(expanded))
                TryAddPluginFromFile(expanded, seen, result);
            else if (Directory.Exists(expanded))
                ScanDirectory(expanded, seen, result);
        }

        // 2. Workspace extensions
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var wsExtDir = Path.Combine(workspacePath, ".openclaw", "extensions");
            if (Directory.Exists(wsExtDir))
                ScanExtensionsDirectory(wsExtDir, seen, result);
        }

        // 3. Global extensions
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalExtDir = Path.Combine(home, ".openclaw", "extensions");
        if (Directory.Exists(globalExtDir))
            ScanExtensionsDirectory(globalExtDir, seen, result);

        return result;
    }

    /// <summary>
    /// Filter discovered plugins by allow/deny lists and enabled state.
    /// </summary>
    public static List<DiscoveredPlugin> Filter(
        List<DiscoveredPlugin> discovered,
        PluginsConfig pluginsConfig)
    {
        var result = new List<DiscoveredPlugin>();

        foreach (var plugin in discovered)
        {
            var id = plugin.Manifest.Id;

            // Deny wins
            if (pluginsConfig.Deny.Contains(id, StringComparer.Ordinal))
                continue;

            // Allow check (empty = all allowed)
            if (pluginsConfig.Allow.Length > 0 &&
                !pluginsConfig.Allow.Contains(id, StringComparer.Ordinal))
                continue;

            // Per-plugin enabled check
            if (pluginsConfig.Entries.TryGetValue(id, out var entry) && !entry.Enabled)
                continue;

            // Slot exclusivity check
            if (plugin.Manifest.Kind is not null)
            {
                if (pluginsConfig.Slots.TryGetValue(plugin.Manifest.Kind, out var slotWinner))
                {
                    if (slotWinner == "none" || !string.Equals(slotWinner, id, StringComparison.Ordinal))
                        continue;
                }
            }

            result.Add(plugin);
        }

        return result;
    }

    // --- Agent Plugin 1.0 discovery ---

    private const string AgentPluginManifestFileName = "plugin.json";
    private const string AgentPluginSkillsDirName = "skills";
    private const string AgentPluginMcpFileName = "mcp.json";
    private const string AgentPluginSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";

    /// <summary>
    /// Discover Agent Plugins from standard locations and configured paths.
    /// Compatible with the Agent Plugins 1.0 specification.
    /// </summary>
    public static List<AgentPluginPackage> DiscoverAgentPlugins(
        PluginsConfig pluginsConfig,
        string? workspacePath = null)
        => DiscoverAgentPluginsWithDiagnostics(pluginsConfig, workspacePath).Packages;

    /// <summary>
    /// Discover Agent Plugins plus structured diagnostics for invalid entries.
    /// </summary>
    public static AgentPluginDiscoveryResult DiscoverAgentPluginsWithDiagnostics(
        PluginsConfig pluginsConfig,
        string? workspacePath = null)
    {
        var result = new AgentPluginDiscoveryResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. Config paths (explicit Plugins:Load:Paths)
        foreach (var configPath in pluginsConfig.Load.Paths)
        {
            var expanded = ExpandPath(configPath);
            if (Directory.Exists(expanded))
                ScanForAgentPlugins(expanded, seen, result);
        }

        // 2. Workspace plugins/
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var wsPluginsDir = Path.Combine(workspacePath, "plugins");
            if (Directory.Exists(wsPluginsDir))
                ScanForAgentPlugins(wsPluginsDir, seen, result);
        }

        // 3. User-level ~/.openclaw/plugins/
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userPluginsDir = Path.Combine(home, ".openclaw", "plugins");
        if (Directory.Exists(userPluginsDir))
            ScanForAgentPlugins(userPluginsDir, seen, result);

        return result;
    }

    private static void ScanForAgentPlugins(string dir, HashSet<string> seen, AgentPluginDiscoveryResult result)
    {
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            var manifestPath = Path.Combine(subDir, AgentPluginManifestFileName);
            if (File.Exists(manifestPath))
                TryAddAgentPlugin(subDir, manifestPath, seen, result);
        }
    }

    private static void TryAddAgentPlugin(string pluginRoot, string manifestPath, HashSet<string> seen, AgentPluginDiscoveryResult result)
    {
        AgentPluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.AgentPluginManifest);
        }
        catch (Exception ex)
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(pluginRoot),
                SourcePath = Path.GetFullPath(pluginRoot),
                Loaded = false,
                Diagnostics = [new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_agent_plugin_manifest",
                    Message = $"Failed to parse plugin.json: {ex.Message}",
                    Path = manifestPath
                }]
            });
            return;
        }

        if (manifest is null)
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(pluginRoot),
                SourcePath = Path.GetFullPath(pluginRoot),
                Loaded = false,
                Diagnostics = [new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_agent_plugin_manifest",
                    Message = "Manifest is null after deserialization.",
                    Path = manifestPath
                }]
            });
            return;
        }

        if (!ValidateManifest(manifest, out var validationErrors))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(pluginRoot),
                SourcePath = Path.GetFullPath(pluginRoot),
                Loaded = false,
                Diagnostics = validationErrors
            });
            return;
        }

        // 非致命（warning）诊断不阻止加载，但也不能被静默吞掉：通过 Reports 暴露给
        // 网关的诊断面（unknown_schema 等），随后由 AgentPluginRuntimeManager 汇总。
        var warningDiagnostics = validationErrors
            .Where(d => !string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (warningDiagnostics.Length > 0)
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Name,
                SourcePath = Path.GetFullPath(pluginRoot),
                Loaded = true,
                Diagnostics = warningDiagnostics
            });
        }

        if (!seen.Add(manifest.Name))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Name,
                SourcePath = Path.GetFullPath(pluginRoot),
                Loaded = false,
                Diagnostics = [new PluginCompatibilityDiagnostic
                {
                    Code = "duplicate_plugin_id",
                    Message = $"Plugin name '{manifest.Name}' was discovered more than once. Later entries are skipped.",
                    Path = manifestPath
                }]
            });
            return;
        }

        var pkg = new AgentPluginPackage
        {
            Manifest = manifest,
            RootPath = Path.GetFullPath(pluginRoot),
            SkillsPath = Directory.Exists(Path.Combine(pluginRoot, AgentPluginSkillsDirName))
                ? Path.Combine(pluginRoot, AgentPluginSkillsDirName)
                : null,
            McpConfigPath = File.Exists(Path.Combine(pluginRoot, AgentPluginMcpFileName))
                ? Path.Combine(pluginRoot, AgentPluginMcpFileName)
                : null
        };

        result.Packages.Add(pkg);
    }

    private static bool ValidateManifest(AgentPluginManifest manifest, out PluginCompatibilityDiagnostic[] errors)
    {
        var list = new List<PluginCompatibilityDiagnostic>();

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            list.Add(new PluginCompatibilityDiagnostic { Code = "missing_name", Message = "plugin.json must have a 'name' field", Path = "" });
        }
        else if (!IsSafeNameSegment(manifest.Name))
        {
            // name 也用于 ${PLUGIN_DATA} 目录名和 MCP server id（plugin/server），必须是单个安全路径段
            list.Add(new PluginCompatibilityDiagnostic
            {
                Code = "invalid_agent_plugin_name",
                Message = $"plugin.json 'name' must be a single safe path segment (no separators, '.', or '..'), got '{manifest.Name}'.",
                Path = ""
            });
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
            list.Add(new PluginCompatibilityDiagnostic { Code = "missing_version", Message = "plugin.json must have a 'version' field", Path = "" });

        if (string.IsNullOrWhiteSpace(manifest.Description))
            list.Add(new PluginCompatibilityDiagnostic { Code = "missing_description", Message = "plugin.json must have a 'description' field", Path = "" });

        if (string.IsNullOrWhiteSpace(manifest.License))
            list.Add(new PluginCompatibilityDiagnostic { Code = "missing_license", Message = "plugin.json must have a 'license' field", Path = "" });

        // Schema validation - local constant comparison, no network access
        var schemaValue = manifest.Schema ?? manifest.SchemaDollar;
        if (!string.IsNullOrWhiteSpace(schemaValue) && schemaValue != AgentPluginSchema)
        {
            // Unknown schema produces a warning but does not block loading
            list.Add(new PluginCompatibilityDiagnostic
            {
                Severity = "warning",
                Code = "unknown_schema",
                Message = $"Unknown schema '{manifest.Schema}'. Expected '{AgentPluginSchema}'.",
                Path = ""
            });
        }

        errors = list.ToArray();
        return list.All(e => e.Severity != "error");
    }

    private static bool IsSafeNameSegment(string name)
    {
        if (name is "" or "." or "..")
            return false;
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            return false;
        return !name.Contains('/') && !name.Contains('\\');
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith('~'))
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[1..].TrimStart('/').TrimStart('\\'));
        return expanded;
    }

    private static void ScanExtensionsDirectory(string extensionsDir, HashSet<string> seen, PluginDiscoveryResult result)
    {
        // Scan for *.ts, *.js, and *.mjs files directly in extensions/
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.ts"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.js"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.mjs"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.cjs"))
            TryAddPluginFromFile(file, seen, result);

        // Scan each installed directory using native-plugin precedence followed by
        // compatible bundle detection.
        foreach (var subDir in Directory.EnumerateDirectories(extensionsDir, "*", PluginDirectoryEnumerationOptions()))
            ScanDirectory(subDir, seen, result);
    }

    private static void ScanDirectory(string dir, HashSet<string> seen, PluginDiscoveryResult result, int depth = 0)
    {
        // Check if this directory is itself a plugin (has manifest)
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            TryAddPluginFromManifest(dir, manifestPath, seen, result);
            return;
        }

        // Native package metadata takes precedence over compatible bundle markers.
        var packageJsonPath = Path.Combine(dir, PackageJsonFileName);
        if (File.Exists(packageJsonPath) && TryAddPluginPack(dir, packageJsonPath, seen, result))
            return;

        var conventionalEntry = new[] { "index.js", "index.mjs", "index.cjs", "index.ts" }
            .Select(candidate => Path.Combine(dir, candidate))
            .FirstOrDefault(File.Exists);
        if (conventionalEntry is not null && !PluginBundleDetector.HasExplicitOrStrongMarker(dir))
        {
            TryAddPluginFromFile(conventionalEntry, seen, result);
            return;
        }

        if (PluginBundleDetector.TryDetect(dir, out var bundle, out var bundleDiagnostic))
        {
            if (bundleDiagnostic is not null)
            {
                result.Reports.Add(new PluginLoadReport
                {
                    PluginId = Path.GetFileName(dir),
                    SourcePath = Path.GetFullPath(dir),
                    EntryPath = null,
                    Origin = PluginFormats.Bundle,
                    Loaded = false,
                    Diagnostics = [bundleDiagnostic]
                });
                return;
            }

            if (bundle is not null && seen.Add(bundle.Manifest.Id))
            {
                result.Plugins.Add(bundle);
            }
            else if (bundle is not null)
            {
                result.Reports.Add(new PluginLoadReport
                {
                    PluginId = bundle.Manifest.Id,
                    SourcePath = bundle.RootPath,
                    EntryPath = null,
                    Origin = PluginFormats.Bundle,
                    Loaded = false,
                    Diagnostics =
                    [
                        new PluginCompatibilityDiagnostic
                        {
                            Code = "duplicate_plugin_id",
                            Message = $"Plugin id '{bundle.Manifest.Id}' was discovered more than once. Later entries are skipped.",
                            Surface = "bundle_manifest",
                            Path = bundle.RootPath
                        }
                    ]
                });
            }
            return;
        }

        if (conventionalEntry is not null)
        {
            TryAddPluginFromFile(conventionalEntry, seen, result);
            return;
        }

        // Scan subdirectories without following links or traversing arbitrary depth.
        if (depth >= MaxPluginScanDepth)
            return;

        foreach (var subDir in Directory.EnumerateDirectories(dir, "*", PluginDirectoryEnumerationOptions()))
        {
            var name = Path.GetFileName(subDir);
            if (name is "node_modules" or ".git")
                continue;
            ScanDirectory(subDir, seen, result, depth + 1);
        }
    }

    private static EnumerationOptions PluginDirectoryEnumerationOptions()
        => new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

    private static void TryAddPluginFromFile(string filePath, HashSet<string> seen, PluginDiscoveryResult result)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            return;

        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            TryAddPluginFromManifest(dir, manifestPath, seen, result);
        }
        else
        {
            // Standalone file — use file base name as id
            var id = Path.GetFileNameWithoutExtension(filePath);
            if (!seen.Add(id))
                return;

            var packageMetadata = ReadPackageMetadata(dir);

            result.Plugins.Add(new DiscoveredPlugin
            {
                Manifest = new PluginManifest { Id = id },
                RootPath = dir,
                EntryPath = Path.GetFullPath(filePath),
                PluginApiRange = packageMetadata.PluginApiRange,
                MinHostVersion = packageMetadata.MinHostVersion,
                ExpectedIntegrity = packageMetadata.ExpectedIntegrity
            });
        }
    }

    private static void TryAddPluginFromManifest(string pluginRoot, string manifestPath, HashSet<string> seen, PluginDiscoveryResult result)
    {
        PluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.PluginManifest);
        }
        catch
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(pluginRoot),
                SourcePath = Path.GetFullPath(pluginRoot),
                EntryPath = null,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "invalid_manifest",
                        Message = $"Failed to parse manifest '{manifestPath}'.",
                        Path = Path.GetFullPath(manifestPath)
                    }
                ]
            });
            return; // Skip broken manifests
        }

        if (manifest is null)
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(pluginRoot),
                SourcePath = Path.GetFullPath(pluginRoot),
                EntryPath = null,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "invalid_manifest",
                        Message = $"Manifest '{manifestPath}' must contain a JSON object.",
                        Path = Path.GetFullPath(manifestPath)
                    }
                ]
            });
            return;
        }

        if (!seen.Add(manifest.Id))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(pluginRoot),
                EntryPath = null,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "duplicate_plugin_id",
                        Message = $"Plugin id '{manifest.Id}' was discovered more than once. Later entries are skipped.",
                        Path = Path.GetFullPath(manifestPath)
                    }
                ]
            });
            return;
        }

        // Find entry file
        var entryPath = FindEntryFile(pluginRoot, out var entryDiagnostic);
        if (entryPath is null)
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(pluginRoot),
                EntryPath = null,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = entryDiagnostic?.Code ?? "entry_not_found",
                        Message = entryDiagnostic?.Message ?? $"No plugin entry file was found for '{manifest.Id}'. Expected index.js/mjs/cjs/ts, src/index.*, or a package.json openclaw.runtimeExtensions/openclaw.extensions entry.",
                        Path = entryDiagnostic?.Path ?? Path.GetFullPath(pluginRoot)
                    }
                ]
            });
            return;
        }

        if (!TryResolveContainedPath(pluginRoot, Path.GetRelativePath(pluginRoot, entryPath), out var containedEntryPath))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(pluginRoot),
                EntryPath = Path.GetFullPath(entryPath),
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "entry_outside_root",
                        Message = $"Plugin entry file for '{manifest.Id}' resolves outside the plugin root.",
                        Path = Path.GetFullPath(entryPath)
                    }
                ]
            });
            return;
        }

        var packageMetadata = ReadPackageMetadata(pluginRoot);
        result.Plugins.Add(new DiscoveredPlugin
        {
            Manifest = manifest,
            RootPath = Path.GetFullPath(pluginRoot),
            EntryPath = containedEntryPath,
            PluginApiRange = packageMetadata.PluginApiRange,
            MinHostVersion = packageMetadata.MinHostVersion,
            ExpectedIntegrity = packageMetadata.ExpectedIntegrity
        });
    }

    private static bool TryAddPluginPack(string dir, string packageJsonPath, HashSet<string> seen, PluginDiscoveryResult result)
    {
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("openclaw", out var ocProp))
                return false;
            if (!TryGetRuntimeEntryArray(ocProp, out var extProp))
                return false;
            if (extProp.ValueKind != JsonValueKind.Array)
                return false;

            var packName = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? Path.GetFileName(dir)
                : Path.GetFileName(dir);

            foreach (var ext in extProp.EnumerateArray())
            {
                var relPath = ext.GetString();
                if (string.IsNullOrEmpty(relPath))
                    continue;

                var fileBase = Path.GetFileNameWithoutExtension(relPath);
                var pluginId = extProp.GetArrayLength() > 1
                    ? $"{packName}/{fileBase}"
                    : packName;

                if (!TryResolveContainedPath(dir, relPath, out var entryPath))
                {
                    result.Reports.Add(new PluginLoadReport
                    {
                        PluginId = pluginId,
                        SourcePath = Path.GetFullPath(dir),
                        EntryPath = Path.GetFullPath(Path.Combine(dir, relPath)),
                        Loaded = false,
                        Diagnostics =
                        [
                            new PluginCompatibilityDiagnostic
                            {
                                Code = "entry_outside_root",
                                Message = $"Package entry '{relPath}' for plugin '{pluginId}' resolves outside the plugin root.",
                                Path = Path.GetFullPath(dir)
                            }
                        ]
                    });
                    continue;
                }

                if (!File.Exists(entryPath))
                {
                    result.Reports.Add(new PluginLoadReport
                    {
                        PluginId = pluginId,
                        SourcePath = Path.GetFullPath(dir),
                        EntryPath = entryPath,
                        Loaded = false,
                        Diagnostics =
                        [
                            new PluginCompatibilityDiagnostic
                            {
                                Code = "entry_not_found",
                                Message = $"Package entry '{relPath}' for plugin '{pluginId}' does not exist.",
                                Path = entryPath
                            }
                        ]
                    });
                    continue;
                }

                if (!seen.Add(pluginId))
                {
                    result.Reports.Add(new PluginLoadReport
                    {
                        PluginId = pluginId,
                        SourcePath = Path.GetFullPath(dir),
                        EntryPath = entryPath,
                        Loaded = false,
                        Diagnostics =
                        [
                            new PluginCompatibilityDiagnostic
                            {
                                Code = "duplicate_plugin_id",
                                Message = $"Plugin id '{pluginId}' was discovered more than once. Later entries are skipped.",
                                Path = entryPath
                            }
                        ]
                    });
                    continue;
                }

                // Check for manifest in the entry's directory
                var entryDir = Path.GetDirectoryName(entryPath) ?? dir;
                var entryManifestPath = Path.Combine(entryDir, ManifestFileName);
                PluginManifest manifest;

                if (File.Exists(entryManifestPath))
                {
                    try
                    {
                        using var ms = File.OpenRead(entryManifestPath);
                        manifest = JsonSerializer.Deserialize(ms, CoreJsonContext.Default.PluginManifest)
                            ?? new PluginManifest { Id = pluginId };
                    }
                    catch
                    {
                        manifest = new PluginManifest { Id = pluginId };
                    }
                }
                else
                {
                    manifest = new PluginManifest { Id = pluginId };
                }

                var packageMetadata = ReadPackageMetadata(dir);
                result.Plugins.Add(new DiscoveredPlugin
                {
                    Manifest = manifest,
                    RootPath = Path.GetFullPath(dir),
                    EntryPath = entryPath,
                    PluginApiRange = packageMetadata.PluginApiRange,
                    MinHostVersion = packageMetadata.MinHostVersion,
                    ExpectedIntegrity = packageMetadata.ExpectedIntegrity
                });
            }

            return true;
        }
        catch
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(dir),
                SourcePath = Path.GetFullPath(dir),
                EntryPath = null,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "invalid_package_json",
                        Message = $"Failed to parse package.json at '{packageJsonPath}'.",
                        Path = Path.GetFullPath(packageJsonPath)
                    }
                ]
            });
            return false;
        }
    }

    private static string? FindEntryFile(string pluginRoot, out PluginCompatibilityDiagnostic? diagnostic)
    {
        diagnostic = null;
        // Installed packages publish built runtime entries separately from source entries.
        var packageJson = Path.Combine(pluginRoot, PackageJsonFileName);
        if (File.Exists(packageJson))
        {
            try
            {
                using var stream = File.OpenRead(packageJson);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                if (root.TryGetProperty("openclaw", out var ocProp) &&
                    TryGetRuntimeEntryArray(ocProp, out var extProp))
                {
                    foreach (var ext in extProp.EnumerateArray())
                    {
                        var relPath = ext.GetString();
                        if (string.IsNullOrEmpty(relPath))
                            continue;

                        if (!TryResolveContainedPath(pluginRoot, relPath, out var entryPath))
                        {
                            diagnostic = new PluginCompatibilityDiagnostic
                            {
                                Code = "entry_outside_root",
                                Message = $"Package entry '{relPath}' resolves outside the plugin root.",
                                Path = Path.GetFullPath(pluginRoot)
                            };
                            return null;
                        }

                        if (File.Exists(entryPath))
                            return entryPath;
                    }
                }
            }
            catch
            {
                // Fall through
            }
        }

        // Check common entry points after package-owned runtime metadata.
        string[] candidates =
        [
            "index.js", "index.mjs", "index.cjs", "index.ts",
            "src/index.js", "src/index.mjs", "src/index.cjs", "src/index.ts"
        ];

        var conventionalEntry = candidates
            .Select(candidate => Path.Combine(pluginRoot, candidate))
            .FirstOrDefault(File.Exists);
        if (conventionalEntry is not null)
            return conventionalEntry;

        // Fallback: any .ts, .js, or .mjs file in root
        foreach (var ext in new[] { "*.js", "*.mjs", "*.cjs", "*.ts" })
        {
            var files = Directory.GetFiles(pluginRoot, ext);
            if (files.Length == 1)
                return files[0];
        }

        return null;
    }

    private static bool TryGetRuntimeEntryArray(JsonElement openClaw, out JsonElement entries)
    {
        if (openClaw.TryGetProperty("runtimeExtensions", out entries) && entries.ValueKind == JsonValueKind.Array)
            return true;

        return openClaw.TryGetProperty("extensions", out entries) && entries.ValueKind == JsonValueKind.Array;
    }

    private static PluginPackageMetadata ReadPackageMetadata(string pluginRoot)
    {
        var packageJson = Path.Combine(pluginRoot, PackageJsonFileName);
        if (!File.Exists(packageJson))
            return new PluginPackageMetadata();

        try
        {
            using var stream = File.OpenRead(packageJson);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("openclaw", out var openClaw))
                return new PluginPackageMetadata();

            string? pluginApiRange = null;
            string? minHostVersion = null;
            string? expectedIntegrity = null;
            if (openClaw.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object)
            {
                pluginApiRange = GetString(compat, "pluginApi");
                minHostVersion = GetString(compat, "minGatewayVersion");
            }

            if (openClaw.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object)
            {
                minHostVersion ??= GetString(install, "minHostVersion");
                expectedIntegrity = GetString(install, "expectedIntegrity");
            }

            return new PluginPackageMetadata(pluginApiRange, minHostVersion, expectedIntegrity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PluginPackageMetadata();
        }
    }

    private static string? GetString(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record PluginPackageMetadata(
        string? PluginApiRange = null,
        string? MinHostVersion = null,
        string? ExpectedIntegrity = null);

    public static bool TryResolveContainedPath(string rootPath, string relativePath, out string resolvedPath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            resolvedPath = string.Empty;
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Join(rootPath, relativePath));
        if (IsUnresolvedLink(candidatePath))
        {
            resolvedPath = string.Empty;
            return false;
        }

        resolvedPath = candidatePath;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        // Resolve symlinks for both paths to prevent symlink-based escape from the root.
        resolvedPath = ResolveRealPath(resolvedPath);
        fullRoot = ResolveRealPath(fullRoot);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(resolvedPath, fullRoot, comparison))
            return true;

        return resolvedPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Resolves the real filesystem path, following symlinked ancestors and final targets.
    /// Falls back to the normalized segment path when a segment does not exist.
    /// </summary>
    private static string ResolveRealPath(string path)
        => ResolveRealPath(path, new HashSet<string>(GetPathComparer()), depth: 0);

    private static string ResolveRealPath(string path, HashSet<string> visited, int depth)
    {
        var full = Path.GetFullPath(path);
        if (depth >= MaxSymlinkResolutionDepth || !visited.Add(full))
            return full;

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        var current = root;
        var remaining = full[root.Length..];
        var segments = remaining.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (Path.IsPathRooted(segment))
                return Path.GetFullPath(current);

            current = Path.Join(current, segment);
            var resolved = TryResolveLinkTarget(current);
            if (resolved is not null)
                current = ResolveRealPath(resolved, visited, depth + 1);
        }

        return Path.GetFullPath(current);
    }

    private static string? TryResolveLinkTarget(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null)
                return target.FullName;
        }
        catch (IOException)
        {
            // Expected when discovery probes inaccessible or broken filesystem entries.
        }
        catch (UnauthorizedAccessException)
        {
            // Expected when discovery probes roots the current process cannot inspect.
        }

        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null)
                return target.FullName;
        }
        catch (IOException)
        {
            // Expected when discovery probes inaccessible or broken filesystem entries.
        }
        catch (UnauthorizedAccessException)
        {
            // Expected when discovery probes roots the current process cannot inspect.
        }

        return null;
    }

    private static bool IsUnresolvedLink(string path)
    {
        try
        {
            FileSystemInfo info = File.Exists(path)
                ? new FileInfo(path)
                : new DirectoryInfo(path);

            return !string.IsNullOrEmpty(info.LinkTarget) && TryResolveLinkTarget(path) is null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

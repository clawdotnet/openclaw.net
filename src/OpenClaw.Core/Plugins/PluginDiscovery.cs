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

    private static void ScanExtensionsDirectory(string extensionsDir, HashSet<string> seen, PluginDiscoveryResult result)
    {
        // Scan for *.ts, *.js, and *.mjs files directly in extensions/
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.ts"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.js"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.mjs"))
            TryAddPluginFromFile(file, seen, result);

        // Scan for subdirectories with index.ts, index.js, or index.mjs
        foreach (var subDir in Directory.EnumerateDirectories(extensionsDir))
        {
            var indexTs = Path.Combine(subDir, "index.ts");
            var indexJs = Path.Combine(subDir, "index.js");
            var indexMjs = Path.Combine(subDir, "index.mjs");

            if (File.Exists(indexTs))
                TryAddPluginFromFile(indexTs, seen, result);
            else if (File.Exists(indexJs))
                TryAddPluginFromFile(indexJs, seen, result);
            else if (File.Exists(indexMjs))
                TryAddPluginFromFile(indexMjs, seen, result);
        }
    }

    private static void ScanDirectory(string dir, HashSet<string> seen, PluginDiscoveryResult result)
    {
        // Check if this directory is itself a plugin (has manifest)
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            TryAddPluginFromManifest(dir, manifestPath, seen, result);
            return;
        }

        // Check for package pack (package.json with openclaw.extensions)
        var packageJsonPath = Path.Combine(dir, PackageJsonFileName);
        if (File.Exists(packageJsonPath))
        {
            TryAddPluginPack(dir, packageJsonPath, seen, result);
            return;
        }

        // Scan subdirectories
        foreach (var subDir in Directory.EnumerateDirectories(dir))
            ScanDirectory(subDir, seen, result);
    }

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

            result.Plugins.Add(new DiscoveredPlugin
            {
                Manifest = new PluginManifest { Id = id },
                RootPath = dir,
                EntryPath = Path.GetFullPath(filePath)
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
            return;

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
                        Message = entryDiagnostic?.Message ?? $"No plugin entry file was found for '{manifest.Id}'. Expected index.ts, index.js, index.mjs, src/index.*, or a package.json openclaw.extensions entry.",
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

        result.Plugins.Add(new DiscoveredPlugin
        {
            Manifest = manifest,
            RootPath = Path.GetFullPath(pluginRoot),
            EntryPath = containedEntryPath
        });
    }

    private static void TryAddPluginPack(string dir, string packageJsonPath, HashSet<string> seen, PluginDiscoveryResult result)
    {
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("openclaw", out var ocProp))
                return;
            if (!ocProp.TryGetProperty("extensions", out var extProp))
                return;
            if (extProp.ValueKind != JsonValueKind.Array)
                return;

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

                result.Plugins.Add(new DiscoveredPlugin
                {
                    Manifest = manifest,
                    RootPath = Path.GetFullPath(dir),
                    EntryPath = entryPath
                });
            }
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
        }
    }

    private static string? FindEntryFile(string pluginRoot, out PluginCompatibilityDiagnostic? diagnostic)
    {
        diagnostic = null;
        // Check common entry points
        string[] candidates =
        [
            "index.ts", "index.js", "index.mjs",
            "src/index.ts", "src/index.js", "src/index.mjs"
        ];

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(pluginRoot, candidate);
            if (File.Exists(path))
                return path;
        }

        // Check package.json for openclaw.extensions
        var packageJson = Path.Combine(pluginRoot, PackageJsonFileName);
        if (File.Exists(packageJson))
        {
            try
            {
                using var stream = File.OpenRead(packageJson);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                if (root.TryGetProperty("openclaw", out var ocProp) &&
                    ocProp.TryGetProperty("extensions", out var extProp) &&
                    extProp.ValueKind == JsonValueKind.Array)
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

        // Fallback: any .ts, .js, or .mjs file in root
        foreach (var ext in new[] { "*.ts", "*.js", "*.mjs" })
        {
            var files = Directory.GetFiles(pluginRoot, ext);
            if (files.Length == 1)
                return files[0];
        }

        return null;
    }

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

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
    private const string ManifestFileName = "openclaw.plugin.json";
    private const string PackageJsonFileName = "package.json";

    /// <summary>
    /// Discover all plugins from standard locations + configured paths.
    /// Follows OpenClaw precedence: config paths → workspace → global → bundled.
    /// </summary>
    public static List<DiscoveredPlugin> Discover(PluginsConfig pluginsConfig, string? workspacePath = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredPlugin>();

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

    private static void ScanExtensionsDirectory(string extensionsDir, HashSet<string> seen, List<DiscoveredPlugin> result)
    {
        // Scan for *.ts and *.js files directly in extensions/
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.ts"))
            TryAddPluginFromFile(file, seen, result);
        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.js"))
            TryAddPluginFromFile(file, seen, result);

        // Scan for subdirectories with index.ts or index.js
        foreach (var subDir in Directory.EnumerateDirectories(extensionsDir))
        {
            var indexTs = Path.Combine(subDir, "index.ts");
            var indexJs = Path.Combine(subDir, "index.js");

            if (File.Exists(indexTs))
                TryAddPluginFromFile(indexTs, seen, result);
            else if (File.Exists(indexJs))
                TryAddPluginFromFile(indexJs, seen, result);
        }
    }

    private static void ScanDirectory(string dir, HashSet<string> seen, List<DiscoveredPlugin> result)
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

    private static void TryAddPluginFromFile(string filePath, HashSet<string> seen, List<DiscoveredPlugin> result)
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

            result.Add(new DiscoveredPlugin
            {
                Manifest = new PluginManifest { Id = id },
                RootPath = dir,
                EntryPath = Path.GetFullPath(filePath)
            });
        }
    }

    private static void TryAddPluginFromManifest(string pluginRoot, string manifestPath, HashSet<string> seen, List<DiscoveredPlugin> result)
    {
        PluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.PluginManifest);
        }
        catch
        {
            return; // Skip broken manifests
        }

        if (manifest is null || !seen.Add(manifest.Id))
            return;

        // Find entry file
        var entryPath = FindEntryFile(pluginRoot);
        if (entryPath is null)
            return;

        result.Add(new DiscoveredPlugin
        {
            Manifest = manifest,
            RootPath = Path.GetFullPath(pluginRoot),
            EntryPath = Path.GetFullPath(entryPath)
        });
    }

    private static void TryAddPluginPack(string dir, string packageJsonPath, HashSet<string> seen, List<DiscoveredPlugin> result)
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

                var entryPath = Path.GetFullPath(Path.Combine(dir, relPath));
                if (!File.Exists(entryPath))
                    continue;

                var fileBase = Path.GetFileNameWithoutExtension(relPath);
                var pluginId = extProp.GetArrayLength() > 1
                    ? $"{packName}/{fileBase}"
                    : packName;

                if (!seen.Add(pluginId))
                    continue;

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

                result.Add(new DiscoveredPlugin
                {
                    Manifest = manifest,
                    RootPath = Path.GetFullPath(dir),
                    EntryPath = entryPath
                });
            }
        }
        catch
        {
            // Skip broken package.json
        }
    }

    private static string? FindEntryFile(string pluginRoot)
    {
        // Check common entry points
        string[] candidates = ["index.ts", "index.js", "src/index.ts", "src/index.js"];

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

                        var entryPath = Path.Combine(pluginRoot, relPath);
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

        // Fallback: any .ts or .js file in root
        foreach (var ext in new[] { "*.ts", "*.js" })
        {
            var files = Directory.GetFiles(pluginRoot, ext);
            if (files.Length == 1)
                return files[0];
        }

        return null;
    }
}

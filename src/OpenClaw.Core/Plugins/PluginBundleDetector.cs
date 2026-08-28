using System.Text.Json;

namespace OpenClaw.Core.Plugins;

/// <summary>
/// Detects Codex, Claude, and Cursor compatible content bundles without loading
/// arbitrary JavaScript from them. Native plugin detection always runs first.
/// </summary>
internal static class PluginBundleDetector
{
    private const string CodexManifest = ".codex-plugin/plugin.json";
    private const string ClaudeManifest = ".claude-plugin/plugin.json";
    private const string CursorManifest = ".cursor-plugin/plugin.json";

    internal static bool HasExplicitOrStrongMarker(string rootPath)
    {
        if (File.Exists(Path.Combine(rootPath, CodexManifest.Replace('/', Path.DirectorySeparatorChar))) ||
            File.Exists(Path.Combine(rootPath, ClaudeManifest.Replace('/', Path.DirectorySeparatorChar))) ||
            File.Exists(Path.Combine(rootPath, CursorManifest.Replace('/', Path.DirectorySeparatorChar))))
        {
            return true;
        }

        var cursorRoot = Path.Combine(rootPath, ".cursor");
        return (Directory.Exists(cursorRoot) &&
                (Directory.Exists(Path.Combine(cursorRoot, "commands")) ||
                 Directory.Exists(Path.Combine(cursorRoot, "agents")) ||
                 Directory.Exists(Path.Combine(cursorRoot, "rules")) ||
                 File.Exists(Path.Combine(cursorRoot, "hooks.json"))));
    }

    public static bool TryDetect(
        string rootPath,
        out DiscoveredPlugin? plugin,
        out PluginCompatibilityDiagnostic? diagnostic)
    {
        plugin = null;
        diagnostic = null;

        var bundleFormat = DetectFormat(rootPath, out var manifestRelativePath);
        if (bundleFormat is null)
            return false;

        JsonDocument? manifestDocument;
        if (manifestRelativePath is not null)
        {
            var manifestPath = Path.Combine(rootPath, manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                diagnostic = new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_bundle_manifest",
                    Message = $"Failed to parse {bundleFormat} bundle manifest '{manifestPath}': {ex.Message}",
                    Surface = "bundle_manifest",
                    Path = manifestPath
                };
                return true;
            }
        }
        else
        {
            manifestDocument = null;
        }

        using (manifestDocument)
        {
            var manifestRoot = manifestDocument?.RootElement;
            if (manifestRoot is { ValueKind: not JsonValueKind.Object })
            {
                var manifestPath = Path.Combine(rootPath, manifestRelativePath!.Replace('/', Path.DirectorySeparatorChar));
                diagnostic = new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_bundle_manifest",
                    Message = $"The {bundleFormat} bundle manifest '{manifestPath}' must contain a JSON object.",
                    Surface = "bundle_manifest",
                    Path = manifestPath
                };
                return true;
            }

            var rawId = GetString(manifestRoot, "id")
                ?? GetString(manifestRoot, "name")
                ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
            var pluginId = NormalizeId(rawId);
            if (string.IsNullOrWhiteSpace(pluginId))
                pluginId = $"{bundleFormat}-bundle";

            var skillRoots = new HashSet<string>(StringComparer.Ordinal);
            var commandRoots = new HashSet<string>(StringComparer.Ordinal);
            AddDefaultDirectory(rootPath, "skills", skillRoots);
            AddManifestPaths(manifestRoot, "skills", skillRoots);

            if (bundleFormat == "claude")
            {
                AddDefaultDirectory(rootPath, "commands", commandRoots);
                AddManifestPaths(manifestRoot, "commands", commandRoots);
            }
            else if (bundleFormat == "cursor")
            {
                AddDefaultDirectory(rootPath, ".cursor/commands", commandRoots);
                AddManifestPaths(manifestRoot, "commands", commandRoots);
            }

            var mappedCapabilities = new List<string>();
            if (skillRoots.Count > 0)
                mappedCapabilities.Add("skills");
            if (commandRoots.Count > 0)
                mappedCapabilities.Add("commands");

            var detectedOnly = DetectNonMappedCapabilities(rootPath, bundleFormat, manifestRoot);
            plugin = new DiscoveredPlugin
            {
                Manifest = new PluginManifest
                {
                    Id = pluginId,
                    Name = GetString(manifestRoot, "displayName") ?? GetString(manifestRoot, "name") ?? pluginId,
                    Description = GetString(manifestRoot, "description"),
                    Version = GetString(manifestRoot, "version"),
                    Skills =
                    [
                        .. skillRoots.OrderBy(static path => path, StringComparer.Ordinal),
                        .. commandRoots.OrderBy(static path => path, StringComparer.Ordinal)
                    ]
                },
                RootPath = Path.GetFullPath(rootPath),
                EntryPath = string.Empty,
                Format = PluginFormats.Bundle,
                BundleFormat = bundleFormat,
                BundleMappedCapabilities = [.. mappedCapabilities.OrderBy(static item => item, StringComparer.Ordinal)],
                BundleDetectedCapabilities = detectedOnly
            };
            return true;
        }
    }

    private static string? DetectFormat(string rootPath, out string? manifestRelativePath)
    {
        if (File.Exists(Path.Combine(rootPath, CodexManifest.Replace('/', Path.DirectorySeparatorChar))))
        {
            manifestRelativePath = CodexManifest;
            return "codex";
        }

        if (File.Exists(Path.Combine(rootPath, ClaudeManifest.Replace('/', Path.DirectorySeparatorChar))))
        {
            manifestRelativePath = ClaudeManifest;
            return "claude";
        }

        if (File.Exists(Path.Combine(rootPath, CursorManifest.Replace('/', Path.DirectorySeparatorChar))))
        {
            manifestRelativePath = CursorManifest;
            return "cursor";
        }

        manifestRelativePath = null;
        var cursorRoot = Path.Combine(rootPath, ".cursor");
        if (Directory.Exists(cursorRoot) &&
            (Directory.Exists(Path.Combine(cursorRoot, "commands")) ||
             Directory.Exists(Path.Combine(cursorRoot, "agents")) ||
             Directory.Exists(Path.Combine(cursorRoot, "rules")) ||
             File.Exists(Path.Combine(cursorRoot, "hooks.json"))))
        {
            return "cursor";
        }

        // Agent Plugins 1.0 packages are identified by a root plugin.json and are owned by
        // the Agent Plugin discovery pipeline; never classify them as Claude content bundles.
        if (File.Exists(Path.Combine(rootPath, "plugin.json")))
            return null;

        if (Directory.Exists(Path.Combine(rootPath, "skills")) ||
            Directory.Exists(Path.Combine(rootPath, "commands")) ||
            Directory.Exists(Path.Combine(rootPath, "agents")) ||
            Directory.Exists(Path.Combine(rootPath, "hooks")) ||
            File.Exists(Path.Combine(rootPath, ".mcp.json")) ||
            File.Exists(Path.Combine(rootPath, ".lsp.json")) ||
            File.Exists(Path.Combine(rootPath, "settings.json")))
        {
            return "claude";
        }

        return null;
    }

    private static string[] DetectNonMappedCapabilities(
        string rootPath,
        string bundleFormat,
        JsonElement? manifestRoot)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        AddIfPresent(rootPath, "hooks", capabilities, "hooks");
        AddIfPresent(rootPath, ".mcp.json", capabilities, "mcp");
        AddIfPresent(rootPath, ".app.json", capabilities, "app_metadata");

        if (bundleFormat == "claude")
        {
            AddIfPresent(rootPath, "agents", capabilities, "agents");
            AddIfPresent(rootPath, "outputStyles", capabilities, "output_styles");
            AddIfPresent(rootPath, ".lsp.json", capabilities, "lsp");
            AddIfPresent(rootPath, "settings.json", capabilities, "settings");
            AddIfPresent(rootPath, "hooks/hooks.json", capabilities, "hook_automation");
        }
        else if (bundleFormat == "cursor")
        {
            AddIfPresent(rootPath, ".cursor/agents", capabilities, "agents");
            AddIfPresent(rootPath, ".cursor/rules", capabilities, "rules");
            AddIfPresent(rootPath, ".cursor/hooks.json", capabilities, "hook_automation");
        }

        foreach (var (propertyName, capability) in new[]
                 {
                     ("hooks", "hooks"),
                     ("mcpServers", "mcp"),
                     ("lspServers", "lsp"),
                     ("settings", "settings"),
                     ("agents", "agents"),
                     ("outputStyles", "output_styles"),
                     ("rules", "rules")
                 })
        {
            if (manifestRoot is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(propertyName, out _))
                capabilities.Add(capability);
        }

        return [.. capabilities.OrderBy(static item => item, StringComparer.Ordinal)];
    }

    private static void AddDefaultDirectory(string rootPath, string relativePath, ISet<string> paths)
    {
        var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(fullPath))
            paths.Add(relativePath);
    }

    private static void AddManifestPaths(JsonElement? manifestRoot, string propertyName, ISet<string> paths)
    {
        if (manifestRoot is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            AddPath(property.GetString(), paths);
            return;
        }

        if (property.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in property.EnumerateArray()
                     .Where(static item => item.ValueKind == JsonValueKind.String))
            AddPath(item.GetString(), paths);
    }

    private static void AddPath(string? path, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length > 0)
            paths.Add(normalized);
    }

    private static void AddIfPresent(string rootPath, string relativePath, ISet<string> capabilities, string capability)
    {
        var path = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path) || Directory.Exists(path))
            capabilities.Add(capability);
    }

    private static string? GetString(JsonElement? element, string propertyName)
        => element is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizeId(string value)
        => value.Trim()
            .Replace('@', ' ')
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(' ', '-')
            .Trim('-');
}

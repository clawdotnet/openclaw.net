using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.McpApp.Models;
using OpenClaw.McpApp.Shared;

namespace OpenClaw.McpApp;

/// <summary>
/// Scans configured discovery paths for <c>openclaw.mcpapp.json</c> manifest files
/// and produces a list of <see cref="McpAppInstallState"/> entries.
/// Handles allow/deny filtering based on <see cref="McpAppsConfig"/>.
/// </summary>
public sealed class McpAppDiscovery
{
    private readonly McpAppsConfig _config;
    private readonly ILogger _logger;

    /// <summary>Filename the discovery scanner looks for.</summary>
    public const string ManifestFileName = "openclaw.mcpapp.json";

    public McpAppDiscovery(McpAppsConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Scans all configured discovery paths and returns discovered app states.
    /// Apps that fail validation are still returned (with <see cref="McpAppInstallState.IsValid"/> = false
    /// and <see cref="McpAppInstallState.ValidationErrors"/> populated).
    /// </summary>
    public IReadOnlyList<McpAppInstallState> Discover(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("McpApp discovery is disabled");
            return [];
        }

        var results = new List<McpAppInstallState>();

        foreach (var rawPath in _config.DiscoveryPaths)
        {
            ct.ThrowIfCancellationRequested();

            var resolvedPath = ResolvePath(rawPath);
            if (!Directory.Exists(resolvedPath))
            {
                _logger.LogDebug("McpApp discovery path does not exist: {Path} (resolved: {ResolvedPath})",
                    rawPath, resolvedPath);
                continue;
            }

            _logger.LogInformation("Scanning for MCP Apps in: {Path}", resolvedPath);

            try
            {
                var manifestFiles = Directory.GetFiles(
                    resolvedPath,
                    ManifestFileName,
                    SearchOption.AllDirectories);

                foreach (var manifestFile in manifestFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var state = TryLoadManifest(manifestFile);
                    if (state is not null)
                        results.Add(state);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error scanning MCP App discovery path: {Path}", resolvedPath);
            }
        }

        _logger.LogInformation("MCP App discovery complete: {Count} app(s) found", results.Count);
        return results;
    }

    /// <summary>
    /// Validates allow/deny filtering for a discovered app.
    /// Returns true if the app is allowed to load.
    /// </summary>
    public bool IsAppAllowed(McpAppInstallState state)
    {
        var appId = state.Manifest.Id;

        // Check per-app entry first
        if (_config.Entries.TryGetValue(appId, out var entry))
        {
            if (!entry.Enabled)
            {
                _logger.LogInformation("McpApp '{AppId}' is disabled in Entries config", appId);
                state.Lifecycle = McpAppLifecycle.Disabled;
                state.ValidationErrors.Add("App is disabled in Entries configuration.");
                return false;
            }
        }

        // Check denylist first (deny wins)
        if (_config.Deny.Length > 0)
        {
            foreach (var denied in _config.Deny)
            {
                if (MatchesGlob(appId, denied))
                {
                    _logger.LogInformation("McpApp '{AppId}' is denied", appId);
                    state.Lifecycle = McpAppLifecycle.Disabled;
                    state.ValidationErrors.Add($"App '{appId}' matches deny pattern '{denied}'.");
                    return false;
                }
            }
        }

        // Check allowlist
        if (_config.Allow.Length > 0)
        {
            var isAllowed = false;
            foreach (var allowed in _config.Allow)
            {
                if (MatchesGlob(appId, allowed))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed)
            {
                _logger.LogInformation("McpApp '{AppId}' is not in the allowlist", appId);
                state.Lifecycle = McpAppLifecycle.Disabled;
                state.ValidationErrors.Add($"App '{appId}' is not in the allowlist.");
                return false;
            }
        }

        return true;
    }

    private McpAppInstallState? TryLoadManifest(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, McpAppManifestJsonContext.Default.McpAppManifest);

            if (manifest is null)
            {
                _logger.LogWarning("Failed to deserialize McpApp manifest: {Path}", manifestPath);
                return null;
            }

            var rootPath = Path.GetDirectoryName(manifestPath)!;
            var state = new McpAppInstallState
            {
                Manifest = manifest,
                ManifestPath = manifestPath,
                RootPath = rootPath,
            };

            // Validate required fields
            ValidateManifest(state);

            if (!state.IsValid)
            {
                _logger.LogWarning("McpApp '{AppId}' at {Path} has validation errors: {Errors}",
                    manifest.Id, manifestPath, string.Join("; ", state.ValidationErrors));
            }
            else
            {
                state.Lifecycle = McpAppLifecycle.Validated;
                _logger.LogInformation("Discovered McpApp '{AppId}' ({Name}) v{Version} at {Path}",
                    manifest.Id, manifest.Name ?? manifest.Id, manifest.Version, manifestPath);
            }

            return state;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in McpApp manifest: {Path}", manifestPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading McpApp manifest: {Path}", manifestPath);
            return null;
        }
    }

    private static void ValidateManifest(McpAppInstallState state)
    {
        var manifest = state.Manifest;

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            state.IsValid = false;
            state.ValidationErrors.Add("Manifest 'id' is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            state.ValidationErrors.Add("Manifest 'version' is recommended.");
        }

        var transport = manifest.Transport?.ToLowerInvariant();
        switch (transport)
        {
            case "stdio":
                if (string.IsNullOrWhiteSpace(manifest.Command))
                {
                    state.IsValid = false;
                    state.ValidationErrors.Add("Transport is 'stdio' but no 'command' is specified.");
                }
                break;
            case "http":
                if (string.IsNullOrWhiteSpace(manifest.Url))
                {
                    state.IsValid = false;
                    state.ValidationErrors.Add("Transport is 'http' but no 'url' is specified.");
                }
                break;
            case "inprocess":
                // In-process doesn't need command/url — validated at runtime
                break;
            default:
                state.IsValid = false;
                state.ValidationErrors.Add($"Unsupported transport '{manifest.Transport}'. Use 'stdio', 'http', or 'inprocess'.");
                break;
        }
    }

    private static string ResolvePath(string rawPath)
    {
        // Resolve environment variables in the path
        var resolved = SecretResolver.Resolve(rawPath) ?? rawPath;

        if (Path.IsPathRooted(resolved))
            return resolved;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, resolved));
    }

    private static bool MatchesGlob(string value, string pattern)
    {
        if (pattern == "*")
            return true;

        // Simple wildcard matching: supports * at start/end or both
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
            return value.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);

        if (pattern.StartsWith('*'))
            return value.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);

        if (pattern.EndsWith('*'))
            return value.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);

        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
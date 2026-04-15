using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Cli;

/// <summary>
/// Built-in plugin management commands: install, remove, list, search.
/// Fetches plugins from npm (which also hosts ClawHub packages) and installs
/// them into the extensions directory for the plugin bridge to discover.
/// </summary>
internal static class PluginCommands
{
    private const string EnvWorkspace = "OPENCLAW_WORKSPACE";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "install" => await InstallAsync(rest),
            "remove" or "uninstall" => await RemoveAsync(rest),
            "list" or "ls" => ListInstalled(rest),
            "search" => await SearchAsync(rest),
            _ => UnknownSubcommand(subcommand)
        };
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var packageSpec = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(packageSpec))
        {
            Console.Error.WriteLine("Usage: openclaw plugins install <package-name|local-path>");
            return 2;
        }

        var global = args.Contains("--global") || args.Contains("-g");
        var dryRun = args.Contains("--dry-run");
        var extensionsDir = ResolveExtensionsDir(global);

        Directory.CreateDirectory(extensionsDir);

        // Check if it's a local path
        if (Directory.Exists(packageSpec) || File.Exists(packageSpec))
        {
            return await InstallFromLocalAsync(packageSpec, extensionsDir, dryRun);
        }

        // Install from npm/ClawHub
        return await InstallFromNpmAsync(packageSpec, extensionsDir, dryRun);
    }

    private static async Task<int> InstallFromNpmAsync(string packageSpec, string extensionsDir, bool dryRun)
    {
        Console.WriteLine(dryRun
            ? $"Dry-run install for {packageSpec} from npm..."
            : $"Installing {packageSpec} from npm...");

        // Use npm pack to download the tarball, then extract into extensions dir
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-install-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Step 1: npm pack to download tarball
            var packResult = await RunNpmAsync($"pack {packageSpec} --pack-destination {Quote(tempDir)}", tempDir);
            if (packResult.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to download package: {packResult.Stderr}");
                return 1;
            }

            // Find the downloaded tarball
            var tarballs = Directory.GetFiles(tempDir, "*.tgz");
            if (tarballs.Length == 0)
            {
                Console.Error.WriteLine("No tarball downloaded.");
                return 1;
            }

            var tarball = tarballs[0];

            // Step 2: Extract tarball into a temp staging directory
            var stagingDir = Path.Combine(tempDir, "staging");
            Directory.CreateDirectory(stagingDir);

            var extractResult = await RunProcessAsync("tar", $"xzf {Quote(tarball)} -C {Quote(stagingDir)}", tempDir);
            if (extractResult.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to extract package: {extractResult.Stderr}");
                return 1;
            }

            // npm pack creates a 'package' directory inside the tarball
            var packageDir = Path.Combine(stagingDir, "package");
            if (!Directory.Exists(packageDir))
            {
                // Some tarballs use a different root
                var dirs = Directory.GetDirectories(stagingDir);
                packageDir = dirs.Length > 0 ? dirs[0] : stagingDir;
            }

            // Step 3: Inspect package before copying into extensions
            var inspection = InspectCandidate(packageDir, packageSpec, sourceIsNpm: true);
            if (!inspection.Success)
            {
                Console.Error.WriteLine(inspection.ErrorMessage);
                return 1;
            }

            PrintInspection(inspection);
            if (!inspection.CanInstall)
                return 1;
            if (dryRun)
                return 0;

            // Step 4: Determine plugin name from manifest or package.json
            var pluginName = ResolvePluginName(packageDir) ?? SanitizePackageName(packageSpec);

            // Step 5: Move to extensions directory
            var targetDir = Path.Combine(extensionsDir, pluginName);
            if (Directory.Exists(targetDir))
            {
                Console.WriteLine($"Replacing existing plugin '{pluginName}'...");
                Directory.Delete(targetDir, recursive: true);
            }

            CopyDirectory(packageDir, targetDir);

            // Step 6: Install npm dependencies if package.json exists
            var packageJson = Path.Combine(targetDir, "package.json");
            if (File.Exists(packageJson))
            {
                Console.WriteLine("Installing dependencies...");
                var npmInstall = await RunNpmAsync("install --production --no-optional", targetDir);
                if (npmInstall.ExitCode != 0)
                    Console.Error.WriteLine($"Warning: npm install failed: {npmInstall.Stderr}");
            }

            Console.WriteLine($"Installed '{pluginName}' to {targetDir}");
            Console.WriteLine("Restart the gateway to load the plugin.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<int> InstallFromLocalAsync(string localPath, string extensionsDir, bool dryRun)
    {
        var sourcePath = Path.GetFullPath(localPath);

        if (File.Exists(sourcePath) && sourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            // Extract tarball
            var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-install-{Guid.NewGuid():N}"[..24]);
            Directory.CreateDirectory(tempDir);

            try
            {
                var extractResult = await RunProcessAsync("tar", $"xzf {Quote(sourcePath)} -C {Quote(tempDir)}", tempDir);
                if (extractResult.ExitCode != 0)
                {
                    Console.Error.WriteLine($"Failed to extract: {extractResult.Stderr}");
                    return 1;
                }

                var packageDir = Path.Combine(tempDir, "package");
                if (!Directory.Exists(packageDir))
                {
                    var dirs = Directory.GetDirectories(tempDir);
                    packageDir = dirs.Length > 0 ? dirs[0] : tempDir;
                }

                var inspection = InspectCandidate(packageDir, localPath, sourceIsNpm: false);
                if (!inspection.Success)
                {
                    Console.Error.WriteLine(inspection.ErrorMessage);
                    return 1;
                }

                PrintInspection(inspection);
                if (!inspection.CanInstall)
                    return 1;
                if (dryRun)
                    return 0;

                var pluginName = ResolvePluginName(packageDir) ?? Path.GetFileNameWithoutExtension(localPath);
                var targetDir = Path.Combine(extensionsDir, pluginName);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, recursive: true);

                CopyDirectory(packageDir, targetDir);
                Console.WriteLine($"Installed '{pluginName}' from tarball to {targetDir}");
                return 0;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }

        if (Directory.Exists(sourcePath))
        {
            var inspection = InspectCandidate(sourcePath, localPath, sourceIsNpm: false);
            if (!inspection.Success)
            {
                Console.Error.WriteLine(inspection.ErrorMessage);
                return 1;
            }

            PrintInspection(inspection);
            if (!inspection.CanInstall)
                return 1;
            if (dryRun)
                return 0;

            var pluginName = ResolvePluginName(sourcePath) ?? Path.GetFileName(sourcePath);
            var targetDir = Path.Combine(extensionsDir, pluginName);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            CopyDirectory(sourcePath, targetDir);
            Console.WriteLine($"Installed '{pluginName}' from local directory to {targetDir}");
            return 0;
        }

        Console.Error.WriteLine($"Path not found: {localPath}");
        return 1;
    }

    private static async Task<int> RemoveAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: openclaw plugins remove <plugin-name>");
            return 2;
        }

        var pluginName = args[0];
        var global = args.Contains("--global") || args.Contains("-g");
        var extensionsDir = ResolveExtensionsDir(global);

        var targetDir = Path.Combine(extensionsDir, pluginName);
        if (!Directory.Exists(targetDir))
        {
            // Try sanitized name
            targetDir = Path.Combine(extensionsDir, SanitizePackageName(pluginName));
        }

        if (!Directory.Exists(targetDir))
        {
            Console.Error.WriteLine($"Plugin '{pluginName}' not found in {extensionsDir}");
            return 1;
        }

        Directory.Delete(targetDir, recursive: true);
        Console.WriteLine($"Removed '{pluginName}' from {extensionsDir}");
        Console.WriteLine("Restart the gateway to unload the plugin.");
        return 0;
    }

    private static int ListInstalled(string[] args)
    {
        var global = args.Contains("--global") || args.Contains("-g");
        var extensionsDir = ResolveExtensionsDir(global);

        if (!Directory.Exists(extensionsDir))
        {
            Console.WriteLine("No plugins installed.");
            return 0;
        }

        var plugins = PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [extensionsDir] }
        });

        if (plugins.Count == 0)
        {
            Console.WriteLine("No plugins installed.");
            return 0;
        }

        Console.WriteLine($"Installed plugins ({plugins.Count}):");
        foreach (var plugin in plugins)
        {
            var name = plugin.Manifest.Name ?? plugin.Manifest.Id ?? Path.GetFileName(plugin.RootPath);
            var version = plugin.Manifest.Version ?? "?";
            var desc = plugin.Manifest.Description ?? "";
            var hasStructuredSurface =
                plugin.Manifest.Channels.Length > 0 ||
                plugin.Manifest.Providers.Length > 0 ||
                plugin.Manifest.Skills.Length > 0 ||
                plugin.Manifest.ConfigSchema is not null;
            var trustLevel = DetermineTrustLevel(plugin.RootPath, sourceIsNpm: false, errorCount: 0, hasStructuredSurface);
            Console.WriteLine($"  {name} ({version}) - {desc}");
            Console.WriteLine($"    Path: {plugin.RootPath}");
            Console.WriteLine($"    Trust: {trustLevel}");
            Console.WriteLine($"    Trust reason: {DetermineTrustReason(trustLevel, errorCount: 0, hasStructuredSurface)}");
            Console.WriteLine($"    Declared: {BuildDeclaredSurfaceSummary(plugin.Manifest)}");
        }

        return 0;
    }

    private static async Task<int> SearchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: openclaw plugins search <query>");
            return 2;
        }

        var query = string.Join(' ', args);
        Console.WriteLine($"Searching npm for '{query}'...");

        var result = await RunNpmAsync($"search openclaw-plugin {query} --json", Directory.GetCurrentDirectory());
        if (result.ExitCode != 0)
        {
            // Fallback to non-JSON search
            var textResult = await RunNpmAsync($"search openclaw {query}", Directory.GetCurrentDirectory());
            Console.WriteLine(textResult.Stdout);
            return textResult.ExitCode;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var packages = doc.RootElement;
            if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() == 0)
            {
                Console.WriteLine("No packages found.");
                return 0;
            }

            Console.WriteLine($"Found {packages.GetArrayLength()} package(s):");
            foreach (var pkg in packages.EnumerateArray())
            {
                var name = pkg.TryGetProperty("name", out var n) ? n.GetString() : "?";
                var desc = pkg.TryGetProperty("description", out var d) ? d.GetString() : "";
                var version = pkg.TryGetProperty("version", out var v) ? v.GetString() : "";
                Console.WriteLine($"  {name}@{version} - {desc}");
            }
        }
        catch
        {
            Console.WriteLine(result.Stdout);
        }

        return 0;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string ResolveExtensionsDir(bool global)
    {
        if (global)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "extensions");
        }

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(workspace, ".openclaw", "extensions");

        var home2 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home2, ".openclaw", "extensions");
    }

    private static string? ResolvePluginName(string packageDir)
    {
        // Try manifest
        var manifestPath = Path.Combine(packageDir, "openclaw.plugin.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
                if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    return SanitizePackageName(name.GetString()!);
            }
            catch { /* fall through */ }
        }

        // Try package.json
        var packageJsonPath = Path.Combine(packageDir, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                var json = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    return SanitizePackageName(name.GetString()!);
            }
            catch { /* fall through */ }
        }

        return null;
    }

    private static string SanitizePackageName(string name)
    {
        // @scope/package → scope-package
        return name.Replace('@', ' ').Replace('/', '-').Trim().Replace(' ', '-');
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunNpmAsync(string arguments, string workingDirectory)
    {
        var npmCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "npm.cmd" : "npm";
        return await RunProcessAsync(npmCmd, arguments, workingDirectory);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return (127, "", $"Command not found: {fileName}. Ensure npm is installed.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));

        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName is "node_modules" or ".git")
                continue;
            CopyDirectory(dir, Path.Combine(destination, dirName));
        }
    }

    private static string Quote(string path)
        => path.Contains(' ') ? $"\"{path}\"" : path;

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            openclaw plugins — Manage OpenClaw plugins

            Usage:
              openclaw plugins install <package|path|tarball>  Install a plugin from npm/ClawHub or local source
              openclaw plugins remove <plugin-name>            Remove an installed plugin
              openclaw plugins list                            List installed plugins
              openclaw plugins search <query>                  Search npm for OpenClaw plugins

            Options:
              -g, --global    Use global extensions directory (~/.openclaw/extensions)
              --dry-run       Inspect the plugin and print declared surfaces without installing it

            Examples:
              openclaw plugins install @sliverp/qqbot
              openclaw plugins install @opik/opik-openclaw
              openclaw plugins install ./my-local-plugin
              openclaw plugins install ./my-plugin.tgz
              openclaw plugins remove qqbot
              openclaw plugins list
              openclaw plugins search openclaw dingtalk
            """);
    }

    internal static PluginInstallInspection InspectCandidate(string rootPath, string sourceLabel, bool sourceIsNpm)
    {
        var manifestPath = Path.Combine(rootPath, "openclaw.plugin.json");
        PluginManifest? manifest = null;
        string? packageName = null;
        string? version = null;
        string? description = null;
        var hasManifest = false;
        var hasExtensionsConfig = false;

        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.PluginManifest);
                hasManifest = manifest is not null;
            }
            catch (Exception ex)
            {
                return PluginInstallInspection.Failure($"Invalid plugin manifest at {manifestPath}: {ex.Message}");
            }
        }

        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                using var stream = File.OpenRead(packageJsonPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                packageName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                version = root.TryGetProperty("version", out var versionProp) ? versionProp.GetString() : null;
                description = root.TryGetProperty("description", out var descriptionProp) ? descriptionProp.GetString() : null;
                hasExtensionsConfig =
                    root.TryGetProperty("openclaw", out var openClaw) &&
                    openClaw.TryGetProperty("extensions", out var extensions) &&
                    extensions.ValueKind == JsonValueKind.Array &&
                    extensions.GetArrayLength() > 0;
            }
            catch (Exception ex)
            {
                return PluginInstallInspection.Failure($"Invalid package.json at {packageJsonPath}: {ex.Message}");
            }
        }

        var entryPath = FindEntryFile(rootPath);
        if (entryPath is null)
            return PluginInstallInspection.Failure($"No plugin entry file found under {rootPath}. Expected openclaw.plugin.json with an entry, package.json openclaw.extensions, or an index.ts/js/mjs file.");

        var effectiveManifest = manifest ?? new PluginManifest
        {
            Id = ResolvePluginName(rootPath) ?? SanitizePackageName(packageName ?? Path.GetFileName(rootPath)),
            Name = packageName,
            Description = description,
            Version = version
        };

        var warnings = new List<string>();
        var diagnostics = new List<PluginCompatibilityDiagnostic>();
        if (!hasManifest)
            warnings.Add("No openclaw.plugin.json manifest was found. Install is allowed, but declared capabilities and config validation metadata are limited.");
        if (!hasExtensionsConfig && !hasManifest)
            warnings.Add("Package relies on standalone entry-file discovery. Review the source before enabling it on a public bind.");

        var declaredChannels = effectiveManifest.Channels ?? [];
        var declaredProviders = effectiveManifest.Providers ?? [];
        var declaredSkills = effectiveManifest.Skills ?? [];

        if (effectiveManifest.ConfigSchema is not null)
            diagnostics.AddRange(PluginConfigValidator.Validate(effectiveManifest, config: null));

        foreach (var skillDir in declaredSkills)
        {
            if (!PluginDiscovery.TryResolveContainedPath(rootPath, skillDir, out var resolvedSkillDir))
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Severity = "error",
                    Code = "skill_path_outside_root",
                    Message = $"Skill directory '{skillDir}' resolves outside the plugin root.",
                    Surface = "skills",
                    Path = rootPath
                });
                continue;
            }

            if (!Directory.Exists(resolvedSkillDir))
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Severity = "error",
                    Code = "skill_directory_missing",
                    Message = $"Declared skill directory '{skillDir}' does not exist.",
                    Surface = "skills",
                    Path = resolvedSkillDir
                });
                continue;
            }

            var rootSkillFile = Path.Combine(resolvedSkillDir, "SKILL.md");
            var nestedSkillFiles = Directory.GetFiles(resolvedSkillDir, "SKILL.md", SearchOption.AllDirectories);
            if (!File.Exists(rootSkillFile) && nestedSkillFiles.Length == 0)
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Severity = "warning",
                    Code = "skill_directory_empty",
                    Message = $"Declared skill directory '{skillDir}' does not currently contain a SKILL.md file.",
                    Surface = "skills",
                    Path = resolvedSkillDir
                });
            }
        }

        var errorCount = diagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warningCount = diagnostics.Count - errorCount;
        var compatibilityStatus = errorCount > 0
            ? "errors"
            : warningCount > 0
                ? "warnings"
                : "verified";
        var hasStructuredSurface =
            declaredChannels.Length > 0 ||
            declaredProviders.Length > 0 ||
            declaredSkills.Length > 0 ||
            effectiveManifest.ConfigSchema is not null;
        var trustLevel = DetermineTrustLevel(sourceLabel, sourceIsNpm, errorCount, hasStructuredSurface);
        var trustReason = DetermineTrustReason(trustLevel, errorCount, hasStructuredSurface);

        return new PluginInstallInspection
        {
            Success = true,
            CanInstall = errorCount == 0,
            PluginId = effectiveManifest.Id,
            DisplayName = effectiveManifest.Name ?? effectiveManifest.Id,
            Version = effectiveManifest.Version ?? version ?? "?",
            Description = effectiveManifest.Description ?? description ?? "",
            EntryPath = entryPath,
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            CompatibilityStatus = compatibilityStatus,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            DeclaredSurface = BuildDeclaredSurfaceSummary(effectiveManifest),
            Diagnostics = diagnostics,
            Warnings = warnings
        };
    }

    private static void PrintInspection(PluginInstallInspection inspection)
    {
        Console.WriteLine($"Plugin: {inspection.DisplayName} ({inspection.PluginId})");
        Console.WriteLine($"Version: {inspection.Version}");
        if (!string.IsNullOrWhiteSpace(inspection.Description))
            Console.WriteLine($"Description: {inspection.Description}");
        Console.WriteLine($"Trust: {inspection.TrustLevel}");
        Console.WriteLine($"Trust reason: {inspection.TrustReason}");
        Console.WriteLine($"Compatibility: {inspection.CompatibilityStatus} (errors={inspection.ErrorCount}, warnings={inspection.WarningCount})");
        Console.WriteLine($"Declared: {inspection.DeclaredSurface}");
        Console.WriteLine($"Entry: {inspection.EntryPath}");
        foreach (var warning in inspection.Warnings)
            Console.WriteLine($"Warning: {warning}");
        foreach (var diagnostic in inspection.Diagnostics)
            Console.WriteLine($"{(string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning")}: [{diagnostic.Code}] {diagnostic.Message}");
        if (!inspection.CanInstall)
            Console.WriteLine("Install blocked: compatibility verification reported one or more errors.");
    }

    private static string DetermineTrustLevel(string sourceLabel, bool sourceIsNpm, int errorCount, bool hasStructuredSurface)
    {
        if (sourceIsNpm &&
            (sourceLabel.StartsWith("@clawdotnet/", StringComparison.OrdinalIgnoreCase) ||
             sourceLabel.StartsWith("@openclaw/", StringComparison.OrdinalIgnoreCase)))
        {
            return "first-party";
        }

        if (hasStructuredSurface && errorCount == 0)
        {
            return "upstream-compatible";
        }

        return "untrusted";
    }

    private static string DetermineTrustReason(string trustLevel, int errorCount, bool hasStructuredSurface)
        => trustLevel switch
        {
            "first-party" => "Package source matches an official OpenClaw or ClawDotNet scope.",
            "upstream-compatible" => "Plugin declares structured OpenClaw surfaces and passed install-time compatibility checks.",
            _ when hasStructuredSurface && errorCount > 0 => "Plugin declares OpenClaw surfaces, but compatibility verification reported blocking errors.",
            _ => "Plugin relies on entry discovery without a structured manifest-backed capability declaration."
        };

    private static string BuildDeclaredSurfaceSummary(PluginManifest manifest)
    {
        var items = new List<string>();
        var channels = manifest.Channels ?? [];
        var providers = manifest.Providers ?? [];
        var skills = manifest.Skills ?? [];
        if (channels.Length > 0)
            items.Add($"channels={string.Join(",", channels)}");
        if (providers.Length > 0)
            items.Add($"providers={string.Join(",", providers)}");
        if (skills.Length > 0)
            items.Add($"skills={skills.Length}");
        if (manifest.ConfigSchema is not null)
            items.Add("config_schema");

        return items.Count == 0 ? "entry-only" : string.Join(" | ", items);
    }

    private static string? FindEntryFile(string rootPath)
    {
        foreach (var candidate in new[] { "index.ts", "index.js", "index.mjs", "src/index.ts", "src/index.js", "src/index.mjs" })
        {
            var path = Path.Combine(rootPath, candidate);
            if (File.Exists(path))
                return path;
        }

        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                using var stream = File.OpenRead(packageJsonPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.TryGetProperty("openclaw", out var openClaw) &&
                    openClaw.TryGetProperty("extensions", out var extensions) &&
                    extensions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var extension in extensions.EnumerateArray())
                    {
                        var relPath = extension.GetString();
                        if (string.IsNullOrWhiteSpace(relPath))
                            continue;

                        if (PluginDiscovery.TryResolveContainedPath(rootPath, relPath, out var resolvedPath) &&
                            File.Exists(resolvedPath))
                        {
                            return resolvedPath;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    internal sealed class PluginInstallInspection
    {
        public required bool Success { get; init; }
        public bool CanInstall { get; init; }
        public string? ErrorMessage { get; init; }
        public string PluginId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Version { get; init; } = "";
        public string Description { get; init; } = "";
        public string EntryPath { get; init; } = "";
        public string TrustLevel { get; init; } = "";
        public string TrustReason { get; init; } = "";
        public string CompatibilityStatus { get; init; } = "";
        public int ErrorCount { get; init; }
        public int WarningCount { get; init; }
        public string DeclaredSurface { get; init; } = "";
        public IReadOnlyList<PluginCompatibilityDiagnostic> Diagnostics { get; init; } = [];
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public static PluginInstallInspection Failure(string errorMessage)
            => new() { Success = false, ErrorMessage = errorMessage };
    }
}

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
            "inspect" => await InspectAsync(rest),
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

            // Step 5: stage dependencies and atomically replace the installed plugin
            var targetDir = Path.Combine(extensionsDir, pluginName);
            var installResult = await InstallPreparedDirectoryAsync(
                packageDir,
                targetDir,
                packageSpec,
                sourceIsNpm: true);
            if (!installResult.Success)
            {
                Console.Error.WriteLine(installResult.Error);
                return 1;
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
                var installResult = await InstallPreparedDirectoryAsync(
                    packageDir,
                    targetDir,
                    localPath,
                    sourceIsNpm: false);
                if (!installResult.Success)
                {
                    Console.Error.WriteLine(installResult.Error);
                    return 1;
                }
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
            var installResult = await InstallPreparedDirectoryAsync(
                sourcePath,
                targetDir,
                localPath,
                sourceIsNpm: false);
            if (!installResult.Success)
            {
                Console.Error.WriteLine(installResult.Error);
                return 1;
            }
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
            Console.WriteLine($"    Format: {plugin.Format}");
            if (plugin.Format == PluginFormats.Bundle)
                Console.WriteLine($"    Bundle format: {plugin.BundleFormat}");
            Console.WriteLine($"    Trust: {trustLevel}");
            Console.WriteLine($"    Trust reason: {DetermineTrustReason(trustLevel, errorCount: 0, hasStructuredSurface)}");
            Console.WriteLine($"    Declared: {BuildDeclaredSurfaceSummary(plugin.Manifest, plugin)}");
        }

        return 0;
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        var target = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("Usage: openclaw plugins inspect <plugin-name|local-directory> [--runtime]");
            return 2;
        }

        var candidatePath = target;
        if (!Directory.Exists(candidatePath) &&
            !File.Exists(candidatePath) &&
            !Path.IsPathRooted(candidatePath))
        {
            var extensionsDir = ResolveExtensionsDir(args.Contains("--global") || args.Contains("-g"));
            candidatePath = Path.Combine(extensionsDir, target);
            if (!Directory.Exists(candidatePath))
                candidatePath = Path.Combine(extensionsDir, SanitizePackageName(target));
        }

        if (!Directory.Exists(candidatePath) && !File.Exists(candidatePath))
        {
            Console.Error.WriteLine($"Plugin '{target}' was not found.");
            return 1;
        }

        var rootPath = Directory.Exists(candidatePath)
            ? Path.GetFullPath(candidatePath)
            : Path.GetDirectoryName(Path.GetFullPath(candidatePath))!;
        var inspection = InspectCandidate(rootPath, target, sourceIsNpm: false);
        if (!inspection.Success)
        {
            Console.Error.WriteLine(inspection.ErrorMessage);
            return 1;
        }

        PrintInspection(inspection);
        if (!inspection.CanInstall)
            return 1;
        if (!args.Contains("--runtime"))
            return 0;

        if (inspection.Format == PluginFormats.Bundle)
        {
            Console.WriteLine("Runtime: content bundle; no arbitrary bundle module was executed.");
            return 0;
        }

        var runtime = await InspectRuntimeAsync(inspection.EntryPath, inspection.PluginId, CancellationToken.None);
        Console.WriteLine($"Runtime: {(runtime.Compatible ? "compatible" : "incompatible")}");
        Console.WriteLine($"Registered: tools={runtime.ToolCount}, channels={runtime.ChannelCount}, commands={runtime.CommandCount}, cli={runtime.CliCommandCount}, providers={runtime.ProviderCount}");
        foreach (var diagnostic in runtime.Diagnostics)
            Console.WriteLine($"{(diagnostic.Severity == "error" ? "Error" : "Warning")}: [{diagnostic.Code}] {diagnostic.Message}");
        if (!runtime.Compatible && !string.IsNullOrWhiteSpace(runtime.Error))
            Console.Error.WriteLine(runtime.Error);
        return runtime.Compatible ? 0 : 1;
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
        // On Windows, launching a batch file by bare name expands %~dp0 against the
        // working directory instead of the batch file's own location. npm.cmd's shim
        // locates npm-prefix.js/npm-cli.js via %~dp0, so a bare-name launch makes it
        // point inside the working directory and fail with MODULE_NOT_FOUND. Launch
        // by full path so %~dp0 is the shim's own directory.
        var npmCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ResolveNpmCmdPath(Environment.GetEnvironmentVariable("PATH"))
            : "npm";
        return await RunProcessAsync(npmCmd, arguments, workingDirectory);
    }

    internal static string ResolveNpmCmdPath(string? pathEnv)
    {
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0)
                    continue;
                var candidate = Path.Combine(trimmed, "npm.cmd");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        // Not found: fall back to the bare name; the file-not-found path below
        // reports "Command not found" to the caller.
        return "npm.cmd";
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
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
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

    internal static async Task<(bool Success, string? Error)> InstallPreparedDirectoryAsync(
        string sourceDir,
        string targetDir,
        string sourceLabel,
        bool sourceIsNpm)
    {
        var parentDir = Path.GetDirectoryName(targetDir)
            ?? throw new InvalidOperationException($"Plugin target '{targetDir}' has no parent directory.");
        Directory.CreateDirectory(parentDir);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var stagingDir = Path.Combine(parentDir, $".{Path.GetFileName(targetDir)}.installing-{suffix}");
        var backupDir = Path.Combine(parentDir, $".{Path.GetFileName(targetDir)}.backup-{suffix}");

        try
        {
            CopyDirectory(sourceDir, stagingDir);

            var stagedInspection = InspectCandidate(stagingDir, sourceLabel, sourceIsNpm);
            if (!stagedInspection.Success)
                return (false, $"Staged plugin inspection failed; the existing plugin was preserved: {stagedInspection.ErrorMessage}");
            if (!stagedInspection.CanInstall)
            {
                var codes = string.Join(", ", stagedInspection.Diagnostics.Select(static item => item.Code).Distinct(StringComparer.Ordinal));
                return (false, $"Staged plugin compatibility failed; the existing plugin was preserved. Diagnostics: {codes}");
            }

            var packageJson = Path.Combine(stagingDir, "package.json");
            if (File.Exists(packageJson) && stagedInspection.Format != PluginFormats.Bundle)
            {
                Console.WriteLine("Installing dependencies in staging...");
                var npmInstall = await RunNpmAsync("install --ignore-scripts --omit=dev --omit=optional", stagingDir);
                if (npmInstall.ExitCode != 0)
                    return (false, $"Dependency installation failed; the existing plugin was preserved: {npmInstall.Stderr}");

                stagedInspection = InspectCandidate(stagingDir, sourceLabel, sourceIsNpm);
                if (!stagedInspection.Success)
                    return (false, $"Post-dependency inspection failed; the existing plugin was preserved: {stagedInspection.ErrorMessage}");
                if (!stagedInspection.CanInstall)
                {
                    var codes = string.Join(", ", stagedInspection.Diagnostics.Select(static item => item.Code).Distinct(StringComparer.Ordinal));
                    return (false, $"Post-dependency compatibility inspection failed; the existing plugin was preserved. Diagnostics: {codes}");
                }
            }

            if (Path.GetExtension(stagedInspection.EntryPath).Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                stagedInspection.Format != PluginFormats.Bundle &&
                !HasLocalJiti(stagedInspection.EntryPath, stagingDir))
            {
                Console.WriteLine("Installing the TypeScript runtime dependency jiti in staging...");
                var jitiInstall = await RunNpmAsync("install --ignore-scripts --no-save --omit=dev --omit=optional jiti", stagingDir);
                if (jitiInstall.ExitCode != 0)
                    return (false, $"TypeScript runtime dependency installation failed; the existing plugin was preserved: {jitiInstall.Stderr}");
            }

            if (stagedInspection.Format != PluginFormats.Bundle)
            {
                var runtimeInspection = await InspectRuntimeAsync(
                    stagedInspection.EntryPath,
                    stagedInspection.PluginId,
                    CancellationToken.None);
                if (!runtimeInspection.Compatible)
                {
                    var details = runtimeInspection.Diagnostics.Count == 0
                        ? runtimeInspection.Error ?? "unknown runtime inspection failure"
                        : string.Join(", ", runtimeInspection.Diagnostics.Select(static item => item.Code).Distinct(StringComparer.Ordinal));
                    return (false, $"Plugin runtime inspection failed; the existing plugin was preserved. Diagnostics: {details}");
                }
            }

            if (Directory.Exists(targetDir))
            {
                Console.WriteLine($"Replacing existing plugin '{Path.GetFileName(targetDir)}' atomically...");
                Directory.Move(targetDir, backupDir);
            }

            try
            {
                Directory.Move(stagingDir, targetDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(backupDir) && !Directory.Exists(targetDir))
                    Directory.Move(backupDir, targetDir);
                throw;
            }

            if (Directory.Exists(backupDir))
            {
                try { Directory.Delete(backupDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Successful install; a stale backup is recoverable.
                    Debug.WriteLine($"Unable to delete stale plugin backup '{backupDir}': {ex.Message}");
                }
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or JsonException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return (false, $"Plugin installation failed; the existing plugin was preserved when possible: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Unable to delete plugin staging directory '{stagingDir}': {ex.Message}");
                }
            }

            if (Directory.Exists(backupDir) && Directory.Exists(targetDir))
            {
                try { Directory.Delete(backupDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Unable to delete plugin backup directory '{backupDir}': {ex.Message}");
                }
            }
        }
    }

    private static bool HasLocalJiti(string entryPath, string rootDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var current = Path.GetDirectoryName(Path.GetFullPath(entryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (!string.IsNullOrWhiteSpace(current) &&
               (string.Equals(current, root, comparison) ||
                current.StartsWith(root + Path.DirectorySeparatorChar, comparison)))
        {
            if (Directory.Exists(Path.Combine(current, "node_modules", "jiti")))
                return true;
            if (string.Equals(current, root, comparison))
                break;
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return false;
    }

    internal static async Task<PluginRuntimeInspection> InspectRuntimeAsync(
        string entryPath,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var bridgeScript = ResolveBridgeScriptPath();
        if (bridgeScript is null)
        {
            return PluginRuntimeInspection.Failure(
                "Plugin bridge script was not found. Reinstall or republish the OpenClaw CLI.");
        }

        var nodeExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable,
                WorkingDirectory = Path.GetDirectoryName(entryPath) ?? Directory.GetCurrentDirectory(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--experimental-vm-modules");
        process.StartInfo.ArgumentList.Add(bridgeScript);
        process.StartInfo.Environment["OPENCLAW_BRIDGE_TRANSPORT_MODE"] = "stdio";

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return PluginRuntimeInspection.Failure($"Unable to start Node.js for runtime inspection: {ex.Message}");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var emptyConfig = JsonDocument.Parse("{}");
            var initRequest = new BridgeInitRequest
            {
                EntryPath = Path.GetFullPath(entryPath),
                PluginId = pluginId,
                Config = emptyConfig.RootElement.Clone(),
                Transport = new BridgeTransportRuntimeConfig { Mode = "stdio" }
            };
            var request = new BridgeRequest
            {
                Id = "inspection-1",
                Method = "init",
                Params = JsonSerializer.SerializeToElement(initRequest, CoreJsonContext.Default.BridgeInitRequest)
            };
            var requestJson = JsonSerializer.Serialize(request, CoreJsonContext.Default.BridgeRequest);
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);

            var responseLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                var stderr = await stderrTask;
                return PluginRuntimeInspection.Failure($"Plugin bridge exited without an inspection response. {stderr}".Trim());
            }

            var response = JsonSerializer.Deserialize(responseLine, CoreJsonContext.Default.BridgeResponse);
            if (response?.Error is not null)
                return PluginRuntimeInspection.Failure(response.Error.Message);
            if (response?.Result is null)
                return PluginRuntimeInspection.Failure("Plugin bridge returned an empty inspection result.");

            var result = JsonSerializer.Deserialize(
                response.Result.Value.GetRawText(),
                CoreJsonContext.Default.BridgeInitResult);
            if (result is null)
                return PluginRuntimeInspection.Failure("Plugin bridge returned an unreadable inspection result.");

            return new PluginRuntimeInspection
            {
                Compatible = result.Compatible,
                Diagnostics = result.Diagnostics,
                ToolCount = result.Tools.Length,
                ChannelCount = result.Channels.Length,
                CommandCount = result.Commands.Length,
                CliCommandCount = result.CliCommands.Length,
                ProviderCount = result.Providers.Length
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PluginRuntimeInspection.Failure("Plugin runtime inspection timed out after 20 seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or NotSupportedException)
        {
            return PluginRuntimeInspection.Failure($"Plugin runtime inspection failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                Debug.WriteLine($"Unable to terminate plugin inspection process: {ex.Message}");
            }
        }
    }

    internal static string? ResolveBridgeScriptPath()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Plugins", "plugin-bridge.mjs");
        if (File.Exists(packaged))
            return packaged;

#if DEBUG
        var source = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "OpenClaw.Agent",
            "Plugins",
            "plugin-bridge.mjs"));
        return File.Exists(source) ? source : null;
#else
        return null;
#endif
    }

    internal sealed class PluginRuntimeInspection
    {
        public bool Compatible { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<PluginCompatibilityDiagnostic> Diagnostics { get; init; } = [];
        public int ToolCount { get; init; }
        public int ChannelCount { get; init; }
        public int CommandCount { get; init; }
        public int CliCommandCount { get; init; }
        public int ProviderCount { get; init; }

        public static PluginRuntimeInspection Failure(string error)
            => new() { Compatible = false, Error = error };
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
              openclaw plugins inspect <plugin|path> [--runtime] Inspect static and optional runtime compatibility
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
              openclaw plugins inspect ./my-codex-bundle --runtime
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
                    ((openClaw.TryGetProperty("runtimeExtensions", out var runtimeExtensions) &&
                      runtimeExtensions.ValueKind == JsonValueKind.Array &&
                      runtimeExtensions.GetArrayLength() > 0) ||
                     (openClaw.TryGetProperty("extensions", out var extensions) &&
                      extensions.ValueKind == JsonValueKind.Array &&
                      extensions.GetArrayLength() > 0));
            }
            catch (Exception ex)
            {
                return PluginInstallInspection.Failure($"Invalid package.json at {packageJsonPath}: {ex.Message}");
            }
        }

        var discovery = PluginDiscovery.DiscoverWithDiagnostics(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [rootPath] }
        });
        var discoveredPlugin = discovery.Plugins.FirstOrDefault();
        var isBundle = discoveredPlugin?.Format == PluginFormats.Bundle;
        var entryPath = isBundle ? rootPath : discoveredPlugin?.EntryPath ?? FindEntryFile(rootPath);
        if (entryPath is null)
            return PluginInstallInspection.Failure($"No plugin entry file or compatible Codex/Claude/Cursor bundle was found under {rootPath}.");

        var effectiveManifest = manifest ?? discoveredPlugin?.Manifest ?? new PluginManifest
        {
            Id = ResolvePluginName(rootPath) ?? SanitizePackageName(packageName ?? Path.GetFileName(rootPath)),
            Name = packageName,
            Description = description,
            Version = version
        };

        var warnings = new List<string>();
        var diagnostics = new List<PluginCompatibilityDiagnostic>();
        diagnostics.AddRange(discovery.Reports.SelectMany(static report => report.Diagnostics));
        if (!hasManifest && !isBundle)
            warnings.Add("No openclaw.plugin.json manifest was found. Install is allowed, but declared capabilities and config validation metadata are limited.");
        if (!hasExtensionsConfig && !hasManifest && !isBundle)
            warnings.Add("Package relies on standalone entry-file discovery. Review the source before enabling it on a public bind.");

        var declaredChannels = effectiveManifest.Channels ?? [];
        var declaredProviders = effectiveManifest.Providers ?? [];
        var declaredSkills = effectiveManifest.Skills ?? [];

        if (!isBundle && effectiveManifest.ConfigSchema is not null)
            diagnostics.AddRange(PluginConfigValidator.Validate(effectiveManifest, config: null));

        if (discoveredPlugin is not null && !isBundle)
            diagnostics.AddRange(PluginPackageCompatibility.Validate(discoveredPlugin));

        if (!isBundle)
            InspectUnsupportedRuntimeSurfaces(rootPath, diagnostics);

        if (isBundle)
        {
            foreach (var capability in discoveredPlugin!.BundleDetectedCapabilities)
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Severity = "warning",
                    Code = "bundle_capability_detected_only",
                    Message = $"Bundle capability '{capability}' is detected but has no OpenClaw.NET runtime mapping.",
                    Surface = capability,
                    Path = rootPath
                });
            }
        }

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
            var isBundleCommandRoot = isBundle &&
                string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(resolvedSkillDir)), "commands", StringComparison.OrdinalIgnoreCase);
            var hasBundleCommands = isBundleCommandRoot &&
                Directory.GetFiles(resolvedSkillDir, "*.md", SearchOption.AllDirectories).Length > 0;
            if (!File.Exists(rootSkillFile) && nestedSkillFiles.Length == 0 && !hasBundleCommands)
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
                : "manifest-valid";
        var hasStructuredSurface =
            (isBundle && discoveredPlugin!.BundleMappedCapabilities.Length > 0) ||
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
            Format = discoveredPlugin?.Format ?? PluginFormats.Native,
            BundleFormat = discoveredPlugin?.BundleFormat,
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            CompatibilityStatus = compatibilityStatus,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            DeclaredSurface = BuildDeclaredSurfaceSummary(effectiveManifest, discoveredPlugin),
            Diagnostics = diagnostics,
            Warnings = warnings
        };
    }

    private static void PrintInspection(PluginInstallInspection inspection)
    {
        Console.WriteLine($"Plugin: {inspection.DisplayName} ({inspection.PluginId})");
        Console.WriteLine($"Version: {inspection.Version}");
        Console.WriteLine($"Format: {inspection.Format}");
        if (inspection.Format == PluginFormats.Bundle)
            Console.WriteLine($"Bundle format: {inspection.BundleFormat}");
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
            "upstream-compatible" => "Plugin declares structured OpenClaw surfaces and passed manifest, package metadata, and static compatibility checks.",
            _ when hasStructuredSurface && errorCount > 0 => "Plugin declares OpenClaw surfaces, but compatibility verification reported blocking errors.",
            _ => "Plugin relies on entry discovery without a structured manifest-backed capability declaration."
        };

    private static string BuildDeclaredSurfaceSummary(PluginManifest manifest, DiscoveredPlugin? plugin = null)
    {
        var items = new List<string>();
        if (plugin?.Format == PluginFormats.Bundle)
        {
            items.Add($"bundle={plugin.BundleFormat}");
            if (plugin.BundleMappedCapabilities.Length > 0)
                items.Add($"mapped={string.Join(",", plugin.BundleMappedCapabilities)}");
            if (plugin.BundleDetectedCapabilities.Length > 0)
                items.Add($"detected_only={string.Join(",", plugin.BundleDetectedCapabilities)}");
        }
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
        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                using var stream = File.OpenRead(packageJsonPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.TryGetProperty("openclaw", out var openClaw))
                {
                    var extensions = openClaw.TryGetProperty("runtimeExtensions", out var runtimeExtensions) &&
                                     runtimeExtensions.ValueKind == JsonValueKind.Array
                        ? runtimeExtensions
                        : openClaw.TryGetProperty("extensions", out var sourceExtensions) &&
                          sourceExtensions.ValueKind == JsonValueKind.Array
                            ? sourceExtensions
                            : default;
                    if (extensions.ValueKind == JsonValueKind.Array)
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        return new[] { "index.js", "index.mjs", "index.cjs", "index.ts", "src/index.js", "src/index.mjs", "src/index.cjs", "src/index.ts" }
            .Select(candidate => Path.Combine(rootPath, candidate))
            .FirstOrDefault(File.Exists);
    }

    private static void InspectUnsupportedRuntimeSurfaces(
        string rootPath,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        foreach (var file in EnumeratePluginSourceFiles(rootPath))
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            AddUnsupportedSurfaceDiagnostic(source, file, "registerGatewayMethod", "unsupported_gateway_method", diagnostics);
        }
    }

    private static IEnumerable<string> EnumeratePluginSourceFiles(string rootPath)
    {
        const int maxDepth = 16;
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((rootPath, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth < maxDepth)
            {
                foreach (var child in Directory.EnumerateDirectories(directory, "*", enumerationOptions))
                {
                    var name = Path.GetFileName(child);
                    if (name is not "node_modules" and not ".git")
                        pending.Push((child, depth + 1));
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", enumerationOptions)
                         .Where(file => Path.GetExtension(file) is ".js" or ".mjs" or ".cjs" or ".ts"))
                yield return file;
        }
    }

    private static void AddUnsupportedSurfaceDiagnostic(
        string source,
        string file,
        string apiName,
        string code,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                source,
                $@"\bapi\s*\.\s*{System.Text.RegularExpressions.Regex.Escape(apiName)}\s*\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            diagnostics.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal)))
            return;

        diagnostics.Add(new PluginCompatibilityDiagnostic
        {
            Severity = "error",
            Code = code,
            Message = $"Plugin source references api.{apiName}(), which is not supported by OpenClaw.NET.",
            Surface = apiName,
            Path = file
        });
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
        public string Format { get; init; } = PluginFormats.Native;
        public string? BundleFormat { get; init; }
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

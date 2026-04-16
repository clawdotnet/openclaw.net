using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Cli;

internal static class SetupLifecycleCommand
{
    private const string GatewayProjectRelativePath = "src/OpenClaw.Gateway/OpenClaw.Gateway.csproj";
    private const string CompanionProjectRelativePath = "src/OpenClaw.Companion/OpenClaw.Companion.csproj";

    public static int RunStatus(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
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

        var status = BuildStatus(configPath, config);
        WriteStatus(output, status);
        return 0;
    }

    public static async Task<int> RunServiceAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var parsed = CliArgs.Parse(args);
        var configPath = ResolveConfigPath(parsed);
        var platform = NormalizePlatform(parsed.GetOption("--platform") ?? "all");

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

        var repoRoot = FindRepoRoot(currentDirectory);
        if (repoRoot is null)
        {
            error.WriteLine("Could not locate the repository root from the current directory.");
            return 2;
        }

        var deployDir = GetDeployDirectory(configPath);
        Directory.CreateDirectory(deployDir);
        foreach (var file in BuildArtifacts(platform, repoRoot, configPath, config))
            await File.WriteAllTextAsync(Path.Combine(deployDir, file.Name), file.Content, CancellationToken.None);

        output.WriteLine($"Wrote deploy artifacts: {deployDir}");
        foreach (var artifact in BuildArtifactItems(deployDir))
            output.WriteLine($"- {artifact.Label}: {artifact.Path}");
        return 0;
    }

    public static async Task<int> RunLaunchAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var parsed = CliArgs.Parse(args);
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

        var repoRoot = FindRepoRoot(currentDirectory);
        if (repoRoot is null)
        {
            error.WriteLine("Could not locate the repository root from the current directory.");
            return 2;
        }

        var gatewayProjectPath = Path.Combine(repoRoot, GatewayProjectRelativePath);
        var companionProjectPath = Path.Combine(repoRoot, CompanionProjectRelativePath);
        if (!File.Exists(gatewayProjectPath) || !File.Exists(companionProjectPath))
        {
            error.WriteLine("Gateway or Companion project could not be found from the repository root.");
            return 2;
        }

        using var gateway = StartGatewayProcess(repoRoot, gatewayProjectPath, configPath);
        var baseUrl = SetupCommand.BuildReachableBaseUrl(config.BindAddress, config.Port);
        var ready = await WaitForGatewayReadyAsync(baseUrl, config.AuthToken, output, CancellationToken.None);
        if (!ready)
        {
            TryStopProcess(gateway);
            error.WriteLine("Gateway did not become ready before the startup timeout.");
            return 1;
        }

        using var companion = StartCompanionProcess(repoRoot, companionProjectPath, baseUrl, config.AuthToken ?? "");
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            output.WriteLine($"Gateway URL: {baseUrl}");
            output.WriteLine($"Admin URL: {baseUrl}/admin");
            output.WriteLine($"Auth mode: {(BindAddressClassifier.IsLoopbackBind(config.BindAddress) ? "loopback-open" : "token-or-session")}");
            output.WriteLine("Streaming logs. Press Ctrl-C to stop.");

            var gatewayStdout = PumpProcessOutputAsync(gateway.StandardOutput, "gateway", output, cts.Token);
            var gatewayStderr = PumpProcessOutputAsync(gateway.StandardError, "gateway", output, cts.Token);
            var companionStdout = PumpProcessOutputAsync(companion.StandardOutput, "companion", output, cts.Token);
            var companionStderr = PumpProcessOutputAsync(companion.StandardError, "companion", output, cts.Token);

            await WaitForExitOrCancelAsync(gateway, companion, cts.Token);
            cts.Cancel();
            await Task.WhenAll(gatewayStdout, gatewayStderr, companionStdout, companionStderr);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            TryStopProcess(companion);
            TryStopProcess(gateway);
        }

        return 0;
    }

    internal static SetupStatusResponse BuildStatus(string configPath, GatewayConfig config, OrganizationPolicySnapshot? policy = null)
    {
        var effectivePolicy = policy ?? new OrganizationPolicySnapshot();
        var publicBind = !BindAddressClassifier.IsLoopbackBind(config.BindAddress);
        var warnings = new List<string>();
        if (publicBind)
            warnings.Add("Reverse proxy and TLS are recommended for public bind deployments.");
        if (string.IsNullOrWhiteSpace(config.AuthToken))
            warnings.Add("No auth token is configured.");

        return new SetupStatusResponse
        {
            Profile = publicBind ? "public" : "local",
            BindAddress = config.BindAddress,
            Port = config.Port,
            PublicBind = publicBind,
            AuthTokenConfigured = !string.IsNullOrWhiteSpace(config.AuthToken),
            BootstrapTokenEnabled = effectivePolicy.BootstrapTokenEnabled,
            AllowedAuthModes = [.. effectivePolicy.AllowedAuthModes],
            MinimumPluginTrustLevel = effectivePolicy.MinimumPluginTrustLevel,
            ReverseProxyRecommended = publicBind,
            ReachableBaseUrl = SetupCommand.BuildReachableBaseUrl(config.BindAddress, config.Port),
            ChannelReadiness = [],
            Artifacts = BuildArtifactItems(GetDeployDirectory(configPath)),
            Warnings = warnings
        };
    }

    internal static string GetDeployDirectory(string configPath)
        => Path.Combine(Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException("Config path must have a directory."), "deploy");

    private static string ResolveConfigPath(CliArgs parsed)
        => Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? GatewayConfigFile.DefaultConfigPath));

    private static string NormalizePlatform(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "linux" or "macos" or "all"
            ? normalized
            : throw new ArgumentException("Platform must be one of: linux, macos, all.");
    }

    private static string? FindRepoRoot(string currentDirectory)
    {
        foreach (var candidate in EnumerateSearchRoots(currentDirectory))
        {
            if (File.Exists(Path.Combine(candidate, GatewayProjectRelativePath)))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchRoots(string currentDirectory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in new[]
                 {
                     currentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
                 })
        {
            if (string.IsNullOrWhiteSpace(seed))
                continue;

            var directory = new DirectoryInfo(Path.GetFullPath(seed));
            while (directory is not null && seen.Add(directory.FullName))
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }

    private static IReadOnlyList<(string Name, string Content)> BuildArtifacts(string platform, string repoRoot, string configPath, GatewayConfig config)
    {
        var deployItems = new List<(string Name, string Content)>();
        var baseUrl = SetupCommand.BuildReachableBaseUrl(config.BindAddress, config.Port);
        if (platform is "linux" or "all")
        {
            deployItems.Add(("openclaw-gateway.service", BuildSystemdUnit("OpenClaw Gateway", repoRoot, "src/OpenClaw.Gateway", $"--config {GatewayConfigFile.QuoteIfNeeded(configPath)}", null)));
            deployItems.Add(("openclaw-companion.service", BuildSystemdUnit("OpenClaw Companion", repoRoot, "src/OpenClaw.Companion", "", new Dictionary<string, string>
            {
                ["OPENCLAW_BASE_URL"] = baseUrl,
                ["OPENCLAW_AUTH_TOKEN"] = config.AuthToken ?? ""
            })));
        }

        if (platform is "macos" or "all")
        {
            deployItems.Add(("ai.openclaw.gateway.plist", BuildLaunchdPlist("ai.openclaw.gateway", repoRoot, "src/OpenClaw.Gateway", ["--config", configPath], null)));
            deployItems.Add(("ai.openclaw.companion.plist", BuildLaunchdPlist("ai.openclaw.companion", repoRoot, "src/OpenClaw.Companion", [], new Dictionary<string, string>
            {
                ["OPENCLAW_BASE_URL"] = baseUrl,
                ["OPENCLAW_AUTH_TOKEN"] = config.AuthToken ?? ""
            })));
        }

        deployItems.Add(("Caddyfile", BuildCaddyfile(config)));
        return deployItems;
    }

    private static IReadOnlyList<SetupArtifactStatusItem> BuildArtifactItems(string deployDirectory)
    {
        var items = new[]
        {
            ("gateway-systemd", "Gateway systemd unit", Path.Combine(deployDirectory, "openclaw-gateway.service")),
            ("companion-systemd", "Companion systemd unit", Path.Combine(deployDirectory, "openclaw-companion.service")),
            ("gateway-launchd", "Gateway launchd plist", Path.Combine(deployDirectory, "ai.openclaw.gateway.plist")),
            ("companion-launchd", "Companion launchd plist", Path.Combine(deployDirectory, "ai.openclaw.companion.plist")),
            ("caddy", "Caddy reverse proxy recipe", Path.Combine(deployDirectory, "Caddyfile"))
        };

        return items.Select(static item => new SetupArtifactStatusItem
        {
            Id = item.Item1,
            Label = item.Item2,
            Path = item.Item3,
            Exists = File.Exists(item.Item3),
            Status = File.Exists(item.Item3) ? "present" : "missing"
        }).ToArray();
    }

    private static string BuildSystemdUnit(string description, string repoRoot, string projectDirectory, string extraArgs, IReadOnlyDictionary<string, string>? environment)
    {
        var envLines = environment is null
            ? ""
            : string.Join(Environment.NewLine, environment.Select(static item => $"Environment=\"{item.Key}={item.Value}\"")) + Environment.NewLine;
        var argsSuffix = string.IsNullOrWhiteSpace(extraArgs) ? "" : $" -- {extraArgs}";

        return
            $$"""
            [Unit]
            Description={{description}}
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            WorkingDirectory={{repoRoot}}
            {{envLines}}ExecStart=/usr/bin/env dotnet run --project {{projectDirectory}} -c Release{{argsSuffix}}
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target
            """;
    }

    private static string BuildLaunchdPlist(string label, string repoRoot, string projectDirectory, IEnumerable<string> extraArgs, IReadOnlyDictionary<string, string>? environment)
    {
        var args = new List<string>
        {
            "/usr/bin/env",
            "dotnet",
            "run",
            "--project",
            projectDirectory,
            "-c",
            "Release"
        };
        if (extraArgs.Any())
            args.Add("--");
        args.AddRange(extraArgs);

        var argLines = string.Join(Environment.NewLine, args.Select(static item => $"    <string>{System.Security.SecurityElement.Escape(item)}</string>"));
        var envLines = environment is null
            ? ""
            : string.Join(Environment.NewLine, environment.Select(static item => $"    <key>{System.Security.SecurityElement.Escape(item.Key)}</key>{Environment.NewLine}    <string>{System.Security.SecurityElement.Escape(item.Value)}</string>")) + Environment.NewLine;

        return
            $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{{label}}</string>
              <key>WorkingDirectory</key>
              <string>{{System.Security.SecurityElement.Escape(repoRoot)}}</string>
              <key>ProgramArguments</key>
              <array>
            {{argLines}}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <true/>
            {{(string.IsNullOrEmpty(envLines) ? "" : $"  <key>EnvironmentVariables</key>{Environment.NewLine}  <dict>{Environment.NewLine}{envLines}  </dict>{Environment.NewLine}")}}</dict>
            </plist>
            """;
    }

    private static string BuildCaddyfile(GatewayConfig config)
    {
        var upstream = $"127.0.0.1:{config.Port.ToString(CultureInfo.InvariantCulture)}";
        return
            $$"""
            your-hostname.example.com {
                encode zstd gzip
                reverse_proxy {{upstream}}
            }
            """;
    }

    private static Process StartGatewayProcess(string repoRoot, string gatewayProjectPath, string configPath)
    {
        var process = CreateProcess(
            repoRoot,
            "dotnet",
            ["run", "--project", gatewayProjectPath, "-c", "Release", "--", "--config", configPath]);
        process.Start();
        return process;
    }

    private static Process StartCompanionProcess(string repoRoot, string companionProjectPath, string baseUrl, string authToken)
    {
        var process = CreateProcess(
            repoRoot,
            "dotnet",
            ["run", "--project", companionProjectPath, "-c", "Release"]);
        process.StartInfo.Environment["OPENCLAW_BASE_URL"] = baseUrl;
        process.StartInfo.Environment["OPENCLAW_AUTH_TOKEN"] = authToken;
        process.Start();
        return process;
    }

    private static Process CreateProcess(string workingDirectory, string fileName, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static async Task<bool> WaitForGatewayReadyAsync(string baseUrl, string? authToken, TextWriter output, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        if (!string.IsNullOrWhiteSpace(authToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var health = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health", cancellationToken);
                using var session = await http.GetAsync($"{baseUrl.TrimEnd('/')}/auth/session", cancellationToken);
                if (health.IsSuccessStatusCode && session.IsSuccessStatusCode)
                {
                    output.WriteLine("Gateway is ready.");
                    return true;
                }
            }
            catch
            {
            }

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    private static async Task PumpProcessOutputAsync(StreamReader reader, string prefix, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                output.WriteLine($"[{prefix}] {line}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForExitOrCancelAsync(Process gateway, Process companion, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (gateway.HasExited || companion.HasExited)
                return;

            await Task.Delay(500, cancellationToken);
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }

    private static void WriteStatus(TextWriter output, SetupStatusResponse status)
    {
        output.WriteLine($"Profile: {status.Profile}");
        output.WriteLine($"Bind: {status.BindAddress}:{status.Port}");
        output.WriteLine($"Reachable base URL: {status.ReachableBaseUrl}");
        output.WriteLine($"Public bind: {status.PublicBind.ToString().ToLowerInvariant()}");
        output.WriteLine($"Auth token configured: {status.AuthTokenConfigured.ToString().ToLowerInvariant()}");
        output.WriteLine($"Bootstrap token enabled: {status.BootstrapTokenEnabled.ToString().ToLowerInvariant()}");
        output.WriteLine($"Allowed auth modes: {string.Join(", ", status.AllowedAuthModes)}");
        output.WriteLine($"Minimum plugin trust: {status.MinimumPluginTrustLevel}");
        output.WriteLine($"Reverse proxy recommended: {status.ReverseProxyRecommended.ToString().ToLowerInvariant()}");

        output.WriteLine();
        output.WriteLine("Artifacts:");
        foreach (var artifact in status.Artifacts)
            output.WriteLine($"- {artifact.Label}: {artifact.Status} ({artifact.Path})");

        if (status.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Warnings:");
            foreach (var warning in status.Warnings)
                output.WriteLine($"- {warning}");
        }
    }
}

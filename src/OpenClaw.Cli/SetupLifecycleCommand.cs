using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;

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

    public static async Task<int> RunVerifyAsync(string[] args, TextWriter output, TextWriter error)
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

        var response = await RunLocalVerificationAsync(
            configPath,
            config,
            offline: parsed.HasFlag("--offline"),
            requireProvider: parsed.HasFlag("--require-provider"),
            source: SetupVerificationSources.Cli,
            CancellationToken.None);

        if (parsed.HasFlag("--json"))
            output.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.SetupVerificationResponse));
        else
            WriteVerification(output, response);
        return response.HasFailures ? 1 : 0;
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
        var withCompanion = parsed.HasFlag("--with-companion");
        var openBrowser = parsed.HasFlag("--open-browser");
        var skipVerify = parsed.HasFlag("--skip-verify");
        var offline = parsed.HasFlag("--offline");
        var requireProvider = parsed.HasFlag("--require-provider");

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
        if (!File.Exists(gatewayProjectPath) || (withCompanion && !File.Exists(companionProjectPath)))
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

        if (!skipVerify)
        {
            var verification = await RunLocalVerificationAsync(
                configPath,
                config,
                offline: offline,
                requireProvider: requireProvider,
                source: SetupVerificationSources.LaunchStartup,
                CancellationToken.None);
            WriteVerification(output, verification);
            if (verification.HasFailures)
            {
                TryStopProcess(gateway);
                error.WriteLine("Gateway started, but first-run verification found blocking issues.");
                return 1;
            }
        }

        Process? companion = null;
        if (withCompanion)
            companion = StartCompanionProcess(repoRoot, companionProjectPath, baseUrl, config.AuthToken ?? "");

        if (openBrowser)
        {
            if (BindAddressClassifier.IsLoopbackBind(config.BindAddress))
                TryOpenBrowser($"{baseUrl.TrimEnd('/')}/chat");
            else
                output.WriteLine("Skipping --open-browser because the configured bind is not loopback-local.");
        }

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
            foreach (var (label, url) in BuildLaunchUrls(baseUrl))
                output.WriteLine($"{label}: {url}");
            output.WriteLine($"Auth mode: {(BindAddressClassifier.IsLoopbackBind(config.BindAddress) ? "loopback-open" : "token-or-session")}");
            output.WriteLine(withCompanion
                ? "Streaming gateway and companion logs. Press Ctrl-C to stop."
                : "Streaming gateway logs. Press Ctrl-C to stop.");

            var gatewayStdout = PumpProcessOutputAsync(gateway.StandardOutput, "gateway", output, cts.Token);
            var gatewayStderr = PumpProcessOutputAsync(gateway.StandardError, "gateway", output, cts.Token);
            var companionStdout = companion is null
                ? Task.CompletedTask
                : PumpProcessOutputAsync(companion.StandardOutput, "companion", output, cts.Token);
            var companionStderr = companion is null
                ? Task.CompletedTask
                : PumpProcessOutputAsync(companion.StandardError, "companion", output, cts.Token);

            await WaitForExitOrCancelAsync(gateway, companion, cts.Token);
            cts.Cancel();
            await Task.WhenAll(gatewayStdout, gatewayStderr, companionStdout, companionStderr);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (companion is not null)
                TryStopProcess(companion);
            TryStopProcess(gateway);
        }

        return 0;
    }

    internal static SetupStatusResponse BuildStatus(string configPath, GatewayConfig config, OrganizationPolicySnapshot? policy = null)
    {
        var localState = LocalSetupStateLoader.Load(config.Memory.StoragePath);
        var effectivePolicy = policy ?? localState.Policy ?? new OrganizationPolicySnapshot();
        var publicBind = !BindAddressClassifier.IsLoopbackBind(config.BindAddress);
        var workspacePath = ResolveWorkspacePath(config);
        var workspaceExists = !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath);
        var runtimeState = RuntimeModeResolver.Resolve(config.Runtime);
        var browser = BrowserToolCapabilityEvaluator.Evaluate(config, runtimeState);
        var modelDoctor = ModelDoctorEvaluator.Build(config);
        var snapshot = localState.VerificationSnapshot;
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
            WorkspacePath = workspacePath,
            WorkspaceExists = workspaceExists,
            HasOperatorAccounts = localState.OperatorAccountCount > 0,
            OperatorAccountCount = localState.OperatorAccountCount,
            ProviderConfigured = ProviderSmokeProbe.IsProviderConfigured(config.Llm),
            ProviderSmokeStatus = SetupVerificationService.GetCheckStatus(snapshot, "provider_smoke"),
            ModelDoctorStatus = SetupVerificationService.GetModelDoctorStatus(modelDoctor),
            BrowserToolRegistered = browser.Registered,
            BrowserExecutionBackendConfigured = browser.ExecutionBackendConfigured,
            BrowserCapabilityReason = browser.Reason,
            LastVerificationAtUtc = snapshot?.RecordedAtUtc,
            LastVerificationSource = snapshot?.Source,
            LastVerificationStatus = snapshot?.Verification.OverallStatus ?? SetupCheckStates.NotRun,
            LastVerificationHasFailures = snapshot?.Verification.HasFailures ?? false,
            LastVerificationHasWarnings = snapshot?.Verification.HasWarnings ?? false,
            BootstrapGuidanceState = SetupVerificationService.GetBootstrapGuidanceState(publicBind, effectivePolicy.BootstrapTokenEnabled, localState.OperatorAccountCount),
            RecommendedNextActions = SetupVerificationService.BuildRecommendedNextActions(
                config,
                effectivePolicy,
                localState.OperatorAccountCount,
                browser,
                workspaceExists,
                publicBind),
            ChannelReadiness = [],
            Artifacts = BuildArtifactItems(GetDeployDirectory(configPath)),
            Warnings = warnings
        };
    }

    internal static string GetDeployDirectory(string configPath)
        => Path.Combine(Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException("Config path must have a directory."), "deploy");

    internal static IReadOnlyList<(string Label, string Url)> BuildLaunchUrls(string baseUrl)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        return
        [
            ("Gateway URL", normalizedBaseUrl),
            ("Chat URL", $"{normalizedBaseUrl}/chat"),
            ("Admin URL", $"{normalizedBaseUrl}/admin")
        ];
    }

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

    private static async Task WaitForExitOrCancelAsync(Process gateway, Process? companion, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (gateway.HasExited || (companion is not null && companion.HasExited))
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
        output.WriteLine($"Workspace: {status.WorkspacePath} exists={status.WorkspaceExists.ToString().ToLowerInvariant()}");
        output.WriteLine($"Operator accounts: {status.OperatorAccountCount}");
        output.WriteLine($"Provider configured: {status.ProviderConfigured.ToString().ToLowerInvariant()} smoke={status.ProviderSmokeStatus}");
        output.WriteLine($"Model doctor: {status.ModelDoctorStatus}");
        output.WriteLine($"Browser tool: registered={status.BrowserToolRegistered.ToString().ToLowerInvariant()} backend_configured={status.BrowserExecutionBackendConfigured.ToString().ToLowerInvariant()} reason={status.BrowserCapabilityReason}");
        output.WriteLine($"Last verification: {(status.LastVerificationAtUtc.HasValue ? $"{status.LastVerificationStatus} at {status.LastVerificationAtUtc:O} via {status.LastVerificationSource}" : SetupCheckStates.NotRun)}");
        output.WriteLine($"Bootstrap guidance: {status.BootstrapGuidanceState}");

        output.WriteLine();
        output.WriteLine("Artifacts:");
        foreach (var artifact in status.Artifacts)
            output.WriteLine($"- {artifact.Label}: {artifact.Status} ({artifact.Path})");

        if (status.RecommendedNextActions.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Recommended next actions:");
            foreach (var action in status.RecommendedNextActions)
                output.WriteLine($"- {action}");
        }

        if (status.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Warnings:");
            foreach (var warning in status.Warnings)
                output.WriteLine($"- {warning}");
        }
    }

    private static async Task<SetupVerificationResponse> RunLocalVerificationAsync(
        string configPath,
        GatewayConfig config,
        bool offline,
        bool requireProvider,
        string source,
        CancellationToken ct)
    {
        _ = configPath;
        var localState = LocalSetupStateLoader.Load(config.Memory.StoragePath);
        var verification = await SetupVerificationService.VerifyAsync(new SetupVerificationRequest
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            Policy = localState.Policy,
            OperatorAccountCount = localState.OperatorAccountCount,
            Offline = offline,
            RequireProvider = requireProvider,
            WorkspacePath = ResolveWorkspacePath(config),
            ModelDoctor = ModelDoctorEvaluator.Build(config)
        }, ct);

        _ = new SetupVerificationSnapshotStore(config.Memory.StoragePath).Save(new SetupVerificationSnapshot
        {
            Source = source,
            Offline = offline,
            RequireProvider = requireProvider,
            Verification = verification
        });

        return verification;
    }

    private static void WriteVerification(TextWriter output, SetupVerificationResponse verification)
    {
        output.WriteLine();
        output.WriteLine("Setup verification:");
        foreach (var check in verification.Checks)
        {
            output.WriteLine($"- [{check.Status}] {check.Label}: {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
                output.WriteLine($"  {check.Detail!.Replace(Environment.NewLine, Environment.NewLine + "  ", StringComparison.Ordinal)}");
        }

        if (verification.RecommendedNextActions.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Verification next actions:");
            foreach (var action in verification.RecommendedNextActions)
                output.WriteLine($"- {action}");
        }

        output.WriteLine();
        output.WriteLine($"Verification result: {verification.OverallStatus}");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                }
            };
            process.Start();
        }
        catch
        {
        }
    }

    private static string? ResolveWorkspacePath(GatewayConfig config)
    {
        var workspace = config.Tooling.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspace))
            return null;

        var resolved = SecretResolver.Resolve(workspace) ?? workspace;
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }
}

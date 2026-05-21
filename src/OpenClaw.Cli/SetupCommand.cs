using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;

namespace OpenClaw.Cli;

internal static class SetupCommand
{
    private const string DefaultConfigPath = "~/.openclaw/config/openclaw.settings.json";
    private const string DefaultApiKeyRef = "env:MODEL_PROVIDER_KEY";
    private const string DefaultProvider = "openai";
    private const string DefaultOllamaPresetId = "ollama-general";
    private const string DefaultEmbeddedPresetId = "embedded-gemma-small-q4";
    private const string DefaultEmbeddedModelId = "gemma-local-small-q4";
    private const string DefaultBackendChoice = "none";

    internal static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool canPrompt)
        => (await RunWithResultAsync(args, input, output, error, currentDirectory, canPrompt)).ExitCode;

    internal static async Task<SetupCommandResult> RunWithResultAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool canPrompt)
    {
        if (args.Length > 0 && string.Equals(args[0], "launch", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await SetupLifecycleCommand.RunLaunchAsync(args[1..], output, error, currentDirectory) };
        if (args.Length > 0 && string.Equals(args[0], "service", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await SetupLifecycleCommand.RunServiceAsync(args[1..], output, error, currentDirectory) };
        if (args.Length > 0 && string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = SetupLifecycleCommand.RunStatus(args[1..], output, error) };
        if (args.Length > 0 && string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await SetupLifecycleCommand.RunVerifyAsync(args[1..], output, error) };
        if (args.Length > 0 && string.Equals(args[0], "channel", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await ChannelSetupCommand.RunAsync(args[1..], input, output, error, canPrompt) };
        if (args.Length > 0 && string.Equals(args[0], "provider", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await RunProviderSetupAsync(args[1..], input, output, error, currentDirectory, canPrompt) };
        if (args.Length > 0 && string.Equals(args[0], "tailscale", StringComparison.OrdinalIgnoreCase))
            return new SetupCommandResult { ExitCode = await RunTailscaleSetupAsync(args[1..], input, output, error, canPrompt) };

        var parsed = CliArgs.Parse(args);
        var nonInteractive = parsed.HasFlag("--non-interactive");
        var requiresPrompt = RequiresPrompt(parsed);

        if (requiresPrompt && !nonInteractive && !canPrompt)
        {
            error.WriteLine("Missing setup inputs and no interactive terminal is available. Re-run with --non-interactive and explicit values, or run 'openclaw setup' from a terminal.");
            return new SetupCommandResult { ExitCode = 2 };
        }

        SetupAnswers answers;
        try
        {
            answers = requiresPrompt && !nonInteractive
                ? PromptForAnswers(parsed, input, output, currentDirectory)
                : BuildAnswersFromArgs(parsed, currentDirectory, requireProfile: nonInteractive);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return new SetupCommandResult { ExitCode = 2 };
        }

        var warnings = new List<string>();
        var config = BuildConfig(answers, warnings);
        var validationErrors = ConfigValidator.Validate(config);
        if (validationErrors.Count > 0)
        {
            error.WriteLine("Config validation failed:");
            foreach (var validationError in validationErrors)
                error.WriteLine($"- {validationError}");
            return new SetupCommandResult { ExitCode = 1 };
        }

        Directory.CreateDirectory(answers.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(answers.ConfigPath)!);
        Directory.CreateDirectory(config.Memory.StoragePath);

        await GatewayConfigFile.SaveAsync(config, answers.ConfigPath);

        var envExamplePath = GatewaySetupArtifacts.BuildEnvExamplePath(answers.ConfigPath);
        await File.WriteAllTextAsync(
            envExamplePath,
            GatewaySetupArtifacts.BuildEnvExample(
                answers.ApiKey,
                answers.AuthToken,
                answers.Workspace,
                BuildReachableBaseUrl(answers.BindAddress, answers.Port)),
            CancellationToken.None);

        output.WriteLine($"Wrote config: {answers.ConfigPath}");
        output.WriteLine($"Wrote env example: {envExamplePath}");
        output.WriteLine($"Profile: {answers.Profile}");
        output.WriteLine($"Workspace: {answers.Workspace}");
        output.WriteLine($"Provider/model: {config.Llm.Provider}/{config.Llm.Model}");
        output.WriteLine($"Bind: {config.BindAddress}:{config.Port}");
        output.WriteLine("Config validation: passed");

        if (warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Warnings:");
            foreach (var warning in warnings)
                output.WriteLine($"- {warning}");
        }

        output.WriteLine();
        output.WriteLine("Next steps:");
        output.WriteLine($"openclaw setup verify --config {GatewayConfigFile.QuoteIfNeeded(answers.ConfigPath)}");
        output.WriteLine($"openclaw setup launch --config {GatewayConfigFile.QuoteIfNeeded(answers.ConfigPath)}");
        output.WriteLine($"dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(answers.ConfigPath)} --doctor");

        if (BindAddressClassifier.IsLoopbackBind(answers.BindAddress))
        {
            output.WriteLine($"dotnet run --project src/OpenClaw.Companion -c Release");
        }
        else
        {
            output.WriteLine("Companion/public launch guidance: set OPENCLAW_BASE_URL and OPENCLAW_AUTH_TOKEN from the generated config or env file before starting the Companion app.");
        }
        return new SetupCommandResult
        {
            ExitCode = 0,
            ConfigPath = answers.ConfigPath
        };
    }

    private static bool RequiresPrompt(CliArgs parsed)
    {
        var required = new[]
        {
            "--profile",
            "--workspace",
            "--provider",
            "--model"
        };

        if (required.Any(option => string.IsNullOrWhiteSpace(parsed.GetOption(option))))
            return true;

        return ProviderRequiresApiKey(parsed.GetOption("--provider")) &&
               string.IsNullOrWhiteSpace(parsed.GetOption("--api-key"));
    }

    private static SetupAnswers PromptForAnswers(CliArgs parsed, TextReader input, TextWriter output, string currentDirectory)
    {
        var profile = BootstrapConfigFactory.NormalizeProfile(Prompt(output, input, "Deployment profile (local|public|tailscale-serve)", parsed.GetOption("--profile") ?? "local"));
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Config path", parsed.GetOption("--config") ?? DefaultConfigPath)));
        var workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Workspace path", parsed.GetOption("--workspace") ?? Path.Combine(currentDirectory, "workspace"))));
        var discoveredOllama = TryDiscoverOllamaModels();
        if (discoveredOllama.Models.Count > 0)
            output.WriteLine($"Detected Ollama models: {string.Join(", ", discoveredOllama.Models)}");

        var providerDefault = parsed.GetOption("--provider") ?? (discoveredOllama.IsAvailable ? "ollama" : DefaultProvider);
        var provider = Prompt(output, input, "Provider", providerDefault);
        var modelPresetId = GetDefaultModelPreset(provider, parsed.GetOption("--model-preset"));
        if (RequiresModelPresetPrompt(provider))
            modelPresetId = Prompt(output, input, "Model preset", modelPresetId!);
        var modelDefault = parsed.GetOption("--model")
            ?? GetDefaultModel(provider, discoveredOllama.Models);
        var model = Prompt(output, input, "Model", modelDefault);
        var apiKey = ProviderRequiresApiKey(provider)
            ? Prompt(output, input, "API key or env: reference", parsed.GetOption("--api-key") ?? DefaultApiKeyRef)
            : parsed.GetOption("--api-key");

        var bindDefault = parsed.GetOption("--bind") ?? GetDefaultBindAddress(profile);
        var bindAddress = Prompt(output, input, "Bind address", bindDefault);

        var portDefault = parsed.GetOption("--port") ?? "18789";
        var port = ParsePort(Prompt(output, input, "Port", portDefault));

        var authDefault = parsed.GetOption("--auth-token") ?? GenerateAuthToken();
        var authToken = Prompt(output, input, "Auth token", authDefault);

        var backendChoice = NormalizeBackendChoice(Prompt(output, input, "Execution backend (none|docker|opensandbox|ssh)", InferBackendChoice(parsed) ?? DefaultBackendChoice));
        var dockerImage = parsed.GetOption("--docker-image");
        var opensandboxEndpoint = parsed.GetOption("--opensandbox-endpoint");
        var sshHost = parsed.GetOption("--ssh-host");
        var sshUser = parsed.GetOption("--ssh-user");
        var sshKey = parsed.GetOption("--ssh-key");

        switch (backendChoice)
        {
            case "docker":
                dockerImage = Prompt(output, input, "Docker image", dockerImage ?? "python:3.12-slim");
                break;
            case "opensandbox":
                opensandboxEndpoint = Prompt(output, input, "OpenSandbox endpoint", opensandboxEndpoint ?? "http://127.0.0.1:8080");
                break;
            case "ssh":
                sshHost = Prompt(output, input, "SSH host", sshHost ?? "remote-host");
                sshUser = Prompt(output, input, "SSH user", sshUser ?? Environment.UserName);
                sshKey = PromptOptional(output, input, "SSH private key path", sshKey);
                break;
        }

        return new SetupAnswers
        {
            Profile = profile,
            ConfigPath = configPath,
            Workspace = workspace,
            Provider = provider,
            ModelPresetId = string.IsNullOrWhiteSpace(modelPresetId) ? null : modelPresetId.Trim(),
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            BindAddress = bindAddress,
            Port = port,
            AuthToken = authToken,
            BackendChoice = backendChoice,
            DockerImage = dockerImage,
            OpenSandboxEndpoint = opensandboxEndpoint,
            SshHost = sshHost,
            SshUser = sshUser,
            SshKey = sshKey
        };
    }

    private static SetupAnswers BuildAnswersFromArgs(CliArgs parsed, string currentDirectory, bool requireProfile)
    {
        var rawProfile = parsed.GetOption("--profile");
        if (requireProfile && string.IsNullOrWhiteSpace(rawProfile))
            throw new ArgumentException("--profile is required when --non-interactive is set.");

        var profile = BootstrapConfigFactory.NormalizeProfile(rawProfile ?? "local");
        return new SetupAnswers
        {
            Profile = profile,
            ConfigPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? DefaultConfigPath)),
            Workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--workspace") ?? Path.Combine(currentDirectory, "workspace"))),
            Provider = parsed.GetOption("--provider") ?? DefaultProvider,
            ModelPresetId = GetDefaultModelPreset(parsed.GetOption("--provider") ?? DefaultProvider, parsed.GetOption("--model-preset")),
            Model = parsed.GetOption("--model") ?? GetDefaultModel(parsed.GetOption("--provider") ?? DefaultProvider, []),
            ApiKey = ProviderRequiresApiKey(parsed.GetOption("--provider"))
                ? parsed.GetOption("--api-key") ?? DefaultApiKeyRef
                : parsed.GetOption("--api-key"),
            BindAddress = parsed.GetOption("--bind") ?? GetDefaultBindAddress(profile),
            Port = ParsePort(parsed.GetOption("--port") ?? "18789"),
            AuthToken = parsed.GetOption("--auth-token") ?? GenerateAuthToken(),
            BackendChoice = NormalizeBackendChoice(InferBackendChoice(parsed) ?? DefaultBackendChoice),
            DockerImage = parsed.GetOption("--docker-image"),
            OpenSandboxEndpoint = parsed.GetOption("--opensandbox-endpoint"),
            SshHost = parsed.GetOption("--ssh-host"),
            SshUser = parsed.GetOption("--ssh-user"),
            SshKey = parsed.GetOption("--ssh-key")
        };
    }

    private static GatewayConfig BuildConfig(SetupAnswers answers, List<string> warnings)
    {
        var configDirectory = Path.GetDirectoryName(answers.ConfigPath)
            ?? throw new InvalidOperationException("Config path must contain a directory.");
        var memoryRoot = Path.Combine(configDirectory, "memory");
        var config = BootstrapConfigFactory.CreateProfileConfig(
            answers.Profile,
            answers.BindAddress,
            answers.Port,
            answers.AuthToken,
            answers.Workspace,
            memoryRoot,
            answers.Provider,
            answers.Model,
            answers.ApiKey ?? string.Empty,
            answers.ModelPresetId,
            warnings);

        ApplyBackend(config, answers, warnings);
        return config;
    }

    private static void ApplyBackend(GatewayConfig config, SetupAnswers answers, List<string> warnings)
    {
        switch (answers.BackendChoice)
        {
            case "none":
                return;
            case "docker":
                if (string.IsNullOrWhiteSpace(answers.DockerImage))
                    throw new ArgumentException("--docker-image is required when docker is selected.");

                config.Execution.Profiles["docker"] = new ExecutionBackendProfileConfig
                {
                    Type = ExecutionBackendType.Docker,
                    Image = answers.DockerImage,
                    WorkingDirectory = answers.Workspace
                };
                if (config.Tooling.AllowShell)
                    config.Execution.Tools["shell"] = new ExecutionToolRouteConfig { Backend = "docker", FallbackBackend = "local", RequireWorkspace = true };
                warnings.AddRange(CheckCommandAvailability("docker", "--version", "Docker backend requested but docker was not found on PATH."));
                if (!config.Tooling.AllowShell)
                    warnings.Add("Public profile keeps shell disabled even though a Docker backend was configured. Enable shell deliberately later if you want agent tool execution routed to Docker.");
                return;
            case "opensandbox":
                if (string.IsNullOrWhiteSpace(answers.OpenSandboxEndpoint))
                    throw new ArgumentException("--opensandbox-endpoint is required when opensandbox is selected.");
                if (!Uri.TryCreate(answers.OpenSandboxEndpoint, UriKind.Absolute, out _))
                    throw new ArgumentException($"Invalid OpenSandbox endpoint: {answers.OpenSandboxEndpoint}");

                config.Tooling.EnableBrowserTool = true;
                config.Sandbox.Provider = SandboxProviderNames.OpenSandbox;
                config.Sandbox.Endpoint = answers.OpenSandboxEndpoint;
                config.Execution.Profiles["opensandbox"] = new ExecutionBackendProfileConfig
                {
                    Type = ExecutionBackendType.OpenSandbox,
                    Endpoint = answers.OpenSandboxEndpoint
                };
                warnings.Add("OpenSandbox backend was configured. You still need to define sandbox tool templates before the gateway can sandbox tool execution.");
                return;
            case "ssh":
                if (string.IsNullOrWhiteSpace(answers.SshHost))
                    throw new ArgumentException("--ssh-host is required when ssh is selected.");
                if (string.IsNullOrWhiteSpace(answers.SshUser))
                    throw new ArgumentException("--ssh-user is required when ssh is selected.");

                config.Execution.Profiles["ssh"] = new ExecutionBackendProfileConfig
                {
                    Type = ExecutionBackendType.Ssh,
                    Host = answers.SshHost,
                    Username = answers.SshUser,
                    PrivateKeyPath = answers.SshKey,
                    WorkingDirectory = answers.Workspace
                };
                if (config.Tooling.AllowShell)
                    config.Execution.Tools["shell"] = new ExecutionToolRouteConfig { Backend = "ssh", FallbackBackend = "local", RequireWorkspace = true };
                warnings.AddRange(CheckCommandAvailability("ssh", "-V", "SSH backend requested but ssh was not found on PATH."));
                if (!config.Tooling.AllowShell)
                    warnings.Add("Public profile keeps shell disabled even though an SSH backend was configured. Enable shell deliberately later if you want agent tool execution routed to SSH.");
                return;
            default:
                throw new ArgumentException($"Unsupported execution backend: {answers.BackendChoice}");
        }
    }

    internal static string BuildReachableBaseUrl(string bindAddress, int port)
        => GatewaySetupArtifacts.BuildReachableBaseUrl(bindAddress, port);

    private static string GetDefaultBindAddress(string profile)
        => profile == "public" ? "0.0.0.0" : "127.0.0.1";

    private static string InferBackendChoice(CliArgs parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.GetOption("--docker-image")))
            return "docker";
        if (!string.IsNullOrWhiteSpace(parsed.GetOption("--opensandbox-endpoint")))
            return "opensandbox";
        if (!string.IsNullOrWhiteSpace(parsed.GetOption("--ssh-host")))
            return "ssh";
        return DefaultBackendChoice;
    }

    private static string NormalizeBackendChoice(string backendChoice)
    {
        var normalized = backendChoice.Trim().ToLowerInvariant();
        if (normalized is not ("none" or "docker" or "opensandbox" or "ssh"))
            throw new ArgumentException("Execution backend must be one of: none, docker, opensandbox, ssh.");
        return normalized;
    }

    private static int ParsePort(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Invalid port: {raw}");
        return port;
    }

    private static string GenerateAuthToken()
        => $"oc_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";

    private static string Prompt(TextWriter output, TextReader input, string label, string defaultValue)
    {
        output.Write($"{label} [{defaultValue}]: ");
        var value = input.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string? PromptOptional(TextWriter output, TextReader input, string label, string? defaultValue)
    {
        var suffix = string.IsNullOrWhiteSpace(defaultValue) ? string.Empty : $" [{defaultValue}]";
        output.Write($"{label}{suffix}: ");
        var value = input.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
            return string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue;
        return value.Trim();
    }

    private static IEnumerable<string> CheckCommandAvailability(string command, string arg, string failureMessage)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return [failureMessage];
            }

            if (process.ExitCode == 0)
                return [];
        }
        catch
        {
        }

        return [failureMessage];
    }

    private static bool ProviderRequiresApiKey(string? provider)
        => !string.Equals(provider?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(provider?.Trim(), "embedded", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresModelPresetPrompt(string? provider)
        => string.Equals(provider?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(provider?.Trim(), "embedded", StringComparison.OrdinalIgnoreCase);

    private static string? GetDefaultModelPreset(string? provider, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => DefaultOllamaPresetId,
            "embedded" => DefaultEmbeddedPresetId,
            _ => null
        };
    }

    private static string GetDefaultModel(string? provider, IReadOnlyList<string> discoveredOllamaModels)
        => provider?.Trim().ToLowerInvariant() switch
        {
            "embedded" => DefaultEmbeddedModelId,
            "ollama" when discoveredOllamaModels.Count > 0 => discoveredOllamaModels[0],
            _ => new GatewayConfig().Llm.Model
        };

    private static OllamaDiscoveryResult TryDiscoverOllamaModels()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var payload = http.GetStringAsync($"{OllamaEndpointNormalizer.DefaultBaseUrl}/api/tags").GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("models", out var modelsElement) ||
                modelsElement.ValueKind != JsonValueKind.Array)
            {
                return OllamaDiscoveryResult.Empty;
            }

            var models = new List<string>();
            foreach (var model in modelsElement.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    models.Add(nameElement.GetString()!);
                }
            }

            return new OllamaDiscoveryResult
            {
                IsAvailable = true,
                Models = models
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
        catch
        {
            return OllamaDiscoveryResult.Empty;
        }
    }

    private static async Task<int> RunTailscaleSetupAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool canPrompt)
    {
        var helpRequested = args.Length > 0 && (args[0] is "-h" or "--help");
        if (args.Length == 0 ||
            helpRequested ||
            !string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("Usage: openclaw setup tailscale serve [--config <path>] [--local-url <url>] [--non-interactive]");
            return helpRequested ? 0 : 2;
        }

        CliArgs parsed;
        try
        {
            parsed = CliArgs.Parse(args[1..]);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        var explicitConfig = !string.IsNullOrWhiteSpace(parsed.GetOption("--config"));
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? DefaultConfigPath));
        GatewayConfig config;
        var configLoaded = false;
        if (File.Exists(configPath))
        {
            try
            {
                config = GatewayConfigFile.Load(configPath);
                configLoaded = true;
            }
            catch (Exception ex)
            {
                error.WriteLine($"Could not load config {configPath}: {ex.Message}");
                return 1;
            }
        }
        else if (explicitConfig)
        {
            error.WriteLine($"Config file not found: {configPath}");
            return 1;
        }
        else
        {
            config = CreateTailscaleServePreviewConfig("http://127.0.0.1:18789");
        }

        var localGatewayUrl = parsed.GetOption("--local-url") ?? parsed.GetOption("--url") ?? TailscaleServeAdvisor.BuildLocalGatewayUrl(config);
        if (!parsed.HasFlag("--non-interactive") && canPrompt)
            localGatewayUrl = Prompt(output, input, "Local gateway URL", localGatewayUrl);

        if (!Uri.TryCreate(localGatewayUrl, UriKind.Absolute, out var localGatewayUri) ||
            (!localGatewayUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !localGatewayUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            error.WriteLine($"Invalid local gateway URL: {localGatewayUrl}");
            return 2;
        }

        config.Deployment = new DeploymentConfig
        {
            Mode = "tailscale-serve",
            PublicExposure = false,
            ReverseProxy = "tailscale-serve",
            ExpectedLocalUrl = localGatewayUri.ToString().TrimEnd('/')
        };

        var status = await TailscaleServeAdvisor.BuildStatusAsync(
            config,
            new TailscaleServeProbeOptions { ForceInclude = true, CheckCli = true },
            CancellationToken.None) ?? throw new InvalidOperationException("Expected Tailscale Serve status.");

        WriteTailscaleServeInstructions(output, configPath, configLoaded, status);
        return 0;
    }

    private static GatewayConfig CreateTailscaleServePreviewConfig(string localGatewayUrl)
    {
        var port = 18789;
        if (Uri.TryCreate(localGatewayUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            port = uri.Port;

        return new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            Port = port,
            Deployment = new DeploymentConfig
            {
                Mode = "tailscale-serve",
                PublicExposure = false,
                ReverseProxy = "tailscale-serve",
                ExpectedLocalUrl = localGatewayUrl.TrimEnd('/')
            }
        };
    }

    private static void WriteTailscaleServeInstructions(
        TextWriter output,
        string configPath,
        bool configLoaded,
        TailscaleServeStatusResponse status)
    {
        var localGateway = status.LocalGatewayUrl.TrimEnd('/');
        output.WriteLine("Tailscale Serve setup for OpenClaw.NET");
        output.WriteLine();
        output.WriteLine("This profile keeps OpenClaw.NET bound to 127.0.0.1 and uses Tailscale Serve to expose it privately inside your tailnet.");
        output.WriteLine(configLoaded
            ? $"Config: {configPath}"
            : $"Config: {configPath} (not found; using default local gateway guidance)");
        output.WriteLine();
        output.WriteLine("Local gateway:");
        output.WriteLine(localGateway);
        output.WriteLine();
        output.WriteLine("Recommended command:");
        output.WriteLine();
        output.WriteLine(status.SuggestedServeCommand);
        output.WriteLine();
        output.WriteLine("After enabling Serve, open the Tailscale-provided HTTPS URL from a device in your tailnet.");
        output.WriteLine();
        output.WriteLine("Useful OpenClaw.NET surfaces:");
        output.WriteLine("- Chat: /chat");
        output.WriteLine("- Admin: /admin");
        output.WriteLine("- MCP: /mcp");
        output.WriteLine("- Integration API: /api/integration/status");
        output.WriteLine("- WebSocket: /ws");
        output.WriteLine("- Doctor: /doctor/text");
        output.WriteLine();
        output.WriteLine("Tailscale CLI status:");
        output.WriteLine($"- CLI detected: {status.TailscaleCliDetected.ToString().ToLowerInvariant()}");
        output.WriteLine($"- Tailnet reachability: {status.TailnetReachability}");
        output.WriteLine($"- Serve detected: {status.ServeDetected}");
        if (status.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Advisory warnings:");
            foreach (var warning in status.Warnings)
                output.WriteLine($"- {warning}");
        }
        output.WriteLine();
        output.WriteLine("Security notes:");
        output.WriteLine("- Tailscale Serve is for private tailnet access.");
        output.WriteLine("- Do not use Funnel for /admin unless you have reviewed public exposure hardening.");
        output.WriteLine("- Keep OpenClaw.NET auth enabled.");
        output.WriteLine("- Use operator accounts/tokens for admin, API, and websocket clients.");
        output.WriteLine();
        output.WriteLine("Follow-up checks:");
        output.WriteLine($"openclaw setup status --config {GatewayConfigFile.QuoteIfNeeded(configPath)}");
        output.WriteLine($"OPENCLAW_BASE_URL={localGateway} OPENCLAW_AUTH_TOKEN=... openclaw admin posture");
        output.WriteLine();
        output.WriteLine("If the gateway is running, check:");
        output.WriteLine($"{localGateway}/health/ready");
    }

    private static async Task<int> RunProviderSetupAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool canPrompt)
    {
        var providerIndex = Array.FindIndex(args, static arg => !arg.StartsWith("--", StringComparison.Ordinal));
        var provider = providerIndex >= 0 ? args[providerIndex] : null;
        if (!string.Equals(provider, "aperture", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("Usage: openclaw setup provider aperture [--config <path>] [--profile-id <id>] [--endpoint <url>] [--model <route>] [--auth-mode <bearer|tailnet-identity>] [--env-var <name>] [--send-request-metadata <true|false>] [--workspace <path>] [--non-interactive]");
            return 2;
        }

        var filteredArgs = args.Where((_, index) => index != providerIndex).ToArray();
        CliArgs parsed;
        try
        {
            parsed = CliArgs.Parse(filteredArgs);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        var nonInteractive = parsed.HasFlag("--non-interactive");
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? DefaultConfigPath));
        var profileId = parsed.GetOption("--profile-id") ?? "aperture-default";
        var endpoint = parsed.GetOption("--endpoint");
        var model = parsed.GetOption("--model");
        string authMode;
        try
        {
            authMode = NormalizeApertureAuthMode(parsed.GetOption("--auth-mode") ?? "bearer");
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
        var envVar = parsed.GetOption("--env-var") ?? "OPENCLAW_APERTURE_TOKEN";
        bool sendMetadata;
        try
        {
            sendMetadata = ParseBooleanOption(parsed.GetOption("--send-request-metadata"), defaultValue: false);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        if (!nonInteractive && canPrompt)
        {
            endpoint = Prompt(output, input, "Aperture endpoint", endpoint ?? "https://YOUR_APERTURE_ENDPOINT");
            model = Prompt(output, input, "Aperture model route", model ?? "YOUR_APERTURE_MODEL_ROUTE");
            try
            {
                authMode = NormalizeApertureAuthMode(Prompt(output, input, "Auth mode (bearer|tailnet-identity)", authMode));
            }
            catch (ArgumentException ex)
            {
                error.WriteLine(ex.Message);
                return 2;
            }
            if (authMode == "bearer")
                envVar = Prompt(output, input, "Bearer token env var", envVar);
            try
            {
                sendMetadata = ParseBooleanOption(Prompt(output, input, "Send request metadata (true|false)", sendMetadata ? "true" : "false"), defaultValue: false);
            }
            catch (ArgumentException ex)
            {
                error.WriteLine(ex.Message);
                return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            error.WriteLine("Aperture endpoint and model route are required. Re-run with --endpoint and --model, or run from an interactive terminal.");
            return 2;
        }

        GatewayConfig config;
        if (File.Exists(configPath))
        {
            config = GatewayConfigFile.Load(configPath);
        }
        else
        {
            var workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--workspace") ?? Path.Join(currentDirectory, "workspace")));
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Config path must contain a directory.");
            var warnings = new List<string>();
            config = GatewaySetupProfileFactory.CreateProfileConfig(
                "local",
                "127.0.0.1",
                18789,
                GenerateAuthToken(),
                workspace,
                Path.Join(configDirectory, "memory"),
                "aperture",
                model,
                authMode == "bearer" ? $"env:{envVar}" : string.Empty,
                modelPresetId: null,
                warnings);
            config.Llm.Endpoint = endpoint;
            config.Llm.AuthMode = authMode;
            config.Llm.SendRequestMetadata = sendMetadata;
        }

        var existingIndex = config.Models.Profiles.FindIndex(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        var profile = new ModelProfileConfig
        {
            Id = profileId,
            Provider = "aperture",
            Model = model,
            BaseUrl = endpoint,
            ApiKey = authMode == "bearer" ? $"env:{envVar}" : null,
            AuthMode = authMode,
            SendRequestMetadata = sendMetadata,
            Tags = ["aperture", "remote", "optional"]
        };

        if (existingIndex >= 0)
            config.Models.Profiles[existingIndex] = profile;
        else
            config.Models.Profiles.Add(profile);

        if (string.IsNullOrWhiteSpace(config.Models.DefaultProfile) && config.Models.Profiles.Count == 1)
            config.Models.DefaultProfile = profileId;

        var validationErrors = ConfigValidator.Validate(config);
        if (validationErrors.Count > 0)
        {
            error.WriteLine("Config validation failed:");
            foreach (var validationError in validationErrors)
                error.WriteLine($"- {validationError}");
            return 1;
        }

        var resolvedWorkspaceRoot = ResolveConfiguredPath(config.Tooling.WorkspaceRoot);
        var resolvedMemoryStoragePath = ResolveConfiguredPath(config.Memory.StoragePath);

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!string.IsNullOrWhiteSpace(resolvedWorkspaceRoot))
            Directory.CreateDirectory(resolvedWorkspaceRoot);
        if (!string.IsNullOrWhiteSpace(resolvedMemoryStoragePath))
            Directory.CreateDirectory(resolvedMemoryStoragePath);
        await GatewayConfigFile.SaveAsync(config, configPath);

        var envExamplePath = GatewaySetupArtifacts.BuildEnvExamplePath(configPath);
        await File.WriteAllTextAsync(
            envExamplePath,
            GatewaySetupArtifacts.BuildEnvExample(
                authMode == "bearer" ? $"env:{envVar}" : null,
                config.AuthToken ?? GenerateAuthToken(),
                string.IsNullOrWhiteSpace(resolvedWorkspaceRoot) ? Path.Join(currentDirectory, "workspace") : resolvedWorkspaceRoot,
                BuildReachableBaseUrl(config.BindAddress, config.Port)),
            CancellationToken.None);

        output.WriteLine($"Wrote config: {configPath}");
        output.WriteLine($"Wrote env example: {envExamplePath}");
        output.WriteLine($"Aperture profile: {profileId}");
        output.WriteLine($"Endpoint: {endpoint}");
        output.WriteLine($"Model route: {model}");
        output.WriteLine($"Auth mode: {authMode}");
        output.WriteLine($"Request metadata: {(sendMetadata ? "enabled" : "disabled")}");
        output.WriteLine();
        output.WriteLine("Next steps:");
        output.WriteLine($"openclaw setup verify --config {GatewayConfigFile.QuoteIfNeeded(configPath)} --offline");
        output.WriteLine("openclaw models doctor");
        return 0;
    }

    private static string NormalizeApertureAuthMode(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is not ("bearer" or "tailnet-identity"))
            throw new ArgumentException("Aperture auth mode must be one of: bearer, tailnet-identity.");
        return normalized;
    }

    private static bool ParseBooleanOption(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (bool.TryParse(raw.Trim(), out var parsed))
            return parsed;
        throw new ArgumentException($"Invalid boolean value: {raw}");
    }

    private static string? ResolveConfiguredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var resolved = SecretResolver.Resolve(value);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            if (value.TrimStart().StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                return null;

            resolved = value;
        }
        resolved = GatewayConfigFile.ExpandPath(resolved);
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }

    private sealed class SetupAnswers
    {
        public required string Profile { get; init; }
        public required string ConfigPath { get; init; }
        public required string Workspace { get; init; }
        public required string Provider { get; init; }
        public string? ModelPresetId { get; init; }
        public required string Model { get; init; }
        public string? ApiKey { get; init; }
        public required string BindAddress { get; init; }
        public required int Port { get; init; }
        public required string AuthToken { get; init; }
        public required string BackendChoice { get; init; }
        public string? DockerImage { get; init; }
        public string? OpenSandboxEndpoint { get; init; }
        public string? SshHost { get; init; }
        public string? SshUser { get; init; }
        public string? SshKey { get; init; }
    }

    private sealed class OllamaDiscoveryResult
    {
        public static OllamaDiscoveryResult Empty { get; } = new();

        public bool IsAvailable { get; init; }
        public IReadOnlyList<string> Models { get; init; } = [];
    }
}

internal sealed class SetupCommandResult
{
    public int ExitCode { get; init; }

    public string? ConfigPath { get; init; }
}

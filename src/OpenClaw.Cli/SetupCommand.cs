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
        var profile = BootstrapConfigFactory.NormalizeProfile(Prompt(output, input, "Deployment profile (local|public)", parsed.GetOption("--profile") ?? "local"));
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Config path", parsed.GetOption("--config") ?? DefaultConfigPath)));
        var workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Workspace path", parsed.GetOption("--workspace") ?? Path.Combine(currentDirectory, "workspace"))));
        var discoveredOllama = TryDiscoverOllamaModels();
        if (discoveredOllama.Models.Count > 0)
            output.WriteLine($"Detected Ollama models: {string.Join(", ", discoveredOllama.Models)}");

        var providerDefault = parsed.GetOption("--provider") ?? (discoveredOllama.IsAvailable ? "ollama" : DefaultProvider);
        var provider = Prompt(output, input, "Provider", providerDefault);
        var modelPresetId = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)
            ? Prompt(output, input, "Model preset", parsed.GetOption("--model-preset") ?? DefaultOllamaPresetId)
            : null;
        var modelDefault = parsed.GetOption("--model")
            ?? (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase) && discoveredOllama.Models.Count > 0
                ? discoveredOllama.Models[0]
                : new GatewayConfig().Llm.Model);
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
            ModelPresetId = parsed.GetOption("--model-preset"),
            Model = parsed.GetOption("--model") ?? new GatewayConfig().Llm.Model,
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
        => !string.Equals(provider?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase);

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

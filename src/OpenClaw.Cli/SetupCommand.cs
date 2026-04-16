using System.Globalization;
using System.Security.Cryptography;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;

namespace OpenClaw.Cli;

internal static class SetupCommand
{
    private const string DefaultConfigPath = "~/.openclaw/config/openclaw.settings.json";
    private const string DefaultApiKeyRef = "env:MODEL_PROVIDER_KEY";
    private const string DefaultProvider = "openai";
    private const string DefaultBackendChoice = "none";

    internal static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool canPrompt)
    {
        if (args.Length > 0 && string.Equals(args[0], "launch", StringComparison.OrdinalIgnoreCase))
            return await SetupLifecycleCommand.RunLaunchAsync(args[1..], output, error, currentDirectory);
        if (args.Length > 0 && string.Equals(args[0], "service", StringComparison.OrdinalIgnoreCase))
            return await SetupLifecycleCommand.RunServiceAsync(args[1..], output, error, currentDirectory);
        if (args.Length > 0 && string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
            return SetupLifecycleCommand.RunStatus(args[1..], output, error);
        if (args.Length > 0 && string.Equals(args[0], "channel", StringComparison.OrdinalIgnoreCase))
            return await ChannelSetupCommand.RunAsync(args[1..], input, output, error, canPrompt);

        var parsed = CliArgs.Parse(args);
        var nonInteractive = parsed.HasFlag("--non-interactive");
        var requiresPrompt = RequiresPrompt(parsed);

        if (requiresPrompt && !nonInteractive && !canPrompt)
        {
            error.WriteLine("Missing setup inputs and no interactive terminal is available. Re-run with --non-interactive and explicit values, or run 'openclaw setup' from a terminal.");
            return 2;
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
            return 2;
        }

        var warnings = new List<string>();
        var config = BuildConfig(answers, warnings);
        var validationErrors = ConfigValidator.Validate(config);
        if (validationErrors.Count > 0)
        {
            error.WriteLine("Config validation failed:");
            foreach (var validationError in validationErrors)
                error.WriteLine($"- {validationError}");
            return 1;
        }

        Directory.CreateDirectory(answers.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(answers.ConfigPath)!);
        Directory.CreateDirectory(config.Memory.StoragePath);

        await GatewayConfigFile.SaveAsync(config, answers.ConfigPath);

        var envExamplePath = BuildEnvExamplePath(answers.ConfigPath);
        await File.WriteAllTextAsync(
            envExamplePath,
            BuildEnvExample(answers, BuildReachableBaseUrl(answers.BindAddress, answers.Port)),
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
        output.WriteLine("Launch:");
        output.WriteLine($"dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(answers.ConfigPath)}");
        output.WriteLine($"OPENCLAW_BASE_URL={BuildReachableBaseUrl(answers.BindAddress, answers.Port)} OPENCLAW_AUTH_TOKEN={answers.AuthToken} dotnet run --project src/OpenClaw.Companion -c Release");

        output.WriteLine();
        output.WriteLine("Verify:");
        output.WriteLine($"dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(answers.ConfigPath)} --doctor");
        output.WriteLine($"OPENCLAW_BASE_URL={BuildReachableBaseUrl(answers.BindAddress, answers.Port)} OPENCLAW_AUTH_TOKEN={answers.AuthToken} dotnet run --project src/OpenClaw.Cli -c Release -- admin posture");
        return 0;
    }

    private static bool RequiresPrompt(CliArgs parsed)
    {
        var required = new[]
        {
            "--profile",
            "--workspace",
            "--provider",
            "--model",
            "--api-key"
        };

        return required.Any(option => string.IsNullOrWhiteSpace(parsed.GetOption(option)));
    }

    private static SetupAnswers PromptForAnswers(CliArgs parsed, TextReader input, TextWriter output, string currentDirectory)
    {
        var profile = NormalizeProfile(Prompt(output, input, "Deployment profile (local|public)", parsed.GetOption("--profile") ?? "local"));
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Config path", parsed.GetOption("--config") ?? DefaultConfigPath)));
        var workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(Prompt(output, input, "Workspace path", parsed.GetOption("--workspace") ?? Path.Combine(currentDirectory, "workspace"))));
        var provider = Prompt(output, input, "Provider", parsed.GetOption("--provider") ?? DefaultProvider);
        var model = Prompt(output, input, "Model", parsed.GetOption("--model") ?? new GatewayConfig().Llm.Model);
        var apiKey = Prompt(output, input, "API key or env: reference", parsed.GetOption("--api-key") ?? DefaultApiKeyRef);

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
            Model = model,
            ApiKey = apiKey,
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

        var profile = NormalizeProfile(rawProfile ?? "local");
        return new SetupAnswers
        {
            Profile = profile,
            ConfigPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? DefaultConfigPath)),
            Workspace = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--workspace") ?? Path.Combine(currentDirectory, "workspace"))),
            Provider = parsed.GetOption("--provider") ?? DefaultProvider,
            Model = parsed.GetOption("--model") ?? new GatewayConfig().Llm.Model,
            ApiKey = parsed.GetOption("--api-key") ?? DefaultApiKeyRef,
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

        var config = new GatewayConfig
        {
            BindAddress = answers.BindAddress,
            Port = answers.Port,
            AuthToken = answers.AuthToken,
            Llm = new LlmProviderConfig
            {
                Provider = answers.Provider,
                Model = answers.Model,
                ApiKey = answers.ApiKey
            },
            Memory = new MemoryConfig
            {
                Provider = "file",
                StoragePath = memoryRoot,
                Retention = new MemoryRetentionConfig
                {
                    ArchivePath = Path.Combine(memoryRoot, "archive")
                }
            },
            Tooling = new ToolingConfig
            {
                WorkspaceRoot = answers.Workspace,
                WorkspaceOnly = true,
                AllowShell = answers.Profile == "local",
                AllowedReadRoots = [answers.Workspace],
                AllowedWriteRoots = [answers.Workspace],
                RequireToolApproval = answers.Profile == "public"
            },
            Security = new SecurityConfig
            {
                AllowQueryStringToken = false,
                TrustForwardedHeaders = answers.Profile == "public",
                RequireRequesterMatchForHttpToolApproval = answers.Profile == "public"
            }
        };

        if (answers.Profile == "public")
        {
            config.Plugins.Enabled = false;
            warnings.Add("Public profile disables third-party bridge plugins by default. Re-enable them only after you have a proxy, TLS, and explicit public-bind trust settings in place.");
        }

        if (answers.Profile == "public" && !answers.ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Public profile is using a direct API key value in the config file. Prefer env:... references or OS-backed secret storage.");

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

    private static string BuildEnvExample(SetupAnswers answers, string baseUrl)
    {
        var lines = new List<string>
        {
            $"{ResolveProviderEnvVariable(answers.ApiKey)}=replace-me",
            $"OPENCLAW_AUTH_TOKEN={answers.AuthToken}",
            $"OPENCLAW_BASE_URL={baseUrl}",
            $"OPENCLAW_WORKSPACE={answers.Workspace}"
        };

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string ResolveProviderEnvVariable(string apiKey)
    {
        if (apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && apiKey.Length > 4)
            return apiKey[4..];

        return "MODEL_PROVIDER_KEY";
    }

    private static string BuildEnvExamplePath(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Config path must contain a directory.");
        var stem = Path.GetFileNameWithoutExtension(configPath);
        return Path.Combine(directory, $"{stem}.env.example");
    }

    internal static string BuildReachableBaseUrl(string bindAddress, int port)
    {
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "::", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "[::]", StringComparison.Ordinal))
        {
            return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (bindAddress.Contains(':') && !bindAddress.StartsWith("[", StringComparison.Ordinal))
            return $"http://[{bindAddress}]:{port.ToString(CultureInfo.InvariantCulture)}";

        return $"http://{bindAddress}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

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

    private static string NormalizeProfile(string profile)
    {
        var normalized = profile.Trim().ToLowerInvariant();
        if (normalized is not ("local" or "public"))
            throw new ArgumentException("Invalid value for --profile (expected: local|public).");
        return normalized;
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

    private sealed class SetupAnswers
    {
        public required string Profile { get; init; }
        public required string ConfigPath { get; init; }
        public required string Workspace { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        public required string ApiKey { get; init; }
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
}

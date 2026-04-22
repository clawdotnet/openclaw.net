using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Bootstrap;

internal enum StartupRecoveryResult
{
    NotHandled,
    Recovered,
    Declined
}

internal static class InteractiveStartupRecovery
{
    private const string ModelProviderKeyEnv = "MODEL_PROVIDER_KEY";
    private const string OpenAiApiKeyEnv = "OPENAI_API_KEY";
    private const string ModelProviderEndpointEnv = "MODEL_PROVIDER_ENDPOINT";
    private const string WorkspaceEnv = "OPENCLAW_WORKSPACE";
    private const string BindAddressEnv = "OpenClaw__BindAddress";
    private const string PortEnv = "OpenClaw__Port";
    private const string MemoryProviderEnv = "OpenClaw__Memory__Provider";
    private const string MemoryStoragePathEnv = "OpenClaw__Memory__StoragePath";
    private const string MemoryRetentionEnabledEnv = "OpenClaw__Memory__Retention__Enabled";

    public static StartupRecoveryResult TryRecover(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        string currentDirectory,
        bool canPrompt,
        TextReader? input = null,
        TextWriter? output = null)
    {
        if (!canPrompt)
            return StartupRecoveryResult.NotHandled;

        if (input is null || output is null)
        {
            if (!IsInteractiveConsole())
                return StartupRecoveryResult.NotHandled;

            input = Console.In;
            output = Console.Out;
        }

        if (!IsRecoverable(ex))
            return StartupRecoveryResult.NotHandled;

        output.WriteLine();
        output.WriteLine(StartupFailureReporter.Render(ex, startup, environmentName, isDoctorMode: false));
        output.WriteLine();
        output.WriteLine("A minimal local setup can apply safe defaults for this run and retry startup.");
        output.WriteLine("It keeps the gateway on 127.0.0.1, uses file memory under a writable local path, and does not persist anything to your shell.");
        if (!PromptYesNo(output, input, "Apply minimal local setup now?", defaultValue: true))
            return StartupRecoveryResult.Declined;

        var defaults = BuildDefaults(ex, startup, currentDirectory);
        output.WriteLine();
        output.WriteLine($"Provider/model: {defaults.Provider}/{defaults.Model}");
        output.WriteLine("Local bind: 127.0.0.1");

        var workspacePath = Prompt(output, input, "Workspace path", defaults.WorkspacePath);
        var memoryPath = Prompt(output, input, "Memory path", defaults.MemoryPath);
        var port = PromptPort(output, input, defaults.Port);
        var apiKey = ResolveApiKey(output, input, defaults.Provider, defaults.ApiKey);
        var endpoint = ResolveEndpoint(output, input, defaults.Provider, defaults.Endpoint);

        try
        {
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(memoryPath);
        }
        catch (Exception createError) when (createError is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Could not prepare the local workspace or memory path: {createError.Message}");
            return StartupRecoveryResult.Declined;
        }

        ApplyLocalOverrides(workspacePath, memoryPath, port, apiKey, endpoint);

        output.WriteLine();
        output.WriteLine("Applied local startup overrides for this process. Retrying gateway startup...");
        output.WriteLine("For a persistent setup later, run: openclaw setup");
        return StartupRecoveryResult.Recovered;
    }

    private static StartupRecoveryDefaults BuildDefaults(Exception ex, GatewayStartupContext? startup, string currentDirectory)
    {
        var config = startup?.Config ?? new GatewayConfig();
        var port = config.Port > 0 ? config.Port : 18789;
        if (IsPortInUse(ex))
            port++;

        var workspacePath = Environment.GetEnvironmentVariable(WorkspaceEnv);
        if (string.IsNullOrWhiteSpace(workspacePath))
            workspacePath = startup?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            workspacePath = currentDirectory;

        var provider = string.IsNullOrWhiteSpace(config.Llm.Provider) ? new GatewayConfig().Llm.Provider : config.Llm.Provider;
        var model = string.IsNullOrWhiteSpace(config.Llm.Model) ? new GatewayConfig().Llm.Model : config.Llm.Model;

        return new StartupRecoveryDefaults
        {
            WorkspacePath = Path.GetFullPath(workspacePath),
            MemoryPath = Path.Combine(currentDirectory, "memory"),
            Port = port,
            Provider = provider,
            Model = model,
            ApiKey = ResolveExistingApiKey(provider),
            Endpoint = ResolveExistingEndpoint(config)
        };
    }

    private static void ApplyLocalOverrides(string workspacePath, string memoryPath, int port, string? apiKey, string? endpoint)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
        Environment.SetEnvironmentVariable(WorkspaceEnv, workspacePath);
        Environment.SetEnvironmentVariable(BindAddressEnv, "127.0.0.1");
        Environment.SetEnvironmentVariable(PortEnv, port.ToString());
        Environment.SetEnvironmentVariable(MemoryProviderEnv, "file");
        Environment.SetEnvironmentVariable(MemoryStoragePathEnv, memoryPath);
        Environment.SetEnvironmentVariable(MemoryRetentionEnabledEnv, "false");

        if (!string.IsNullOrWhiteSpace(apiKey))
            Environment.SetEnvironmentVariable(ModelProviderKeyEnv, apiKey);
        if (!string.IsNullOrWhiteSpace(endpoint))
            Environment.SetEnvironmentVariable(ModelProviderEndpointEnv, endpoint);
    }

    private static string? ResolveExistingApiKey(string provider)
    {
        var configured = Environment.GetEnvironmentVariable(ModelProviderKeyEnv);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var openAiApiKey = Environment.GetEnvironmentVariable(OpenAiApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(openAiApiKey))
                return openAiApiKey;
        }

        return null;
    }

    private static string? ResolveExistingEndpoint(GatewayConfig config)
        => Environment.GetEnvironmentVariable(ModelProviderEndpointEnv) ?? config.Llm.Endpoint;

    private static string? ResolveApiKey(TextWriter output, TextReader input, string provider, string? existingApiKey)
    {
        if (!RequiresApiKey(provider))
            return existingApiKey;

        if (!string.IsNullOrWhiteSpace(existingApiKey))
        {
            if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelProviderKeyEnv)) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OpenAiApiKeyEnv)))
            {
                output.WriteLine($"Using {OpenAiApiKeyEnv} from the current environment for this run.");
            }

            return existingApiKey;
        }

        return PromptRequiredSecret(output, input, $"{provider} API key");
    }

    private static string? ResolveEndpoint(TextWriter output, TextReader input, string provider, string? existingEndpoint)
    {
        if (!RequiresEndpoint(provider))
            return existingEndpoint;

        if (!string.IsNullOrWhiteSpace(existingEndpoint))
            return existingEndpoint;

        return PromptRequired(output, input, $"{provider} endpoint");
    }

    private static bool RequiresApiKey(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "openai" or "anthropic" or "claude" or "gemini" or "google" or "azure-openai" or "openai-compatible" or "groq" or "together" or "lmstudio" or "amazon-bedrock" or "anthropic-vertex" => true,
            _ => false
        };

    private static bool RequiresEndpoint(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "azure-openai" or "openai-compatible" or "groq" or "together" or "lmstudio" or "amazon-bedrock" or "anthropic-vertex" => true,
            _ => false
        };

    private static bool IsRecoverable(Exception ex)
    {
        var message = ex.Message;
        return Contains(message, "OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address.") ||
               Contains(message, "MODEL_PROVIDER_KEY must be set") ||
               Contains(message, "MODEL_PROVIDER_ENDPOINT must be set") ||
               Contains(message, "Endpoint must be set for provider") ||
               Contains(message, "Memory.StoragePath") ||
               IsStorageAccessError(ex) ||
               IsPortInUse(ex);
    }

    private static bool IsInteractiveConsole()
        => Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    private static bool IsStorageAccessError(Exception ex)
    {
        var baseMessage = ex.GetBaseException().Message;
        return baseMessage.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Access to the path", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortInUse(Exception ex)
    {
        var baseException = ex.GetBaseException();
        return string.Equals(baseException.GetType().Name, "AddressInUseException", StringComparison.Ordinal) ||
               baseException.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               baseException.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string Prompt(TextWriter output, TextReader input, string label, string defaultValue)
    {
        output.Write($"{label} [{defaultValue}]: ");
        var value = input.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string PromptRequired(TextWriter output, TextReader input, string label)
    {
        while (true)
        {
            output.Write($"{label}: ");
            var value = input.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            output.WriteLine("A value is required.");
        }
    }

    private static string PromptRequiredSecret(TextWriter output, TextReader input, string label)
    {
        if (ReferenceEquals(input, Console.In) && ReferenceEquals(output, Console.Out) && !Console.IsInputRedirected)
        {
            return ReadSecretFromConsole(label);
        }

        return PromptRequired(output, input, label);
    }

    private static string ReadSecretFromConsole(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var buffer = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Count == 0)
                        continue;

                    buffer.RemoveAt(buffer.Count - 1);
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                    buffer.Add(key.KeyChar);
            }

            var value = new string([.. buffer]).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            Console.WriteLine("A value is required.");
        }
    }

    private static bool PromptYesNo(TextWriter output, TextReader input, string label, bool defaultValue)
    {
        var suffix = defaultValue ? "[Y/n]" : "[y/N]";
        output.Write($"{label} {suffix}: ");
        var value = input.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value is "y" or "yes";
    }

    private static int PromptPort(TextWriter output, TextReader input, int defaultPort)
    {
        while (true)
        {
            var value = Prompt(output, input, "Port", defaultPort.ToString());
            if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                return port;

            output.WriteLine("Port must be between 1 and 65535.");
        }
    }

    private sealed class StartupRecoveryDefaults
    {
        public required string WorkspacePath { get; init; }
        public required string MemoryPath { get; init; }
        public required int Port { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        public string? ApiKey { get; init; }
        public string? Endpoint { get; init; }
    }
}

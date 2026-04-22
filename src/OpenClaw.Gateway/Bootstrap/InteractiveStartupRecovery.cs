using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Bootstrap;

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

    public static StartupRecoveryOutcome TryQuickstart(
        string currentDirectory,
        LocalStartupStateStore stateStore,
        TextReader? input = null,
        TextWriter? output = null)
    {
        if (input is null || output is null)
        {
            if (!TerminalPrompts.IsInteractiveConsole())
                return StartupRecoveryOutcome.NotHandled;

            input = Console.In;
            output = Console.Out;
        }

        var remembered = stateStore.Load();
        var defaults = BuildQuickstartDefaults(currentDirectory, remembered);

        output.WriteLine("Quickstart will apply a minimal local profile for this run.");
        output.WriteLine($"Provider/model: {defaults.Provider}/{defaults.Model}");
        output.WriteLine($"Workspace: {defaults.WorkspacePath}");
        output.WriteLine($"Memory path: {defaults.MemoryPath}");
        output.WriteLine($"Local bind: 127.0.0.1:{defaults.Port}");

        var apiKey = ResolveApiKey(output, input, defaults.Provider, defaults.ApiKey);
        var endpoint = ResolveEndpoint(output, input, defaults.Provider, defaults.Endpoint);

        try
        {
            Directory.CreateDirectory(defaults.WorkspacePath);
            Directory.CreateDirectory(defaults.MemoryPath);
        }
        catch (Exception createError) when (createError is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Could not prepare the local workspace or memory path: {createError.Message}");
            return StartupRecoveryOutcome.Declined;
        }

        var session = new LocalStartupSession(
            Mode: "quickstart",
            WorkspacePath: defaults.WorkspacePath,
            MemoryPath: defaults.MemoryPath,
            Port: defaults.Port,
            Provider: defaults.Provider,
            Model: defaults.Model,
            ApiKeyReference: ResolveApiKeyReference(defaults.Provider),
            Endpoint: endpoint);
        ApplyLocalOverrides(session, apiKey);

        output.WriteLine();
        output.WriteLine("Applied quickstart local overrides for this process.");
        output.WriteLine("The gateway will start on 127.0.0.1 with writable local storage.");
        return StartupRecoveryOutcome.Recovered(session);
    }

    public static StartupRecoveryOutcome TryRecover(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        string currentDirectory,
        bool canPrompt,
        LocalStartupStateStore stateStore,
        bool suggestQuickstart,
        TextReader? input = null,
        TextWriter? output = null)
    {
        if (!canPrompt)
            return StartupRecoveryOutcome.NotHandled;

        if (input is null || output is null)
        {
            if (!TerminalPrompts.IsInteractiveConsole())
                return StartupRecoveryOutcome.NotHandled;

            input = Console.In;
            output = Console.Out;
        }

        if (!IsRecoverable(ex))
            return StartupRecoveryOutcome.NotHandled;

        output.WriteLine();
        output.WriteLine(StartupFailureReporter.Render(ex, startup, environmentName, isDoctorMode: false, suggestQuickstart));
        output.WriteLine();
        output.WriteLine("A minimal local setup can apply safe defaults for this run and retry startup.");
        output.WriteLine("It keeps the gateway on 127.0.0.1, uses file memory under a writable local path, and does not persist anything until you choose to save it.");
        if (!TerminalPrompts.PromptYesNo(output, input, "Apply minimal local setup now?", defaultValue: true))
            return StartupRecoveryOutcome.Declined;

        var defaults = BuildRecoveryDefaults(ex, startup, currentDirectory, stateStore.Load());
        output.WriteLine();
        output.WriteLine($"Provider/model: {defaults.Provider}/{defaults.Model}");
        output.WriteLine("Local bind: 127.0.0.1");

        if (IsPortInUse(ex) &&
            defaults.Port < 65535 &&
            TerminalPrompts.PromptYesNo(output, input, $"Port {defaults.Port} is busy. Use {defaults.Port + 1} instead?", defaultValue: true))
        {
            defaults = defaults with { Port = defaults.Port + 1 };
        }

        var workspacePath = TerminalPrompts.Prompt(output, input, "Workspace path", defaults.WorkspacePath);
        var memoryPath = TerminalPrompts.Prompt(output, input, "Memory path", defaults.MemoryPath);
        var port = TerminalPrompts.PromptPort(output, input, defaults.Port);
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
            return StartupRecoveryOutcome.Declined;
        }

        var session = new LocalStartupSession(
            Mode: "recovery",
            WorkspacePath: Path.GetFullPath(workspacePath),
            MemoryPath: Path.GetFullPath(memoryPath),
            Port: port,
            Provider: defaults.Provider,
            Model: defaults.Model,
            ApiKeyReference: ResolveApiKeyReference(defaults.Provider),
            Endpoint: endpoint);
        ApplyLocalOverrides(session, apiKey);

        output.WriteLine();
        output.WriteLine("Applied local startup overrides for this process. Retrying gateway startup...");
        output.WriteLine("For a persistent setup later, run: openclaw setup");
        return StartupRecoveryOutcome.Recovered(session);
    }

    private static StartupRecoveryDefaults BuildQuickstartDefaults(string currentDirectory, LocalStartupState remembered)
    {
        var config = new GatewayConfig();
        var workspacePath = ResolvePreferredPath(remembered.WorkspacePath, currentDirectory);
        var memoryPath = ResolvePreferredPath(remembered.MemoryPath, Path.Combine(currentDirectory, "memory"));
        var provider = string.IsNullOrWhiteSpace(remembered.Provider) ? config.Llm.Provider : remembered.Provider;
        var model = string.IsNullOrWhiteSpace(remembered.Model) ? config.Llm.Model : remembered.Model;
        var port = remembered.Port is >= 1 and <= 65535 ? remembered.Port.Value : 18789;
        return new StartupRecoveryDefaults(
            WorkspacePath: workspacePath,
            MemoryPath: memoryPath,
            Port: port,
            Provider: provider,
            Model: model,
            ApiKey: ResolveExistingApiKey(provider),
            Endpoint: ResolveExistingEndpoint(config));
    }

    private static StartupRecoveryDefaults BuildRecoveryDefaults(
        Exception ex,
        GatewayStartupContext? startup,
        string currentDirectory,
        LocalStartupState remembered)
    {
        var config = startup?.Config ?? new GatewayConfig();
        var port = config.Port > 0 ? config.Port : (remembered.Port is >= 1 and <= 65535 ? remembered.Port.Value : 18789);
        var workspacePath = ResolvePreferredPath(
            remembered.WorkspacePath,
            Environment.GetEnvironmentVariable(WorkspaceEnv) ?? startup?.WorkspacePath ?? currentDirectory);
        var memoryPath = ResolvePreferredPath(remembered.MemoryPath, Path.Combine(currentDirectory, "memory"));

        var provider = string.IsNullOrWhiteSpace(config.Llm.Provider) ? new GatewayConfig().Llm.Provider : config.Llm.Provider;
        var model = string.IsNullOrWhiteSpace(config.Llm.Model) ? new GatewayConfig().Llm.Model : config.Llm.Model;

        return new StartupRecoveryDefaults(
            WorkspacePath: workspacePath,
            MemoryPath: memoryPath,
            Port: port,
            Provider: provider,
            Model: model,
            ApiKey: ResolveExistingApiKey(provider),
            Endpoint: ResolveExistingEndpoint(config));
    }

    private static string ResolvePreferredPath(string? rememberedPath, string fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(rememberedPath))
            return Path.GetFullPath(rememberedPath);

        return Path.GetFullPath(fallbackPath);
    }

    private static void ApplyLocalOverrides(LocalStartupSession session, string? apiKey)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
        Environment.SetEnvironmentVariable(WorkspaceEnv, session.WorkspacePath);
        Environment.SetEnvironmentVariable(BindAddressEnv, "127.0.0.1");
        Environment.SetEnvironmentVariable(PortEnv, session.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(MemoryProviderEnv, "file");
        Environment.SetEnvironmentVariable(MemoryStoragePathEnv, session.MemoryPath);
        Environment.SetEnvironmentVariable(MemoryRetentionEnabledEnv, "false");

        if (!string.IsNullOrWhiteSpace(apiKey))
            Environment.SetEnvironmentVariable(ModelProviderKeyEnv, apiKey);
        if (!string.IsNullOrWhiteSpace(session.Endpoint))
            Environment.SetEnvironmentVariable(ModelProviderEndpointEnv, session.Endpoint);
    }

    private static string ResolveApiKeyReference(string provider)
    {
        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelProviderKeyEnv)) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OpenAiApiKeyEnv)))
        {
            return "env:OPENAI_API_KEY";
        }

        return "env:MODEL_PROVIDER_KEY";
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

        return TerminalPrompts.PromptRequiredSecret(output, input, $"{provider} API key");
    }

    private static string? ResolveEndpoint(TextWriter output, TextReader input, string provider, string? existingEndpoint)
    {
        if (!RequiresEndpoint(provider))
            return existingEndpoint;

        if (!string.IsNullOrWhiteSpace(existingEndpoint))
            return existingEndpoint;

        return TerminalPrompts.PromptRequired(output, input, $"{provider} endpoint");
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

    private static bool IsStorageAccessError(Exception ex)
    {
        var baseMessage = ex.GetBaseException().Message;
        return baseMessage.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Access to the path", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPortInUse(Exception ex)
    {
        var baseException = ex.GetBaseException();
        return string.Equals(baseException.GetType().Name, "AddressInUseException", StringComparison.Ordinal) ||
               baseException.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               baseException.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private sealed record StartupRecoveryDefaults(
        string WorkspacePath,
        string MemoryPath,
        int Port,
        string Provider,
        string Model,
        string? ApiKey,
        string? Endpoint);
}

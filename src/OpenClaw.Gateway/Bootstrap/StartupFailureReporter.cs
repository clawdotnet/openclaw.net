using System.Text;

namespace OpenClaw.Gateway.Bootstrap;

internal static class StartupFailureReporter
{
    public static void Write(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart = false,
        TextWriter? error = null)
    {
        var writer = error ?? Console.Error;
        writer.WriteLine(Render(ex, startup, environmentName, isDoctorMode, suggestQuickstart));
        writer.Flush();
    }

    internal static string Render(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart = false)
    {
        var report = Analyze(ex, startup, environmentName, isDoctorMode, suggestQuickstart);
        var sb = new StringBuilder();
        sb.AppendLine($"Startup failed: {report.Title}");
        sb.AppendLine();
        sb.AppendLine(report.Summary);

        if (report.Details.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            foreach (var detail in report.Details)
                sb.AppendLine($"- {detail}");
        }

        if (report.Actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Suggested fixes:");
            foreach (var action in report.Actions)
                sb.AppendLine($"- {action}");
        }

        return sb.ToString().TrimEnd();
    }

    private static StartupFailureReport Analyze(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var message = ex.Message;

        if (Contains(message, "OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address."))
            return BuildAuthTokenReport(startup, environmentName, isDoctorMode, suggestQuickstart);

        if (IsPortInUse(ex))
            return BuildPortInUseReport(ex, startup, isDoctorMode, suggestQuickstart);

        if (Contains(message, "MODEL_PROVIDER_KEY must be set") ||
            Contains(message, "MODEL_PROVIDER_ENDPOINT must be set") ||
            Contains(message, "Endpoint must be set for provider") ||
            Contains(message, "Configured provider '") ||
            Contains(message, "Unsupported LLM provider"))
        {
            return BuildProviderReport(ex, startup, isDoctorMode, suggestQuickstart);
        }

        if (Contains(message, "Memory.StoragePath") || IsStorageAccessError(ex))
            return BuildStorageReport(ex, startup, environmentName, isDoctorMode, suggestQuickstart);

        return BuildGenericReport(ex, startup, environmentName, isDoctorMode, suggestQuickstart);
    }

    private static StartupFailureReport BuildAuthTokenReport(
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var details = new List<string>();
        AddIfValue(details, "Current ASPNETCORE_ENVIRONMENT", environmentName);
        AddIfValue(details, "BindAddress", startup?.Config.BindAddress);
        details.Add("Non-loopback binds require OPENCLAW_AUTH_TOKEN so remote requests are authenticated.");

        if (string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            details.Add("Production settings can apply container-style defaults, so local launches often need explicit Development or bind-path overrides.");
        }

        var actions = new List<string>
        {
            "For local-only use, set OpenClaw__BindAddress to 127.0.0.1.",
            "If you are starting the gateway on your machine, set ASPNETCORE_ENVIRONMENT to Development so the repo-local defaults are used.",
            "If you do want a non-loopback bind, set OPENCLAW_AUTH_TOKEN to a strong random value before launch."
        };
        AddOperatorNextSteps(actions, isDoctorMode, suggestQuickstart);

        return new StartupFailureReport(
            Title: "authentication is required for a non-loopback bind.",
            Summary: "The gateway is configured to listen on a network-reachable address, but no auth token was configured.",
            Details: details,
            Actions: actions);
    }

    private static StartupFailureReport BuildStorageReport(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var details = new List<string>();
        AddIfValue(details, "Current ASPNETCORE_ENVIRONMENT", environmentName);
        AddIfValue(details, "Memory.StoragePath", startup?.Config.Memory.StoragePath);
        if (string.Equals(startup?.Config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
            AddIfValue(details, "Memory.Sqlite.DbPath", startup?.Config.Memory.Sqlite.DbPath);
        if (startup?.Config.Memory.Retention.Enabled == true)
            AddIfValue(details, "Memory.Retention.ArchivePath", startup.Config.Memory.Retention.ArchivePath);
        AddIfValue(details, "Underlying error", ex.GetBaseException().Message);

        var actions = new List<string>();
        if (string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            actions.Add("If you are starting locally, set ASPNETCORE_ENVIRONMENT to Development so the gateway uses repo-local storage defaults.");
        }

        actions.Add("Set OpenClaw__Memory__StoragePath to a writable directory such as ./memory.");
        if (string.Equals(startup?.Config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add("If sqlite is enabled, set OpenClaw__Memory__Sqlite__DbPath under that writable directory as well.");
        }
        if (startup?.Config.Memory.Retention.Enabled == true)
        {
            actions.Add("If retention archiving is enabled, set OpenClaw__Memory__Retention__ArchivePath under that writable directory too.");
        }

        actions.Add("Make sure the chosen directory exists or can be created by the current user.");
        AddOperatorNextSteps(actions, isDoctorMode, suggestQuickstart);

        return new StartupFailureReport(
            Title: "the configured memory storage path is not writable.",
            Summary: "The gateway could not create one of its startup files under the configured storage root.",
            Details: details,
            Actions: actions);
    }

    private static StartupFailureReport BuildProviderReport(
        Exception ex,
        GatewayStartupContext? startup,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var config = startup?.Config;
        var provider = config?.Llm.Provider ?? TryExtractQuotedValue(ex.Message, "Configured provider '");
        var providerLabel = FormatProviderName(provider);
        var details = new List<string>();
        AddIfValue(details, "Provider", provider);
        AddIfValue(details, "Model", config?.Llm.Model);
        AddIfValue(details, "Underlying error", ex.GetBaseException().Message);

        var actions = new List<string>();
        string title;
        string summary;
        if (Contains(ex.Message, "MODEL_PROVIDER_KEY must be set"))
        {
            title = $"{providerLabel} credentials are missing.";
            summary = "The built-in LLM provider could not be initialized because the required API key was not configured.";
            actions.Add("Set MODEL_PROVIDER_KEY before launch.");
            if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("If your OpenAI key is stored in OPENAI_API_KEY, copy or map that value into MODEL_PROVIDER_KEY before starting the gateway.");
            }
        }
        else if (Contains(ex.Message, "MODEL_PROVIDER_ENDPOINT must be set") ||
                 Contains(ex.Message, "Endpoint must be set for provider"))
        {
            title = $"{providerLabel} endpoint is missing.";
            summary = "The selected LLM provider needs an explicit endpoint, but no endpoint was configured.";
            actions.Add("Set MODEL_PROVIDER_ENDPOINT to the provider base URL before launch.");
            if (string.Equals(provider, "azure-openai", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("For Azure OpenAI, use the Azure resource endpoint, for example https://<resource>.openai.azure.com/.");
            }
        }
        else
        {
            title = $"{providerLabel} could not be initialized.";
            summary = "The configured LLM provider was not available during startup.";
            actions.Add("Confirm OpenClaw:Llm:Provider points to a built-in provider or to a plugin-backed provider that is enabled.");
            actions.Add("If this provider should come from a plugin, enable that plugin before launch.");
        }

        AddOperatorNextSteps(actions, isDoctorMode, suggestQuickstart);

        return new StartupFailureReport(
            Title: title,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    private static StartupFailureReport BuildPortInUseReport(
        Exception ex,
        GatewayStartupContext? startup,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var details = new List<string>();
        AddIfValue(details, "BindAddress", startup?.Config.BindAddress);
        if (startup is not null)
            details.Add($"Port: {startup.Config.Port}");
        AddIfValue(details, "Underlying error", ex.GetBaseException().Message);

        var actions = new List<string>
        {
            "Stop the process that is already using this port, or change OpenClaw__Port to a free port."
        };
        AddOperatorNextSteps(actions, isDoctorMode, suggestQuickstart);

        return new StartupFailureReport(
            Title: "the configured HTTP port is already in use.",
            Summary: "Kestrel could not bind the gateway listener because another process already owns the requested address.",
            Details: details,
            Actions: actions);
    }

    private static StartupFailureReport BuildGenericReport(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        bool isDoctorMode,
        bool suggestQuickstart)
    {
        var details = new List<string>
        {
            $"Exception: {ex.GetType().Name}",
            $"Message: {ex.GetBaseException().Message}"
        };
        AddIfValue(details, "Current ASPNETCORE_ENVIRONMENT", environmentName);
        AddIfValue(details, "BindAddress", startup?.Config.BindAddress);
        if (startup is not null)
            details.Add($"Port: {startup.Config.Port}");
        AddIfValue(details, "Memory.StoragePath", startup?.Config.Memory.StoragePath);
        AddIfValue(details, "Provider", startup?.Config.Llm.Provider);
        AddIfValue(details, "Model", startup?.Config.Llm.Model);

        var actions = new List<string>
        {
            "Review the startup settings above and correct the misconfiguration before retrying."
        };
        AddOperatorNextSteps(actions, isDoctorMode, suggestQuickstart);

        return new StartupFailureReport(
            Title: "an unexpected startup exception occurred.",
            Summary: "The gateway exited before it finished starting.",
            Details: details,
            Actions: actions);
    }

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

    private static void AddOperatorNextSteps(ICollection<string> actions, bool isDoctorMode, bool suggestQuickstart)
    {
        if (suggestQuickstart)
            actions.Add("For an interactive local bootstrap, rerun the gateway with --quickstart.");

        if (!isDoctorMode)
        {
            actions.Add("Run the preflight checks with: dotnet run --project src/OpenClaw.Gateway -c Release -- --doctor");
        }
    }

    private static void AddIfValue(ICollection<string> details, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            details.Add($"{label}: {value}");
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractQuotedValue(string value, string prefix)
    {
        var start = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += prefix.Length;
        var end = value.IndexOf('\'', start);
        return end > start ? value[start..end] : null;
    }

    private static string FormatProviderName(string? provider)
        => provider?.ToLowerInvariant() switch
        {
            "openai" => "OpenAI provider",
            "azure-openai" => "Azure OpenAI provider",
            "anthropic" or "claude" => "Anthropic provider",
            "gemini" or "google" => "Gemini provider",
            { Length: > 0 } => $"Provider '{provider}'",
            _ => "The configured provider"
        };

    private sealed record StartupFailureReport(
        string Title,
        string Summary,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> Actions);
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Bootstrap;

internal static class ConfigurationSourceDiagnosticsBuilder
{
    public static ConfigSourceDiagnostics Build(IConfigurationRoot configuration, GatewayConfig config)
    {
        var items = new List<ConfigSourceDiagnosticItem>
        {
            BuildPlainItem(configuration, "Bind address", "OpenClaw:BindAddress", config.BindAddress),
            BuildPlainItem(configuration, "Memory storage path", "OpenClaw:Memory:StoragePath", config.Memory.StoragePath),
            BuildPlainItem(configuration, "SQLite DB path", "OpenClaw:Memory:Sqlite:DbPath", config.Memory.Sqlite.DbPath),
            BuildPlainItem(configuration, "Retention archive path", "OpenClaw:Memory:Retention:ArchivePath", config.Memory.Retention.ArchivePath),
            BuildPlainItem(configuration, "Workspace root", "OpenClaw:Tooling:WorkspaceRoot", config.Tooling.WorkspaceRoot),
            BuildPlainItem(configuration, "Provider", "OpenClaw:Llm:Provider", config.Llm.Provider),
            BuildProviderModelItem(configuration, config),
            BuildPlainItem(configuration, "Provider endpoint", "OpenClaw:Llm:Endpoint", config.Llm.Endpoint),
            BuildApiKeyItem(configuration, config)
        };

        return new ConfigSourceDiagnostics { Items = items };
    }

    public static string Render(ConfigSourceDiagnostics diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Items.Select(static item =>
            $"- {item.Label}: {item.EffectiveValue} (source: {item.Source})"));

    private static ConfigSourceDiagnosticItem BuildProviderModelItem(IConfigurationRoot configuration, GatewayConfig config)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL")))
        {
            return new ConfigSourceDiagnosticItem
            {
                Label = "Model",
                Key = "OpenClaw:Llm:Model",
                EffectiveValue = Display(config.Llm.Model),
                Source = "environment variable MODEL_PROVIDER_MODEL (compatibility override)"
            };
        }

        return BuildPlainItem(configuration, "Model", "OpenClaw:Llm:Model", config.Llm.Model);
    }

    private static ConfigSourceDiagnosticItem BuildApiKeyItem(IConfigurationRoot configuration, GatewayConfig config)
    {
        const string key = "OpenClaw:Llm:ApiKey";
        var configuredRef = configuration[key];
        var configuredSource = ResolveWinningSource(configuration, key);
        var modelProviderKey = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        var resolvedFromRef = SecretResolver.Resolve(configuredRef);
        var hasEffectiveKey = !string.IsNullOrWhiteSpace(config.Llm.ApiKey);

        string source;
        if (!string.IsNullOrWhiteSpace(configuredRef) && !string.IsNullOrWhiteSpace(resolvedFromRef))
        {
            source = $"{configuredSource}; {DescribeSecretReference(configuredRef)}";
        }
        else if (!string.IsNullOrWhiteSpace(modelProviderKey))
        {
            source = "environment variable MODEL_PROVIDER_KEY (compatibility override)";
        }
        else if (!string.IsNullOrWhiteSpace(configuredRef))
        {
            source = $"{configuredSource}; {DescribeSecretReference(configuredRef)} (unresolved)";
        }
        else
        {
            source = "not configured";
        }

        return new ConfigSourceDiagnosticItem
        {
            Label = "API key",
            Key = key,
            EffectiveValue = hasEffectiveKey ? "configured (redacted)" : "not configured",
            Source = source,
            Redacted = true
        };
    }

    private static ConfigSourceDiagnosticItem BuildPlainItem(
        IConfigurationRoot configuration,
        string label,
        string key,
        string? effectiveValue)
    {
        if (string.Equals(key, "OpenClaw:Llm:Endpoint", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT")))
        {
            return new ConfigSourceDiagnosticItem
            {
                Label = label,
                Key = key,
                EffectiveValue = Display(effectiveValue),
                Source = "environment variable MODEL_PROVIDER_ENDPOINT (compatibility override)"
            };
        }

        return new ConfigSourceDiagnosticItem
        {
            Label = label,
            Key = key,
            EffectiveValue = Display(effectiveValue),
            Source = ResolveWinningSource(configuration, key)
        };
    }

    private static string ResolveWinningSource(IConfigurationRoot configuration, string key)
    {
        string? source = null;
        foreach (var provider in configuration.Providers)
        {
            if (provider.TryGet(key, out _))
                source = DescribeProvider(provider, key);
        }

        return source ?? "built-in default";
    }

    private static string DescribeProvider(IConfigurationProvider provider, string key)
        => provider switch
        {
            JsonConfigurationProvider json when !string.IsNullOrWhiteSpace(json.Source.Path)
                => json.Source.Path!,
            EnvironmentVariablesConfigurationProvider
                => $"environment variable {ToEnvironmentVariableName(key)}",
            CommandLineConfigurationProvider
                => "command line",
            MemoryConfigurationProvider
                => "in-memory override",
            _ => provider.GetType().Name.Replace("ConfigurationProvider", "", StringComparison.Ordinal)
        };

    private static string DescribeSecretReference(string value)
    {
        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return $"secret reference {value}";

        if (value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
            return "raw secret reference (value redacted)";

        var envValue = Environment.GetEnvironmentVariable(value);
        if (envValue is not null)
            return $"environment variable {value}";

        return "literal or bare secret reference (value redacted)";
    }

    private static string ToEnvironmentVariableName(string key)
        => key.Replace(':', '_').Replace("_", "__", StringComparison.Ordinal);

    private static string Display(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unset" : value;
}

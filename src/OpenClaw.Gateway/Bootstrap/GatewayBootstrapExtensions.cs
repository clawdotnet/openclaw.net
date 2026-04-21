using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Bootstrap;

internal static class GatewayBootstrapExtensions
{
    public static async Task<BootstrapResult> AddOpenClawBootstrapAsync(this WebApplicationBuilder builder, string[] args)
    {
        ApplyConfigFileOverride(builder, args);

        builder.Services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

        var config = LoadGatewayConfig(builder.Configuration);

        var isNonLoopbackBind = !GatewaySecurity.IsLoopbackBind(config.BindAddress);
        var isDoctorMode = args.Any(a => string.Equals(a, "--doctor", StringComparison.Ordinal));
        var isHealthCheckMode = args.Any(a => string.Equals(a, "--health-check", StringComparison.Ordinal));

        if (isHealthCheckMode)
        {
            var exitCode = await RunHealthCheckAsync(config, isNonLoopbackBind);
            return new BootstrapResult
            {
                ShouldExit = true,
                ExitCode = exitCode
            };
        }

        if (isNonLoopbackBind && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            var message = "OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address.";
            if (isDoctorMode)
            {
                Console.Error.WriteLine(message);
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw new InvalidOperationException(message);
        }

        var configErrors = ConfigValidator.Validate(config).ToList();
        foreach (var featureError in ValidateOptionalFeatureCompatibility(config))
            configErrors.Add(featureError);
        if (configErrors.Count > 0)
        {
            foreach (var err in configErrors)
                Console.Error.WriteLine($"Configuration error: {err}");

            if (isDoctorMode)
            {
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw new InvalidOperationException($"Gateway configuration has {configErrors.Count} error(s). See above for details.");
        }

        GatewayRuntimeState runtimeState;
        try
        {
            runtimeState = RuntimeModeResolver.Resolve(config.Runtime);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            if (isDoctorMode)
            {
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw;
        }

        if (isDoctorMode)
        {
            var ok = await DoctorCheck.RunAsync(config, runtimeState);
            return new BootstrapResult
            {
                ShouldExit = true,
                ExitCode = ok ? 0 : 1
            };
        }

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind);

        return new BootstrapResult
        {
            ShouldExit = false,
            ExitCode = 0,
            Startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = runtimeState,
                IsNonLoopbackBind = isNonLoopbackBind,
                WorkspacePath = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE")
            }
        };
    }

    internal static GatewayConfig LoadGatewayConfig(IConfiguration configuration)
    {
        var openClawSection = configuration.GetSection("OpenClaw");
        var config = openClawSection.Get<GatewayConfig>() ?? new GatewayConfig();
        ApplyConfiguredToolingOverrides(openClawSection, config);
        HydratePluginEntryConfigJson(config, configuration);
        var pluginAdminSettingsPath = PluginAdminSettingsService.GetSettingsPath(config);
        if (PluginAdminSettingsService.TryLoadPersistedEntries(pluginAdminSettingsPath, out var pluginEntries, out _)
            && pluginEntries is not null)
        {
            PluginAdminSettingsService.ApplyEntries(config, pluginEntries);
        }
        ApplyEnvironmentOverrides(config);
        ApplyExecutionCompatibility(config);
        NormalizeCodingBackendConfig(config);
        return config;
    }

    internal static IReadOnlyList<string> ValidateOptionalFeatureCompatibility(GatewayConfig config)
    {
        var errors = new List<string>();
        if (ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config) &&
            !OptionalFeatureSupport.OpenSandboxEnabled)
        {
            errors.Add("Sandbox.Provider='OpenSandbox' requires a gateway build compiled with -p:OpenClawEnableOpenSandbox=true. Rebuild with that flag or set Sandbox.Provider='None'.");
        }

        return errors;
    }

    private static void ApplyConfigFileOverride(WebApplicationBuilder builder, string[] args)
    {
        var extraConfigPath = FindArgValue(args, "--config")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(extraConfigPath))
            return;

        var fullPath = Path.GetFullPath(ExpandPath(extraConfigPath));
        builder.Configuration.AddJsonFile(fullPath, optional: false, reloadOnChange: true);
    }

    private static string? FindArgValue(string[] argv, string name)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg.Equals(name, StringComparison.Ordinal) && i + 1 < argv.Length)
                return argv[i + 1];

            var prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..];
        }

        return null;
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (!expanded.StartsWith('~'))
            return expanded;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            expanded[1..].TrimStart('/').TrimStart('\\'));
    }

    private static void ApplyEnvironmentOverrides(GatewayConfig config)
    {
        config.Llm.ApiKey = ResolveSecretRefOrNull(config.Llm.ApiKey) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        config.Llm.Model = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? config.Llm.Model;
        config.Llm.Endpoint = ResolveSecretRefOrNull(config.Llm.Endpoint) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT");
        config.AuthToken ??= Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
    }

    private static void ApplyExecutionCompatibility(GatewayConfig config)
    {
        if (!ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config))
            return;

        if (!config.Execution.Profiles.ContainsKey("opensandbox"))
        {
            config.Execution.Profiles["opensandbox"] = new ExecutionBackendProfileConfig
            {
                Type = ExecutionBackendType.OpenSandbox,
                Enabled = true,
                Endpoint = config.Sandbox.Endpoint,
                ApiKey = config.Sandbox.ApiKey,
                TimeoutSeconds = Math.Max(5, config.Tooling.ToolTimeoutSeconds)
            };
        }

        foreach (var (toolName, defaultMode) in ToolSandboxPolicy.EnumerateBuiltInCandidates(config))
        {
            if (config.Execution.Tools.ContainsKey(toolName))
                continue;

            var mode = ToolSandboxPolicy.ResolveMode(config, toolName, defaultMode);
            if (mode == ToolSandboxMode.None)
                continue;

            config.Execution.Tools[toolName] = new ExecutionToolRouteConfig
            {
                Backend = "opensandbox",
                FallbackBackend = mode == ToolSandboxMode.Prefer ? "local" : null,
                RequireWorkspace = false
            };
        }
    }

    private static void NormalizeCodingBackendConfig(GatewayConfig config)
    {
        foreach (var backend in config.CodingBackends.EnumerateConfiguredBackends())
        {
            backend.ExecutablePath = NormalizeOptionalPath(backend.ExecutablePath);
            backend.DefaultWorkspacePath = NormalizeOptionalPath(backend.DefaultWorkspacePath);
            backend.Credentials.TokenFilePath = NormalizeOptionalPath(backend.Credentials.TokenFilePath);
        }
    }

    private static void HydratePluginEntryConfigJson(GatewayConfig config, IConfiguration configuration)
    {
        var entriesSection = configuration.GetSection("OpenClaw").GetSection("Plugins").GetSection("Entries");
        foreach (var pluginSection in entriesSection.GetChildren())
        {
            if (!config.Plugins.Entries.TryGetValue(pluginSection.Key, out var entry))
            {
                entry = new PluginEntryConfig();
                config.Plugins.Entries[pluginSection.Key] = entry;
            }

            var pluginConfigSection = pluginSection.GetSection("Config");
            if (!pluginConfigSection.Exists())
                continue;

            entry.Config = BuildJsonElement(pluginConfigSection);
        }
    }

    private static JsonElement? BuildJsonElement(IConfigurationSection section)
    {
        var node = BuildJsonNode(section);
        if (node is null)
            return null;

        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonNode? BuildJsonNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
            return BuildScalarNode(section.Value);

        if (TryGetArrayChildren(children, out var orderedChildren))
        {
            var array = new JsonArray();
            foreach (var child in orderedChildren)
                array.Add(BuildJsonNode(child));
            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
            obj[child.Key] = BuildJsonNode(child);

        return obj;
    }

    private static JsonNode? BuildScalarNode(string? value)
    {
        if (value is null)
            return null;

        if (bool.TryParse(value, out var boolValue))
            return JsonValue.Create(boolValue);

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            return JsonValue.Create(longValue);

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
            return JsonValue.Create(decimalValue);

        return JsonValue.Create(value);
    }

    private static bool TryGetArrayChildren(
        IEnumerable<IConfigurationSection> children,
        out IConfigurationSection[] orderedChildren)
    {
        var indexed = new List<(int Index, IConfigurationSection Section)>();
        foreach (var child in children)
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                orderedChildren = [];
                return false;
            }

            indexed.Add((index, child));
        }

        indexed.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        orderedChildren = indexed.Select(item => item.Section).ToArray();
        return true;
    }

    private static void ApplyConfiguredToolingOverrides(IConfiguration section, GatewayConfig config)
    {
        var toolingSection = section.GetSection("Tooling");

        var allowedReadRoots = ReadStringArray(toolingSection, "AllowedReadRoots");
        if (allowedReadRoots is not null)
            config.Tooling.AllowedReadRoots = allowedReadRoots;

        var allowedWriteRoots = ReadStringArray(toolingSection, "AllowedWriteRoots");
        if (allowedWriteRoots is not null)
            config.Tooling.AllowedWriteRoots = allowedWriteRoots;
    }

    private static string[]? ReadStringArray(IConfiguration section, string key)
    {
        var values = section.GetSection(key)
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        return values.Length > 0 ? values : null;
    }

    private static string? ResolveSecretRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return SecretResolver.Resolve(value);
        }

        return value;
    }

    private static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var expanded = ExpandPath(value);
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), expanded));
    }

    private static async Task<int> RunHealthCheckAsync(GatewayConfig config, bool isNonLoopbackBind)
    {
        var url = $"http://127.0.0.1:{config.Port}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (isNonLoopbackBind && !string.IsNullOrWhiteSpace(config.AuthToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AuthToken);

        try
        {
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}

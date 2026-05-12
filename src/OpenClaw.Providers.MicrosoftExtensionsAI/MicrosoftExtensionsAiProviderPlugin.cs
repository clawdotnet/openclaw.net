using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OpenClaw.PluginKit;

namespace OpenClaw.Providers.MicrosoftExtensionsAI;

public sealed class MicrosoftExtensionsAiProviderPlugin : INativeDynamicPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public void Register(INativeDynamicPluginContext context)
    {
        var config = ReadConfig(context.Config);
        if (config.Providers is not { Length: > 0 })
            throw new InvalidOperationException("Microsoft.Extensions.AI provider bridge requires at least one provider registration in config.providers.");

        var seenProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in config.Providers)
        {
            var providerId = NormalizeRequired(provider.ProviderId, "providerId");
            if (!seenProviderIds.Add(providerId))
                throw new InvalidOperationException($"Duplicate Microsoft.Extensions.AI provider id '{providerId}' in bridge config.");

            var models = NormalizeModels(provider.Models, providerId);
            var factoryTypeName = NormalizeRequired(provider.FactoryTypeName, "factoryTypeName");
            var factory = CreateFactory(factoryTypeName, provider.FactoryAssemblyPath);
            var client = factory.Create(new MicrosoftExtensionsAiProviderFactoryContext
            {
                PluginId = context.PluginId,
                ProviderId = providerId,
                Models = models,
                Config = provider.Config,
                Logger = context.Logger
            });

            if (client is null)
                throw new InvalidOperationException($"Microsoft.Extensions.AI factory '{factoryTypeName}' returned null for provider '{providerId}'.");

            context.RegisterProvider(providerId, models, client);
        }
    }

    private static MicrosoftExtensionsAiProviderConfig ReadConfig(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException("Microsoft.Extensions.AI provider bridge requires dynamic native plugin config.");

        try
        {
            return config.Value.Deserialize<MicrosoftExtensionsAiProviderConfig>(JsonOptions)
                ?? throw new InvalidOperationException("Microsoft.Extensions.AI provider bridge config could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Microsoft.Extensions.AI provider bridge config is invalid JSON.", ex);
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"Microsoft.Extensions.AI provider bridge config field '{fieldName}' is required.");

        return normalized;
    }

    private static string[] NormalizeModels(IEnumerable<string>? models, string providerId)
    {
        var normalized = models?
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        if (normalized.Length == 0)
            throw new InvalidOperationException($"Microsoft.Extensions.AI provider '{providerId}' must declare at least one model.");

        return normalized;
    }

    private static IMicrosoftExtensionsAiChatClientFactory CreateFactory(string factoryTypeName, string? factoryAssemblyPath)
    {
        var factoryType = ResolveFactoryType(factoryTypeName, factoryAssemblyPath);
        if (!typeof(IMicrosoftExtensionsAiChatClientFactory).IsAssignableFrom(factoryType))
        {
            throw new InvalidOperationException(
                $"Microsoft.Extensions.AI factory type '{factoryType.FullName}' must implement {nameof(IMicrosoftExtensionsAiChatClientFactory)}.");
        }

        try
        {
            return (IMicrosoftExtensionsAiChatClientFactory?)Activator.CreateInstance(factoryType)
                ?? throw new InvalidOperationException($"Microsoft.Extensions.AI factory type '{factoryType.FullName}' could not be created.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Microsoft.Extensions.AI factory type '{factoryType.FullName}' could not be created. It must expose a public parameterless constructor.",
                ex);
        }
    }

    private static Type ResolveFactoryType(string factoryTypeName, string? factoryAssemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(factoryAssemblyPath))
        {
            var assembly = LoadFactoryAssembly(factoryAssemblyPath);
            return assembly.GetType(factoryTypeName, throwOnError: false, ignoreCase: false)
                ?? assembly.GetType(RemoveAssemblyQualification(factoryTypeName), throwOnError: false, ignoreCase: false)
                ?? throw new InvalidOperationException($"Microsoft.Extensions.AI factory type '{factoryTypeName}' could not be found in '{factoryAssemblyPath}'.");
        }

        var resolved = Type.GetType(factoryTypeName, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
            return resolved;

        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(MicrosoftExtensionsAiProviderPlugin).Assembly);
        resolved = loadContext?.Assemblies
            .Select(assembly => assembly.GetType(factoryTypeName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);

        return resolved
            ?? throw new InvalidOperationException(
                $"Microsoft.Extensions.AI factory type '{factoryTypeName}' could not be resolved. Use an assembly-qualified type name or set factoryAssemblyPath.");
    }

    private static Assembly LoadFactoryAssembly(string factoryAssemblyPath)
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(MicrosoftExtensionsAiProviderPlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var pluginRoot = EnsureTrailingSeparator(Path.GetFullPath(pluginDirectory));
        var fullPath = Path.IsPathRooted(factoryAssemblyPath)
            ? Path.GetFullPath(factoryAssemblyPath)
            : Path.GetFullPath(Path.Join(pluginRoot, factoryAssemblyPath));

        if (!Path.IsPathRooted(factoryAssemblyPath) &&
            !fullPath.StartsWith(pluginRoot, GetPathComparison()))
        {
            throw new InvalidOperationException(
                $"Microsoft.Extensions.AI factory assembly '{factoryAssemblyPath}' must stay within the bridge plugin directory.");
        }

        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"Microsoft.Extensions.AI factory assembly '{factoryAssemblyPath}' was not found at '{fullPath}'.");

        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(MicrosoftExtensionsAiProviderPlugin).Assembly)
            ?? AssemblyLoadContext.Default;
        var pathComparison = GetPathComparison();
        var loaded = loadContext.Assemblies.FirstOrDefault(assembly =>
            !string.IsNullOrWhiteSpace(assembly.Location) &&
            string.Equals(Path.GetFullPath(assembly.Location), fullPath, pathComparison));

        return loaded ?? loadContext.LoadFromAssemblyPath(fullPath);
    }

    private static string EnsureTrailingSeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string RemoveAssemblyQualification(string typeName)
    {
        var commaIndex = typeName.IndexOf(',', StringComparison.Ordinal);
        return commaIndex < 0 ? typeName : typeName[..commaIndex].Trim();
    }
}

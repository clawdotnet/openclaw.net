using OpenClaw.Core.Models;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Gateway.Routing;

internal sealed class OpenSquillaBundleLoader : IOpenSquillaBundleLoader
{
    public BundleRoutingConfig Load(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var resolvedBundlePath = Path.GetFullPath(bundlePath);
        var manifestPath = ResolveBundleFilePath(resolvedBundlePath, "manifest.json");
        var manifestAssets = LoadManifestAssetPaths(resolvedBundlePath, manifestPath);
        var classifierPath = FirstNonEmpty(manifestAssets.ClassifierModelPath, ResolveBundleFilePath(resolvedBundlePath, "classifier.onnx"));
        var embeddingPath = FirstNonEmpty(manifestAssets.EmbeddingModelPath, ResolveBundleFilePath(resolvedBundlePath, "embeddings.onnx"));
        var tokenizerPath = FirstNonEmpty(manifestAssets.TokenizerPath, ResolveBundleFilePath(resolvedBundlePath, "tokenizer.json"));
        var runtimeConfigPath = FirstNonEmpty(manifestAssets.RuntimeConfigPath, ResolveBundleFilePath(resolvedBundlePath, "runtime-config.json"));

        return new BundleRoutingConfig
        {
            Assets = new DynamicTurnRoutingAssetsConfig
            {
                ClassifierModelPath = classifierPath,
                EmbeddingModelPath = embeddingPath,
                TokenizerPath = tokenizerPath,
                ManifestPath = manifestPath,
                RuntimeConfigPath = runtimeConfigPath,
                Dimensions = ResolveEmbeddingDimensions(runtimeConfigPath, manifestPath)
            },
            Policy = new DynamicTurnRoutingPolicyConfig()
        };
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string ResolveBundleFilePath(string bundlePath, string fileName)
        => Path.Join(bundlePath, fileName);

    private static DynamicTurnRoutingAssetsConfig LoadManifestAssetPaths(string bundlePath, string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return new DynamicTurnRoutingAssetsConfig();

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            return new DynamicTurnRoutingAssetsConfig
            {
                ClassifierModelPath = ResolveManifestPath(bundlePath, TryFindString(document.RootElement, "classifiermodelpath", "classifierpath", "classifier")),
                EmbeddingModelPath = ResolveManifestPath(bundlePath, TryFindString(document.RootElement, "embeddingmodelpath", "embeddingsmodelpath", "embeddingpath", "embeddingspath", "embeddings")),
                TokenizerPath = ResolveManifestPath(bundlePath, TryFindString(document.RootElement, "tokenizerpath", "tokenizer")),
                RuntimeConfigPath = ResolveManifestPath(bundlePath, TryFindString(document.RootElement, "runtimeconfigpath", "runtimeconfig"))
            };
        }
        catch (Exception ex) when (IsExpectedBundleConfigException(ex))
        {
            throw new InvalidOperationException($"Failed to load OpenSquilla bundle manifest '{manifestPath}'.", ex);
        }
    }

    private static string ResolveManifestPath(string bundlePath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(Path.Join(bundlePath, path));
    }

    private static string? TryFindString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && IsOneOf(property.Name, propertyNames))
                    return property.Value.GetString();

                var nested = TryFindString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => TryFindString(item, propertyNames))
                .FirstOrDefault(static nested => !string.IsNullOrWhiteSpace(nested));
        }

        return null;
    }

    private static bool IsOneOf(string propertyName, IReadOnlyCollection<string> normalizedNames)
        => normalizedNames.Contains(NormalizePropertyName(propertyName));

    private static string NormalizePropertyName(string propertyName)
        => propertyName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static int ResolveEmbeddingDimensions(string runtimeConfigPath, string manifestPath)
    {
        if (TryReadEmbeddingDimensions(runtimeConfigPath, out var runtimeDimensions))
            return runtimeDimensions;

        if (TryReadEmbeddingDimensions(manifestPath, out var manifestDimensions))
            return manifestDimensions;

        return 384;
    }

    private static bool TryReadEmbeddingDimensions(string jsonPath, out int dimensions)
    {
        dimensions = 0;

        if (!File.Exists(jsonPath))
            return false;

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var document = JsonDocument.Parse(stream);
            return TryFindEmbeddingDimensions(document.RootElement, out dimensions);
        }
        catch (Exception ex) when (IsExpectedBundleConfigException(ex))
        {
            throw new InvalidOperationException($"Failed to read routing embedding dimensions from '{jsonPath}'.", ex);
        }
    }

    private static bool TryFindEmbeddingDimensions(JsonElement element, out int dimensions)
    {
        dimensions = 0;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsDimensionPropertyName(property.Name) && TryGetPositiveInt(property.Value, out dimensions))
                    return true;

                if (TryFindEmbeddingDimensions(property.Value, out dimensions))
                    return true;
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in element
                .EnumerateArray()
                .Select(static item => (Found: TryFindEmbeddingDimensions(item, out var itemDimensions), Dimensions: itemDimensions))
                .Where(static candidate => candidate.Found))
            {
                dimensions = candidate.Dimensions;
                return true;
            }
        }

        return false;
    }

    private static bool IsDimensionPropertyName(string propertyName)
    {
        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "dimensions" or "embeddingdimensions" or "embeddingsize";
    }

    private static bool TryGetPositiveInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value > 0;
    }

    private static bool IsExpectedBundleConfigException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException;
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.LayaService.Models;

public sealed record VerifiedCheckpoint(
    string Name,
    string Revision,
    string AbsolutePath,
    IReadOnlyDictionary<string, string> FileHashes);

public sealed record VerifiedManifest(
    string ManifestPath,
    string Revision,
    IReadOnlyDictionary<string, VerifiedCheckpoint> Checkpoints);

public sealed record ModelManifestDocument
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("revision")]
    public required string Revision { get; init; }

    [JsonPropertyName("checkpoints")]
    public required Dictionary<string, CheckpointManifestEntry> Checkpoints { get; init; }
}

public sealed record CheckpointManifestEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required Dictionary<string, string> Sha256 { get; init; }
}

public static class ModelManifest
{
    public const string DefaultRevision = "1c5edc17a7acd8701df6fc341c0d179f1c62c982";
    public static IReadOnlyList<string> ModelFiles { get; } = Array.AsReadOnly(new[]
    {
        "model.safetensors",
        "rl_agent_config.json",
        "encoder/config.json",
        "tokenizer/tokenizer.json",
        "tokenizer/tokenizer_config.json"
    });
    public static IReadOnlySet<string> CheckpointNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "english", "multilingual", "typed-decisions"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DefaultHashes =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["english"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model.safetensors"] = "891102d372688fc2a094dac56a384bc537b87c63f21f9f3dac0be2b7cbc8d86c",
                ["rl_agent_config.json"] = "ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd",
                ["encoder/config.json"] = "bf3ab80598fdccf414855a2ce80f22859e4492d06ca8a62ddd1cfb63972f8979",
                ["tokenizer/tokenizer.json"] = "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30",
                ["tokenizer/tokenizer_config.json"] = "50044de60daaa73df97d262e15a40d4faf0160e7d742df64b377877a1320dd12"
            },
            ["multilingual"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model.safetensors"] = "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204",
                ["rl_agent_config.json"] = "25061739243b617ad88d1219ba6f8a9c86c5881ca28df024fa2d9b3b2fcc30c6",
                ["encoder/config.json"] = "83f6916d13ef0f556ac461f28308dc2bffa7ebeadee8ec9e2db5812020ea5bb4",
                ["tokenizer/tokenizer.json"] = "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f",
                ["tokenizer/tokenizer_config.json"] = "6c6b2d8e3c84ce0e671c129cd6b374b235d6f9863042a5836358d00a89bbb5a1"
            },
            ["typed-decisions"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model.safetensors"] = "4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e",
                ["rl_agent_config.json"] = "ebf0cd524d92342a6be5e48e9fca3d7c2babfb5a56ccd79d2171ef5d8c7f7be8",
                ["encoder/config.json"] = "5268d24ad3b77c8151de5dcb0762ba4391619aad9ab0bda33e36fb083cfeae6d",
                ["tokenizer/tokenizer.json"] = "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30",
                ["tokenizer/tokenizer_config.json"] = "08d4cf3ac4dca381759441b85b91a6d40e688471dcd33d15d6649eb0a9a854d1"
            }
        };

    public static VerifiedManifest LoadAndVerify(string manifestPath, string expectedRevision)
        => LoadAndVerify(manifestPath, expectedRevision, requireExpectedRevision: true);

    public static VerifiedManifest LoadAndVerify(string manifestPath)
        => LoadAndVerify(manifestPath, expectedRevision: null, requireExpectedRevision: false);

    private static VerifiedManifest LoadAndVerify(string manifestPath, string? expectedRevision, bool requireExpectedRevision)
    {
        try
        {
            if (requireExpectedRevision && !IsRevision(expectedRevision)) throw InvalidManifest();
            var fullManifestPath = System.IO.Path.GetFullPath(manifestPath);
            var root = System.IO.Path.GetDirectoryName(fullManifestPath) ?? throw InvalidManifest();
            using var document = JsonDocument.Parse(File.ReadAllBytes(fullManifestPath), new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            var manifest = document.RootElement.Deserialize<ModelManifestDocument>() ?? throw InvalidManifest();
            if (manifest.Version != 1 || !IsRevision(manifest.Revision) ||
                (expectedRevision is not null && !string.Equals(manifest.Revision, expectedRevision, StringComparison.Ordinal)) ||
                manifest.Checkpoints is null || manifest.Checkpoints.Count == 0)
            {
                throw InvalidManifest();
            }

            var verified = new Dictionary<string, VerifiedCheckpoint>(StringComparer.Ordinal);
            foreach (var (name, entry) in manifest.Checkpoints)
            {
                if (!CheckpointNames.Contains(name) || entry?.Path is null || entry.Sha256 is null ||
                    !HasExactFiles(entry.Sha256.Keys))
                {
                    throw InvalidManifest();
                }

                var directory = ResolveContainedPath(root, entry.Path);
                if (!Directory.Exists(directory)) throw InvalidManifest();
                RejectReparsePoints(root, directory);
                foreach (var file in ModelFiles)
                {
                    var expectedHash = entry.Sha256[file];
                    if (!IsHash(expectedHash)) throw InvalidManifest();
                    var filePath = ResolveContainedPath(directory, file);
                    if (!File.Exists(filePath)) throw InvalidManifest();
                    RejectReparsePoints(root, filePath);
                    using var stream = File.OpenRead(filePath);
                    var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                    {
                        throw InvalidManifest();
                    }
                }

                verified.Add(name, new VerifiedCheckpoint(name, manifest.Revision, directory,
                    new Dictionary<string, string>(entry.Sha256, StringComparer.Ordinal)));
            }

            return new VerifiedManifest(fullManifestPath, manifest.Revision, verified);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw InvalidManifest();
        }
    }

    internal static bool IsRevision(string? revision)
        => revision is { Length: 40 } && revision.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string? GetPinnedHash(string revision, string checkpoint, string file)
        => revision == DefaultRevision && DefaultHashes.TryGetValue(checkpoint, out var checkpointHashes) &&
           checkpointHashes.TryGetValue(file, out var hash) ? hash : null;

    internal static bool HasExactFiles(IEnumerable<string> names)
    {
        var actual = names.ToHashSet(StringComparer.Ordinal);
        return actual.Count == ModelFiles.Count && ModelFiles.All(actual.Contains);
    }

    internal static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || System.IO.Path.IsPathRooted(relativePath)) throw InvalidManifest();
        var fullRoot = System.IO.Path.GetFullPath(root);
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relativePath));
        var relative = System.IO.Path.GetRelativePath(fullRoot, fullPath);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + System.IO.Path.AltDirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
        {
            throw InvalidManifest();
        }
        return fullPath;
    }

    internal static bool IsHash(string? hash)
        => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void RejectReparsePoints(string root, string target)
    {
        var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root), System.IO.Path.GetFullPath(target));
        var current = System.IO.Path.GetFullPath(root);
        foreach (var segment in relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw InvalidManifest();
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidManifest();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static InvalidDataException InvalidManifest() => new("Invalid model manifest or checkpoint assets.");
}
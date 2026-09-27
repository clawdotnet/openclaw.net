using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.LayaService.Models;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class ModelManifestTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void LoadAndVerify_AcceptsEveryCheckpointAndReturnsAbsoluteAssets()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = WriteManifest(directory.Path, Revision, ["english", "multilingual", "typed-decisions"]);

        var verified = ModelManifest.LoadAndVerify(manifestPath, Revision);

        Assert.Equal(Revision, verified.Revision);
        Assert.Equal(new[] { "english", "multilingual", "typed-decisions" }, verified.Checkpoints.Keys.Order().ToArray());
        foreach (var checkpoint in verified.Checkpoints.Values)
        {
            Assert.True(Path.IsPathFullyQualified(checkpoint.AbsolutePath));
            Assert.Equal(ModelManifest.ModelFiles.Count, checkpoint.FileHashes.Count);
        }
    }

    [Fact]
    public void LoadAndVerify_RejectsWrongVersionRevisionAndUnknownCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path, Revision, ["english"]);
        var original = File.ReadAllText(path);

        File.WriteAllText(path, original.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, Revision));

        File.WriteAllText(path, original);
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, new string('b', 40)));

        var unknown = JsonDocument.Parse(original).RootElement;
        var badManifest = new
        {
            version = 1,
            revision = Revision,
            checkpoints = new Dictionary<string, object?>
            {
                ["unknown"] = unknown.GetProperty("checkpoints").GetProperty("english").Clone()
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(badManifest));
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, Revision));
    }

    [Fact]
    public void LoadAndVerify_RejectsPathTraversalMissingAndModifiedAssets()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path, Revision, ["english"]);
        var original = File.ReadAllText(path);
        var manifest = JsonDocument.Parse(original).RootElement;
        var checkpoint = manifest.GetProperty("checkpoints").GetProperty("english");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(checkpoint.GetProperty("sha256"))!;

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            revision = Revision,
            checkpoints = new { english = new { path = "../outside", sha256 = hashes } }
        }));
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, Revision));

        File.WriteAllText(path, original);
        File.Delete(Path.Combine(directory.Path, "english", ModelManifest.ModelFiles[0]));
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, Revision));

        WriteManifest(directory.Path, Revision, ["english"]);
        File.AppendAllText(Path.Combine(directory.Path, "english", ModelManifest.ModelFiles[0]), "changed");
        Assert.Throws<InvalidDataException>(() => ModelManifest.LoadAndVerify(path, Revision));
    }

    internal static string WriteManifest(string root, string revision, IReadOnlyList<string> checkpoints)
    {
        var checkpointData = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var checkpoint in checkpoints)
        {
            var checkpointPath = Path.Combine(root, checkpoint);
            Directory.CreateDirectory(checkpointPath);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in ModelManifest.ModelFiles)
            {
                var filePath = Path.Combine(checkpointPath, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var contents = Encoding.UTF8.GetBytes($"fixture:{checkpoint}:{file}");
                File.WriteAllBytes(filePath, contents);
                hashes.Add(file, Convert.ToHexStringLower(SHA256.HashData(contents)));
            }

            checkpointData.Add(checkpoint, new { path = checkpoint, sha256 = hashes });
        }

        var manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { version = 1, revision, checkpoints = checkpointData }));
        return manifestPath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
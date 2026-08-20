using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// Writes <see cref="OntologyAppManifest.FileName"/> atomically. Idempotent on re-runs:
/// same content overwrites. A failure leaves no partial file — a temp file is written, then
/// <see cref="File.Move(string, string, bool)"/> swaps it into place. The directory is
/// created if missing.
/// </summary>
public static class OntologyAppManifestWriter
{
    /// <summary>
    /// Expands a leading <c>~/</c> to the user's home directory and resolves relative paths
    /// against the current working directory. Rooted paths are returned unchanged.
    /// </summary>
    public static string ExpandPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = path.Substring(2).TrimStart('/', '\\');
            return Path.GetFullPath(Path.Combine(home, rest));
        }

        // GetFullPath normalizes separators to the platform default and resolves relative
        // paths against the current working directory, so rooted and relative inputs both
        // come back in a form File.WriteAllText/File.Move accept consistently.
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Writes <paramref name="manifest"/> as indented JSON into
    /// <c>{directory}/openclaw.mcpapp.json</c>, atomically. The manifest must carry an
    /// <c>id</c> (the gateway rejects manifests without one).
    /// </summary>
    public static void Write(string directory, JsonObject manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(manifest);

        var id = manifest["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Manifest must contain a non-empty 'id'.", nameof(manifest));
        }

        Directory.CreateDirectory(directory);

        var targetPath = Path.Combine(directory, OntologyAppManifest.FileName);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}

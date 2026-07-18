using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Publishes a file from the local filesystem (including temp/cache directories)
/// to the media store so the user can download it via /media/{id}.
///
/// If the source file is not within an AllowedWriteRoot (e.g. /tmp), the tool
/// copies it to {WorkspaceRoot}/.downloads/ first so GatewayWorkers can pick
/// it up with TryUploadFilePathAsync, which only reads from AllowedWriteRoots.
///
/// GatewayWorkers scans every ToolCall.Result for [FILE_PATH:] markers after each
/// turn, uploads the bytes to MediaCacheStore, and delivers a file_attachment
/// WebSocket envelope to the client — no extra plumbing needed.
/// </summary>
public sealed class PublishFileTool : ITool
{
    private readonly ToolingConfig _config;

    public PublishFileTool(ToolingConfig config) => _config = config;

    public string Name => "publish_file";

    public string Description =>
        "Publish a file so the user can download it via a download link. " +
        "Pass the absolute path of any file that already exists on the filesystem — " +
        "including files created by 'shell' in /tmp or other temporary directories. " +
        "If the file is outside the workspace the tool will automatically copy it into the workspace downloads folder. " +
        "The system will then register the file and deliver a download link to the user automatically.";

    public string ParameterSchema =>
        """{"type":"object","properties":{"path":{"type":"string","description":"Absolute path of the file to publish for download"}},"required":["path"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
        if (!doc.RootElement.TryGetProperty("path", out var pathEl) ||
            pathEl.ValueKind != System.Text.Json.JsonValueKind.String)
            return "Error: 'path' is required.";

        var path = pathEl.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required.";

        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);

        if (!ToolPathPolicy.IsReadAllowed(_config, resolvedPath))
            return $"Error: Read access denied for path: {path}";

        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        // If the file is already inside an AllowedWriteRoot, GatewayWorkers can
        // pick it up directly — no copy needed.
        var publishPath = ToolPathPolicy.IsWriteAllowed(_config, resolvedPath)
            ? resolvedPath
            : await CopyToDownloadsFolderAsync(resolvedPath, ct);

        if (publishPath is null)
            return $"Error: Could not copy file to a publishable location. Ensure WorkspaceRoot or an AllowedWriteRoot is configured.";

        var fileName = Path.GetFileName(publishPath);
        var sizeBytes = new FileInfo(publishPath).Length;
        var sizeLabel = sizeBytes switch
        {
            < 1024        => $"{sizeBytes} B",
            < 1_048_576   => $"{sizeBytes / 1024.0:F1} KB",
            _             => $"{sizeBytes / 1_048_576.0:F1} MB"
        };

        // The [FILE_PATH:] marker is detected by GatewayWorkers after each turn.
        // It reads the file bytes, saves them to MediaCacheStore (/media/{id}),
        // and sends a file_attachment WebSocket envelope to the client.
        return $"File ready for download: {fileName} ({sizeLabel})\n[FILE_PATH:{publishPath}]";
    }

    // Copies the source file to {WorkspaceRoot}/.downloads/ (falling back to
    // {CurrentDirectory}/.downloads/) and returns the destination path.
    // Returns null if no writable destination can be determined.
    private async Task<string?> CopyToDownloadsFolderAsync(string sourcePath, CancellationToken ct)
    {
        var downloadsDir = ResolveDownloadsDirectory();
        if (downloadsDir is null)
            return null;

        try
        {
            Directory.CreateDirectory(downloadsDir);

            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(downloadsDir, fileName);

            // Avoid overwriting with a suffix if a file with the same name already exists.
            if (File.Exists(destPath) &&
                !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext  = Path.GetExtension(fileName);
                destPath = Path.Combine(downloadsDir, $"{stem}_{Guid.NewGuid().ToString("N")[..8]}{ext}");
            }

            await using var src  = File.OpenRead(sourcePath);
            await using var dest = File.Create(destPath);
            await src.CopyToAsync(dest, ct);

            return destPath;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveDownloadsDirectory()
    {
        // Prefer the configured WorkspaceRoot; fall back to the process working directory.
        var workspaceRaw = SecretResolver.Resolve(_config.WorkspaceRoot);
        var workspaceBase = !string.IsNullOrWhiteSpace(workspaceRaw)
            ? workspaceRaw
            : Directory.GetCurrentDirectory();

        var downloadsDir = Path.Combine(Path.GetFullPath(workspaceBase), ".downloads");

        // Verify the destination would be writable according to policy.
        return ToolPathPolicy.IsWriteAllowed(_config, downloadsDir) ? downloadsDir : null;
    }
}

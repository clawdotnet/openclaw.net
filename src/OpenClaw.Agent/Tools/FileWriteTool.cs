using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Writes content to a file. Creates directories as needed.
/// </summary>
public sealed class FileWriteTool : ITool
{
    private readonly ToolingConfig _config;

    public FileWriteTool(ToolingConfig config) => _config = config;

    public string Name => "write_file";
    public string Description => "Write content to a file on the local filesystem. Creates parent directories if needed.";
    public string ParameterSchema => """{"type":"object","properties":{"path":{"type":"string","description":"File path to write to"},"content":{"type":"string","description":"Content to write"}},"required":["path","content"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var path = args.RootElement.GetProperty("path").GetString()!;
        var content = args.RootElement.GetProperty("content").GetString()!;

        if (!ToolPathPolicy.IsWriteAllowed(_config, path))
            return $"Error: Write access denied for path: {path}";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort cleanup */ }
            throw;
        }

        return $"Written {content.Length} characters to {path}";
    }
}

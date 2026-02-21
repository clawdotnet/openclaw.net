using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Reads file contents. Bounded output to prevent context overflow.
/// </summary>
public sealed class FileReadTool : ITool
{
    private readonly ToolingConfig _config;

    public FileReadTool(ToolingConfig config) => _config = config;

    public string Name => "read_file";
    public string Description => "Read the contents of a file from the local filesystem.";
    public string ParameterSchema => """{"type":"object","properties":{"path":{"type":"string","description":"Absolute or relative file path"},"max_lines":{"type":"integer","default":200}},"required":["path"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var path = args.RootElement.GetProperty("path").GetString()!;
        var maxLines = args.RootElement.TryGetProperty("max_lines", out var ml) ? ml.GetInt32() : 200;
        maxLines = Math.Clamp(maxLines, 1, 5_000);

        if (!ToolPathPolicy.IsReadAllowed(_config, path))
            return $"Error: Read access denied for path: {path}";

        if (!File.Exists(path))
            return $"Error: File not found: {path}";

        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        var sb = new System.Text.StringBuilder(capacity: 4096);
        var read = 0;
        while (read < maxLines)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (read > 0)
                sb.Append('\n');
            sb.Append(line);
            read++;
        }

        if (read >= maxLines)
        {
            var extra = await reader.ReadLineAsync(ct);
            if (extra is not null)
                sb.Append($"\n[truncated: more lines]");
        }

        return sb.ToString();
    }
}

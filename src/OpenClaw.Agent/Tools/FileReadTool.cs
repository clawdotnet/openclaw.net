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
    public string Description => "Read the contents of a file from the local filesystem. For large files, use start_line and max_lines to read in chunks.";
    public string ParameterSchema => """{"type":"object","properties":{"path":{"type":"string","description":"Absolute or relative file path"},"start_line":{"type":"integer","description":"1-based line number to start reading from (default: 1)","default":1},"max_lines":{"type":"integer","description":"Maximum number of lines to read (default: 500, max: 5000)","default":500}},"required":["path"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var path = args.RootElement.GetProperty("path").GetString()!;
        var startLine = args.RootElement.TryGetProperty("start_line", out var sl) ? sl.GetInt32() : 1;
        var maxLines = args.RootElement.TryGetProperty("max_lines", out var ml) ? ml.GetInt32() : 500;
        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 5_000);
        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);

        if (!ToolPathPolicy.IsReadAllowed(_config, resolvedPath))
            return $"Error: Read access denied for path: {path}";

        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        await using var stream = new FileStream(resolvedPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using var reader = new StreamReader(stream);

        var sb = new System.Text.StringBuilder(capacity: 4096);
        var totalLines = 0;
        var read = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            totalLines++;

            if (totalLines < startLine)
                continue;

            if (read >= maxLines)
            {
                // Count remaining lines for the summary without storing them.
                while (await reader.ReadLineAsync(ct) is not null)
                    totalLines++;
                break;
            }

            if (read > 0)
                sb.Append('\n');
            sb.Append(line);
            read++;
        }

        // Append pagination hint when the file has more content.
        if (totalLines > startLine - 1 + read)
        {
            var nextLine = startLine + read;
            sb.Append($"\n[Showing lines {startLine}-{startLine + read - 1} of {totalLines} total. Use start_line={nextLine} to read more.]");
        }
        else if (startLine > 1)
        {
            sb.Append($"\n[End of file. Showed lines {startLine}-{startLine + read - 1} of {totalLines} total.]");
        }

        return sb.ToString();
    }
}

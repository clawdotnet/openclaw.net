using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class MemorySearchTool : ITool
{
    private readonly IMemoryNoteSearch _search;

    public MemorySearchTool(IMemoryNoteSearch search) => _search = search;

    public string Name => "memory_search";

    public string Description =>
        "Search persistent memory notes by keyword (SQLite FTS when enabled). Useful for recalling prior decisions and preferences.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" },
            "prefix": { "type": "string", "description": "Optional key prefix filter (e.g. 'project:myproj:')" },
            "limit": { "type": "integer", "default": 10, "minimum": 1, "maximum": 50 },
            "format": { "type": "string", "enum": ["text","json"], "default": "text" }
          },
          "required": ["query"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var query = root.GetProperty("query").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required.";

        var prefix = root.TryGetProperty("prefix", out var p) ? p.GetString() : null;
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 10;
        limit = Math.Clamp(limit, 1, 50);
        var format = root.TryGetProperty("format", out var f) ? (f.GetString() ?? "text") : "text";

        var hits = await _search.SearchNotesAsync(query, prefix, limit, ct);
        if (hits.Count == 0)
            return "No matching memory notes found.";

        if (format == "json")
            return JsonSerializer.Serialize(hits, OpenClaw.Core.Models.CoreJsonContext.Default.ListMemoryNoteHit);

        var sb = new StringBuilder();
        sb.AppendLine($"Matches: {hits.Count}");
        foreach (var hit in hits)
        {
            sb.AppendLine($"- {hit.Key} (updated {hit.UpdatedAt:O}, score {hit.Score:0.###})");
            sb.AppendLine(Indent(Truncate(hit.Content, 400)));
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string Indent(string s)
        => "  " + s.Replace("\n", "\n  ", StringComparison.Ordinal);
}


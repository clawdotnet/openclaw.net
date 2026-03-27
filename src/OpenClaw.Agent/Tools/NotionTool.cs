using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

public sealed class NotionTool : ITool, IDisposable
{
    private readonly NotionApiClient _client;

    public NotionTool(NotionConfig config, HttpClient? httpClient = null)
    {
        _client = new NotionApiClient(config, httpClient);
    }

    public string Name => "notion";

    public string Description =>
        "Read from a scoped Notion scratchpad or note database. Supports page reads, note lookup, listing, and search.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["read_page","get_note","list_notes","search"]
            },
            "page_id": { "type": "string", "description": "Optional page id. Defaults to DefaultPageId for read_page." },
            "database_id": { "type": "string", "description": "Optional database id. Defaults to DefaultDatabaseId for list_notes/search." },
            "query": { "type": "string", "description": "Search query for search." },
            "limit": { "type": "integer", "description": "Max results to return." }
          },
          "required": ["op"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;
        var op = root.GetProperty("op").GetString() ?? "";
        var limit = root.TryGetProperty("limit", out var limitProp) ? limitProp.GetInt32() : _client.MaxSearchResults;

        try
        {
            return op switch
            {
                "read_page" => await ReadPageAsync(root, ct),
                "get_note" => await GetNoteAsync(root, ct),
                "list_notes" => await ListNotesAsync(root, limit, ct),
                "search" => await SearchAsync(root, limit, ct),
                _ => $"Error: Unknown op '{op}'."
            };
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> ReadPageAsync(JsonElement root, CancellationToken ct)
    {
        var pageId = root.TryGetProperty("page_id", out var pageProp) && pageProp.ValueKind == JsonValueKind.String
            ? pageProp.GetString()
            : null;

        var page = await _client.ReadPageAsync(pageId ?? _client.RequireDefaultPageId(), ct);
        return FormatPage(page);
    }

    private async Task<string> GetNoteAsync(JsonElement root, CancellationToken ct)
    {
        var pageId = RequiredString(root, "page_id");
        var page = await _client.ReadPageAsync(pageId, ct);
        return FormatPage(page);
    }

    private async Task<string> ListNotesAsync(JsonElement root, int limit, CancellationToken ct)
    {
        var databaseId = root.TryGetProperty("database_id", out var dbProp) && dbProp.ValueKind == JsonValueKind.String
            ? dbProp.GetString()
            : null;

        var notes = await _client.ListNotesAsync(_client.RequireAllowedDatabaseId(databaseId), limit, ct);
        if (notes.Count == 0)
            return "No notes found.";

        var sb = new StringBuilder();
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            sb.Append('[').Append(i + 1).Append("] ").Append(note.Title);
            sb.Append("  page_id=").Append(note.PageId);
            if (note.LastEditedAt is not null)
                sb.Append("  last_edited=").Append(note.LastEditedAt.Value.ToString("O"));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> SearchAsync(JsonElement root, int limit, CancellationToken ct)
    {
        var query = RequiredString(root, "query");
        var databaseId = root.TryGetProperty("database_id", out var dbProp) && dbProp.ValueKind == JsonValueKind.String
            ? dbProp.GetString()
            : null;

        var notes = await _client.SearchAsync(query, databaseId, limit, ct);
        if (notes.Count == 0)
            return "No matching Notion pages found.";

        var sb = new StringBuilder();
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            sb.Append('[').Append(i + 1).Append("] ").Append(note.Title);
            sb.Append("  page_id=").Append(note.PageId);
            if (!string.IsNullOrWhiteSpace(note.ParentDatabaseId))
                sb.Append("  database_id=").Append(note.ParentDatabaseId);
            if (note.LastEditedAt is not null)
                sb.Append("  last_edited=").Append(note.LastEditedAt.Value.ToString("O"));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatPage(NotionPageContent page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"title: {page.Page.Title}");
        sb.AppendLine($"page_id: {page.Page.Id}");
        if (!string.IsNullOrWhiteSpace(page.Page.ParentDatabaseId))
            sb.AppendLine($"database_id: {page.Page.ParentDatabaseId}");
        if (page.Page.LastEditedAt is not null)
            sb.AppendLine($"last_edited: {page.Page.LastEditedAt.Value:O}");
        if (!string.IsNullOrWhiteSpace(page.Page.Url))
            sb.AppendLine($"url: {page.Page.Url}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(page.Body) ? "(empty)" : page.Body);
        return sb.ToString().TrimEnd();
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} is required.");

        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.");

        return value;
    }
}

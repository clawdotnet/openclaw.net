using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

public sealed class NotionWriteTool : ITool, IDisposable
{
    private readonly NotionConfig _config;
    private readonly ToolingConfig? _toolingConfig;
    private readonly NotionApiClient _client;

    public NotionWriteTool(NotionConfig config, HttpClient? httpClient = null, ToolingConfig? toolingConfig = null)
    {
        _config = config;
        _toolingConfig = toolingConfig;
        _client = new NotionApiClient(config, httpClient);
    }

    public string Name => "notion_write";

    public string Description =>
        "Write to a scoped Notion scratchpad or note database. Supports append, create, and update operations.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["append_page","create_note","update_note"]
            },
            "page_id": { "type": "string" },
            "database_id": { "type": "string" },
            "title": { "type": "string" },
            "content": { "type": "string" },
            "append": { "type": "boolean", "default": false },
            "tags": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["op"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (_toolingConfig?.ReadOnlyMode == true)
            return "Error: notion_write is disabled because Tooling.ReadOnlyMode is enabled.";

        if (_config.ReadOnly)
            return "Error: notion_write is disabled because Plugins.Native.Notion.ReadOnly is enabled.";

        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;
        var op = root.GetProperty("op").GetString() ?? "";

        try
        {
            return op switch
            {
                "append_page" => await AppendPageAsync(root, ct),
                "create_note" => await CreateNoteAsync(root, ct),
                "update_note" => await UpdateNoteAsync(root, ct),
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

    private async Task<string> AppendPageAsync(JsonElement root, CancellationToken ct)
    {
        var pageId = root.TryGetProperty("page_id", out var pageProp) && pageProp.ValueKind == JsonValueKind.String
            ? pageProp.GetString()
            : null;
        var content = RequiredString(root, "content");

        await _client.AppendPageAsync(pageId ?? _client.RequireDefaultPageId(), content, ct);
        return $"OK: appended content to page {pageId ?? _client.DefaultPageId}.";
    }

    private async Task<string> CreateNoteAsync(JsonElement root, CancellationToken ct)
    {
        var databaseId = root.TryGetProperty("database_id", out var dbProp) && dbProp.ValueKind == JsonValueKind.String
            ? dbProp.GetString()
            : null;
        var title = RequiredString(root, "title");
        var content = RequiredString(root, "content");
        var tags = ReadStringArray(root, "tags");

        var note = await _client.CreateNoteAsync(_client.RequireAllowedDatabaseId(databaseId), title, content, tags, ct);
        return $"OK: created note '{note.Title}' (page_id: {note.PageId}).";
    }

    private async Task<string> UpdateNoteAsync(JsonElement root, CancellationToken ct)
    {
        var pageId = RequiredString(root, "page_id");
        var title = root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
            ? titleProp.GetString()
            : null;
        var content = root.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String
            ? contentProp.GetString()
            : null;
        var append = root.TryGetProperty("append", out var appendProp) &&
                     appendProp.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                     appendProp.GetBoolean();

        var note = await _client.UpdateNoteAsync(pageId, title, content, append, ct);
        return $"OK: updated note '{note.Title}' (page_id: {note.PageId}).";
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

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                items.Add(value);
        }

        return items;
    }
}

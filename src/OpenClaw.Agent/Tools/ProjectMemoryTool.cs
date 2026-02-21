using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Persistent per-project memory that survives across sessions.
/// Allows the agent to save and recall project-level context (architecture decisions,
/// conventions, user preferences, etc.) scoped to a project/workspace identifier.
/// </summary>
public sealed class ProjectMemoryTool : ITool
{
    private readonly IMemoryStore _memory;
    private readonly string _projectId;

    public string Name => "project_memory";

    public string Description =>
        "Save or recall persistent project-level context. Use 'save' to store architecture decisions, " +
        "conventions, and preferences. Use 'load' to recall them. Use 'list' to see all saved keys. " +
        "Use 'delete' to remove a key. This memory persists across conversations.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["save", "load", "list", "delete"],
              "description": "The action to perform"
            },
            "key": {
              "type": "string",
              "description": "The memory key (required for save/load/delete)"
            },
            "content": {
              "type": "string",
              "description": "The content to save (required for save)"
            }
          },
          "required": ["action"]
        }
        """;

    public ProjectMemoryTool(IMemoryStore memory, string projectId)
    {
        _memory = memory;
        _projectId = projectId;
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize(argumentsJson, CoreJsonContext.Default.DictionaryStringObject);
        if (args is null)
            return "Error: Invalid arguments.";

        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        var key = args.TryGetValue("key", out var k) ? k?.ToString() : null;
        var content = args.TryGetValue("content", out var c) ? c?.ToString() : null;

        return action switch
        {
            "save" => await SaveAsync(key, content, ct),
            "load" => await LoadAsync(key, ct),
            "list" => await ListAsync(ct),
            "delete" => await DeleteAsync(key, ct),
            _ => "Error: Unknown action. Use 'save', 'load', 'list', or 'delete'."
        };
    }

    private async ValueTask<string> SaveAsync(string? key, string? content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Error: 'key' is required for save.";
        if (string.IsNullOrWhiteSpace(content))
            return "Error: 'content' is required for save.";

        var fullKey = ProjectKey(key);
        await _memory.SaveNoteAsync(fullKey, content, ct);
        return $"Saved project memory: {key}";
    }

    private async ValueTask<string> LoadAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Error: 'key' is required for load.";

        var fullKey = ProjectKey(key);
        var content = await _memory.LoadNoteAsync(fullKey, ct);
        return content ?? $"No project memory found for key: {key}";
    }

    private async ValueTask<string> ListAsync(CancellationToken ct)
    {
        // List all project-scoped notes by scanning the memory store
        // We'll look for all keys starting with our project prefix
        var prefix = $"project:{_projectId}:";
        var notes = await _memory.ListNotesWithPrefixAsync(prefix, ct);
        if (notes.Count == 0)
            return "No project memory saved yet.";

        var sb = new System.Text.StringBuilder("Project memory keys:\n");
        foreach (var noteKey in notes)
        {
            // Strip prefix to show clean key names
            var cleanKey = noteKey.StartsWith(prefix, StringComparison.Ordinal)
                ? noteKey[prefix.Length..]
                : noteKey;
            sb.AppendLine($"  - {cleanKey}");
        }
        return sb.ToString();
    }

    private async ValueTask<string> DeleteAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Error: 'key' is required for delete.";

        var fullKey = ProjectKey(key);
        await _memory.DeleteNoteAsync(fullKey, ct);
        return $"Deleted project memory: {key}";
    }

    private string ProjectKey(string key) => $"project:{_projectId}:{key}";
}

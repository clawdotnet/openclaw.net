using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Durable memory notes — the agent's "soul" that persists across sessions.
/// Stored as markdown files on disk.
/// </summary>
public sealed class MemoryNoteTool : ITool
{
    private readonly IMemoryStore _store;

    public MemoryNoteTool(IMemoryStore store) => _store = store;

    public string Name => "memory";
    public string Description => "Read or write persistent memory notes. Use to remember user preferences, project context, and important information across sessions.";
    public string ParameterSchema => """{"type":"object","properties":{"action":{"type":"string","enum":["read","write"]},"key":{"type":"string","description":"Note identifier"},"content":{"type":"string","description":"Content to write (only for write action)"}},"required":["action","key"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString()!;
        var key = args.RootElement.GetProperty("key").GetString()!;

        // Validate key to prevent path traversal
        var keyError = InputSanitizer.CheckMemoryKey(key);
        if (keyError is not null)
            return keyError;

        return action switch
        {
            "read" => await _store.LoadNoteAsync(key, ct) ?? $"No note found for key: {key}",
            "write" => await WriteNote(args.RootElement, key, ct),
            _ => $"Unknown action: {action}"
        };
    }

    private async Task<string> WriteNote(System.Text.Json.JsonElement root, string key, CancellationToken ct)
    {
        var content = root.GetProperty("content").GetString()!;
        await _store.SaveNoteAsync(key, content, ct);
        return $"Saved note: {key}";
    }
}

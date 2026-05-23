using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using static OpenClaw.Agent.Tools.FractalMemoryToolHelpers;

namespace OpenClaw.Agent.Tools;

public sealed class FractalMemorySearchTool(IStructuredMemoryProvider provider) : ITool
{
    public string Name => "fractal_memory_search";
    public string Description => "Search optional Fractal Memory structured project memory. Read-only.";
    public string ParameterSchema => """
        {
          "type":"object",
          "properties":{
            "query":{"type":"string","description":"Search query."},
            "limit":{"type":"integer","default":10,"minimum":1,"maximum":50},
            "scope":{"type":"string","description":"Optional Fractal Memory path scope."}
          },
          "required":["query"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = Parse(argumentsJson);
        var root = doc.RootElement;
        var query = GetString(root, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Error("query is required.");
        var result = await provider.SearchAsync(query, GetInt(root, "limit", 10, 1, 50), GetString(root, "scope"), ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemorySearchResult);
    }
}

public sealed class FractalMemoryOpenTool(IStructuredMemoryProvider provider, FractalMemoryConfig config) : ITool
{
    public string Name => "fractal_memory_open";
    public string Description => "Open a Fractal Memory node as structured project memory. Read-only.";
    public string ParameterSchema => """
        {
          "type":"object",
          "properties":{
            "path":{"type":"string","description":"Fractal Memory node path."},
            "depth":{"type":"integer","default":1,"minimum":0,"maximum":3},
            "view":{"type":"string","enum":["index","state","timeline","decisions","children"],"default":"index"}
          },
          "required":["path"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = Parse(argumentsJson);
        var root = doc.RootElement;
        var path = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Error("path is required.");
        var result = await provider.OpenAsync(
            path,
            GetInt(root, "depth", config.DefaultDepth, 0, 3),
            GetString(root, "view") ?? config.DefaultView,
            ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryOpenResult);
    }
}

public sealed class FractalMemoryRecentTool(IStructuredMemoryProvider provider) : ITool
{
    public string Name => "fractal_memory_recent";
    public string Description => "List recently changed Fractal Memory nodes. Read-only.";
    public string ParameterSchema => """
        {
          "type":"object",
          "properties":{
            "days":{"type":"integer","default":30,"minimum":1,"maximum":3650},
            "limit":{"type":"integer","default":10,"minimum":1,"maximum":100},
            "scope":{"type":"string","description":"Optional Fractal Memory path scope."}
          }
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = Parse(argumentsJson);
        var root = doc.RootElement;
        var result = await provider.RecentAsync(
            GetInt(root, "days", 30, 1, 3650),
            GetInt(root, "limit", 10, 1, 100),
            GetString(root, "scope"),
            ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryRecentResult);
    }
}

public sealed class FractalMemoryExportTool(IStructuredMemoryProvider provider, FractalMemoryConfig config) : ITool
{
    public string Name => "fractal_memory_export";
    public string Description => "Export compact Fractal Memory context for a node. Read-only.";
    public string ParameterSchema => """
        {
          "type":"object",
          "properties":{
            "path":{"type":"string","description":"Fractal Memory node path."},
            "mode":{"type":"string","enum":["compact","standard","verbose"],"default":"compact"}
          },
          "required":["path"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = Parse(argumentsJson);
        var root = doc.RootElement;
        var path = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Error("path is required.");
        var result = await provider.ExportAsync(path, GetString(root, "mode") ?? config.DefaultExportMode, ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryExportResult);
    }
}

public sealed class FractalMemoryValidateTool(IStructuredMemoryProvider provider) : ITool
{
    public string Name => "fractal_memory_validate";
    public string Description => "Validate the configured Fractal Memory repository. Read-only.";
    public string ParameterSchema => """{"type":"object","properties":{}}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        _ = argumentsJson;
        var result = await provider.ValidateAsync(ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryValidationResult);
    }
}

public sealed class FractalMemoryHandoffCreateTool(IStructuredMemoryProvider provider, FractalMemoryConfig config) : ITool, IToolActionDescriptorProvider
{
    public string Name => "fractal_memory_handoff_create";
    public string Description => "Create a Fractal Memory handoff packet for a node. Write-capable and disabled unless Fractal writes are explicitly enabled.";
    public string ParameterSchema => """
        {
          "type":"object",
          "properties":{
            "path":{"type":"string","description":"Fractal Memory node path."}
          },
          "required":["path"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = Parse(argumentsJson);
        var path = GetString(doc.RootElement, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Error("path is required.");
        var result = await provider.CreateHandoffAsync(path, ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryHandoffResult);
    }

    public ToolActionDescriptor ResolveActionDescriptor(string argumentsJson)
        => BuildWriteDescriptor(Name, "create_handoff", argumentsJson, config.RequireApprovalForWrites);
}

public sealed class FractalMemoryIndexRefreshTool(IStructuredMemoryProvider provider, FractalMemoryConfig config) : ITool, IToolActionDescriptorProvider
{
    public string Name => "fractal_memory_index_refresh";
    public string Description => "Refresh Fractal Memory indexes. Write/update-capable and disabled unless Fractal writes are explicitly enabled.";
    public string ParameterSchema => """{"type":"object","properties":{}}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        _ = argumentsJson;
        var result = await provider.RefreshIndexAsync(ct);
        return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryValidationResult);
    }

    public ToolActionDescriptor ResolveActionDescriptor(string argumentsJson)
        => BuildWriteDescriptor(Name, "refresh_index", argumentsJson, config.RequireApprovalForWrites);
}

internal static class FractalMemoryToolHelpers
{
    public static JsonDocument Parse(string argumentsJson)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

    public static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int GetInt(JsonElement root, string propertyName, int fallback, int min, int max)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(fallback, min, max);

    public static string Error(string message)
        => JsonSerializer.Serialize(
            new MutationResponse { Success = false, Error = message },
            CoreJsonContext.Default.MutationResponse);

    public static ToolActionDescriptor BuildWriteDescriptor(
        string toolName,
        string action,
        string argumentsJson,
        bool requireApproval)
    {
        var path = "";
        try
        {
            using var doc = Parse(argumentsJson);
            path = GetString(doc.RootElement, "path") ?? "";
        }
        catch (JsonException)
        {
            // Malformed arguments fall back to an unscoped write descriptor.
            path = "";
        }

        return new ToolActionDescriptor
        {
            Action = action,
            IsMutation = true,
            RequiresApproval = requireApproval,
            ReadOnly = false,
            RiskLevel = "medium",
            Summary = string.IsNullOrWhiteSpace(path)
                ? $"{toolName} updates Fractal Memory state."
                : $"{toolName} updates Fractal Memory state for '{path}'.",
            ApprovalFingerprint = BuildFingerprint(toolName, action, path)
        };
    }

    private static string BuildFingerprint(string toolName, string action, string path)
    {
        var payload = $"{toolName}|{action}|{path}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}

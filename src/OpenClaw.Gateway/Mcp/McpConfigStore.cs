using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway.Mcp;

internal sealed class McpConfigStore
{
    private readonly string _path;
    private readonly ILogger<McpConfigStore> _logger;

    public McpConfigStore(string storagePath, ILogger<McpConfigStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, "mcp", "mcp.json");
        _logger = logger;
    }

    public async Task<string?> TryLoadRawAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return null;

        return await File.ReadAllTextAsync(_path, ct);
    }

    public async Task<Dictionary<string, McpServerConfig>?> TryLoadServersAsync(CancellationToken ct = default)
    {
        var raw = await TryLoadRawAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return TryParseServers(raw, _path, _logger);
    }

    internal static Dictionary<string, McpServerConfig>? TryParseServers(string raw, string path, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (TryReadBool(root, "enabled", out var enabled) && !enabled)
                return [];

            if (!root.TryGetProperty("servers", out var serversElement) ||
                serversElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
            }

            if (serversElement.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("Workspace MCP config at {Path} contains a non-object 'servers' field.", path);
                return new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
            }

            return ParseServers(serversElement);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse workspace MCP config from {Path}", path);
            return null;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Workspace MCP config payload cannot be empty.", nameof(json));

        using var _ = JsonDocument.Parse(json);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<string, McpServerConfig> ParseServers(JsonElement serversElement)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var property in serversElement.EnumerateObject().Where(static property => property.Value.ValueKind == JsonValueKind.Object))
        {
            servers[property.Name] = ParseServerConfig(property.Value);
        }

        return servers;
    }

    private static McpServerConfig ParseServerConfig(JsonElement element)
    {
        var config = new McpServerConfig();
        if (TryReadBool(element, "enabled", out var enabled))
            config.Enabled = enabled;
        if (TryReadString(element, "name", out var name))
            config.Name = name;
        if (TryReadString(element, "transport", out var transport))
            config.Transport = transport;
        if (TryReadString(element, "command", out var command))
            config.Command = command;
        if (TryReadString(element, "workingDirectory", out var workingDirectory))
            config.WorkingDirectory = workingDirectory;
        if (TryReadString(element, "url", out var url))
            config.Url = url;
        if (TryReadString(element, "toolNamePrefix", out var prefix))
            config.ToolNamePrefix = prefix;
        if (TryReadInt32(element, "startupTimeoutSeconds", out var startupTimeout))
            config.StartupTimeoutSeconds = startupTimeout;
        if (TryReadInt32(element, "requestTimeoutSeconds", out var requestTimeout))
            config.RequestTimeoutSeconds = requestTimeout;
        if (element.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Array)
            config.Arguments = argumentsElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        if (element.TryGetProperty("environment", out var environmentElement) && environmentElement.ValueKind == JsonValueKind.Object)
            config.Environment = ParseDictionary(environmentElement);
        if (element.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
            config.Headers = ParseDictionary(headersElement);

        return config;
    }

    private static Dictionary<string, string> ParseDictionary(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            result[property.Name] = property.Value.GetString() ?? string.Empty;

        return result;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return true;
    }

    private static bool TryReadInt32(JsonElement element, string propertyName, out int value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property) ||
            (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}

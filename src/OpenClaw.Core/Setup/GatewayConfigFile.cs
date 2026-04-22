using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public static class GatewayConfigFile
{
    public static GatewayConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        if (root.TryGetProperty("OpenClaw", out var openClaw))
        {
            return JsonSerializer.Deserialize(openClaw.GetRawText(), CoreJsonContext.Default.GatewayConfig)
                ?? throw new InvalidOperationException($"Could not deserialize OpenClaw config from {configPath}.");
        }

        return JsonSerializer.Deserialize(root.GetRawText(), CoreJsonContext.Default.GatewayConfig)
            ?? throw new InvalidOperationException($"Could not deserialize gateway config from {configPath}.");
    }

    public static async Task SaveAsync(GatewayConfig config, string configPath)
    {
        var openClawNode = JsonNode.Parse(JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig))
            ?? throw new InvalidOperationException("Failed to serialize gateway config.");
        var root = new JsonObject
        {
            ["OpenClaw"] = openClawNode
        };

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);
    }
}

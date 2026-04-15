using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class GatewayConfigFile
{
    internal const string DefaultConfigPath = "~/.openclaw/config/openclaw.settings.json";

    public static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || string.Equals(path, "~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    public static string QuoteIfNeeded(string path)
        => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

public sealed class ConfigurationRequest
{
    public string? Instruction { get; set; }
    public string? Revision { get; set; }
    public Dictionary<string, JsonElement> Changes { get; set; } = [];
}

public sealed class ConfigurationState
{
    public bool Success { get; set; }
    public string Revision { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = [];
    public Dictionary<string, JsonElement> Changes { get; set; } = [];
    public string[] Errors { get; set; } = [];
    public bool RestartRequired { get; set; }
    public string[] RestartRequiredFields { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigurationRequest))]
[JsonSerializable(typeof(ConfigurationState))]
public partial class ConfigurationJsonContext : JsonSerializerContext;

public static class ConfigurationSettingChoices
{
    public static IReadOnlyList<string> For(string key) => key switch
    {
        "usageFooter" => ["off", "tokens", "full"],
        "autonomyMode" => ["readonly", "supervised", "full"],
        "allowlistSemantics" => ["legacy", "strict"],
        "whatsappType" => ["official", "bridge", "first_party_worker"],
        _ when key.EndsWith("DmPolicy", StringComparison.Ordinal) => ["open", "pairing", "closed"],
        _ => []
    };
}

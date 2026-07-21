using System.Text.Json.Serialization;
using OpenClaw.Companion.Models;

namespace OpenClaw.Companion;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CompanionSettings))]
internal partial class CompanionJsonContext : JsonSerializerContext
{
}

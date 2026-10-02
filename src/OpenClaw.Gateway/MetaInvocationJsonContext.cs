using System.Text.Json.Serialization;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetaInvocationRecord))]
[JsonSerializable(typeof(List<MetaInvocationRecord>))]
[JsonSerializable(typeof(MetaSkillInvocationRequest))]
[JsonSerializable(typeof(MetaInvocationResponse))]
internal sealed partial class MetaInvocationJsonContext : JsonSerializerContext;
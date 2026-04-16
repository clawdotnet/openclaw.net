using System.Text.Json.Serialization;

namespace OpenClaw.Gateway;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OperatorAccountService.StoreState), TypeInfoPropertyName = "OperatorAccountStoreState")]
internal partial class GatewayJsonContext : JsonSerializerContext;

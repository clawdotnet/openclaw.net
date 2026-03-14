using System.Text.Json.Serialization;

namespace OpenClawNet.Sandbox.OpenSandbox;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenSandboxCreateRequest))]
[JsonSerializable(typeof(OpenSandboxCreateResponse))]
[JsonSerializable(typeof(OpenSandboxExecRequest))]
[JsonSerializable(typeof(OpenSandboxExecResponse))]
[JsonSerializable(typeof(OpenSandboxRenewRequest))]
[JsonSerializable(typeof(OpenSandboxRenewResponse))]
internal sealed partial class OpenSandboxJsonContext : JsonSerializerContext
{
}


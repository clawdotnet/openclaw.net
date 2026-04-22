using System.Text.Json.Serialization;

namespace OpenClaw.Gateway.Bootstrap;

[JsonSerializable(typeof(LocalStartupState))]
internal sealed partial class StartupJsonContext : JsonSerializerContext
{
}

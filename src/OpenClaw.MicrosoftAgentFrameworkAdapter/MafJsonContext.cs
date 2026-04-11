using A2A;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

internal sealed class MafSessionEnvelope
{
    public int SchemaVersion { get; init; } = 2;
    public required string SessionId { get; init; }
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string MafPackageVersion { get; init; }
    public required string HistoryHash { get; init; }
    public required JsonElement State { get; init; }
}

[JsonSerializable(typeof(MafSessionEnvelope))]
[JsonSerializable(typeof(AgentCard))]
internal sealed partial class MafJsonContext : JsonSerializerContext
{
}

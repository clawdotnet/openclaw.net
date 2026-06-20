using System.Text.Json.Serialization;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Strongly-typed loop dispatch payload for future persisted scheduler storage.
/// Uses source-generated JSON to stay AOT-safe.
/// </summary>
public sealed record AgentLoopRequestPayload(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("prompt")] string Prompt
);

#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpenClaw.Gateway.A2A;

internal sealed class OpenClawA2ARegistrationAgent : AIAgent
{
    public override string Name => OpenClawA2ANames.AgentName;

    public override string Description => "OpenClaw A2A server registration placeholder.";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenClaw A2A execution is handled by OpenClawA2AAgentHandler.");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenClaw A2A execution is handled by OpenClawA2AAgentHandler.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenClaw A2A execution is handled by OpenClawA2AAgentHandler.");

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenClaw A2A execution is handled by OpenClawA2AAgentHandler.");

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
}
#endif

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

public sealed class OpenClawA2AAgent : AIAgent
{
    public const string HostedAgentName = "openclaw";

    private readonly MafOptions _options;
    private readonly IOpenClawA2AExecutionBridge _bridge;
    private readonly ILogger<OpenClawA2AAgent> _logger;

    public OpenClawA2AAgent(
        IOptions<MafOptions> options,
        IOpenClawA2AExecutionBridge bridge,
        ILogger<OpenClawA2AAgent> logger)
    {
        _options = options.Value;
        _bridge = bridge;
        _logger = logger;
    }

    public override string Name => HostedAgentName;

    public override string Description => _options.AgentDescription;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AgentSession>(OpenClawA2AAgentSession.CreateNew());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (session is not OpenClawA2AAgentSession openClawSession)
            throw new InvalidOperationException($"Unsupported session type '{session.GetType().FullName}'.");

        return ValueTask.FromResult(JsonSerializer.SerializeToElement(openClawSession, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenClawA2AAgentSession? session;
        try
        {
            session = serializedState.Deserialize<OpenClawA2AAgentSession>(jsonSerializerOptions);
        }
        catch (JsonException)
        {
            session = null;
        }

        if (session is null
            || string.IsNullOrWhiteSpace(session.SessionId)
            || string.IsNullOrWhiteSpace(session.SenderId))
        {
            session = OpenClawA2AAgentSession.CreateNew();
        }

        return ValueTask.FromResult<AgentSession>(session);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .ToAgentResponseAsync(cancellationToken)
            .ConfigureAwait(false);

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var validSession = session as OpenClawA2AAgentSession;
        if (validSession is null
            || string.IsNullOrWhiteSpace(validSession.SessionId)
            || string.IsNullOrWhiteSpace(validSession.SenderId))
        {
            validSession = OpenClawA2AAgentSession.CreateNew();
        }

        var messageList = messages.ToList();
        var userText = ExtractUserText(messageList);
        var messageId = ExtractMessageId(messageList);
        var responseId = Guid.NewGuid().ToString("N");
        var pendingUpdates = new List<AgentResponseUpdate>();
        string? errorMessage = null;

        try
        {
            await _bridge.ExecuteStreamingAsync(
                new OpenClawA2AExecutionRequest
                {
                    SessionId = validSession.SessionId,
                    ChannelId = "a2a",
                    SenderId = validSession.SenderId,
                    UserText = userText,
                    MessageId = messageId
                },
                (evt, ct) =>
                {
                    switch (evt.Type)
                    {
                        case AgentStreamEventType.TextDelta when !string.IsNullOrEmpty(evt.Content):
                            pendingUpdates.Add(CreateUpdate(evt.Content, responseId, messageId));
                            break;
                        case AgentStreamEventType.Error when !string.IsNullOrWhiteSpace(evt.Content):
                            errorMessage = evt.Content;
                            break;
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A execution failed for session {SessionId}", validSession.SessionId);
            pendingUpdates.Add(CreateUpdate("A2A request failed.", responseId, messageId));
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _logger.LogWarning("A2A execution failed for session {SessionId}: {Message}", validSession.SessionId, errorMessage);
            pendingUpdates.Add(CreateUpdate(errorMessage, responseId, messageId));
        }

        if (pendingUpdates.Count == 0)
            pendingUpdates.Add(CreateUpdate($"[{Name}] Request completed.", responseId, messageId));

        foreach (var update in pendingUpdates)
            yield return update;
    }

    private AgentResponseUpdate CreateUpdate(string content, string responseId, string? messageId)
        => new(ChatRole.Assistant, content)
        {
            AgentId = Name,
            ResponseId = responseId,
            MessageId = messageId
        };

    private static string ExtractUserText(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text;
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    private static string? ExtractMessageId(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault(static message =>
            message.Role == ChatRole.User
            && !string.IsNullOrWhiteSpace(message.MessageId))?.MessageId;

    private sealed class OpenClawA2AAgentSession : AgentSession
    {
        public required string SessionId { get; init; }

        public required string SenderId { get; init; }

        public static OpenClawA2AAgentSession CreateNew()
        {
            var sessionId = Guid.NewGuid().ToString("N");
            return new OpenClawA2AAgentSession
            {
                SessionId = sessionId,
                SenderId = sessionId
            };
        }
    }
}
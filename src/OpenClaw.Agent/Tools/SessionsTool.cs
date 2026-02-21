using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Allows the agent to discover currently active memory sessions (agents/users),
/// read their recent history, and dispatch messages to them.
/// Acts as an Agent-to-Agent/Channel bridge.
/// </summary>
public sealed class SessionsTool : ITool
{
    private readonly SessionManager _sessionManager;
    private readonly ChannelWriter<InboundMessage> _pipelineChannel;

    public string Name => "sessions";
    public string Description => "Lists active OpenClaw sessions, reads their history, or sends a cross-session message to another sub-agent or user channel.";

    public string ParameterSchema => """
    {
      "type": "object",
      "properties": {
        "action": {
          "type": "string",
          "enum": ["list", "history", "send"],
          "description": "The action to perform: list active sessions, get history of a session, or send a message."
        },
        "sessionId": { "type": "string", "description": "Required for history or send." },
        "message": { "type": "string", "description": "Required for send." },
        "limit": { "type": "integer", "description": "Max history lines to return (default: 50)." }
      },
      "required": ["action"]
    }
    """;

    public SessionsTool(SessionManager sessionManager, ChannelWriter<InboundMessage> pipelineChannel)
    {
        _sessionManager = sessionManager;
        _pipelineChannel = pipelineChannel;
    }

    public Type ParametersType => typeof(SessionsArgs);

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize(argumentsJson, SessionsSerializerContext.Default.SessionsArgs);
        if (args is null) return "Error: Invalid arguments.";

        switch (args.Action)
        {
            case "list":
                return await HandleListAsync(ct);
            case "history":
                if (string.IsNullOrWhiteSpace(args.SessionId)) return "Error: session_id is required for history action.";
                return await HandleHistoryAsync(args.SessionId, args.Limit ?? 10, ct);
            case "send":
                if (string.IsNullOrWhiteSpace(args.SessionId)) return "Error: target session_id is required for send action.";
                if (string.IsNullOrWhiteSpace(args.Message)) return "Error: message content is required for send action.";
                return await HandleSendAsync(args.SessionId, args.Message, ct);
            default:
                return "Error: Unknown action. Valid actions are 'list', 'history', 'send'.";
        }
    }

    private async Task<string> HandleListAsync(CancellationToken ct)
    {
        var active = await _sessionManager.ListActiveAsync(ct);
        if (active.Count == 0) return "No active sessions found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Total Active Sessions: {active.Count}");
        foreach (var session in active)
        {
            sb.AppendLine($"- ID: {session.Id}, Channel: {session.ChannelId}, Sender: {session.SenderId}, State: {session.State}");
        }
        return sb.ToString();
    }

    private async Task<string> HandleHistoryAsync(string sessionId, int limit, CancellationToken ct)
    {
        var session = await _sessionManager.LoadAsync(sessionId, ct);
        if (session is null) return $"Error: Session '{sessionId}' not found.";

        if (session.History.Count == 0) return "Session history is currently empty.";

        var recent = session.History.TakeLast(limit).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Last {recent.Count} turns for session {sessionId}:");
        foreach (var turn in recent)
        {
            if (turn.Content == "[tool_use]") continue;
            sb.AppendLine($"[{turn.Timestamp:T}] {turn.Role}: {turn.Content}");
        }
        return sb.ToString();
    }

    private async Task<string> HandleSendAsync(string targetSessionId, string message, CancellationToken ct)
    {
        var targetContext = await _sessionManager.LoadAsync(targetSessionId, ct);
        if (targetContext is null) return $"Error: Target session '{targetSessionId}' does not exist.";

        var msg = new InboundMessage
        {
            SessionId = targetContext.Id,
            ChannelId = targetContext.ChannelId,
            SenderId = targetContext.SenderId,
            Text = message
        };

        await _pipelineChannel.WriteAsync(msg, ct);
        return $"Message queued for delivery to session {targetSessionId}.";
    }
}

public sealed class SessionsArgs
{
    [Description("Action to perform: 'list' (list active sessions), 'history' (read session context), 'send' (send cross-session message)")]
    public required string Action { get; set; }

    [Description("Target Session ID. Required for 'history' and 'send' actions.")]
    public string? SessionId { get; set; }

    [Description("Message text to send. Required for 'send' action.")]
    public string? Message { get; set; }

    [Description("Number of recent chat turns to read. Defaults to 10 for 'history' action.")]
    public int? Limit { get; set; }
}

[JsonSerializable(typeof(SessionsArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SessionsSerializerContext : JsonSerializerContext;

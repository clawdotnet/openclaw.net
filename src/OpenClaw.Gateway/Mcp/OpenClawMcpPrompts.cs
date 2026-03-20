using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// MCP prompt implementations. Prompts are purely template-based and do not
/// perform any I/O — they produce pre-composed message sequences that guide
/// an AI model to use available MCP resources and tools effectively.
/// </summary>
[McpServerPromptType]
internal sealed class OpenClawMcpPrompts
{
    [McpServerPrompt(Name = "openclaw_operator_summary"),
     Description("Guide a model to summarize gateway health for an operator.")]
    public GetPromptResult OperatorSummary(
        [Description("Optional area to emphasize, such as providers, approvals, or plugins.")]
        string? focus = null)
    {
        var subject = string.IsNullOrWhiteSpace(focus) ? "overall gateway health" : focus.Trim();

        return new GetPromptResult
        {
            Description = "Summarize the OpenClaw gateway state for an operator.",
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = $"Summarize {subject}. Start with openclaw://dashboard, then inspect " +
                               "openclaw://status, openclaw://approvals, openclaw://providers, " +
                               "openclaw://plugins, and openclaw://operator-audit as needed. " +
                               "Use the runtime event tools if you need more detail on recent anomalies."
                    }
                }
            ]
        };
    }

    [McpServerPrompt(Name = "openclaw_session_summary"),
     Description("Guide a model to summarize a specific session using MCP resources.")]
    public GetPromptResult SessionSummary(
        [Description("The session ID to summarize.")] string sessionId)
    {
        var escaped = Uri.EscapeDataString(sessionId);

        return new GetPromptResult
        {
            Description = "Summarize an OpenClaw session using typed MCP resources.",
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = $"Summarize session '{sessionId}'. " +
                               $"Read openclaw://sessions/{escaped} and " +
                               $"openclaw://sessions/{escaped}/timeline, " +
                               "then explain the current state, recent runtime events, " +
                               "and any notable provider activity."
                    }
                }
            ]
        };
    }
}

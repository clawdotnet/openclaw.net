using Microsoft.Extensions.AI;

namespace OpenClaw.Agent;

internal sealed class AgentStreamCollectResult
{
    public List<string> TextDeltas { get; } = [];
    public string FullText => string.Concat(TextDeltas);
    public List<FunctionCallContent> ToolCalls { get; } = [];
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public bool IsUsageEstimated { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string? Error { get; set; }
}

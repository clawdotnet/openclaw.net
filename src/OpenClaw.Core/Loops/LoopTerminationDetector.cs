using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Detects loop termination signals through two paths:
/// 1. (Primary) Explicit tool call from the model via ILoopControlService.SignalCompleteAsync
/// 2. (Fallback) Keyword matching in model response text
/// </summary>
public sealed class LoopTerminationDetector
{
    private readonly ILoopControlService _loopControl;
    private readonly ILogger<LoopTerminationDetector> _logger;

    private static readonly FrozenSet<string> TerminationKeywords = new[]
    {
        "LOOP_TERMINATE",
        "DONE",
        "WORK_COMPLETE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public LoopTerminationDetector(ILoopControlService loopControl, ILogger<LoopTerminationDetector> logger)
    {
        _loopControl = loopControl ?? throw new ArgumentNullException(nameof(loopControl));
        _logger = logger;
    }

    /// <summary>
    /// Scans a chunk of response text for termination keywords.
    /// Returns true if termination was triggered.
    /// </summary>
    public async Task<bool> ScanTextAsync(string sessionId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in TerminationKeywords)
        {
            if (!ContainsWholeKeyword(text, keyword))
                continue;

            _logger.LogInformation(
                "Loop termination keyword '{Keyword}' detected in response for session {SessionId}",
                keyword, sessionId);
            await _loopControl.SignalCompleteAsync(sessionId, ct);
            return true;
        }

        return false;
    }

    private static bool ContainsWholeKeyword(string text, string keyword)
    {
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(keyword, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var before = index == 0 || !IsKeywordCharacter(text[index - 1]);
            var afterIndex = index + keyword.Length;
            var after = afterIndex == text.Length || !IsKeywordCharacter(text[afterIndex]);
            if (before && after)
                return true;

            startIndex = index + 1;
        }

        return false;
    }

    private static bool IsKeywordCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>
    /// Called when the LoopControlTool fires (primary termination path).
    /// </summary>
    public async Task OnToolCompleteAsync(string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("LoopControlTool signaled completion for session {SessionId}", sessionId);
        await _loopControl.SignalCompleteAsync(sessionId, ct);
    }
}

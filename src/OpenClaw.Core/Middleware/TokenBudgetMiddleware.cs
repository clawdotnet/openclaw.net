using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Middleware;

/// <summary>
/// Middleware that enforces a per-session token budget. When the budget is exceeded,
/// the message is short-circuited with a user-friendly response.
/// Token counts are tracked on the <see cref="MessageContext"/> and updated externally
/// after each agent turn completes.
/// Optionally also enforces a USD cost budget via a pluggable callback.
/// </summary>
public sealed class TokenBudgetMiddleware : IMessageMiddleware
{
    private readonly long _maxTokensPerSession;
    private readonly Func<string?, string, string, (decimal MaxCost, decimal CurrentCost, bool Exceeded)>? _costChecker;
    private readonly ILogger? _logger;

    public string Name => "TokenBudget";

    /// <param name="maxTokensPerSession">Max total tokens (input + output) per session. 0 = unlimited.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="costChecker">
    /// Optional callback that checks USD cost budget for a (channelId, senderId) pair.
    /// Returns (maxCost, currentCost, exceeded). Only called when non-null.
    /// </param>
    public TokenBudgetMiddleware(
        long maxTokensPerSession,
        ILogger? logger = null,
        Func<string?, string, string, (decimal MaxCost, decimal CurrentCost, bool Exceeded)>? costChecker = null)
    {
        _maxTokensPerSession = maxTokensPerSession;
        _costChecker = costChecker;
        _logger = logger;
    }

    public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
    {
        // Token budget check
        if (_maxTokensPerSession > 0)
        {
            var total = context.SessionInputTokens + context.SessionOutputTokens;
            if (total >= _maxTokensPerSession)
            {
                _logger?.LogWarning("Token budget exceeded for {Channel}:{Sender} ({Total}/{Max})",
                    context.ChannelId, context.SenderId, total, _maxTokensPerSession);
                context.ShortCircuit(
                    $"This session has reached its token budget ({total:N0}/{_maxTokensPerSession:N0} tokens). " +
                    "Please start a new conversation.");
                return ValueTask.CompletedTask;
            }
        }

        // USD cost budget check (contract governance)
        if (_costChecker is not null)
        {
            var (maxCost, currentCost, exceeded) = _costChecker(context.SessionId, context.ChannelId, context.SenderId);
            if (exceeded)
            {
                _logger?.LogWarning("Cost budget exceeded for {Channel}:{Sender} ({Current:C}/{Max:C})",
                    context.ChannelId, context.SenderId, currentCost, maxCost);
                context.ShortCircuit(
                    $"This session has reached its cost budget (${currentCost:F2}/${maxCost:F2} USD). " +
                    "Please start a new conversation or adjust the contract budget.");
                return ValueTask.CompletedTask;
            }
        }

        return next();
    }
}

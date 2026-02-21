using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Middleware;

/// <summary>
/// Middleware that enforces a per-session token budget. When the budget is exceeded,
/// the message is short-circuited with a user-friendly response.
/// Token counts are tracked on the <see cref="MessageContext"/> and updated externally
/// after each agent turn completes.
/// </summary>
public sealed class TokenBudgetMiddleware : IMessageMiddleware
{
    private readonly long _maxTokensPerSession;
    private readonly ILogger? _logger;

    public string Name => "TokenBudget";

    /// <param name="maxTokensPerSession">Max total tokens (input + output) per session. 0 = unlimited.</param>
    public TokenBudgetMiddleware(long maxTokensPerSession, ILogger? logger = null)
    {
        _maxTokensPerSession = maxTokensPerSession;
        _logger = logger;
    }

    public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
    {
        if (_maxTokensPerSession <= 0)
            return next();

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

        return next();
    }
}

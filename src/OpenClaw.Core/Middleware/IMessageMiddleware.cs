namespace OpenClaw.Core.Middleware;

/// <summary>
/// Context passed through the middleware pipeline for each inbound message.
/// Middleware can inspect/modify the message, session, or short-circuit the pipeline.
/// </summary>
public sealed class MessageContext
{
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string Text { get; set; }
    public string? MessageId { get; init; }

    /// <summary>Session-level token counters (input + output accumulated across turns).</summary>
    public long SessionInputTokens { get; set; }
    public long SessionOutputTokens { get; set; }

    /// <summary>Arbitrary properties for middleware to pass data down the chain.</summary>
    private Dictionary<string, object>? _properties;

    /// <summary>
    /// Arbitrary properties for middleware to pass data down the chain.
    /// Lazily allocated to keep the hot path allocation-light when unused.
    /// </summary>
    public Dictionary<string, object> Properties => _properties ??= new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>When set to true by a middleware, the message is dropped and the response text is returned directly.</summary>
    public bool IsShortCircuited { get; private set; }
    public string? ShortCircuitResponse { get; private set; }

    /// <summary>Short-circuit the pipeline and return the given response text directly.</summary>
    public void ShortCircuit(string responseText)
    {
        IsShortCircuited = true;
        ShortCircuitResponse = responseText;
    }
}

/// <summary>
/// A composable middleware component that can inspect, transform, or short-circuit messages
/// before they reach the agent runtime.
/// </summary>
public interface IMessageMiddleware
{
    /// <summary>Display name for logging/diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Process the message context. Call <paramref name="next"/> to continue the pipeline,
    /// or call <see cref="MessageContext.ShortCircuit"/> to bypass remaining middleware.
    /// </summary>
    ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct);
}

/// <summary>
/// Builds and executes a middleware pipeline from an ordered list of <see cref="IMessageMiddleware"/>.
/// </summary>
public sealed class MiddlewarePipeline
{
    private readonly IReadOnlyList<IMessageMiddleware> _middleware;

    public MiddlewarePipeline(IReadOnlyList<IMessageMiddleware> middleware)
    {
        _middleware = middleware;
    }

    /// <summary>
    /// Execute the middleware pipeline. Returns true if the message should proceed to the agent,
    /// false if a middleware short-circuited the request.
    /// </summary>
    public async ValueTask<bool> ExecuteAsync(MessageContext context, CancellationToken ct)
    {
        if (_middleware.Count == 0)
            return true;

        var index = 0;

        ValueTask Next()
        {
            if (context.IsShortCircuited)
                return ValueTask.CompletedTask;

            if (index < _middleware.Count)
            {
                var mw = _middleware[index++];
                return mw.InvokeAsync(context, Next, ct);
            }

            return ValueTask.CompletedTask;
        }

        await Next();
        return !context.IsShortCircuited;
    }
}

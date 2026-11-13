namespace OpenClaw.Core.Abstractions;

/// <summary>A failed send with enough information to avoid duplicating partial or uncertain delivery.</summary>
public sealed class ChannelDeliveryException(
    string message,
    bool canRetry,
    int deliveredParts = 0,
    bool outcomeUnknown = false,
    Exception? innerException = null,
    TimeSpan? retryAfter = null) : Exception(message, innerException)
{
    public bool CanRetry { get; } = canRetry && deliveredParts == 0 && !outcomeUnknown;
    public int DeliveredParts { get; } = deliveredParts;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

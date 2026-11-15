namespace OpenClaw.Core.Abstractions;

/// <summary>An intentional policy decision not to send. This is neither delivery nor a retryable failure.</summary>
public sealed class ChannelDeliverySuppressedException(string reason) : Exception(reason);

/// <summary>Tracks confirmed transport acceptance across a multipart send without changing adapter signatures.</summary>
public sealed class ChannelDeliveryAttempt
{
    public int AcceptedParts { get; private set; }

    public void RecordAccepted() => AcceptedParts++;

    public async ValueTask ExecuteAsync(Func<Task> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await send();
            if (AcceptedParts == 0)
                throw new ChannelDeliveryException("No accepted send was recorded.", false, outcomeUnknown: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelDeliverySuppressedException) when (AcceptedParts == 0)
        {
            throw;
        }
        catch (ChannelDeliveryException ex)
        {
            throw new ChannelDeliveryException(ex.Message, ex.CanRetry,
                AcceptedParts + ex.DeliveredParts, ex.OutcomeUnknown, ex, ex.RetryAfter);
        }
        catch (Exception ex)
        {
            // An unclassified failure may have occurred after the remote side accepted the request.
            throw new ChannelDeliveryException("Send outcome is unknown.", false,
                AcceptedParts, outcomeUnknown: true, innerException: ex);
        }
    }
}

using System.Collections.Concurrent;

namespace OpenClaw.Core.Pipeline;

public sealed record ToolApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// In-memory approval queue for tool executions. Used to implement supervised autonomy mode.
/// </summary>
public sealed class ToolApprovalService
{
    private sealed class Pending
    {
        public required ToolApprovalRequest Request { get; init; }
        public required TaskCompletionSource<bool> Tcs { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public ToolApprovalRequest Create(string sessionId, string channelId, string senderId, string toolName, string arguments, TimeSpan timeout)
    {
        var approvalId = $"apr_{Guid.NewGuid():N}"[..20];
        var req = new ToolApprovalRequest
        {
            ApprovalId = approvalId,
            SessionId = sessionId,
            ChannelId = channelId,
            SenderId = senderId,
            ToolName = toolName,
            Arguments = arguments
        };

        var p = new Pending
        {
            Request = req,
            Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ExpiresAt = DateTimeOffset.UtcNow.Add(timeout)
        };

        _pending[approvalId] = p;
        return req;
    }

    public bool TrySetDecision(string approvalId, bool approved)
    {
        if (!_pending.TryRemove(approvalId, out var p))
            return false;

        p.Tcs.TrySetResult(approved);
        return true;
    }

    public async Task<bool> WaitForDecisionAsync(string approvalId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_pending.TryGetValue(approvalId, out var p))
            return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await p.Tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or shutdown => deny by default
            _pending.TryRemove(approvalId, out _);
            return false;
        }
    }

    public IReadOnlyList<ToolApprovalRequest> ListPending(string? channelId = null, string? senderId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<ToolApprovalRequest>();

        foreach (var kvp in _pending)
        {
            var p = kvp.Value;
            if (p.ExpiresAt <= now)
            {
                _pending.TryRemove(kvp.Key, out _);
                continue;
            }

            if (channelId is not null && !string.Equals(channelId, p.Request.ChannelId, StringComparison.Ordinal))
                continue;
            if (senderId is not null && !string.Equals(senderId, p.Request.SenderId, StringComparison.Ordinal))
                continue;

            result.Add(p.Request);
        }

        return result;
    }
}


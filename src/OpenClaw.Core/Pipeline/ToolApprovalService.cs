using System.Collections.Concurrent;

namespace OpenClaw.Core.Pipeline;

public enum ToolApprovalDecisionResult
{
    Recorded,
    NotFound,
    Unauthorized
}

public sealed record ToolApprovalDecisionOutcome
{
    public required ToolApprovalDecisionResult Result { get; init; }
    public ToolApprovalRequest? Request { get; init; }
}

public enum ToolApprovalWaitResult
{
    Approved,
    Denied,
    TimedOut,
    NotFound
}

public sealed record ToolApprovalWaitOutcome
{
    public required ToolApprovalWaitResult Result { get; init; }
    public ToolApprovalRequest? Request { get; init; }
}

public sealed record ToolApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public string? Action { get; init; }
    public bool IsMutation { get; init; }
    public string Summary { get; init; } = "";
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

    public ToolApprovalRequest Create(
        string sessionId,
        string channelId,
        string senderId,
        string toolName,
        string arguments,
        TimeSpan timeout,
        string? action = null,
        bool isMutation = false,
        string? summary = null)
    {
        var approvalId = $"apr_{Guid.NewGuid():N}"[..20];
        var req = new ToolApprovalRequest
        {
            ApprovalId = approvalId,
            SessionId = sessionId,
            ChannelId = channelId,
            SenderId = senderId,
            ToolName = toolName,
            Arguments = arguments,
            Action = string.IsNullOrWhiteSpace(action) ? null : action,
            IsMutation = isMutation,
            Summary = summary ?? ""
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

    internal bool TrySetDecision(string approvalId, bool approved)
        => TrySetDecisionWithRequest(approvalId, approved, requesterChannelId: null, requesterSenderId: null, requireRequesterMatch: false)
            .Result is ToolApprovalDecisionResult.Recorded;

    public ToolApprovalDecisionResult TrySetDecision(
        string approvalId,
        bool approved,
        string? requesterChannelId,
        string? requesterSenderId,
        bool requireRequesterMatch = true)
        => TrySetDecisionWithRequest(approvalId, approved, requesterChannelId, requesterSenderId, requireRequesterMatch).Result;

    public ToolApprovalDecisionOutcome TrySetDecisionWithRequest(
        string approvalId,
        bool approved,
        string? requesterChannelId,
        string? requesterSenderId,
        bool requireRequesterMatch = true)
    {
        if (!_pending.TryGetValue(approvalId, out var pending))
            return new ToolApprovalDecisionOutcome { Result = ToolApprovalDecisionResult.NotFound };

        if (requireRequesterMatch)
        {
            if (string.IsNullOrWhiteSpace(requesterChannelId) || string.IsNullOrWhiteSpace(requesterSenderId))
                return new ToolApprovalDecisionOutcome
                {
                    Result = ToolApprovalDecisionResult.Unauthorized,
                    Request = pending.Request
                };

            if (!string.Equals(requesterChannelId, pending.Request.ChannelId, StringComparison.Ordinal) ||
                !string.Equals(requesterSenderId, pending.Request.SenderId, StringComparison.Ordinal))
            {
                return new ToolApprovalDecisionOutcome
                {
                    Result = ToolApprovalDecisionResult.Unauthorized,
                    Request = pending.Request
                };
            }
        }

        if (!_pending.TryRemove(approvalId, out var p))
            return new ToolApprovalDecisionOutcome { Result = ToolApprovalDecisionResult.NotFound };

        p.Tcs.TrySetResult(approved);
        return new ToolApprovalDecisionOutcome
        {
            Result = ToolApprovalDecisionResult.Recorded,
            Request = p.Request
        };
    }

    public async Task<ToolApprovalWaitOutcome> WaitForDecisionOutcomeAsync(string approvalId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_pending.TryGetValue(approvalId, out var p))
        {
            return new ToolApprovalWaitOutcome
            {
                Result = ToolApprovalWaitResult.NotFound
            };
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var approved = await p.Tcs.Task.WaitAsync(cts.Token);
            return new ToolApprovalWaitOutcome
            {
                Result = approved ? ToolApprovalWaitResult.Approved : ToolApprovalWaitResult.Denied,
                Request = p.Request
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (p.Tcs.Task.IsCompletedSuccessfully)
            {
                return new ToolApprovalWaitOutcome
                {
                    Result = p.Tcs.Task.Result ? ToolApprovalWaitResult.Approved : ToolApprovalWaitResult.Denied,
                    Request = p.Request
                };
            }

            // Timeout => deny by default and drain the pending request.
            _pending.TryRemove(approvalId, out _);
            p.Tcs.TrySetCanceled();

            // Re-check: TrySetDecision may have won the race before TrySetCanceled
            if (p.Tcs.Task.IsCompletedSuccessfully)
            {
                return new ToolApprovalWaitOutcome
                {
                    Result = p.Tcs.Task.Result ? ToolApprovalWaitResult.Approved : ToolApprovalWaitResult.Denied,
                    Request = p.Request
                };
            }

            return new ToolApprovalWaitOutcome
            {
                Result = ToolApprovalWaitResult.TimedOut,
                Request = p.Request
            };
        }
    }

    public async Task<bool> WaitForDecisionAsync(string approvalId, TimeSpan timeout, CancellationToken ct)
        => (await WaitForDecisionOutcomeAsync(approvalId, timeout, ct)).Result == ToolApprovalWaitResult.Approved;

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

    public ToolApprovalRequest? GetPending(string approvalId)
    {
        if (!_pending.TryGetValue(approvalId, out var pending))
            return null;

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(approvalId, out _);
            return null;
        }

        return pending.Request;
    }
}

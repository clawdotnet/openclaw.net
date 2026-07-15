using System.Collections.Concurrent;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Actions;

internal interface IActionAdapterConnector
{
    ValueTask<ActionAdapterStepResult> InvokeAsync(ActionCall step, CancellationToken cancellationToken);
}

internal interface IActionIdempotencyRegistry
{
    bool TryRegister(string idempotencyKey);
}

internal sealed class InMemoryActionIdempotencyRegistry : IActionIdempotencyRegistry
{
    private readonly ConcurrentDictionary<string, byte> _knownKeys = new(StringComparer.Ordinal);

    public bool TryRegister(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return false;

        return _knownKeys.TryAdd(idempotencyKey, 0);
    }
}

internal sealed class ActionAdapter
{
    private readonly IActionAdapterConnector _connector;
    private readonly IActionIdempotencyRegistry _idempotencyRegistry;

    internal ActionAdapter(IActionAdapterConnector connector, IActionIdempotencyRegistry idempotencyRegistry)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _idempotencyRegistry = idempotencyRegistry ?? throw new ArgumentNullException(nameof(idempotencyRegistry));
    }

    public async ValueTask<ActionAdapterResult> ExecuteAsync(ActionProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (!_idempotencyRegistry.TryRegister(proposal.IdempotencyKey))
            return ActionAdapterResult.Failed("idempotency_conflict", rollbackTriggered: false);

        foreach (var preCheck in proposal.PreChecks)
        {
            var preCheckResult = await _connector.InvokeAsync(preCheck, cancellationToken).ConfigureAwait(false);
            if (!preCheckResult.Success)
                return ActionAdapterResult.Failed(preCheckResult.ResultCode ?? "precheck_failed", rollbackTriggered: false);
        }

        foreach (var step in proposal.Execution)
        {
            var stepResult = await _connector.InvokeAsync(step, cancellationToken).ConfigureAwait(false);
            if (!stepResult.Success)
                return await RunRollbackAsync(proposal, stepResult.ResultCode ?? "execution_failed", cancellationToken).ConfigureAwait(false);
        }

        return ActionAdapterResult.Succeeded();
    }

    private async ValueTask<ActionAdapterResult> RunRollbackAsync(ActionProposal proposal, string failureCode, CancellationToken cancellationToken)
    {
        if (proposal.Rollback.Count == 0)
            return ActionAdapterResult.Failed(failureCode, rollbackTriggered: true);

        foreach (var rollbackStep in proposal.Rollback)
        {
            var rollbackResult = await _connector.InvokeAsync(rollbackStep, cancellationToken).ConfigureAwait(false);
            if (!rollbackResult.Success)
            {
                return ActionAdapterResult.RollbackFailed(
                    rollbackResult.ResultCode ?? "rollback_failed",
                    ["failed", "rolling_back", "rollback_failed"]);
            }
        }

        return ActionAdapterResult.RolledBack(failureCode, ["failed", "rolling_back", "rolled_back"]);
    }
}

internal sealed class ActionAdapterStepResult
{
    public bool Success { get; init; }
    public string? ResultCode { get; init; }

    public static ActionAdapterStepResult SuccessResult { get; } = new() { Success = true };

    public static ActionAdapterStepResult Succeeded()
        => SuccessResult;

    public static ActionAdapterStepResult Failure(string resultCode)
        => new()
        {
            Success = false,
            ResultCode = resultCode
        };
}

internal sealed class ActionAdapterResult
{
    public required string Status { get; init; }
    public required bool RollbackTriggered { get; init; }
    public string? ResultCode { get; init; }
    public IReadOnlyList<string> StatusHistory { get; init; } = [];

    public static ActionAdapterResult Succeeded()
        => new()
        {
            Status = "succeeded",
            RollbackTriggered = false,
            StatusHistory = ["succeeded"]
        };

    public static ActionAdapterResult Failed(string resultCode, bool rollbackTriggered)
        => new()
        {
            Status = "failed",
            RollbackTriggered = rollbackTriggered,
            ResultCode = resultCode,
            StatusHistory = rollbackTriggered ? ["failed", "rolling_back", "failed"] : ["failed"]
        };

    public static ActionAdapterResult RolledBack(string resultCode, IReadOnlyList<string> statusHistory)
        => new()
        {
            Status = "rolled_back",
            RollbackTriggered = true,
            ResultCode = resultCode,
            StatusHistory = statusHistory
        };

    public static ActionAdapterResult RollbackFailed(string resultCode, IReadOnlyList<string> statusHistory)
        => new()
        {
            Status = "rollback_failed",
            RollbackTriggered = true,
            ResultCode = resultCode,
            StatusHistory = statusHistory
        };
}

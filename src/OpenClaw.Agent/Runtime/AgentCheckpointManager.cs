using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent;

internal sealed class AgentCheckpointManager(IMemoryStore memory, ILogger? logger, OpenClaw.Core.Actions.DurableActionJournal? journal = null)
{
    public async ValueTask PersistToolBatchCheckpointAsync(
        Session session,
        TurnContext turnCtx,
        int iteration,
        IReadOnlyList<ToolInvocation> invocations,
        CancellationToken ct)
    {
        if (invocations.Count == 0)
            return;

        var sequence = (session.ExecutionCheckpoint?.Sequence ?? 0) + 1;
        var checkpoint = new SessionExecutionCheckpoint
        {
            CheckpointId = $"chk_{Guid.NewGuid():N}"[..20],
            Kind = SessionCheckpointKinds.ToolBatch,
            State = SessionCheckpointStates.ReadyToResume,
            Sequence = sequence,
            Iteration = iteration,
            HistoryCount = session.History.Count,
            CorrelationId = turnCtx.CorrelationId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ToolCalls = invocations.Select(static invocation => new SessionCheckpointToolCall
            {
                CallId = invocation.CallId,
                ToolName = invocation.ToolName,
                ResultStatus = string.IsNullOrWhiteSpace(invocation.ResultStatus)
                    ? ToolResultStatuses.Completed
                    : invocation.ResultStatus!,
                FailureCode = invocation.FailureCode,
                DurationMs = (long)invocation.Duration.TotalMilliseconds,
                ArgumentsBytes = Encoding.UTF8.GetByteCount(invocation.Arguments ?? ""),
                ResultBytes = Encoding.UTF8.GetByteCount(invocation.Result ?? "")
            }).ToList()
        };

        session.ExecutionCheckpoint = checkpoint;

        const int MaxRetries = 3;
        var delay = TimeSpan.FromMilliseconds(100);

        async ValueTask RecordRetryAsync(Exception ex, int attempt)
        {
            checkpoint.PersistedAtUtc = null;
            logger?.LogWarning(
                ex,
                "[{CorrelationId}] Checkpoint persistence failed (attempt {Attempt}/{MaxRetries}) for session={SessionId}",
                turnCtx.CorrelationId,
                attempt,
                MaxRetries,
                session.Id);
            await Task.Delay(delay, ct);
            delay *= 2;
        }

        void RecordFinalFailure(Exception ex)
        {
            checkpoint.PersistedAtUtc = null;
            logger?.LogWarning(
                ex,
                "[{CorrelationId}] Failed to persist checkpoint after {MaxRetries} attempts for session={SessionId}",
                turnCtx.CorrelationId,
                MaxRetries,
                session.Id);
        }

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                checkpoint.PersistedAtUtc = DateTimeOffset.UtcNow;
                await memory.SaveSessionAsync(session, ct);
                if (journal is not null) await journal.AcknowledgePersistedHistoryAsync(session, ct);
                logger?.LogInformation(
                    "[{CorrelationId}] Persisted checkpoint {CheckpointId} for session={SessionId} toolCalls={ToolCallCount}",
                    turnCtx.CorrelationId,
                    checkpoint.CheckpointId,
                    session.Id,
                    invocations.Count);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                checkpoint.PersistedAtUtc = null;
                throw;
            }
            catch (IOException ex) when (attempt < MaxRetries)
            {
                await RecordRetryAsync(ex, attempt);
            }
            catch (TimeoutException ex) when (attempt < MaxRetries)
            {
                await RecordRetryAsync(ex, attempt);
            }
            catch (InvalidOperationException ex) when (attempt < MaxRetries)
            {
                await RecordRetryAsync(ex, attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < MaxRetries)
            {
                await RecordRetryAsync(ex, attempt);
            }
            catch (IOException ex)
            {
                RecordFinalFailure(ex);
                throw;
            }
            catch (TimeoutException ex)
            {
                RecordFinalFailure(ex);
                throw;
            }
            catch (InvalidOperationException ex)
            {
                RecordFinalFailure(ex);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                RecordFinalFailure(ex);
                throw;
            }
        }
    }

    public static SessionExecutionCheckpoint? TryGetResumableCheckpoint(Session session)
    {
        var checkpoint = session.ExecutionCheckpoint;
        if (checkpoint is null ||
            !string.Equals(checkpoint.Kind, SessionCheckpointKinds.ToolBatch, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.State, SessionCheckpointStates.ReadyToResume, StringComparison.Ordinal) ||
            checkpoint.PersistedAtUtc is null)
        {
            return null;
        }

        if (session.History.Count != checkpoint.HistoryCount)
            return null;

        var lastTurn = session.History.Count == 0 ? null : session.History[^1];
        if (lastTurn?.Content != "[tool_use]" || lastTurn.ToolCalls is not { Count: > 0 })
            return null;

        return checkpoint;
    }

    public static void MarkCheckpointCompleted(Session session, string state, string reason)
    {
        var checkpoint = session.ExecutionCheckpoint;
        if (checkpoint is null ||
            !string.Equals(checkpoint.State, SessionCheckpointStates.ReadyToResume, StringComparison.Ordinal))
        {
            return;
        }

        checkpoint.State = state;
        checkpoint.CompletedAtUtc = DateTimeOffset.UtcNow;
        checkpoint.CompletionReason = reason;
    }

    public static string BuildCheckpointResumeInstruction(SessionExecutionCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Checkpoint resume]");
        sb.AppendLine($"Resume from checkpoint {checkpoint.CheckpointId}.");
        sb.AppendLine("The previous assistant tool batch and tool results have already completed and are present in this conversation context.");
        sb.AppendLine("Continue the interrupted task from those results. Do not repeat completed tool calls unless the results show that retrying is necessary.");
        sb.AppendLine("[/Checkpoint resume]");
        return sb.ToString();
    }

    public static string BuildCheckpointResumeUserNote(string userMessage)
        => "[Checkpoint resume user note]\n" + userMessage.Trim() + "\n[/Checkpoint resume user note]";

    public static bool IsBareResumeRequest(string userMessage)
    {
        var trimmed = userMessage.Trim();
        return trimmed.Length == 0 ||
            trimmed.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("continue", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/resume", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/continue", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveCheckpointCallId(ToolInvocation invocation, int index)
        => string.IsNullOrWhiteSpace(invocation.CallId)
            ? $"checkpoint_call_{index + 1}"
            : invocation.CallId!;

    public static IDictionary<string, object?> DeserializeToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            var parsed = JsonSerializer.Deserialize(arguments, CoreJsonContext.Default.DictionaryStringObject);
            return parsed ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_raw"] = arguments
            };
        }
    }
}

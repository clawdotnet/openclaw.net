namespace OpenClaw.Core.Security;

public sealed record SentinelSubstitutionContext
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? CorrelationId { get; init; }
    public string? WorkspaceId { get; init; }
}

public sealed record SentinelSubstitutionResult
{
    public required string ExecutionArgumentsJson { get; init; }
    public required string PersistedArgumentsJson { get; init; }
    public bool Substituted { get; init; }
}

public interface ISentinelSubstitutionService
{
    ValueTask<SentinelSubstitutionResult> SubstituteAsync(SentinelSubstitutionContext context, CancellationToken ct);
}

public sealed class NoopSentinelSubstitutionService : ISentinelSubstitutionService
{
    public ValueTask<SentinelSubstitutionResult> SubstituteAsync(SentinelSubstitutionContext context, CancellationToken ct)
        => ValueTask.FromResult(new SentinelSubstitutionResult
        {
            ExecutionArgumentsJson = context.ArgumentsJson,
            PersistedArgumentsJson = context.ArgumentsJson,
            Substituted = false
        });
}

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Routing.Decisions;

/// <summary>Decision metadata only: no conversation text, tool arguments, or credentials.</summary>
public sealed class DecisionRoutingDiagnostic
{
    public string DecisionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string RubricVersion { get; init; } = DecisionTurnRoutingPolicy.RubricVersion;
    public required string Provider { get; init; }
    public DecisionMetadata? Metadata { get; init; }
    public required string Mode { get; init; }
    public required string SessionId { get; init; }
    public required string BaselineTier { get; init; }
    public string? BaselineProfileId { get; init; }
    public string? ProposedTier { get; init; }
    public string? ProposedProfileId { get; init; }
    public required string AppliedTier { get; init; }
    public string? AppliedProfileId { get; init; }
    public required string Reason { get; init; }
    public string? Model { get; init; }
    public string? RawChoice { get; init; }
    public Dictionary<string, double>? Probabilities { get; init; }
    public double? Confidence { get; init; }
    public double? HighRiskProbability { get; init; }
    public double? RequiresToolsProbability { get; init; }
    public bool ContextTruncated { get; init; }
    public long LatencyMs { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
}

public interface IDecisionRoutingObserver
{
    void Record(DecisionRoutingDiagnostic diagnostic);
}

public sealed class JsonlDecisionRoutingObserver(string? path, ILogger<JsonlDecisionRoutingObserver> logger) : IDecisionRoutingObserver
{
    private readonly Lock _gate = new();

    public void Record(DecisionRoutingDiagnostic diagnostic)
    {
        logger.LogInformation("Decision routing {Provider} {Mode}: baseline={BaselineTier} proposed={ProposedTier} applied={AppliedTier} reason={Reason} latencyMs={LatencyMs} inputTokens={InputTokens} estimatedCostUsd={CostUsd} decisionId={DecisionId}",
            diagnostic.Provider, diagnostic.Mode, diagnostic.BaselineTier, diagnostic.ProposedTier, diagnostic.AppliedTier,
            diagnostic.Reason, diagnostic.LatencyMs, diagnostic.InputTokens, diagnostic.EstimatedCostUsd, diagnostic.DecisionId);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, JsonSerializer.Serialize(diagnostic, DecisionJsonContext.Default.DecisionRoutingDiagnostic) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Storage availability must not decide whether an agent turn can execute.
            logger.LogWarning("Unable to append decision routing diagnostics ({ErrorType}).", ex.GetType().Name);
        }
    }
}

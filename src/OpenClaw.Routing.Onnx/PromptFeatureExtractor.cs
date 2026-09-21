using OpenClaw.Agent.Routing;

namespace OpenClaw.Routing.Onnx;

internal sealed record RoutingFeatureInput(
    string CurrentUserText,
    string[] PriorUserTurns,
    string? PreviousAssistantText,
    int TurnIndex,
    string? PreviousTier,
    int ToolCount,
    int ContextTextLength);

internal static partial class PromptFeatureExtractor
{
    internal const int EmbeddingSegmentDimensions = 512;

    internal const int FeatureVectorDimensions = EmbeddingSegmentDimensions * 3;

    public static float[] BuildFeatureVector(
        RoutingFeatureInput input,
        ReadOnlySpan<float> currentEmbedding,
        ReadOnlySpan<float> historyEmbedding,
        ReadOnlySpan<float> assistantEmbedding)
    {
        var features = new float[FeatureVectorDimensions];

        var offset = 0;
        CopyEmbeddingSegment(currentEmbedding, features.AsSpan(offset, EmbeddingSegmentDimensions), nameof(currentEmbedding));
        offset += EmbeddingSegmentDimensions;

        CopyEmbeddingSegment(historyEmbedding, features.AsSpan(offset, EmbeddingSegmentDimensions), nameof(historyEmbedding));
        offset += EmbeddingSegmentDimensions;

        CopyEmbeddingSegment(assistantEmbedding, features.AsSpan(offset, EmbeddingSegmentDimensions), nameof(assistantEmbedding));
        return features;
    }

    public static TurnRoutingSignals ExtractSignals(string text, int turnIndex)
        => TurnRoutingGuardrails.ExtractSignals(text, turnIndex);

    private static void CopyEmbeddingSegment(ReadOnlySpan<float> source, Span<float> destination, string parameterName)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException(
                $"Expected embedding length {destination.Length}, but received {source.Length}.",
                parameterName);
        }

        source.CopyTo(destination);
    }

}

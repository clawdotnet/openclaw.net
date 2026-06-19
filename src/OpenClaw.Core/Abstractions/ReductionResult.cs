namespace OpenClaw.Core.Abstractions;

public readonly record struct ReductionResult
{
    public required string Text { get; init; }
    public required int OriginalLength { get; init; }
    public required int ReducedLength { get; init; }
    public required double Ratio { get; init; }
    public string? ReducerId { get; init; }

    public bool WasReduced => ReducedLength < OriginalLength && !string.IsNullOrEmpty(ReducerId);

    public static ReductionResult Unchanged(string text)
        => new()
        {
            Text = text,
            OriginalLength = text.Length,
            ReducedLength = text.Length,
            Ratio = 1.0,
        };
}

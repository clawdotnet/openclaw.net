namespace OpenClaw.Plugins.TokenJuice.Reduction;

public sealed class SemanticDensityCalculator
{
    private readonly double _threshold;

    public SemanticDensityCalculator(double threshold = 0.3) => _threshold = threshold;

    public bool ShouldReduce(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var totalLines = lines.Length;
        if (totalLines == 0) return false;

        var uniqueLines = lines.Distinct(StringComparer.Ordinal).Count();
        var totalChars = text.Length;
        var nonWhitespaceChars = text.Count(c => !char.IsWhiteSpace(c));

        if (totalChars == 0) return false;

        var density = (uniqueLines / (double)Math.Max(totalLines, 1)) *
                      (nonWhitespaceChars / (double)Math.Max(totalChars, 1));

        return density < _threshold;
    }
}

using System.Text.Json.Serialization;

namespace OpenClaw.LayaService.Evaluation;

public sealed record LabeledPrediction(
    string AnswerType,
    IReadOnlyList<string> CandidateKeys,
    IReadOnlyList<double> Probabilities,
    string Label);

public sealed record ReliabilityBin(
    [property: JsonPropertyName("lower")] double Lower,
    [property: JsonPropertyName("upper")] double Upper,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("mean_top_probability")] double MeanTopProbability,
    [property: JsonPropertyName("accuracy")] double Accuracy);

public sealed record RiskCoveragePoint(
    [property: JsonPropertyName("confidence_threshold")] double ConfidenceThreshold,
    [property: JsonPropertyName("coverage")] double Coverage,
    [property: JsonPropertyName("error_rate")] double? ErrorRate);

public sealed record CalibrationMetricsResult(
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("accuracy")] double Accuracy,
    [property: JsonPropertyName("nll")] double Nll,
    [property: JsonPropertyName("brier")] double Brier,
    [property: JsonPropertyName("ece")] double Ece,
    [property: JsonPropertyName("reliability_bins")] IReadOnlyList<ReliabilityBin> ReliabilityBins,
    [property: JsonPropertyName("risk_coverage")] IReadOnlyList<RiskCoveragePoint> RiskCoverage);

public static class CalibrationMetrics
{
    private static readonly double[] CoverageThresholds = [0.0, 0.2, 0.4, 0.6, 0.8, 0.9, 0.95, 0.99];

    public static CalibrationMetricsResult Measure(IReadOnlyList<LabeledPrediction> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidDataException("No labeled predictions.");
        var bins = Enumerable.Range(0, 10).Select(_ => new List<(double Confidence, bool Correct)>()).ToArray();
        var ranked = new List<(double Confidence, bool Correct)>(rows.Count);
        var correctCount = 0;
        var nll = 0.0;
        var brier = 0.0;

        foreach (var row in rows)
        {
            var normalized = Normalize(row);
            var expectedIndex = IndexOfLabel(normalized.CandidateKeys, row.Label);
            var predictedIndex = IndexOfMaximum(normalized.Probabilities);
            var correct = predictedIndex == expectedIndex;
            var topProbability = normalized.Probabilities[predictedIndex];
            bins[Math.Min(9, (int)(topProbability * 10))].Add((topProbability, correct));
            ranked.Add((Confidence(normalized.AnswerType, normalized.Probabilities), correct));
            if (correct) correctCount++;
            nll -= Math.Log(Math.Max(normalized.Probabilities[expectedIndex], 1e-9));
            for (var index = 0; index < normalized.Probabilities.Count; index++)
            {
                var error = normalized.Probabilities[index] - (index == expectedIndex ? 1 : 0);
                brier += error * error;
            }
        }

        var reliability = new List<ReliabilityBin>();
        var ece = 0.0;
        for (var index = 0; index < bins.Length; index++)
        {
            var values = bins[index];
            if (values.Count == 0) continue;
            var meanConfidence = values.Average(value => value.Confidence);
            var accuracy = values.Count(value => value.Correct) / (double)values.Count;
            ece += values.Count / (double)rows.Count * Math.Abs(meanConfidence - accuracy);
            reliability.Add(new ReliabilityBin(index / 10.0, (index + 1) / 10.0, values.Count, meanConfidence, accuracy));
        }

        var riskCoverage = CoverageThresholds.Select(threshold =>
        {
            var selected = ranked.Where(value => value.Confidence >= threshold).ToArray();
            return new RiskCoveragePoint(threshold, selected.Length / (double)rows.Count,
                selected.Length == 0 ? null : 1 - selected.Count(value => value.Correct) / (double)selected.Length);
        }).ToArray();
        return new CalibrationMetricsResult(rows.Count, correctCount / (double)rows.Count, nll / rows.Count,
            brier / rows.Count, ece, reliability, riskCoverage);
    }

    internal static LabeledPrediction Normalize(LabeledPrediction row)
    {
        if (row.AnswerType is not ("choice" or "score" or "noul") || row.CandidateKeys.Count < 2 ||
            row.CandidateKeys.Count != row.Probabilities.Count || row.CandidateKeys.Distinct(StringComparer.Ordinal).Count() != row.CandidateKeys.Count ||
            !row.CandidateKeys.Contains(row.Label, StringComparer.Ordinal) ||
            row.Probabilities.Any(probability => !double.IsFinite(probability) || probability is < 0 or > 1))
        {
            throw new InvalidDataException("Invalid labeled probability distribution.");
        }
        var sum = row.Probabilities.Sum();
        if (Math.Abs(sum - 1) > 0.002) throw new InvalidDataException("Invalid labeled probability sum.");
        return row with { Probabilities = row.Probabilities.Select(probability => probability / sum).ToArray() };
    }

    internal static double Confidence(string answerType, IReadOnlyList<double> probabilities)
        => answerType == "noul"
            ? probabilities.Max()
            : Math.Max(0, 1 + probabilities.Sum(probability => probability * Math.Log(Math.Max(probability, 1e-12))) / Math.Log(probabilities.Count));

    internal static int IndexOfMaximum(IReadOnlyList<double> values)
    {
        var index = 0;
        for (var candidate = 1; candidate < values.Count; candidate++)
            if (values[candidate] > values[index]) index = candidate;
        return index;
    }

    internal static int IndexOfLabel(IReadOnlyList<string> values, string label)
    {
        for (var index = 0; index < values.Count; index++)
            if (string.Equals(values[index], label, StringComparison.Ordinal)) return index;
        throw new InvalidDataException("Label is not present in the candidate distribution.");
    }
}
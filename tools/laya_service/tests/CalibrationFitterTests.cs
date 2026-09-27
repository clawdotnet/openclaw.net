using System.Text.Json;
using OpenClaw.LayaService.Evaluation;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class CalibrationFitterTests
{
    [Fact]
    public void Metrics_MeasuresAccuracyNllBrierEceAndRiskCoverage()
    {
        var rows = new[]
        {
            new LabeledPrediction("choice", ["a", "b"], [0.8, 0.2], "a"),
            new LabeledPrediction("choice", ["a", "b"], [0.3, 0.7], "a")
        };

        var metrics = CalibrationMetrics.Measure(rows);

        Assert.Equal(2, metrics.Samples);
        Assert.Equal(0.5, metrics.Accuracy);
        Assert.Equal((-Math.Log(0.8) - Math.Log(0.3)) / 2, metrics.Nll, 10);
        Assert.Equal((0.08 + 0.98) / 2, metrics.Brier, 10);
        Assert.Equal(2, metrics.ReliabilityBins.Count);
        Assert.Equal(8, metrics.RiskCoverage.Count);
    }

    [Fact]
    public void Metrics_NormalizesRoundedProbabilitiesAndSupportsNoulAndScore()
    {
        var rows = new[]
        {
            new LabeledPrediction("noul", ["false", "true"], [0.2495, 0.7495], "true"),
            new LabeledPrediction("score", ["0", "1"], [0.8, 0.2], "1")
        };

        var metrics = CalibrationMetrics.Measure(rows);

        Assert.Equal(2, metrics.Samples);
        Assert.Equal(0.5, metrics.Accuracy);
        Assert.Equal((-Math.Log(0.7495 / 0.999) - Math.Log(0.2)) / 2, metrics.Nll, 10);
    }

    [Fact]
    public void Fit_ProducesVersionTwoArtifactFromDisjointRawObservations()
    {
        var training = BuildObservations("train", 20);
        var validation = BuildObservations("validation", 20);

        var artifact = CalibrationFitter.Fit(training, validation);
        var json = CalibrationFitter.Serialize(artifact);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("1.0.0", document.RootElement.GetProperty("sdk_version").GetString());
        Assert.Equal("NLaya", document.RootElement.GetProperty("runtime").GetString());
        Assert.Equal(0.1, document.RootElement.GetProperty("temperatures").GetProperty("english").GetProperty("choice:2").GetDouble());
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_RejectsMixedModelSchemaRuntimeAndInsufficientBucketSamples()
    {
        var training = BuildObservations("train", 20);
        var validation = BuildObservations("validation", 20);
        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, [training[0] with { Model = "laya@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }]));

        var mixedSchema = validation.ToArray();
        mixedSchema[0] = mixedSchema[0] with { SchemaHash = new string('b', 64) };
        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, mixedSchema));

        var mixedRuntime = validation.ToArray();
        mixedRuntime[0] = mixedRuntime[0] with { Runtime = "OtherRuntime" };
        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, mixedRuntime));
        var unknownCheckpoint = validation.ToArray();
        unknownCheckpoint[0] = unknownCheckpoint[0] with { Checkpoint = "unknown" };
        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, unknownCheckpoint));
        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training[..19], validation));
    }

    [Fact]
    public void Fit_RejectsMismatchedBuckets()
    {
        var training = BuildObservations("train", 20);
        var validation = BuildObservations("validation", 20);
        using var answer = JsonDocument.Parse("""{"type":"score","score":0,"probabilities":{"0":0.7,"1":0.3}}""");
        validation[0] = validation[0] with { RawAnswer = answer.RootElement.Clone() };

        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, validation));
    }

    [Fact]
    public void Fit_RejectsNonNumericRawProbabilities()
    {
        var training = BuildObservations("train", 20);
        var validation = BuildObservations("validation", 20);
        using var answer = JsonDocument.Parse("""{"type":"choice","probabilities":{"a":"0.6","b":0.4}}""");
        training[0] = training[0] with { RawAnswer = answer.RootElement.Clone() };

        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, validation));
    }

    [Fact]
    public async Task WriteAsync_ReturnsSha256OfExactArtifactBytes()
    {
        var artifact = CalibrationFitter.Fit(BuildObservations("train", 20), BuildObservations("validation", 20));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "calibration.json");
        try
        {
            var identifier = await CalibrationFitter.WriteAsync(artifact, path, CancellationToken.None);
            var payload = await File.ReadAllBytesAsync(path);

            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), identifier);
            Assert.Equal((byte)'\n', payload[^1]);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Fit_RejectsOverlappingCasesAndFingerprints()
    {
        var training = BuildObservations("train", 20);
        var validation = BuildObservations("validation", 20).ToArray();
        validation[0] = validation[0] with
        {
            CaseId = training[0].CaseId,
            CaseFingerprint = training[0].CaseFingerprint
        };

        Assert.Throws<InvalidDataException>(() => CalibrationFitter.Fit(training, validation));
    }

    private static Observation[] BuildObservations(string prefix, int count)
    {
        var rows = new Observation[count];
        for (var index = 0; index < count; index++)
        {
            var correct = index % 2 == 0;
            using var answer = JsonDocument.Parse(correct
                ? """{"type":"choice","choice":"a","confidence":0.3,"answer_confidence":0.6,"probabilities":{"a":0.6,"b":0.4}}"""
                : """{"type":"choice","choice":"b","confidence":0.3,"answer_confidence":0.6,"probabilities":{"a":0.4,"b":0.6}}""");
            rows[index] = new Observation(
                prefix + "-" + index,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prefix + index))),
                "tier",
                "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982",
                "english",
                new string('a', 64),
                "1.0.0",
                answer.RootElement.Clone(),
                correct ? "a" : "b");
        }
        return rows;
    }
}
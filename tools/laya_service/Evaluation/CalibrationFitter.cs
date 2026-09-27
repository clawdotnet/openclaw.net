using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.LayaService.Inference;
using OpenClaw.LayaService.Models;

namespace OpenClaw.LayaService.Evaluation;

public sealed record CalibrationArtifact
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("schema_hash")]
    public required string SchemaHash { get; init; }

    [JsonPropertyName("sdk_version")]
    public required string SdkVersion { get; init; }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = "NLaya";

    [JsonPropertyName("temperatures")]
    public required SortedDictionary<string, SortedDictionary<string, double>> Temperatures { get; init; }

    [JsonPropertyName("validation")]
    public required SortedDictionary<string, SortedDictionary<string, CalibrationBucketValidation>> Validation { get; init; }
}

public sealed record CalibrationBucketValidation(
    [property: JsonPropertyName("calibration_samples")] int CalibrationSamples,
    [property: JsonPropertyName("raw")] CalibrationMetricsResult Raw,
    [property: JsonPropertyName("calibrated")] CalibrationMetricsResult Calibrated);

public static class CalibrationFitter
{
    private static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };
    private static readonly HashSet<string> ObservationProperties =
        ["case_id", "case_fingerprint", "question_id", "model", "checkpoint", "schema_hash", "sdk_version", "runtime", "source_calibration", "answer", "label"];

    public static async Task<IReadOnlyList<Observation>> ReadObservationsAsync(string path, CancellationToken cancellationToken)
    {
        var observations = new List<Observation>();
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any(property => !ObservationProperties.Contains(property.Name)) ||
                document.RootElement.TryGetProperty("state", out _))
            {
                throw new InvalidDataException("Observation contains unsupported fields.");
            }
            observations.Add(document.RootElement.Deserialize<Observation>() ?? throw new InvalidDataException("Invalid observation row."));
        }
        return observations;
    }

    public static CalibrationArtifact Fit(IReadOnlyList<Observation> training, IReadOnlyList<Observation> validation, int minimumSamples = 20)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);
        if (minimumSamples < 1 || training.Count == 0 || validation.Count == 0) throw new InvalidDataException("Calibration datasets must be nonempty.");
        ValidateObservationSet(training);
        ValidateObservationSet(validation);
        if (training.Select(row => row.CaseId).Intersect(validation.Select(row => row.CaseId), StringComparer.Ordinal).Any() ||
            training.Select(row => row.CaseFingerprint).Intersect(validation.Select(row => row.CaseFingerprint), StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Calibration and validation cases must be disjoint.");
        }

        var identities = training.Concat(validation)
            .Select(row => (row.Model, row.SchemaHash, row.RuntimeVersion, row.Runtime))
            .Distinct()
            .ToArray();
        if (identities.Length != 1) throw new InvalidDataException("Use one model, schema and runtime per calibration artifact.");
        var (model, schemaHash, runtimeVersion, runtime) = identities[0];
        if (!model.StartsWith("laya@", StringComparison.Ordinal) || !ModelManifest.IsRevision(model[5..]) ||
            !ModelManifest.IsHash(schemaHash) || runtimeVersion != NLayaDecisionPredictor.SdkVersion || runtime != "NLaya")
        {
            throw new InvalidDataException("Calibration provenance is not pinned to this NLaya runtime.");
        }

        var trainingGroups = Group(training);
        var validationGroups = Group(validation);
        if (!trainingGroups.Keys.ToHashSet().SetEquals(validationGroups.Keys))
            throw new InvalidDataException("Training and validation must cover the same calibration buckets.");

        var temperatures = new SortedDictionary<string, SortedDictionary<string, double>>(StringComparer.Ordinal);
        var validationMetrics = new SortedDictionary<string, SortedDictionary<string, CalibrationBucketValidation>>(StringComparer.Ordinal);
        foreach (var group in trainingGroups.Keys.OrderBy(key => key.Checkpoint, StringComparer.Ordinal).ThenBy(key => key.Bucket, StringComparer.Ordinal))
        {
            var train = trainingGroups[group];
            var test = validationGroups[group];
            if (train.Count < minimumSamples || test.Count < minimumSamples || train.Select(row => row.Prediction.Label).Distinct(StringComparer.Ordinal).Count() < 2)
                throw new InvalidDataException("Each bucket needs the minimum samples on both splits and multiple training labels.");

            var temperature = FitTemperature(train);
            var rawMetrics = CalibrationMetrics.Measure(test.Select(row => row.Prediction).ToArray());
            var calibratedPredictions = test.Select(row => row.Prediction with
            {
                Probabilities = Scale(row.Prediction.Probabilities, temperature)
            }).ToArray();
            var calibratedMetrics = CalibrationMetrics.Measure(calibratedPredictions);
            if (!temperatures.TryGetValue(group.Checkpoint, out var checkpointTemperatures))
            {
                checkpointTemperatures = new SortedDictionary<string, double>(StringComparer.Ordinal);
                temperatures.Add(group.Checkpoint, checkpointTemperatures);
                validationMetrics.Add(group.Checkpoint, new SortedDictionary<string, CalibrationBucketValidation>(StringComparer.Ordinal));
            }
            checkpointTemperatures.Add(group.Bucket, temperature);
            validationMetrics[group.Checkpoint].Add(group.Bucket, new CalibrationBucketValidation(train.Count, rawMetrics, calibratedMetrics));
        }

        return new CalibrationArtifact
        {
            Model = model,
            SchemaHash = schemaHash,
            SdkVersion = runtimeVersion,
            Runtime = runtime,
            Temperatures = temperatures,
            Validation = validationMetrics
        };
    }

    public static string Serialize(CalibrationArtifact artifact)
        => JsonSerializer.Serialize(artifact, ArtifactJson) + "\n";

    public static async Task<string> WriteAsync(CalibrationArtifact artifact, string outputPath, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(Serialize(artifact));
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Invalid output path.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateObservationSet(IReadOnlyList<Observation> observations)
    {
        var uniqueQuestionRows = new HashSet<(string CaseId, string QuestionId)>();
        foreach (var row in observations)
        {
            if (string.IsNullOrWhiteSpace(row.CaseId) || string.IsNullOrWhiteSpace(row.QuestionId) ||
                !ModelManifest.IsHash(row.CaseFingerprint) || !ModelManifest.IsHash(row.SchemaHash) ||
                !ModelManifest.CheckpointNames.Contains(row.Checkpoint) || row.SourceCalibration != "raw" || row.Runtime != "NLaya" ||
                !uniqueQuestionRows.Add((row.CaseId, row.QuestionId)))
            {
                throw new InvalidDataException("Invalid or duplicate raw observation.");
            }
            _ = ToPrediction(row);
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate observation property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static Dictionary<(string Checkpoint, string Bucket), List<(Observation Observation, LabeledPrediction Prediction)>> Group(
        IReadOnlyList<Observation> observations)
    {
        var groups = new Dictionary<(string Checkpoint, string Bucket), List<(Observation, LabeledPrediction)>>();
        foreach (var row in observations)
        {
            var prediction = ToPrediction(row);
            var key = (row.Checkpoint, prediction.AnswerType + ":" + prediction.CandidateKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!groups.TryGetValue(key, out var values)) groups.Add(key, values = []);
            values.Add((row, prediction));
        }
        return groups;
    }

    private static LabeledPrediction ToPrediction(Observation row)
    {
        using var questionDocument = JsonDocument.Parse("{}");
        var answer = row.RawAnswer;
        var type = answer.GetProperty("type").GetString() ?? throw new InvalidDataException("Invalid raw answer.");
        if (type == "noul")
        {
            var probability = answer.GetProperty("noul").GetDouble();
            return CalibrationMetrics.Normalize(new LabeledPrediction(type, ["false", "true"], [1 - probability, probability], row.Label));
        }

        var probabilities = answer.GetProperty("probabilities");
        var keys = probabilities.EnumerateObject().Select(item => item.Name).ToArray();
        var values = probabilities.EnumerateObject().Select(item => item.Value.GetDouble()).ToArray();
        return CalibrationMetrics.Normalize(new LabeledPrediction(type, keys, values, row.Label));
    }

    private static double FitTemperature(IReadOnlyList<(Observation Observation, LabeledPrediction Prediction)> rows)
    {
        var bestTemperature = 0.1;
        var bestLoss = double.PositiveInfinity;
        for (var index = 0; index <= 160; index++)
        {
            var temperature = Math.Pow(10, -1 + index / 80.0);
            var loss = 0.0;
            foreach (var row in rows)
            {
                var prediction = row.Prediction;
                var expectedIndex = CalibrationMetrics.IndexOfLabel(prediction.CandidateKeys, prediction.Label);
                loss -= Math.Log(Math.Max(Scale(prediction.Probabilities, temperature)[expectedIndex], 1e-9));
            }
            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestTemperature = temperature;
            }
        }
        return bestTemperature;
    }

    private static double[] Scale(IReadOnlyList<double> probabilities, double temperature)
    {
        var logits = probabilities.Select(probability => Math.Log(Math.Max(probability, 1e-9)) / temperature).ToArray();
        var peak = logits.Max();
        var values = logits.Select(value => Math.Exp(value - peak)).ToArray();
        var sum = values.Sum();
        return values.Select(value => value / sum).ToArray();
    }
}
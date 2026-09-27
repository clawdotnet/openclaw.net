using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLaya;
using OpenClaw.LayaService.Models;
using OpenClaw.LayaService.Protocol;

namespace OpenClaw.LayaService.Inference;

public sealed class CalibrationStore
{
    private readonly string? _schemaHash;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> _temperatures;

    private CalibrationStore(string identifier, string? schemaHash, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> temperatures)
    {
        Identifier = identifier;
        _schemaHash = schemaHash;
        _temperatures = temperatures;
    }

    public string Identifier { get; }

    public static CalibrationStore Load(string? path, string model)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CalibrationStore("uncalibrated", null, new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal));
        }

        var payload = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 2 ||
            !root.TryGetProperty("model", out var artifactModel) || artifactModel.ValueKind != JsonValueKind.String || artifactModel.GetString() != model)
        {
            throw new InvalidDataException("Invalid calibration artifact or model identity.");
        }

        var schema = root.TryGetProperty("schema_hash", out var schemaValue) ? schemaValue : default;
        var sdk = root.TryGetProperty("sdk_version", out var sdkValue) ? sdkValue : default;
        var runtime = root.TryGetProperty("runtime", out var runtimeValue) ? runtimeValue : default;
        var temperaturesElement = root.TryGetProperty("temperatures", out var temperaturesValue) ? temperaturesValue : default;
        var validation = root.TryGetProperty("validation", out var validationValue) ? validationValue : default;
        if (schema.ValueKind != JsonValueKind.String || !ModelManifest.IsHash(schema.GetString()) ||
            sdk.ValueKind != JsonValueKind.String || sdk.GetString() != NLayaDecisionPredictor.SdkVersion ||
            runtime.ValueKind != JsonValueKind.String || runtime.GetString() != "NLaya" ||
            temperaturesElement.ValueKind != JsonValueKind.Object || temperaturesElement.GetPropertyCount() == 0 ||
            validation.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid calibration artifact or model identity.");
        }

        var temperatures = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var checkpoint in temperaturesElement.EnumerateObject())
        {
            if (!ModelManifest.CheckpointNames.Contains(checkpoint.Name) || checkpoint.Value.ValueKind != JsonValueKind.Object ||
                checkpoint.Value.GetPropertyCount() == 0)
            {
                throw new InvalidDataException("Invalid calibration checkpoint.");
            }

            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var bucket in checkpoint.Value.EnumerateObject())
            {
                var parts = bucket.Name.Split(':');
                if (parts.Length != 2 || parts[0] is not ("choice" or "score" or "noul") ||
                    !int.TryParse(parts[1], out var count) || count < 2 || bucket.Value.ValueKind != JsonValueKind.Number ||
                    !bucket.Value.TryGetDouble(out var temperature) || !double.IsFinite(temperature) || temperature is < 0.1 or > 10)
                {
                    throw new InvalidDataException("Invalid calibration temperature.");
                }
                values.Add(bucket.Name, temperature);
            }
            temperatures.Add(checkpoint.Name, values);
        }

        return new CalibrationStore(Convert.ToHexStringLower(SHA256.HashData(payload)), schema.GetString(), temperatures);
    }

    public void ValidateRequest(DecisionWireRequest request, string checkpoint, Questions questions)
    {
        if (_schemaHash is null) return;
        if (!string.Equals(_schemaHash, StrictJson.SchemaHash(request.Questions), StringComparison.Ordinal))
        {
            throw new ProtocolRejectionException("calibration_schema_mismatch");
        }
        if (!_temperatures.TryGetValue(checkpoint, out var checkpointTemperatures) ||
            questions.Values.Any(question => !checkpointTemperatures.ContainsKey(Bucket(question.TypeName, question.OptionCount))))
        {
            throw new ProtocolRejectionException("calibration_bucket_missing");
        }
    }

    public JsonElement Apply(DecisionWireRequest request, string checkpoint, JsonElement answers)
    {
        if (_schemaHash is null) return answers.Clone();
        if (!string.Equals(_schemaHash, StrictJson.SchemaHash(request.Questions), StringComparison.Ordinal) ||
            !_temperatures.TryGetValue(checkpoint, out var checkpointTemperatures))
        {
            throw new ProtocolRejectionException("calibration_schema_mismatch");
        }

        var questions = Questions.Parse(request.Questions.GetRawText());
        var transformed = JsonNode.Parse(answers.GetRawText())!.AsObject();
        foreach (var (questionId, question) in questions)
        {
            if (!checkpointTemperatures.TryGetValue(Bucket(question.TypeName, question.OptionCount), out var temperature) ||
                !transformed.TryGetPropertyValue(questionId, out var answerNode) || answerNode is not JsonObject answer)
            {
                throw new ProtocolRejectionException("calibration_bucket_missing");
            }
            TransformAnswer(answer, question.TypeName, question, temperature);
        }
        return JsonDocument.Parse(transformed.ToJsonString()).RootElement.Clone();
    }

    private static void TransformAnswer(JsonObject answer, string type, Question question, double temperature)
    {
        var labels = type switch
        {
            "choice" => question.Options!.Select(option => option.Key).ToArray(),
            "score" => Enumerable.Range(0, question.Levels!.Count).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            "noul" => ["false", "true"],
            _ => throw new InvalidDataException("Invalid calibration answer type.")
        };
        double[] probabilities;
        if (type == "noul")
        {
            if (!answer.TryGetPropertyValue("noul", out var positive) || positive is not JsonValue value || !value.TryGetValue<double>(out var probability) || probability is < 0 or > 1)
                throw new InvalidDataException("Invalid noul probabilities.");
            probabilities = [1 - probability, probability];
        }
        else
        {
            if (!answer.TryGetPropertyValue("probabilities", out var probabilityNode) || probabilityNode is not JsonObject distribution ||
                distribution.Count != labels.Length || labels.Any(label => !distribution.ContainsKey(label)))
                throw new InvalidDataException("Invalid answer probabilities.");
            probabilities = labels.Select(label => distribution[label]!.GetValue<double>()).ToArray();
            if (probabilities.Any(value => !double.IsFinite(value) || value is < 0 or > 1) || Math.Abs(probabilities.Sum() - 1) > 1e-5)
                throw new InvalidDataException("Invalid answer probabilities.");
        }

        var scaled = Scale(probabilities, temperature);
        var maxIndex = Array.IndexOf(scaled, scaled.Max());
        if (type == "noul")
        {
            answer["noul"] = scaled[1];
            answer["value"] = scaled[1] >= 0.5;
            answer["answer_confidence"] = Math.Max(scaled[0], scaled[1]);
        }
        else
        {
            var outputProbabilities = new JsonObject();
            for (var index = 0; index < labels.Length; index++) outputProbabilities[labels[index]] = scaled[index];
            answer["probabilities"] = outputProbabilities;
            answer["answer_confidence"] = scaled[maxIndex];
            if (type == "choice") answer["choice"] = labels[maxIndex];
            else answer["score"] = scaled.Select((probability, index) => probability * index).Sum();
        }

        answer["confidence"] = type == "noul" ? Math.Max(scaled[0], scaled[1]) : EntropyConfidence(scaled);
    }

    private static double[] Scale(IReadOnlyList<double> probabilities, double temperature)
    {
        var logits = probabilities.Select(probability => Math.Log(Math.Max(probability, 1e-9)) / temperature).ToArray();
        var peak = logits.Max();
        var exponents = logits.Select(value => Math.Exp(value - peak)).ToArray();
        var total = exponents.Sum();
        return exponents.Select(value => value / total).ToArray();
    }

    private static double EntropyConfidence(IReadOnlyList<double> probabilities)
        => Math.Max(0, 1 + probabilities.Sum(probability => probability * Math.Log(Math.Max(probability, 1e-12))) / Math.Log(probabilities.Count));

    private static string Bucket(string type, int options) => type + ":" + options.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate calibration property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
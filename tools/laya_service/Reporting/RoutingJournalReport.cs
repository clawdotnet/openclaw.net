using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.LayaService.Evaluation;

namespace OpenClaw.LayaService.Reporting;

public sealed record CalibrationCohort(
    string Provider,
    string? Model,
    string? RubricVersion,
    string? Checkpoint,
    string? Revision,
    string? CalibrationId,
    string? SchemaHash,
    JsonArray Identity,
    CalibrationMetricsResult Metrics);

public sealed record RoutingReport(JsonObject Document, IReadOnlyList<CalibrationCohort> CalibrationCohorts);

public static class RoutingJournalReport
{
    private static readonly string[] Tiers = ["T0", "T1", "T2", "T3"];

    public static IReadOnlyList<JsonElement> ReadJsonLines(string path)
    {
        var rows = new List<JsonElement>();
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                RejectDuplicateProperties(document.RootElement);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{path}:{lineNumber}: expected an object");
                rows.Add(document.RootElement.Clone());
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: invalid JSON", exception);
            }
        }
        return rows;
    }

    public static RoutingReport Summarize(IReadOnlyList<JsonElement> rows, IReadOnlyList<JsonElement>? labelRows = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidDataException("The journal contains no decisions.");
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var proposed = new List<JsonElement>();
        var completed = new List<JsonElement>();
        var latencies = new List<double>(rows.Count);
        var reportedTokens = 0.0;
        var reportedCost = 0.0;
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each decision must be an object.");
            var id = RequiredString(row, "decision_id");
            if (id.Length == 0 || !byId.TryAdd(id, row)) throw new InvalidDataException("Every decision must have a unique nonempty decision_id.");
            if (!Tiers.Contains(StringValue(row, "baseline_tier"), StringComparer.Ordinal) ||
                !Tiers.Contains(StringValue(row, "applied_tier"), StringComparer.Ordinal))
                throw new InvalidDataException($"Invalid baseline/applied tier for {id}.");
            var proposedTier = OptionalString(row, "proposed_tier");
            if (proposedTier is not null && !Tiers.Contains(proposedTier, StringComparer.Ordinal))
                throw new InvalidDataException($"Invalid proposed tier for {id}.");
            if (proposedTier is not null) proposed.Add(row);

            var latency = RequiredFiniteNumber(row, "latency_ms");
            if (latency < 0) throw new InvalidDataException($"Invalid latency for {id}.");
            latencies.Add(latency);
            if (row.TryGetProperty("input_tokens", out var tokens) && tokens.ValueKind != JsonValueKind.Null)
            {
                var tokenCount = Number(tokens, $"Invalid input token count for {id}.");
                if (tokenCount < 0) throw new InvalidDataException($"Invalid input token count for {id}.");
                completed.Add(row);
                reportedTokens += tokenCount;
            }
            if (row.TryGetProperty("estimated_cost_usd", out var cost) && cost.ValueKind != JsonValueKind.Null)
            {
                var amount = Number(cost, $"Invalid cost for {id}.");
                if (amount < 0) throw new InvalidDataException($"Invalid cost for {id}.");
                reportedCost += amount;
            }
        }

        var labels = new Dictionary<string, (string ExpectedTier, bool HighRisk)>(StringComparer.Ordinal);
        foreach (var label in labelRows ?? Array.Empty<JsonElement>())
        {
            var id = RequiredString(label, "decision_id");
            var expected = RequiredString(label, "expected_tier");
            if (!byId.ContainsKey(id) || !Tiers.Contains(expected, StringComparer.Ordinal) || labels.ContainsKey(id))
                throw new InvalidDataException("Labels must reference unique journal decision IDs and expected_tier T0 through T3.");
            var highRisk = false;
            if (label.TryGetProperty("high_risk", out var highRiskElement))
            {
                if (highRiskElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("high_risk must be a JSON boolean.");
                highRisk = highRiskElement.GetBoolean();
            }
            labels.Add(id, (expected, highRisk));
        }

        var cohorts = BuildCalibrationCohorts(rows, labels);
        var document = new JsonObject
        {
            ["decisions"] = rows.Count,
            ["responses_with_usage"] = completed.Count,
            ["eligible_proposals"] = proposed.Count,
            ["proposal_coverage"] = proposed.Count / (double)rows.Count,
            ["modes"] = CountBy(rows, "mode", "unknown"),
            ["providers"] = CountBy(rows, "provider", "jev"),
            ["models"] = CountBy(rows, "model", "unreported"),
            ["rubric_versions"] = CountBy(rows, "rubric_version", "unknown"),
            ["reasons"] = CountBy(rows, "reason", "unknown"),
            ["proposed_tiers"] = CountValues(proposed.Select(row => RequiredString(row, "proposed_tier"))),
            ["proposal_disagreement_with_baseline"] = proposed.Count == 0 ? null : proposed.Count(row => RequiredString(row, "proposed_tier") != RequiredString(row, "baseline_tier")) / (double)proposed.Count,
            ["added_latency_ms"] = new JsonObject
            {
                ["p50"] = Percentile(latencies, 0.50),
                ["p95"] = Percentile(latencies, 0.95),
                ["max"] = latencies.Max()
            },
            ["reported_input_tokens"] = reportedTokens,
            ["estimated_reported_decision_cost_usd"] = Math.Round(reportedCost, 8),
            ["quality"] = labels.Count == 0 ? null : BuildQuality(
                rows.Where(row => labels.ContainsKey(RequiredString(row, "decision_id"))).ToArray(), labels, rows.Count),
            ["limitations"] = new JsonArray(
                "Decision cost excludes failed calls without usage, downstream models, retries, and cache effects.",
                "Tier labels do not measure task success or establish calibrated confidence.",
                "With ONNX disabled, baseline tier T2 is a bookkeeping default; the actual configured model is unchanged.",
                "The proposal includes confidence gates and safety floors; missing proposals fall back to the baseline.",
                "Compare model/rubric cohorts separately before tuning thresholds."),
            ["calibration_quality"] = BuildCalibrationJson(cohorts)
        };

        var quality = document["quality"] as JsonObject;
        if (quality is not null && rows.Any(row => OptionalString(row, "provider") == "laya"))
        {
            quality["decision_with_fallback"] = quality["jev_with_fallback"]!.DeepClone();
            quality.Remove("jev_with_fallback");
        }
        return new RoutingReport(document, cohorts);
    }

    private static JsonObject? BuildQuality(
        IReadOnlyList<JsonElement> rows,
        IReadOnlyDictionary<string, (string ExpectedTier, bool HighRisk)> labels,
        int totalDecisions)
    {
        var byPolicy = new JsonObject
        {
            ["baseline"] = MeasureQuality(rows, labels, row => RequiredString(row, "baseline_tier")),
            ["always_t2"] = MeasureQuality(rows, labels, _ => "T2"),
            ["jev_with_fallback"] = MeasureQuality(rows, labels, row => OptionalString(row, "proposed_tier") ?? RequiredString(row, "baseline_tier"))
        };
        byPolicy["labeled_decisions"] = rows.Count;
        byPolicy["label_coverage"] = rows.Count / (double)totalDecisions;
        return byPolicy;
    }

    private static JsonObject MeasureQuality(
        IReadOnlyList<JsonElement> rows,
        IReadOnlyDictionary<string, (string ExpectedTier, bool HighRisk)> labels,
        Func<JsonElement, string> predict)
    {
        var confusion = Tiers.ToDictionary(tier => tier,
            _ => Tiers.ToDictionary(tier => tier, _ => 0, StringComparer.Ordinal), StringComparer.Ordinal);
        var correct = 0;
        var under = 0;
        var over = 0;
        var highRiskCount = 0;
        var highRiskRetained = 0;
        foreach (var row in rows)
        {
            var label = labels[RequiredString(row, "decision_id")];
            var predicted = predict(row);
            confusion[label.ExpectedTier][predicted]++;
            var expectedIndex = Array.IndexOf(Tiers, label.ExpectedTier);
            var predictedIndex = Array.IndexOf(Tiers, predicted);
            correct += predictedIndex == expectedIndex ? 1 : 0;
            under += predictedIndex < expectedIndex ? 1 : 0;
            over += predictedIndex > expectedIndex ? 1 : 0;
            if (label.HighRisk)
            {
                highRiskCount++;
                if (predictedIndex >= Math.Max(2, expectedIndex)) highRiskRetained++;
            }
        }

        var perTier = new JsonObject();
        var f1Total = 0.0;
        foreach (var tier in Tiers)
        {
            var truePositive = confusion[tier][tier];
            var falsePositive = Tiers.Where(other => other != tier).Sum(other => confusion[other][tier]);
            var falseNegative = Tiers.Where(other => other != tier).Sum(other => confusion[tier][other]);
            var support = confusion[tier].Values.Sum();
            var f1 = 2 * truePositive + falsePositive + falseNegative == 0
                ? 0
                : 2 * truePositive / (double)(2 * truePositive + falsePositive + falseNegative);
            f1Total += f1;
            perTier[tier] = new JsonObject { ["support"] = support, ["f1"] = f1 };
        }

        var confusionJson = new JsonObject();
        foreach (var tier in Tiers) confusionJson[tier] = CountValues(Tiers.Select(predicted => (predicted, confusion[tier][predicted])));
        return new JsonObject
        {
            ["samples"] = rows.Count,
            ["accuracy"] = correct / (double)rows.Count,
            ["under_routing_rate"] = under / (double)rows.Count,
            ["over_routing_rate"] = over / (double)rows.Count,
            ["high_risk_samples"] = highRiskCount,
            ["high_risk_capability_retention"] = highRiskCount == 0 ? null : highRiskRetained / (double)highRiskCount,
            ["macro_f1"] = f1Total / Tiers.Length,
            ["per_tier"] = perTier,
            ["confusion"] = confusionJson
        };
    }

    private static IReadOnlyList<CalibrationCohort> BuildCalibrationCohorts(
        IReadOnlyList<JsonElement> rows,
        IReadOnlyDictionary<string, (string ExpectedTier, bool HighRisk)> labels)
    {
        var groups = new Dictionary<string, (JsonArray Identity, List<LabeledPrediction> Rows)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = RequiredString(row, "decision_id");
            if (!labels.TryGetValue(id, out var label) || !row.TryGetProperty("probabilities", out var probabilities) ||
                probabilities.ValueKind != JsonValueKind.Object || probabilities.GetPropertyCount() == 0) continue;
            var metadata = row.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement
                : default;
            var provider = OptionalString(row, "provider") ?? "jev";
            var model = OptionalString(row, "model");
            var rubric = OptionalString(row, "rubric_version");
            var checkpoint = OptionalString(metadata, "checkpoint");
            var revision = OptionalString(metadata, "revision");
            var calibrationId = OptionalString(metadata, "calibration_id");
            var schemaHash = OptionalString(metadata, "schema_hash");
            var identity = new JsonArray(provider, model, rubric, checkpoint, revision, calibrationId, schemaHash);
            var identityKey = identity.ToJsonString();
            if (!groups.TryGetValue(identityKey, out var group))
            {
                group = (identity, []);
                groups.Add(identityKey, group);
            }
            var keys = probabilities.EnumerateObject().Select(property => property.Name).ToArray();
            var values = probabilities.EnumerateObject().Select(property => Number(property.Value, "Invalid probability distribution.")).ToArray();
            group.Rows.Add(new LabeledPrediction("choice", keys, values, label.ExpectedTier));
        }

        return groups.Select(pair =>
        {
            var identity = pair.Value.Identity;
            var values = identity.Select(node => node?.GetValue<string>()).ToArray();
            return new CalibrationCohort(values[0]!, values[1], values[2], values[3], values[4], values[5], values[6],
                identity, CalibrationMetrics.Measure(pair.Value.Rows));
        }).ToArray();
    }

    private static JsonArray BuildCalibrationJson(IReadOnlyList<CalibrationCohort> cohorts)
    {
        var output = new JsonArray();
        foreach (var cohort in cohorts)
        {
            var metrics = JsonSerializerNode(cohort.Metrics);
            metrics["cohort"] = cohort.Identity.DeepClone();
            output.Add(metrics);
        }
        return output;
    }

    private static JsonObject JsonSerializerNode<T>(T value)
        => JsonSerializer.SerializeToNode(value)!.AsObject();

    private static JsonObject CountBy(IEnumerable<JsonElement> rows, string property, string fallback)
        => CountValues(rows.Select(row => OptionalString(row, property) ?? fallback));

    private static JsonObject CountValues(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values) counts[value] = counts.GetValueOrDefault(value) + 1;
        var output = new JsonObject();
        foreach (var pair in counts) output[pair.Key] = pair.Value;
        return output;
    }

    private static JsonObject CountValues(IEnumerable<(string Key, int Count)> values)
    {
        var output = new JsonObject();
        foreach (var (key, count) in values) output[key] = count;
        return output;
    }

    private static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Missing or invalid {name}.");
        return property.GetString()!;
    }

    private static string StringValue(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : string.Empty;

    private static string? OptionalString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double RequiredFiniteNumber(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) throw new InvalidDataException($"Missing or invalid {name}.");
        return Number(property, $"Invalid {name}.");
    }

    private static double Number(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException(error);
        return number;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
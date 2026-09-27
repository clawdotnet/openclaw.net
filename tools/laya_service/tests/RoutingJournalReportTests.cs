using System.Text.Json;
using OpenClaw.LayaService.Reporting;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class RoutingJournalReportTests
{
    [Fact]
    public void Summarize_MeasuresJevFallbackAgainstLabeledDecisions()
    {
        var rows = new[]
        {
            Element("""{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","proposed_tier":"T0","latency_ms":100,"input_tokens":2000,"estimated_cost_usd":0.000084}"""),
            Element("""{"decision_id":"b","baseline_tier":"T3","applied_tier":"T3","reason":"timeout","latency_ms":1500}""")
        };
        var labels = new[]
        {
            Element("""{"decision_id":"a","expected_tier":"T0"}"""),
            Element("""{"decision_id":"b","expected_tier":"T3","high_risk":true}""")
        };

        var report = RoutingJournalReport.Summarize(rows, labels).Document;
        var quality = report["quality"]!;

        Assert.Equal(1, quality["label_coverage"]!.GetValue<double>());
        Assert.Equal(1, quality["jev_with_fallback"]!["accuracy"]!.GetValue<double>());
        Assert.Equal(0.5, quality["baseline"]!["accuracy"]!.GetValue<double>());
        Assert.Equal(0.5, quality["always_t2"]!["under_routing_rate"]!.GetValue<double>());
        Assert.Equal(1, quality["jev_with_fallback"]!["high_risk_capability_retention"]!.GetValue<double>());
        Assert.Equal(0.000084, report["estimated_reported_decision_cost_usd"]!.GetValue<double>());
        Assert.Equal(1500, report["added_latency_ms"]!["p95"]!.GetValue<double>());
    }

    [Fact]
    public void Summarize_SplitsLayaCalibrationByCheckpointAndCountsAbstention()
    {
        var rows = new[]
        {
            LayaRow("0", "english"),
            LayaRow("1", "multilingual")
        };
        var labels = new[] { Element("""{"decision_id":"0","expected_tier":"T0"}"""), Element("""{"decision_id":"1","expected_tier":"T0"}""") };

        var report = RoutingJournalReport.Summarize(rows, labels).Document;

        Assert.Equal(2, report["calibration_quality"]!.AsArray().Count);
        Assert.Equal(0, report["calibration_quality"]![0]!["accuracy"]!.GetValue<double>());
        Assert.NotNull(report["quality"]!["decision_with_fallback"]);
        Assert.Null(report["quality"]!["jev_with_fallback"]);
    }

    [Fact]
    public void Summarize_UnlabeledDataDoesNotClaimAccuracy()
    {
        var row = Element("""{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","latency_ms":0}""");

        var report = RoutingJournalReport.Summarize([row]).Document;

        Assert.Null(report["quality"]);
        Assert.Null(report["proposal_disagreement_with_baseline"]);
        Assert.Empty(report["calibration_quality"]!.AsArray());
    }

    [Fact]
    public void Summarize_RejectsEmptyDuplicateAndUnmatchedData()
    {
        var row = Element("""{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","latency_ms":1}""");
        Assert.Throws<InvalidDataException>(() => RoutingJournalReport.Summarize([]));
        Assert.Throws<InvalidDataException>(() => RoutingJournalReport.Summarize([row, row]));
        Assert.Throws<InvalidDataException>(() => RoutingJournalReport.Summarize([row], [Element("""{"decision_id":"unknown","expected_tier":"T0"}""")]));
    }

    [Fact]
    public async Task ReadJsonLines_ReportsPathAndLineForMalformedRows()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl");
        await File.WriteAllTextAsync(path, "{}\nnot-json\n");
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => RoutingJournalReport.ReadJsonLines(path));
            Assert.Contains(path + ":2: invalid JSON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonElement LayaRow(string id, string checkpoint)
        => Element(JsonSerializer.Serialize(new
        {
            decision_id = id,
            baseline_tier = "T2",
            applied_tier = "T2",
            latency_ms = 1,
            provider = "laya",
            model = "laya@revision",
            rubric_version = "v1",
            metadata = new { checkpoint, calibration_id = "raw" },
            probabilities = new Dictionary<string, double> { ["T0"] = 0.1, ["T1"] = 0.1, ["T2"] = 0.1, ["T3"] = 0.1, ["abstain"] = 0.6 }
        }));

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
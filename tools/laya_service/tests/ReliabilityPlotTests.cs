using System.Text.Json;
using OpenClaw.LayaService.Reporting;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class ReliabilityPlotTests
{
    [Fact]
    public void WritePng_RequiresLabeledProbabilityCohort()
    {
        var report = RoutingJournalReport.Summarize([
            Element("""{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","latency_ms":1}""")
        ]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Assert.Throws<InvalidDataException>(() => ReliabilityPlot.WritePng(report, path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WritePng_WritesPngForCalibrationCohort()
    {
        var report = RoutingJournalReport.Summarize(
            [Element("""{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","latency_ms":1,"probabilities":{"T0":0.8,"T1":0.1,"T2":0.05,"T3":0.05}}""")],
            [Element("""{"decision_id":"a","expected_tier":"T0"}""")]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        try
        {
            ReliabilityPlot.WritePng(report, path);
            var bytes = File.ReadAllBytes(path);

            Assert.True(bytes.Length > 100);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
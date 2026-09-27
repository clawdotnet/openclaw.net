using ScottPlot;
using ScottPlot.MultiplotLayouts;

namespace OpenClaw.LayaService.Reporting;

public static class ReliabilityPlot
{
    public static void WritePng(RoutingReport report, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.CalibrationCohorts.Count == 0) throw new InvalidDataException("Reliability plots require labeled probabilities.");
        var multiplot = new Multiplot
        {
            Layout = new Grid(report.CalibrationCohorts.Count, 2)
        };
        for (var index = 0; index < report.CalibrationCohorts.Count; index++)
        {
            var cohort = report.CalibrationCohorts[index];
            var reliability = index == 0 ? multiplot.Subplots.GetPlot(0) : multiplot.AddPlot();
            reliability.Title(Title(cohort), 12);
            reliability.Axes.Bottom.Label.Text = "Mean top probability";
            reliability.Axes.Left.Label.Text = "Accuracy";
            reliability.Axes.SetLimits(0, 1, 0, 1);
            reliability.Add.Line(0, 0, 1, 1);
            var bins = cohort.Metrics.ReliabilityBins;
            reliability.Add.ScatterLine(bins.Select(bin => bin.MeanTopProbability).ToArray(), bins.Select(bin => bin.Accuracy).ToArray());

            var risk = multiplot.AddPlot();
            risk.Title(Title(cohort), 12);
            risk.Axes.Bottom.Label.Text = "Coverage";
            risk.Axes.Left.Label.Text = "Error rate";
            risk.Axes.SetLimits(0, 1, 0, 1);
            var points = cohort.Metrics.RiskCoverage.Where(point => point.ErrorRate.HasValue).ToArray();
            risk.Add.ScatterLine(points.Select(point => point.Coverage).ToArray(), points.Select(point => point.ErrorRate!.Value).ToArray());
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        multiplot.SavePng(fullPath, 1400, Math.Max(480, report.CalibrationCohorts.Count * 420));
    }

    private static string Title(CalibrationCohort cohort)
        => $"{cohort.Provider} / {cohort.Checkpoint ?? cohort.Model ?? "unknown"}\n{cohort.RubricVersion ?? "unknown rubric"} | revision {Short(cohort.Revision)} | calibration {Short(cohort.CalibrationId)}";

    private static string Short(string? value) => string.IsNullOrEmpty(value) ? "n/a" : value[..Math.Min(value.Length, 8)];
}
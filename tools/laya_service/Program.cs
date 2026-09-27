using System.Net;
using System.Globalization;
using System.Text.Json;
using OpenClaw.LayaService.Evaluation;
using OpenClaw.LayaService.Hosting;
using OpenClaw.LayaService.Inference;
using OpenClaw.LayaService.Models;
using OpenClaw.LayaService.Reporting;

namespace OpenClaw.LayaService;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CommandInvocation? invocation = null;
        try
        {
            invocation = CommandLine.Parse(args);
            if (invocation.Command == "download")
            {
                var destination = invocation.Options.Get("destination")
                    ?? throw new ArgumentException("A destination is required.");
                var revision = invocation.Options.Get("revision") ?? ModelManifest.DefaultRevision;
                var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true };
                using var http = new HttpClient(handler, disposeHandler: true);
                var manifestPath = await HuggingFaceDownloader.DownloadAsync(
                    new DownloadOptions(destination, revision, invocation.Options.GetMany("checkpoint")), http, CancellationToken.None);
                Console.WriteLine(manifestPath);
                return 0;
            }

            if (invocation.Command == "serve")
            {
                var manifestPath = invocation.Options.Get("manifest")
                    ?? throw new ArgumentException("A manifest is required.");
                var manifest = ModelManifest.LoadAndVerify(manifestPath);
                var options = new ServeOptions(
                    manifestPath,
                    invocation.Options.Get("calibration"),
                    ParseIntOption(invocation.Options.Get("port"), 8099, 1, 65535),
                    invocation.Options.Get("device") ?? "cpu",
                    invocation.Options.Get("checkpoint") ?? "auto",
                    ParseIntOption(invocation.Options.Get("threads"), 4, 1, 256));
                await using var predictor = await NLayaDecisionPredictor.LoadAsync(manifest, options, CancellationToken.None);
                await using var app = DecisionServer.Build(predictor,
                    new ServiceOptions(options.Port, 16, 65536, TimeSpan.FromSeconds(60)));
                await app.RunAsync();
                return 0;
            }

            if (invocation.Command == "evaluate")
            {
                if (invocation.Arguments.Count != 1) throw new ArgumentException("An evaluation dataset is required.");
                var output = invocation.Options.Get("output") ?? throw new ArgumentException("An output path is required.");
                var endpoint = new Uri(invocation.Options.Get("endpoint") ?? "http://127.0.0.1:8099/v1/decisions", UriKind.Absolute);
                var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                using var http = new HttpClient(handler, disposeHandler: true);
                http.Timeout = TimeSpan.FromSeconds(30);
                var count = await CaseEvaluator.EvaluateAsync(invocation.Arguments[0], endpoint, output, http, CancellationToken.None);
                Console.WriteLine(count.ToString(CultureInfo.InvariantCulture));
                return 0;
            }

            if (invocation.Command == "calibrate")
            {
                var fitPath = invocation.Options.Get("fit") ?? throw new ArgumentException("A training observations path is required.");
                var validationPath = invocation.Options.Get("validate") ?? throw new ArgumentException("A validation observations path is required.");
                var outputPath = invocation.Options.Get("output") ?? throw new ArgumentException("An output path is required.");
                var training = await CalibrationFitter.ReadObservationsAsync(fitPath, CancellationToken.None);
                var validation = await CalibrationFitter.ReadObservationsAsync(validationPath, CancellationToken.None);
                var artifact = CalibrationFitter.Fit(training, validation);
                Console.WriteLine(await CalibrationFitter.WriteAsync(artifact, outputPath, CancellationToken.None));
                return 0;
            }

            if (invocation.Command == "report")
            {
                if (invocation.Arguments.Count != 1) throw new ArgumentException("A routing journal path is required.");
                var rows = RoutingJournalReport.ReadJsonLines(invocation.Arguments[0]);
                var labelPath = invocation.Options.Get("labels");
                var labels = labelPath is null ? Array.Empty<System.Text.Json.JsonElement>() : RoutingJournalReport.ReadJsonLines(labelPath);
                var report = RoutingJournalReport.Summarize(rows, labels);
                var plotPath = invocation.Options.Get("plot");
                if (plotPath is not null) ReliabilityPlot.WritePng(report, plotPath);
                var rendered = report.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
                var outputPath = invocation.Options.Get("output");
                if (outputPath is null)
                {
                    Console.Write(rendered);
                }
                else
                {
                    await WriteAtomicTextAsync(outputPath, rendered, CancellationToken.None);
                }
                return 0;
            }

            Console.Error.WriteLine($"{invocation.Command}_not_implemented");
            return 2;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("invalid_arguments");
            return 2;
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine(invocation?.Command switch
            {
                "evaluate" => "invalid_evaluation_data",
                "calibrate" => "invalid_calibration_data",
                "report" => "invalid_report_data",
                _ => "invalid_model_assets"
            });
            return 2;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(invocation?.Command == "evaluate" ? "evaluation_failed" : "download_failed");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("operation_cancelled");
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("command_failed");
            return 1;
        }
    }

    private static int ParseIntOption(string? value, int defaultValue, int minimum, int maximum)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
        {
            throw new ArgumentException("Invalid numeric option.");
        }
        return result;
    }

    private static async Task WriteAtomicTextAsync(string outputPath, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Invalid output path.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new System.Text.UTF8Encoding(false), cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
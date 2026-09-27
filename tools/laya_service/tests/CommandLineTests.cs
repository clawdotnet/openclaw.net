using OpenClaw.LayaService;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("serve --manifest models.json", "serve")]
    [InlineData("download --destination models", "download")]
    [InlineData("evaluate cases.jsonl --output observations.jsonl", "evaluate")]
    [InlineData("calibrate --fit train.jsonl --validate holdout.jsonl --output calibration.json", "calibrate")]
    [InlineData("report journal.jsonl", "report")]
    public void Parse_AcceptsAllCommands(string args, string expectedCommand)
    {
        var parsed = CommandLine.Parse(args.Split(' '));

        Assert.Equal(expectedCommand, parsed.Command);
    }

    [Fact]
    public void Parse_DownloadPreservesRepeatedCheckpoints()
    {
        var parsed = CommandLine.Parse(["download", "--destination", "models", "--checkpoint", "english", "--checkpoint", "multilingual"]);

        Assert.Equal("download", parsed.Command);
        Assert.Equal(new[] { "english", "multilingual" }, parsed.Options.GetMany("checkpoint"));
    }

    [Fact]
    public void Parse_PreservesPositionalArguments()
    {
        var parsed = CommandLine.Parse(["evaluate", "cases.jsonl", "--output", "observations.jsonl"]);

        Assert.Equal(new[] { "cases.jsonl" }, parsed.Arguments);
    }

    [Fact]
    public async Task Download_InvalidRevisionFailsBeforeAnyNetworkRequest()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var exitCode = await Program.Main(["download", "--destination", directoryPath,
                "--revision", "latest", "--checkpoint", "english"]);

            Assert.Equal(2, exitCode);
            Assert.Equal("invalid_arguments", error.ToString().Trim());
            Assert.Empty(Directory.EnumerateFileSystemEntries(directoryPath));
        }
        finally
        {
            Console.SetError(previousError);
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("multilingual", "checkpoint_not_installed")]
    [InlineData("unknown", "unknown_checkpoint")]
    public async Task Serve_ReportsCheckpointStartupFailure(string checkpoint, string expectedReason)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var manifestPath = ModelManifestTests.WriteManifest(directoryPath, new string('a', 40), ["english"]);
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var exitCode = await Program.Main(["serve", "--manifest", manifestPath, "--checkpoint", checkpoint]);

            Assert.Equal(2, exitCode);
            Assert.Equal(expectedReason, error.ToString().Trim());
        }
        finally
        {
            Console.SetError(previousError);
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task Calibrate_FitsObservationFilesAndWritesV2Artifact()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var trainingPath = Path.Combine(directoryPath, "training.jsonl");
        var validationPath = Path.Combine(directoryPath, "validation.jsonl");
        var outputPath = Path.Combine(directoryPath, "calibration.json");
        await File.WriteAllLinesAsync(trainingPath, BuildObservations("train", 20));
        await File.WriteAllLinesAsync(validationPath, BuildObservations("validation", 20));
        var previousOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = await Program.Main(["calibrate", "--fit", trainingPath, "--validate", validationPath, "--output", outputPath]);

            Assert.Equal(0, exitCode);
            using var artifact = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(2, artifact.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("NLaya", artifact.RootElement.GetProperty("runtime").GetString());
            var payload = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), output.ToString().Trim());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task Report_WritesSummaryJsonAndReliabilityPlot()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var journalPath = Path.Combine(directoryPath, "journal.jsonl");
        var labelsPath = Path.Combine(directoryPath, "labels.jsonl");
        var outputPath = Path.Combine(directoryPath, "report.json");
        var plotPath = Path.Combine(directoryPath, "report.png");
        await File.WriteAllTextAsync(journalPath,
            """{"decision_id":"a","baseline_tier":"T2","applied_tier":"T2","proposed_tier":"T0","latency_ms":10,"input_tokens":100,"estimated_cost_usd":0.000001,"probabilities":{"T0":0.8,"T1":0.1,"T2":0.05,"T3":0.05}}""" + "\n");
        await File.WriteAllTextAsync(labelsPath, """{"decision_id":"a","expected_tier":"T0"}""" + "\n");
        try
        {
            var exitCode = await Program.Main(["report", journalPath, "--labels", labelsPath, "--output", outputPath, "--plot", plotPath]);

            Assert.Equal(0, exitCode);
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(1, report.RootElement.GetProperty("decisions").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("quality").GetProperty("jev_with_fallback").GetProperty("accuracy").GetDouble());
            Assert.True(new FileInfo(plotPath).Length > 100);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static string[] BuildObservations(string prefix, int count)
    {
        var rows = new string[count];
        for (var index = 0; index < count; index++)
        {
            var id = prefix + "-" + index;
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
            var answer = index % 2 == 0
                ? "{\"type\":\"choice\",\"choice\":\"a\",\"probabilities\":{\"a\":0.7,\"b\":0.3}}"
                : "{\"type\":\"choice\",\"choice\":\"b\",\"probabilities\":{\"a\":0.3,\"b\":0.7}}";
            rows[index] = JsonSerializer.Serialize(new
            {
                case_id = id,
                case_fingerprint = fingerprint,
                question_id = "tier",
                model = "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982",
                checkpoint = "english",
                schema_hash = new string('a', 64),
                sdk_version = "1.0.0",
                runtime = "NLaya",
                source_calibration = "raw",
                answer = JsonDocument.Parse(answer).RootElement,
                label = index % 2 == 0 ? "a" : "b"
            });
        }
        return rows;
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("download --unknown value")]
    [InlineData("download --destination")]
    public void Parse_RejectsUnknownCommandsAndMalformedOptions(string args)
        => Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split(' ')));
}

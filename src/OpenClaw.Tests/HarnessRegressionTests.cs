using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Models;
using OpenClaw.Testing;
using Xunit;
using CoreGatewayConfigFile = OpenClaw.Core.Setup.GatewayConfigFile;

namespace OpenClaw.Tests;

public sealed class HarnessRegressionTests
{
    [Fact]
    public async Task Runner_ExecutesScenarios()
    {
        var scenario = new StubScenario("harness.stub", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Passed);
        var report = await new HarnessRegressionRunner([scenario]).RunAsync();

        Assert.Single(report.Results);
        Assert.Equal("harness.stub", report.Results[0].Id);
        Assert.Equal(1, scenario.RunCount);
    }

    [Fact]
    public async Task PassingScenarios_ProducePassedStatus()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.pass", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Passed)
        ]).RunAsync();

        Assert.Equal(HarnessRegressionScenarioStatus.Passed, report.OverallStatus);
        Assert.Equal(1, report.Summary.Passed);
        Assert.Equal(0, HarnessRegressionRunner.GetExitCode(report));
    }

    [Fact]
    public async Task FailingScenario_CausesNonZeroExit()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.fail", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Failed)
        ]).RunAsync();

        Assert.Equal(HarnessRegressionScenarioStatus.Failed, report.OverallStatus);
        Assert.Equal(1, HarnessRegressionRunner.GetExitCode(report));
    }

    [Fact]
    public async Task SkippedScenario_DoesNotFailByDefault()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.skip", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Skipped)
        ]).RunAsync();

        Assert.Equal(1, report.Summary.Skipped);
        Assert.Equal(0, HarnessRegressionRunner.GetExitCode(report));
    }

    [Fact]
    public async Task StrictMode_TreatsRequiredWarningAsFailure()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.warn", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Warning)
        ]).RunAsync(new HarnessRegressionOptions { Strict = true });

        Assert.Equal(HarnessRegressionScenarioStatus.Failed, report.OverallStatus);
        Assert.Equal(1, HarnessRegressionRunner.GetExitCode(report));
    }

    [Fact]
    public async Task Runner_NormalizesScenarioStatusBeforeFormatting()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.upper", HarnessRegressionCategory.Harness, "FAILED")
        ]).RunAsync();

        Assert.Equal(HarnessRegressionScenarioStatus.Failed, report.Results[0].Status);
        Assert.Contains("FAIL harness.upper", HarnessRegressionReportFormatter.ToText(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_ClampsNegativeScenarioDuration()
    {
        var report = await new HarnessRegressionRunner([
            new NegativeDurationScenario()
        ]).RunAsync();

        Assert.True(report.Results[0].DurationMs >= 0);
    }

    [Fact]
    public async Task Runner_CleansTempWorkspaceWhenCancelled()
    {
        var scenario = new CancellingScenario();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new HarnessRegressionRunner([scenario]).RunAsync());

        Assert.NotNull(scenario.TempWorkspacePath);
        Assert.False(Directory.Exists(scenario.TempWorkspacePath));
    }

    [Fact]
    public async Task CategoryFilter_SelectsMatchingScenarios()
    {
        var security = new StubScenario("security.pass", HarnessRegressionCategory.Security, HarnessRegressionScenarioStatus.Passed);
        var memory = new StubScenario("memory.pass", HarnessRegressionCategory.Memory, HarnessRegressionScenarioStatus.Passed);

        var report = await new HarnessRegressionRunner([security, memory]).RunAsync(
            new HarnessRegressionOptions { Category = HarnessRegressionCategory.Security });

        Assert.Single(report.Results);
        Assert.Equal("security.pass", report.Results[0].Id);
        Assert.Equal(1, security.RunCount);
        Assert.Equal(0, memory.RunCount);
    }

    [Fact]
    public async Task JsonOutput_SerializesReport()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.json", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Passed)
        ]).RunAsync();

        var json = HarnessRegressionReportFormatter.ToJson(report);
        var restored = JsonSerializer.Deserialize(json, HarnessRegressionJsonContext.Default.HarnessRegressionReport);

        Assert.NotNull(restored);
        Assert.Equal(report.Id, restored!.Id);
        Assert.Single(restored.Results);
    }

    [Fact]
    public async Task TextOutput_ContainsSummary()
    {
        var report = await new HarnessRegressionRunner([
            new StubScenario("harness.text", HarnessRegressionCategory.Harness, HarnessRegressionScenarioStatus.Passed)
        ]).RunAsync();

        var text = HarnessRegressionReportFormatter.ToText(report);

        Assert.Contains("OpenClaw Harness Regression", text, StringComparison.Ordinal);
        Assert.Contains("PASS harness.text", text, StringComparison.Ordinal);
        Assert.Contains("Summary:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineMode_SkipsProviderCredentialAndNetworkChecks()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Join(root, "openclaw.settings.json");
            await CoreGatewayConfigFile.SaveAsync(new GatewayConfig
            {
                Llm =
                {
                    Provider = "openai",
                    Model = "gpt-4o",
                    ApiKey = null
                }
            }, configPath);

            var report = await new HarnessRegressionRunner().RunAsync(new HarnessRegressionOptions
            {
                ConfigPath = configPath,
                Category = HarnessRegressionCategory.Providers,
                Offline = true
            });

            var result = Assert.Single(report.Results);
            Assert.Equal("providers.config_shape", result.Id);
            Assert.Equal(HarnessRegressionScenarioStatus.Passed, result.Status);
            Assert.Contains("Credential and network checks were skipped", result.Details ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MemoryStoreRoundTripScenario_WorksWithTempPath()
    {
        var report = await new HarnessRegressionRunner().RunAsync(new HarnessRegressionOptions
        {
            Category = HarnessRegressionCategory.Memory
        });

        var result = Assert.Single(report.Results);
        Assert.Equal("memory.store_round_trip", result.Id);
        Assert.Equal(HarnessRegressionScenarioStatus.Passed, result.Status);
    }

    [Fact]
    public async Task HarnessModelSerializationScenarios_Run()
    {
        var report = await new HarnessRegressionRunner().RunAsync(new HarnessRegressionOptions
        {
            Category = HarnessRegressionCategory.Harness
        });

        Assert.Contains(report.Results, result => result.Id == "harness.contract_serialization" && result.Status == HarnessRegressionScenarioStatus.Passed);
        Assert.Contains(report.Results, result => result.Id == "harness.evidence_bundle_serialization" && result.Status == HarnessRegressionScenarioStatus.Passed);
        Assert.Contains(report.Results, result => result.Id == "harness.governance_ledger_serialization" && result.Status == HarnessRegressionScenarioStatus.Passed);
    }

    [Fact]
    public async Task HarnessCommand_RunsTextOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await HarnessCommands.RunAsync(["test", "--category", "memory"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("OpenClaw Harness Regression", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Summary:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommand_RunsJsonOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await HarnessCommands.RunAsync(["test", "--category", "memory", "--json"], output, error);
        var restored = JsonSerializer.Deserialize(output.ToString(), HarnessRegressionJsonContext.Default.HarnessRegressionReport);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(restored);
        Assert.Equal("memory.store_round_trip", Assert.Single(restored!.Results).Id);
    }

    [Fact]
    public async Task HarnessCommand_StrictReturnsNonZeroForSkippedRequiredScenario()
    {
        var root = CreateTempRoot();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await HarnessCommands.RunAsync(
                ["test", "--config", Path.Join(root, "missing.json"), "--category", "security", "--strict"],
                output,
                error);

            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("SKIP security.public_bind_hardening", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegressionAlias_RunsHarnessTest()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await HarnessCommands.RunRegressionAliasAsync(["test", "--category", "sessions"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("sessions.store_round_trip", output.ToString(), StringComparison.Ordinal);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Join(Path.GetTempPath(), $"openclaw-harness-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CancellingScenario : IHarnessRegressionScenario
    {
        public string Id => "harness.cancel";
        public string Name => Id;
        public string Category => HarnessRegressionCategory.Harness;
        public bool Required => true;
        public string? TempWorkspacePath { get; private set; }

        public ValueTask<HarnessRegressionScenarioResult> RunAsync(
            HarnessRegressionContext context,
            CancellationToken cancellationToken = default)
        {
            TempWorkspacePath = context.TempWorkspacePath;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class NegativeDurationScenario : IHarnessRegressionScenario
    {
        public string Id => "harness.negative_duration";
        public string Name => Id;
        public string Category => HarnessRegressionCategory.Harness;
        public bool Required => true;

        public ValueTask<HarnessRegressionScenarioResult> RunAsync(
            HarnessRegressionContext context,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new HarnessRegressionScenarioResult
            {
                Id = Id,
                Name = Name,
                Category = Category,
                Status = HarnessRegressionScenarioStatus.Passed,
                Summary = "negative duration",
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = -1
            });
        }
    }

    private sealed class StubScenario(
        string id,
        string category,
        string status,
        bool required = true) : IHarnessRegressionScenario
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Category { get; } = category;
        public bool Required { get; } = required;
        public int RunCount { get; private set; }

        public ValueTask<HarnessRegressionScenarioResult> RunAsync(
            HarnessRegressionContext context,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new HarnessRegressionScenarioResult
            {
                Id = Id,
                Name = Name,
                Category = Category,
                Status = status,
                Required = Required,
                Summary = $"{status} summary",
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }
}

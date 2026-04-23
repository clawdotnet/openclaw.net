using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AutomationRunStoreTests
{
    public static TheoryData<string> StoreKinds => new()
    {
        "file",
        "sqlite"
    };

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task SaveAndReadRunStateAndHistory_RoundTrips(string storeKind)
    {
        using var storeHarness = CreateStore(storeKind);
        var store = storeHarness.Store;

        await store.SaveRunStateAsync(new AutomationRunState
        {
            AutomationId = "auto.test",
            Outcome = "success",
            LifecycleState = AutomationLifecycleStates.Completed,
            VerificationStatus = AutomationVerificationStatuses.Verified,
            HealthState = AutomationHealthStates.Healthy,
            LastRunAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastCompletedAtUtc = DateTimeOffset.UtcNow,
            LastVerifiedSuccessAtUtc = DateTimeOffset.UtcNow,
            FailureStreak = 0,
            UnverifiedStreak = 0,
            LastRunId = "run-1",
            SessionId = "automation:auto.test",
            MessagePreview = "test prompt",
            VerificationSummary = "All verification checks passed."
        }, CancellationToken.None);

        await store.SaveRunRecordAsync(new AutomationRunRecord
        {
            RunId = "run-1",
            AutomationId = "auto.test",
            TriggerSource = AutomationRunTriggerSources.Schedule,
            LifecycleState = AutomationLifecycleStates.Completed,
            VerificationStatus = AutomationVerificationStatuses.Verified,
            SessionId = "automation:auto.test",
            VerificationSummary = "All verification checks passed.",
            VerificationChecks =
            [
                new VerificationCheckResult
                {
                    CheckId = "file-check",
                    Kind = VerificationKinds.FileExists,
                    Status = AutomationVerificationStatuses.Verified,
                    Summary = "ok"
                }
            ],
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTimeOffset.UtcNow,
            InputTokens = 12,
            OutputTokens = 34,
            MessagePreview = "test prompt"
        }, CancellationToken.None);

        var state = await store.GetRunStateAsync("auto.test", CancellationToken.None);
        var run = await store.GetRunRecordAsync("auto.test", "run-1", CancellationToken.None);
        var runs = await store.ListRunRecordsAsync("auto.test", 10, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(AutomationVerificationStatuses.Verified, state!.VerificationStatus);
        Assert.Equal("run-1", state.LastRunId);

        Assert.NotNull(run);
        Assert.Equal(AutomationRunTriggerSources.Schedule, run!.TriggerSource);
        Assert.Single(run.VerificationChecks);

        var listed = Assert.Single(runs);
        Assert.Equal("run-1", listed.RunId);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task PruneRunRecords_KeepsNewestRuns(string storeKind)
    {
        using var storeHarness = CreateStore(storeKind);
        var store = storeHarness.Store;

        for (var i = 0; i < 3; i++)
        {
            await store.SaveRunRecordAsync(new AutomationRunRecord
            {
                RunId = $"run-{i}",
                AutomationId = "auto.prune",
                TriggerSource = AutomationRunTriggerSources.Schedule,
                LifecycleState = AutomationLifecycleStates.Completed,
                VerificationStatus = AutomationVerificationStatuses.Verified,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i).AddSeconds(30)
            }, CancellationToken.None);
        }

        await store.PruneRunRecordsAsync("auto.prune", 2, CancellationToken.None);
        var runs = await store.ListRunRecordsAsync("auto.prune", 10, CancellationToken.None);

        Assert.Equal(2, runs.Count);
        Assert.DoesNotContain(runs, item => item.RunId == "run-2");
    }

    private static StoreHarness CreateStore(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-automation-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return string.Equals(kind, "sqlite", StringComparison.OrdinalIgnoreCase)
            ? new StoreHarness(root, new SqliteFeatureStore(Path.Combine(root, "openclaw.db")))
            : new StoreHarness(root, new FileFeatureStore(root));
    }

    private sealed class StoreHarness(string root, IAutomationStore store) : IDisposable
    {
        public string Root { get; } = root;
        public IAutomationStore Store { get; } = store;

        public void Dispose()
        {
            if (Store is IDisposable disposable)
                disposable.Dispose();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}

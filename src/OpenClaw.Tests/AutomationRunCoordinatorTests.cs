using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AutomationRunCoordinatorTests
{
    [Fact]
    public async Task PrepareDispatch_MarkRunning_AndFinalizeVerified_Run()
    {
        using var harness = CreateHarness();
        var verificationPath = Path.Combine(harness.Root, "verified.txt");
        var automation = new AutomationDefinition
        {
            Id = "auto.verified",
            Name = "Verified automation",
            Prompt = "Create the file.",
            Verification = new VerificationPolicy
            {
                Checks =
                [
                    new VerificationCheckDefinition
                    {
                        Id = "file",
                        Kind = VerificationKinds.FileExists,
                        Path = verificationPath
                    }
                ]
            }
        };

        var message = await harness.Coordinator.PrepareDispatchAsync(new AutomationDispatchRequest
        {
            AutomationId = automation.Id,
            TriggerSource = AutomationRunTriggerSources.Schedule,
            SessionId = "automation:auto.verified",
            ChannelId = "cron",
            SenderId = "automation:auto.verified",
            Prompt = automation.Prompt
        }, CancellationToken.None);

        Assert.NotNull(message);
        Assert.NotNull(message!.AutomationRunId);

        await harness.Coordinator.MarkRunningAsync(automation, message, CancellationToken.None);
        await File.WriteAllTextAsync(verificationPath, "ok");

        var session = new Session
        {
            Id = "automation:auto.verified",
            ChannelId = "cron",
            SenderId = "automation:auto.verified"
        };
        harness.Contracts.AttachToSession(session, new ContractPolicy
        {
            Id = "ctr_verified",
            Verification = automation.Verification
        });

        await harness.Coordinator.FinalizeRunAsync(automation, message, session, new AutomationRunCompletion
        {
            InputTokens = 10,
            OutputTokens = 20
        }, CancellationToken.None);

        var state = await harness.Store.GetRunStateAsync(automation.Id, CancellationToken.None);
        var run = await harness.Store.GetRunRecordAsync(automation.Id, message.AutomationRunId!, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(AutomationVerificationStatuses.Verified, state!.VerificationStatus);
        Assert.Equal(AutomationHealthStates.Healthy, state.HealthState);
        Assert.Equal("success", state.Outcome);
        Assert.NotNull(state.LastVerifiedSuccessAtUtc);

        Assert.NotNull(run);
        Assert.Equal(AutomationVerificationStatuses.Verified, run!.VerificationStatus);
        Assert.Single(run.VerificationChecks);
    }

    [Fact]
    public async Task FinalizeRun_AfterThreeExhaustedFailures_QuarantinesAutomation()
    {
        using var harness = CreateHarness();
        var automation = new AutomationDefinition
        {
            Id = "auto.quarantine",
            Name = "Quarantine automation",
            Prompt = "Fail",
            RetryPolicy = new AutomationRetryPolicy
            {
                Enabled = false,
                MaxRetries = 0
            }
        };

        for (var i = 0; i < 3; i++)
        {
            var message = await harness.Coordinator.PrepareDispatchAsync(new AutomationDispatchRequest
            {
                AutomationId = automation.Id,
                TriggerSource = AutomationRunTriggerSources.Schedule,
                SessionId = $"automation:auto.quarantine:{i}",
                ChannelId = "cron",
                SenderId = "automation:auto.quarantine",
                Prompt = automation.Prompt
            }, CancellationToken.None);

            Assert.NotNull(message);
            await harness.Coordinator.MarkRunningAsync(automation, message!, CancellationToken.None);
            await harness.Coordinator.FinalizeRunAsync(automation, message!, session: null, new AutomationRunCompletion
            {
                VerificationStatus = AutomationVerificationStatuses.Failed,
                VerificationSummary = $"failure {i}",
                RetryAttempt = 0
            }, CancellationToken.None);
        }

        var state = await harness.Store.GetRunStateAsync(automation.Id, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(3, state!.FailureStreak);
        Assert.Equal(AutomationHealthStates.Quarantined, state.HealthState);
        Assert.NotNull(state.QuarantinedAtUtc);
        Assert.Contains("failure", state.QuarantineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkRunStuckAsync_TransitionsRunningStateToStuck()
    {
        using var harness = CreateHarness();
        var automation = new AutomationDefinition
        {
            Id = "auto.stuck",
            Name = "Stuck automation",
            Prompt = "Wait"
        };

        await harness.Store.SaveRunStateAsync(new AutomationRunState
        {
            AutomationId = automation.Id,
            Outcome = "running",
            LifecycleState = AutomationLifecycleStates.Running,
            VerificationStatus = AutomationVerificationStatuses.NotRun,
            HealthState = AutomationHealthStates.Unknown,
            LastRunAtUtc = DateTimeOffset.UtcNow.Subtract(AutomationRunCoordinator.StuckThreshold).Subtract(TimeSpan.FromMinutes(1)),
            LastRunId = "run-stuck"
        }, CancellationToken.None);

        await harness.Store.SaveRunRecordAsync(new AutomationRunRecord
        {
            RunId = "run-stuck",
            AutomationId = automation.Id,
            TriggerSource = AutomationRunTriggerSources.Schedule,
            LifecycleState = AutomationLifecycleStates.Running,
            VerificationStatus = AutomationVerificationStatuses.NotRun,
            StartedAtUtc = DateTimeOffset.UtcNow.Subtract(AutomationRunCoordinator.StuckThreshold).Subtract(TimeSpan.FromMinutes(1))
        }, CancellationToken.None);

        var state = await harness.Store.GetRunStateAsync(automation.Id, CancellationToken.None);
        var transitioned = await harness.Coordinator.MarkRunStuckAsync(automation, state!, CancellationToken.None);
        var updated = await harness.Store.GetRunStateAsync(automation.Id, CancellationToken.None);

        Assert.True(transitioned);
        Assert.NotNull(updated);
        Assert.Equal(AutomationLifecycleStates.Stuck, updated!.LifecycleState);
        Assert.Equal(AutomationVerificationStatuses.Failed, updated.VerificationStatus);
        Assert.Equal(AutomationHealthStates.Degraded, updated.HealthState);
    }

    private static CoordinatorHarness CreateHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-run-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var startup = new GatewayStartupContext
        {
            Config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = root
                }
            },
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = false
        };
        var store = new FileFeatureStore(root);
        var contracts = new ContractGovernanceService(
            startup,
            new ContractStore(root, NullLogger<ContractStore>.Instance),
            new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
            new OpenClaw.Core.Observability.ProviderUsageTracker(),
            NullLogger<ContractGovernanceService>.Instance);
        var coordinator = new AutomationRunCoordinator(store, contracts, NullLogger<AutomationRunCoordinator>.Instance);
        return new CoordinatorHarness(root, store, contracts, coordinator);
    }

    private sealed class CoordinatorHarness(
        string root,
        FileFeatureStore store,
        ContractGovernanceService contracts,
        AutomationRunCoordinator coordinator) : IDisposable
    {
        public string Root { get; } = root;
        public FileFeatureStore Store { get; } = store;
        public ContractGovernanceService Contracts { get; } = contracts;
        public AutomationRunCoordinator Coordinator { get; } = coordinator;

        public void Dispose()
        {
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

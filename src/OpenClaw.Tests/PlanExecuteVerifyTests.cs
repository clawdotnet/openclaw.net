using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Governance;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Setup;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PlanExecuteVerifyTests
{
    [Fact]
    public void GatewayConfig_DefaultsToNormalMode()
    {
        var config = new GatewayConfig();

        Assert.Equal(HarnessExecutionModes.Normal, config.Harness.ExecutionMode);
        Assert.False(config.Harness.PlanExecuteVerify.Enabled);
        Assert.Contains(PlanExecuteVerifyContractTriggers.HighRiskTools, config.Harness.PlanExecuteVerify.ContractRequiredFor);
    }

    [Fact]
    public void GatewayConfigFile_LoadsPlanExecuteVerifyConfig()
    {
        var root = CreateTempDir();
        var path = Path.Join(root, "openclaw.json");
        File.WriteAllText(path, """
            {
              "OpenClaw": {
                "harness": {
                  "executionMode": "plan-execute-verify",
                  "planExecuteVerify": {
                    "enabled": true,
                    "contractRequiredFor": ["shell"],
                    "requireApprovalForRisk": ["critical"],
                    "createEvidenceBundles": true,
                    "runVerification": true,
                    "autoRollbackOnFailedVerification": false,
                    "maxPlanActions": 7,
                    "maxVerificationSteps": 5
                  }
                }
              }
            }
            """);

        var config = GatewayConfigFile.Load(path);

        Assert.Equal(HarnessExecutionModes.PlanExecuteVerify, config.Harness.ExecutionMode);
        Assert.True(config.Harness.PlanExecuteVerify.Enabled);
        Assert.Equal(["shell"], config.Harness.PlanExecuteVerify.ContractRequiredFor);
        Assert.Equal(["critical"], config.Harness.PlanExecuteVerify.RequireApprovalForRisk);
        Assert.Equal(7, config.Harness.PlanExecuteVerify.MaxPlanActions);
    }

    [Fact]
    public async Task PevDisabled_DoesNotCreateContractForHighRiskTool()
    {
        var root = CreateTempDir();
        var service = CreateService(root, new GatewayConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        Assert.False(decision.RequiresPlanExecuteVerify);
        Assert.Empty(service.ListRuns());
        Assert.Empty(await new FileHarnessContractStore(root).ListAsync(new HarnessContractListQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task PevEnabled_CreatesContractEvidenceAndRequiresApprovalForHighRiskAction()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());

        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        Assert.True(decision.RequiresPlanExecuteVerify);
        Assert.True(decision.RequiresApproval);
        Assert.NotNull(decision.Run);
        Assert.Equal(PlanExecuteVerifyStatus.AwaitingApproval, decision.Run!.Status);

        var contracts = await new FileHarnessContractStore(root).ListAsync(new HarnessContractListQuery(), CancellationToken.None);
        var evidence = await new FileEvidenceBundleStore(root).ListAsync(new EvidenceBundleListQuery(), CancellationToken.None);
        Assert.Single(contracts);
        Assert.Equal(HarnessContractApprovalRequirements.Required, contracts[0].ApprovalRequired);
        Assert.Single(evidence);
        Assert.Equal(contracts[0].Id, evidence[0].HarnessContractId);
    }

    [Fact]
    public async Task PevEnabled_DoesNotCreateContractForLowRiskReadTool()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());

        var decision = await service.EvaluateToolAsync(CreateContext("read_file", ToolGovernanceRiskLevel.Low, readOnly: true), CancellationToken.None);

        Assert.False(decision.RequiresPlanExecuteVerify);
        Assert.Empty(service.ListRuns());
    }

    [Fact]
    public async Task ApprovalDecision_LinksGovernanceLedgerAndEvidence()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var run = service.GetRun(decision.Run!.Id);
        var ledger = await new FileGovernanceLedgerStore(root).ListAsync(new GovernanceLedgerListQuery(), CancellationToken.None);
        var evidence = await new FileEvidenceBundleStore(root).GetAsync(run!.EvidenceBundleId!, CancellationToken.None);

        Assert.True(run.Approved);
        Assert.Equal(PlanExecuteVerifyStatus.Executing, run.Status);
        Assert.Single(ledger);
        Assert.Equal(run.HarnessContractId, ledger[0].HarnessContractId);
        Assert.NotNull(evidence);
        Assert.Contains(evidence!.Items, item => item.Kind == EvidenceItemKinds.Approval && item.Status == GovernanceDecisions.Approved);

        var contract = await new FileHarnessContractStore(root).GetAsync(run.HarnessContractId!, CancellationToken.None);
        Assert.Equal(HarnessContractStatus.Executing, contract!.Status);
        Assert.NotNull(contract.ApprovedAtUtc);
    }

    [Fact]
    public async Task CompleteTool_VerificationPassesForSuccessfulToolResult()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var completed = await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Completed), CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(PlanExecuteVerifyStatus.Verified, completed!.Status);
        Assert.Equal(HarnessVerificationStatus.Passed, completed.Verification!.Status);
    }

    [Fact]
    public async Task CompleteTool_VerificationFailureDoesNotAutoRollbackByDefault()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var completed = await service.CompleteToolAsync(
            decision.Run,
            CreateInvocation(ToolResultStatuses.Failed, ToolFailureCodes.ToolFailed),
            CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(PlanExecuteVerifyStatus.Failed, completed!.Status);
        Assert.Equal(PlanExecuteVerifyDecisionKinds.Escalate, completed.Decision);
        Assert.NotEqual(PlanExecuteVerifyStatus.RolledBack, completed.Status);
    }

    [Fact]
    public async Task RejectedApproval_PreservesRejectedRunAndContractStatus()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        await service.RecordApprovalDecisionAsync(decision.Run, approved: false, CancellationToken.None);
        var completed = await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Blocked, ToolFailureCodes.ApprovalRequired), CancellationToken.None);

        Assert.Equal(PlanExecuteVerifyStatus.Rejected, completed!.Status);
        var contract = await new FileHarnessContractStore(root).GetAsync(completed.HarnessContractId!, CancellationToken.None);
        Assert.Equal(HarnessContractStatus.Rejected, contract!.Status);
    }

    [Fact]
    public async Task ManualVerify_UpdatesRunContractAndEvidence()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);
        await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Completed), CancellationToken.None);

        var verified = await service.VerifyRunAsync(decision.Run!.Id, CancellationToken.None);

        Assert.Equal(PlanExecuteVerifyStatus.Verified, verified!.Status);
        var contract = await new FileHarnessContractStore(root).GetAsync(verified.HarnessContractId!, CancellationToken.None);
        var evidence = await new FileEvidenceBundleStore(root).GetAsync(verified.EvidenceBundleId!, CancellationToken.None);
        Assert.Equal(HarnessContractStatus.Verified, contract!.Status);
        Assert.Contains(evidence!.Checks, check => check.Kind == EvidenceItemKinds.VerificationResult);
    }

    [Fact]
    public async Task CancelRun_UpdatesContractAndEvidence()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        var cancelled = await service.CancelRunAsync(decision.Run!.Id, CancellationToken.None);

        Assert.Equal(PlanExecuteVerifyStatus.Cancelled, cancelled!.Status);
        var contract = await new FileHarnessContractStore(root).GetAsync(cancelled.HarnessContractId!, CancellationToken.None);
        var evidence = await new FileEvidenceBundleStore(root).GetAsync(cancelled.EvidenceBundleId!, CancellationToken.None);
        Assert.Equal(HarnessContractStatus.Cancelled, contract!.Status);
        Assert.Contains(evidence!.Checks, check => check.Name == "Plan-Execute-Verify cancellation");
    }

    [Fact]
    public async Task MultiToolWorkflowTrigger_CreatesContractForConfiguredMultiToolRun()
    {
        var root = CreateTempDir();
        var config = CreateEnabledConfig();
        config.Harness.PlanExecuteVerify.ContractRequiredFor = [PlanExecuteVerifyContractTriggers.MultiToolWorkflows];
        config.Harness.PlanExecuteVerify.RequireApprovalForRisk = [];
        var service = CreateService(root, config);

        var decision = await service.EvaluateToolAsync(
            CreateContext("read_file", ToolGovernanceRiskLevel.Low, readOnly: true, toolCallCount: 2),
            CancellationToken.None);

        Assert.True(decision.RequiresPlanExecuteVerify);
        Assert.False(decision.RequiresApproval);
        Assert.Equal(PlanExecuteVerifyStatus.Executing, decision.Run!.Status);
    }

    [Fact]
    public async Task MaxPlanActionsBelowOne_RejectsPevDecisionBeforeContractCreation()
    {
        var root = CreateTempDir();
        var config = CreateEnabledConfig();
        config.Harness.PlanExecuteVerify.MaxPlanActions = 0;
        var service = CreateService(root, config);

        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);

        Assert.Equal(PlanExecuteVerifyDecisionKinds.Reject, decision.Decision);
        Assert.Empty(service.ListRuns());
    }

    [Fact]
    public async Task MaxVerificationSteps_LimitsVerifierChecks()
    {
        var root = CreateTempDir();
        var config = CreateEnabledConfig();
        config.Harness.PlanExecuteVerify.MaxVerificationSteps = 1;
        var service = CreateService(root, config);
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var completed = await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Completed), CancellationToken.None);

        Assert.Contains(completed!.Verification!.Checks, check => check.Id == "verification.omitted");
        Assert.DoesNotContain(completed.Verification.Checks, check => check.Id == "approval");
    }

    [Fact]
    public async Task DisabledVerification_DoesNotMarkRunOrContractVerified()
    {
        var root = CreateTempDir();
        var config = CreateEnabledConfig();
        config.Harness.PlanExecuteVerify.RunVerification = false;
        var service = CreateService(root, config);
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var completed = await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Completed), CancellationToken.None);

        Assert.Equal(PlanExecuteVerifyStatus.Escalated, completed!.Status);
        Assert.Equal(HarnessVerificationStatus.Skipped, completed.Verification!.Status);
        var contract = await new FileHarnessContractStore(root).GetAsync(completed.HarnessContractId!, CancellationToken.None);
        Assert.Equal(HarnessContractStatus.Executing, contract!.Status);
    }

    [Fact]
    public async Task RegressionCategories_AddSkippedVerificationCheck()
    {
        var root = CreateTempDir();
        var config = CreateEnabledConfig();
        config.Harness.PlanExecuteVerify.RegressionCategories = ["security"];
        var service = CreateService(root, config);
        var decision = await service.EvaluateToolAsync(CreateContext("shell", ToolGovernanceRiskLevel.Critical), CancellationToken.None);
        await service.RecordApprovalDecisionAsync(decision.Run, approved: true, CancellationToken.None);

        var completed = await service.CompleteToolAsync(decision.Run, CreateInvocation(ToolResultStatuses.Completed), CancellationToken.None);

        Assert.Contains(completed!.Verification!.Checks, check => check.Id == "regression" && check.Status == HarnessVerificationStatus.Skipped);
    }

    [Fact]
    public async Task InferResourceSet_DoesNotTreatShellCommandAsFilePath()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());

        var decision = await service.EvaluateToolAsync(
            CreateContext("shell", ToolGovernanceRiskLevel.Critical, argumentsJson: """{"cmd":"dotnet test"}"""),
            CancellationToken.None);

        var contract = await new FileHarnessContractStore(root).GetAsync(decision.Run!.HarnessContractId!, CancellationToken.None);
        Assert.DoesNotContain(contract!.ReadSet, item => item.Path == "dotnet test");
        Assert.DoesNotContain(contract.WriteSet, item => item.Path == "dotnet test");
    }

    [Fact]
    public async Task ToolExecutor_PevEnabledWrapsHighRiskToolWithoutChangingExecutionPipeline()
    {
        var root = CreateTempDir();
        var service = CreateService(root, CreateEnabledConfig());
        var tool = new EchoTool("shell", "ok");
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: null,
            logger: NullLogger.Instance,
            config: CreateEnabledConfig(),
            planExecuteVerify: service);

        var result = await executor.ExecuteAsync(
            "shell",
            """{"cmd":"dotnet test"}""",
            "call_1",
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: (_, _, _) => ValueTask.FromResult(true),
            CancellationToken.None);

        Assert.Equal("ok", result.ResultText);
        Assert.Equal(1, tool.ExecutionCount);
        var run = Assert.Single(service.ListRuns());
        Assert.Equal(PlanExecuteVerifyStatus.Verified, run.Status);
        Assert.NotNull(run.HarnessContractId);
        Assert.NotNull(run.EvidenceBundleId);
    }

    [Fact]
    public async Task ToolExecutor_BlocksNonProceedPevDecision()
    {
        var tool = new EchoTool("shell", "ok");
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: null,
            logger: NullLogger.Instance,
            config: CreateEnabledConfig(),
            planExecuteVerify: new RejectingPlanExecuteVerifyOrchestrator());

        var result = await executor.ExecuteAsync(
            "shell",
            """{"cmd":"dotnet test"}""",
            "call_1",
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: (_, _, _) => ValueTask.FromResult(true),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public void PlanExecuteVerifyModels_RoundTripWithSourceGeneratedJson()
    {
        var run = new PlanExecuteVerifyRun
        {
            Id = "pev_roundtrip",
            Status = PlanExecuteVerifyStatus.Verified,
            Decision = PlanExecuteVerifyDecisionKinds.Proceed,
            HarnessContractId = "hctr_1",
            EvidenceBundleId = "evb_1",
            Verification = new HarnessVerificationResult
            {
                Status = HarnessVerificationStatus.Passed,
                Checks = [new HarnessVerificationCheck { Id = "tool", Name = "Tool", Status = HarnessVerificationStatus.Passed }]
            }
        };

        var json = JsonSerializer.Serialize(run, CoreJsonContext.Default.PlanExecuteVerifyRun);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.PlanExecuteVerifyRun);

        Assert.NotNull(restored);
        Assert.Equal(run.Id, restored!.Id);
        Assert.Equal(HarnessVerificationStatus.Passed, restored.Verification!.Status);
        Assert.Single(restored.Verification.Checks);
    }

    private static PlanExecuteVerifyService CreateService(string root, GatewayConfig config)
        => new(
            config,
            new HarnessContractService(
                new FileHarnessContractStore(root),
                new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
                NullLogger<HarnessContractService>.Instance),
            new EvidenceBundleService(
                new FileEvidenceBundleStore(root),
                new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
                NullLogger<EvidenceBundleService>.Instance),
            new GovernanceLedgerService(
                new FileGovernanceLedgerStore(root),
                new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
                null,
                NullLogger<GovernanceLedgerService>.Instance),
            new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
            NullLogger<PlanExecuteVerifyService>.Instance);

    private static GatewayConfig CreateEnabledConfig()
        => new()
        {
            Harness = new HarnessConfig
            {
                ExecutionMode = HarnessExecutionModes.PlanExecuteVerify,
                PlanExecuteVerify = new PlanExecuteVerifyOptions
                {
                    Enabled = true,
                    CreateEvidenceBundles = true,
                    RunVerification = true,
                    AutoRollbackOnFailedVerification = false
                }
            }
        };

    private static PlanExecuteVerifyToolContext CreateContext(
        string toolName,
        ToolGovernanceRiskLevel risk,
        bool readOnly = false,
        int toolCallCount = 1,
        string argumentsJson = """{"cmd":"dotnet test","path":"README.md"}""")
        => new()
        {
            Session = CreateSession(),
            CorrelationId = "corr_1",
            CallId = "call_1",
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ActionDescriptor = new ToolActionDescriptor
            {
                Action = readOnly ? "read" : "execute",
                IsMutation = !readOnly,
                RequiresApproval = risk is ToolGovernanceRiskLevel.High or ToolGovernanceRiskLevel.Critical,
                Summary = $"Run {toolName}",
                RiskLevel = risk.ToString().ToLowerInvariant()
            },
            GovernanceDescriptor = ToolGovernanceDescriptorCatalog.Resolve(
                toolName,
                toolName,
                new ToolActionDescriptor
                {
                    IsMutation = !readOnly,
                    RequiresApproval = risk is ToolGovernanceRiskLevel.High or ToolGovernanceRiskLevel.Critical,
                    RiskLevel = risk.ToString().ToLowerInvariant()
                }) with { ReadOnly = readOnly },
            ExistingApprovalRequired = risk is ToolGovernanceRiskLevel.High or ToolGovernanceRiskLevel.Critical,
            ToolCallCount = toolCallCount
        };

    private static ToolInvocation CreateInvocation(string status, string? failureCode = null)
        => new()
        {
            CallId = "call_1",
            ToolName = "shell",
            Arguments = """{"cmd":"dotnet test"}""",
            Result = failureCode is null ? "ok" : "failed",
            ResultStatus = status,
            FailureCode = failureCode,
            FailureMessage = failureCode is null ? null : "tool failed",
            Duration = TimeSpan.FromMilliseconds(10)
        };

    private static Session CreateSession()
        => new()
        {
            Id = "sess_pev",
            ChannelId = "websocket",
            SenderId = "user_pev",
            History = [new ChatTurn { Role = "user", Content = "Run a governed shell command." }]
        };

    private static TurnContext CreateTurnContext()
        => new()
        {
            SessionId = "sess_pev",
            ChannelId = "websocket"
        };

    private static string CreateTempDir()
    {
        var path = Path.Join(Path.GetTempPath(), "openclaw-pev-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EchoTool(string name, string result) : ITool
    {
        public int ExecutionCount { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParameterSchema => """{"type":"object","properties":{"cmd":{"type":"string"}}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            ExecutionCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectingPlanExecuteVerifyOrchestrator : IPlanExecuteVerifyOrchestrator
    {
        public ValueTask<PlanExecuteVerifyDecision> EvaluateToolAsync(
            PlanExecuteVerifyToolContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PlanExecuteVerifyDecision
            {
                Decision = PlanExecuteVerifyDecisionKinds.Reject,
                RequiresPlanExecuteVerify = true,
                Summary = "Rejected by test orchestrator."
            });

        public ValueTask RecordApprovalDecisionAsync(PlanExecuteVerifyRun? run, bool approved, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<PlanExecuteVerifyRun?> CompleteToolAsync(PlanExecuteVerifyRun? run, ToolInvocation invocation, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PlanExecuteVerifyRun?>(run);

        public ValueTask<PlanExecuteVerifyRun?> VerifyRunAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PlanExecuteVerifyRun?>(null);

        public PlanExecuteVerifyRun? GetRun(string id) => null;

        public IReadOnlyList<PlanExecuteVerifyRun> ListRuns(int limit = 100) => [];
    }
}

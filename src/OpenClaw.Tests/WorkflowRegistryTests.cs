using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowRegistryTests
{
    [Fact]
    public void AgentWorkflowRegistry_Registers_MafDurableHttp_Backend()
    {
        using var registry = CreateRegistry(new GatewayConfig
        {
            Workflows = new WorkflowsConfig
            {
                Enabled = true,
                Backends =
                {
                    ["durable-review"] = new WorkflowBackendConfig
                    {
                        Kind = AgentWorkflowBackendKinds.MafDurableHttp,
                        DisplayName = "Durable Agent Review",
                        BaseUrl = "https://durable.example.test/",
                        WorkflowName = "DurableAgentReview"
                    }
                }
            }
        });

        var summary = Assert.Single(registry.List());
        Assert.Equal("durable-review", summary.Id);
        Assert.Equal(AgentWorkflowBackendKinds.MafDurableHttp, summary.Kind);
        Assert.Equal("DurableAgentReview", summary.WorkflowName);
        Assert.Equal("Durable Agent Review", summary.DisplayName);
        Assert.True(summary.Enabled);
    }

    [Fact]
    public void AgentWorkflowRegistry_Stays_Empty_When_Workflows_Disabled()
    {
        using var registry = CreateRegistry(new GatewayConfig
        {
            Workflows = new WorkflowsConfig
            {
                Enabled = false,
                Backends =
                {
                    ["durable-review"] = new WorkflowBackendConfig
                    {
                        BaseUrl = "https://durable.example.test/"
                    }
                }
            }
        });

        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task AgentWorkflowRegistry_Throws_For_Unknown_Backend()
    {
        using var registry = CreateRegistry(new GatewayConfig());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.GetAsync("missing", "run_123", CancellationToken.None));
    }

    private static AgentWorkflowRegistry CreateRegistry(GatewayConfig config)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-workflow-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        return new AgentWorkflowRegistry(
            config,
            new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance),
            NullLoggerFactory.Instance);
    }
}

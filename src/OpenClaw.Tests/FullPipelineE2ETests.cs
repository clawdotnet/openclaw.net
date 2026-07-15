using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Agent.Actions;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FullPipelineE2ETests
{
    [Fact]
    public async Task FullPipeline_LowRisk_LoadGraph_To_BusinessApi_Writeback()
    {
        // 1. Create temp graph file
        var workspace = CreateTempDir();
        var graphPath = Path.Combine(workspace, "quality-slice.jsonld");
        await File.WriteAllTextAsync(graphPath, """
        {
          "@context": "http://openclaw.net/ontology/industrial.jsonld",
          "@type": "ex:ProductBatch",
          "ex:id": "BATCH-001",
          "ex:defectRate": 0.023
        }
        """);

        // 2. load_temporary_graph
        var graphTool = new LoadTemporaryGraphTool(new ToolingConfig
        {
            AllowedReadRoots = [workspace]
        });
        var graphArgs = JsonSerializer.Serialize(new { path = graphPath, format = "json" });
        var graphResult = await graphTool.ExecuteAsync(graphArgs, TestContext.Current.CancellationToken);
        using var graphDoc = JsonDocument.Parse(graphResult);
        Assert.Equal("ok", graphDoc.RootElement.GetProperty("status").GetString());
        Assert.True(graphDoc.RootElement.TryGetProperty("payload_json", out _));

        // 3. Build ActionProposal (simulating LLM inference output)
        var proposal = BuildProposal();

        // 4. Start Mock HTTP Server
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateCustomerTier", HttpStatusCode.OK, "{\"tierUpdated\":true}");

        // 5. Wire ActionAdapter with test HTTP connector
        var config = Options.Create(new ActionAdapterConfig
        {
            Enabled = true,
            Connectors = new Dictionary<string, ConnectorDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = new ConnectorDefinition
                {
                    BaseUrl = "http://test.local",
                    TimeoutSeconds = 30,
                    AllowedCalls = ["getCustomer", "updateTier"],
                    Auth = new ConnectorAuthConfig { Type = "None" }
                }
            }
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
        var connector = new HttpActionAdapterConnector(config,
            httpClient,
            NullLogger<HttpActionAdapterConnector>.Instance);
        var registry = new InMemoryActionIdempotencyRegistry();
        var adapter = new ActionAdapter(connector, registry);
        var tool = new ActionExecuteTool(new ActionPolicyEngine(), adapter);

        // 6. Call action_execute with proceed decision
        var proposalJson = JsonSerializer.Serialize(proposal, CoreJsonContext.Default.ActionProposal);
        var actionArgs = BuildActionArgs(proposalJson, "proceed");
        var actionResult = await tool.ExecuteAsync(actionArgs, TestContext.Current.CancellationToken);

        // 7. Verify action result
        using var actionDoc = JsonDocument.Parse(actionResult);
        Assert.Equal("execution_completed",
            actionDoc.RootElement.GetProperty("status").GetString());

        // 8. Verify governance mapping
        var mapping = actionDoc.RootElement.GetProperty("governanceMapping");
        var hctrId = mapping.GetProperty("harnessContractId").GetString();
        var pevId = mapping.GetProperty("pevId").GetString();
        Assert.NotNull(hctrId);
        Assert.NotEmpty(hctrId);
        Assert.NotNull(pevId);
        Assert.NotEmpty(pevId);

        // 9. Verify Mock Server received correct requests (preCheck + 2 execution)
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FullPipeline_RequireApproval_WithAdapter_Executes()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateTier", HttpStatusCode.OK, "ok");
        handler.SetResponse("/getCustomer", HttpStatusCode.OK, "ok");

        var config = Options.Create(new ActionAdapterConfig
        {
            Enabled = true,
            Connectors = new Dictionary<string, ConnectorDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = new ConnectorDefinition
                {
                    BaseUrl = "http://test.local",
                    AllowedCalls = ["getCustomer", "updateTier"],
                    Auth = new ConnectorAuthConfig { Type = "None" }
                }
            }
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
        var connector = new HttpActionAdapterConnector(config, httpClient,
            NullLogger<HttpActionAdapterConnector>.Instance);
        var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());

        // First call: require_approval with approval payload → adapter executes
        var proposal = BuildProposal();
        var proposalJson = JsonSerializer.Serialize(proposal, CoreJsonContext.Default.ActionProposal);
        var actionArgs = BuildActionArgs(proposalJson, "require_approval", """{"approver":"u_zhangsan","decisionAt":"2026-07-15T08:30:00Z","decisionReason":"approved","ticketRef":"ITSM-1"}""");
        var tool = new ActionExecuteTool(new ActionPolicyEngine(), adapter);
        var result = await tool.ExecuteAsync(actionArgs, TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("execution_completed",
            doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FullPipeline_ConfigDisabled_AllProposalOnly()
    {
        // Without adapter injection (config disabled), tool returns decision only
        var tool = new ActionExecuteTool(); // no adapter

        var proposal = BuildProposal();
        var proposalJson = JsonSerializer.Serialize(proposal, CoreJsonContext.Default.ActionProposal);
        var actionArgs = BuildActionArgs(proposalJson, "proceed");
        var result = await tool.ExecuteAsync(actionArgs, TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("execution_started",
            doc.RootElement.GetProperty("status").GetString());
    }

    private static ActionProposal BuildProposal()
        => new()
        {
            ActionName = "sync_customer_tier",
            Source = new ActionProposalSource
            {
                MetaSkill = "customer-risk-assistant",
                RunId = "run_1",
                StepId = "step_1"
            },
            Trigger = new ActionProposalTrigger
            {
                Condition = "riskLevel == medium",
                EvidenceRefs = ["ev_001"]
            },
            Target = new ActionProposalTarget
            {
                System = "crm",
                Operation = "updateCustomerTier"
            },
            PreChecks =
            [
                new ActionCall
                {
                    Call = "crm.getCustomer",
                    Args = new Dictionary<string, JsonElement>()
                }
            ],
            Execution =
            [
                new ActionCall
                {
                    Call = "crm.updateTier",
                    Args = new Dictionary<string, JsonElement>()
                },
                new ActionCall
                {
                    Call = "crm.updateTier",
                    Args = new Dictionary<string, JsonElement>()
                }
            ],
            Rollback =
            [
                new ActionCall
                {
                    Call = "crm.updateTier",
                    Args = new Dictionary<string, JsonElement>()
                }
            ],
            IdempotencyKey = $"proposal-C123-{Guid.NewGuid():n}",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["env"] = "test"
            }
        };

    private static string BuildActionArgs(string proposalJson, string decision, string? approvalJson = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"proposal\":");
        sb.Append(proposalJson);
        sb.Append(",\"decision\":\"");
        sb.Append(decision);
        sb.Append('"');
        if (approvalJson is not null)
        {
            sb.Append(",\"approval\":");
            sb.Append(approvalJson);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-e2e", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
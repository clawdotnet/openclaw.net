using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Tests.Stubs;

using Strategos.Abstractions;
using Strategos.Primitives;
using Strategos.Selection;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class GatewayEventReceiverTests
{
    [Fact]
    public async Task Returns_401_When_Bearer_Token_Mismatches()
    {
        var (host, _) = BuildHost(expectedToken: "secret", recorded: out _);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var client = host.GetTestClient();

        using var req = NewRequest(bearer: "wrong");
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Valid_Token_With_Completed_Event_Records_Outcome_And_Returns_200()
    {
        var (host, selector) = BuildHost(expectedToken: "secret", recorded: out _);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var cache = host.Services.GetRequiredService<RunIdAgentSelectionCache>();
        cache.Set("run-1", "SecurityReviewer", "mock", "General");

        var entry = NewEntry(action: "run_completed");
        using var req = NewRequest(bearer: "secret", body: entry);
        var client = host.GetTestClient();
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var outcome = Assert.Single(selector.Outcomes);
        Assert.Equal("mock", outcome.AgentId);
        Assert.Equal("General", outcome.TaskCategory);
        Assert.True(outcome.Success);
    }

    [Fact]
    public async Task Duplicate_Event_Id_Is_Deduplicated_In_Memory()
    {
        var (host, selector) = BuildHost(expectedToken: "secret", recorded: out _);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var cache = host.Services.GetRequiredService<RunIdAgentSelectionCache>();
        cache.Set("run-1", "SecurityReviewer", "mock", "General");

        var client = host.GetTestClient();
        var entry = NewEntry(action: "run_completed", id: "evt_dup_0000000000");

        using (var req1 = NewRequest(bearer: "secret", body: entry))
        {
            using var resp1 = await client.SendAsync(req1, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        }
        using (var req2 = NewRequest(bearer: "secret", body: entry))
        {
            using var resp2 = await client.SendAsync(req2, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        }

        Assert.Single(selector.Outcomes); // only recorded once
    }

    [Fact]
    public async Task Non_Workflow_Component_Is_Ignored_With_200()
    {
        var (host, selector) = BuildHost(expectedToken: "secret", recorded: out _);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var client = host.GetTestClient();
        var entry = NewEntry(action: "run_completed", component: "tool");
        using var req = NewRequest(bearer: "secret", body: entry);
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(selector.Outcomes);
    }

    [Fact]
    public async Task RecordOutcome_Failure_Does_Not_Propagate_To_Subsequent_Events()
    {
        var selector = new ThrowingAgentSelector();
        var cache = new RunIdAgentSelectionCache();
        cache.Set("run-1", "SecurityReviewer", "mock", "General");

        var mapper = new AgentOutcomeMapper(cache, NullLogger<AgentOutcomeMapper>.Instance);
        var logger = NullLogger<GatewayEventReceiver>.Instance;
        var receiver = new GatewayEventReceiver(mapper, selector, expectedBearerToken: "secret", logger: logger);

        // Direct call, no HTTP, to assert the failure-isolation contract.
        var okCtx = NewHttpContext(bearer: "secret");
        var okResult = await receiver.HandleAsync(okCtx, TestContext.Current.CancellationToken);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(okResult);

        var secondCtx = NewHttpContext(bearer: "secret", runId: "run-2");
        var secondResult = await receiver.HandleAsync(secondCtx, TestContext.Current.CancellationToken);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(secondResult);
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private static HttpRequestMessage NewRequest(string bearer, RuntimeEventEntry? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/runtime-events");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private static RuntimeEventEntry NewEntry(
        string action,
        string component = "workflow",
        string id = "evt_unique000000000",
        string runId = "run-1",
        string stepName = "SecurityReviewer")
        => new()
        {
            Id = id,
            Component = component,
            Action = action,
            Severity = "info",
            Summary = "test",
            Metadata = new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["stepName"] = stepName,
            },
        };

    private static (IHost host, StubAgentSelector selector) BuildHost(
        string expectedToken,
        out object? recorded)
    {
        _ = expectedToken;
        recorded = null;
        return BuildHostCore();
    }

    private static (IHost host, StubAgentSelector selector) BuildHostCore()
    {
        var selector = new StubAgentSelector();
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<RunIdAgentSelectionCache>();
                    services.AddSingleton<AgentOutcomeMapper>(sp => new AgentOutcomeMapper(
                        sp.GetRequiredService<RunIdAgentSelectionCache>(),
                        NullLogger<AgentOutcomeMapper>.Instance));
                    services.AddSingleton<IAgentSelector>(selector);
                    services.AddSingleton<GatewayEventReceiver>(sp => new GatewayEventReceiver(
                        sp.GetRequiredService<AgentOutcomeMapper>(),
                        sp.GetRequiredService<IAgentSelector>(),
                        expectedBearerToken: "secret",
                        logger: NullLogger<GatewayEventReceiver>.Instance));
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/runtime-events", async (HttpContext ctx, GatewayEventReceiver r, CancellationToken ct) =>
                        {
                            return await r.HandleAsync(ctx, ct);
                        });
                    });
                });
            })
            .Build();
        return (host, selector);
    }

    private static DefaultHttpContext NewHttpContext(string bearer, string runId = "run-1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/runtime-events";
        ctx.Request.Headers["Authorization"] = $"Bearer {bearer}";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            Component = "workflow",
            Action = "run_completed",
            Severity = "info",
            Summary = "test",
            Metadata = new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["stepName"] = "SecurityReviewer",
            },
        }));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        return ctx;
    }

    private sealed class ThrowingAgentSelector : IAgentSelector
    {
        public Task<Result<AgentSelection>> SelectAgentAsync(AgentSelectionContext context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<Result<Strategos.Primitives.Unit>> RecordOutcomeAsync(string agentId, string taskCategory, AgentOutcome outcome, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("simulated selector failure");
        }
    }
}

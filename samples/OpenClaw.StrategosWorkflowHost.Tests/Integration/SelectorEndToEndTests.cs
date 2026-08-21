using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;

using Strategos.Abstractions;
using Strategos.Infrastructure.Selection;
using Strategos.Selection;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests.Integration;

public class SelectorEndToEndTests
{
    [Fact]
    public async Task Selection_Then_Outcome_Webhook_Updates_Thompson_Belief_By_One()
    {
        // ─── Arrange: a real Thompson Sampling selector + InMemoryBeliefStore ───
        var beliefLogger = NullLogger<InMemoryBeliefStore>.Instance;
        var selectorLogger = NullLogger<ThompsonSamplingAgentSelector>.Instance;
        var beliefStore = new InMemoryBeliefStore(beliefLogger);
        var selector = new ThompsonSamplingAgentSelector(
            beliefStore,
            new TaskCategoryClassifier(),
            selectorLogger,
            randomSeed: 42);

        var cache = new RunIdAgentSelectionCache();

        // Two inner clients — one "good" (always returns valid JSON), one "bad"
        // (returns malformed JSON to simulate a failed chat).
        var goodInner = Substitute.For<IChatClient>();
        goodInner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "good")));
        var badInner = Substitute.For<IChatClient>();
        badInner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "bad")));

        var options = new SelectorOptions
        {
            Enabled = true,
            AvailableAgents = new[] { "good", "bad" },
            TaskCategory = "General",
        };

        var decorator = new SelectorBackedChatClient(
            selector,
            cache,
            goodInner,
            new Dictionary<string, IChatClient> { ["good"] = goodInner, ["bad"] = badInner },
            options,
            NullLogger<SelectorBackedChatClient>.Instance);

        // First call: selector picks an agent, decorator routes, cache records.
        // We force the picker by stubbing the inner clients — Thompson Sampling
        // still picks randomly but we'll observe the *recorded* agentId below.
        var chatOpts = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["runId"] = "run-e2e-1",
                ["stepName"] = "SecurityReviewer",
            },
        };
        var firstResponse = await decorator.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            chatOpts);

        Assert.NotNull(firstResponse);
        var selected = cache.TryGet("run-e2e-1", "SecurityReviewer");
        Assert.NotNull(selected); // selection was recorded

        // ─── Act: stand up the webhook receiver in-process and POST outcome ───
        var mapper = new AgentOutcomeMapper(cache, NullLogger<AgentOutcomeMapper>.Instance);
        var receiver = new GatewayEventReceiver(
            mapper,
            selector,
            expectedBearerToken: "secret",
            logger: NullLogger<GatewayEventReceiver>.Instance);

        var beforeBelief = (await beliefStore.GetBeliefAsync(
            selected!.Value.AgentId, "General", TestContext.Current.CancellationToken)).Value;
        var beforeObservations = beforeBelief.ObservationCount;

        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/runtime-events", async (HttpContext ctx, CancellationToken ct) =>
                        {
                            await receiver.HandleAsync(ctx, ct);
                        });
                    });
                });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var client = host.GetTestClient();
        var entry = new RuntimeEventEntry
        {
            Id = $"evt_e2e_{Guid.NewGuid():N}"[..20],
            Component = "workflow",
            Action = "run_completed",
            Severity = "info",
            Summary = "e2e",
            Metadata = new Dictionary<string, string>
            {
                ["runId"] = "run-e2e-1",
                ["stepName"] = "SecurityReviewer",
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/runtime-events")
        {
            Content = JsonContent.Create(entry),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");

        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        // ─── Assert: outcome was recorded; belief observation count went up by 1 ───
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var afterBelief = (await beliefStore.GetBeliefAsync(
            selected.Value.AgentId, "General", TestContext.Current.CancellationToken)).Value;
        Assert.Equal(beforeObservations + 1, afterBelief.ObservationCount);
        Assert.True(afterBelief.Mean >= beforeBelief.Mean); // success outcome pulls mean up
    }
}

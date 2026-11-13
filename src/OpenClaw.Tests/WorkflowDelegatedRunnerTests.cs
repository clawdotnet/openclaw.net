using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowDelegatedRunnerTests
{
    [Fact]
    public async Task RunAndRespond_WhenDelegated_ShouldUseCallerWithoutMutatingPayload()
    {
        using var fixture = new Fixture();
        var start = new AgentWorkflowRequest { SenderId = "claimed", Input = "review", SessionId = "session" };
        var response = new AgentWorkflowResponse { PortId = "approval", ActorId = "claimed", Approved = true };
        await fixture.Runner.RunAsync(start, fixture.Caller("user-a"), TestContext.Current.CancellationToken);
        await fixture.Runner.GetAsync("run", fixture.Caller("user-b"), TestContext.Current.CancellationToken);
        await fixture.Runner.RespondAsync("run", response, fixture.Caller("user-b"), TestContext.Current.CancellationToken);
        Assert.Equal("claimed", start.SenderId);
        Assert.Equal("claimed", response.ActorId);
        Assert.Collection(fixture.Calls,
            call =>
            {
                Assert.Equal("Bearer token-user-a", call.Authorization);
                Assert.Equal("user-a", JsonDocument.Parse(call.Body!).RootElement.GetProperty("senderId").GetString());
            },
            call => Assert.Equal("Bearer token-user-b", call.Authorization),
            call =>
            {
                Assert.Equal("Bearer token-user-b", call.Authorization);
                Assert.Equal("user-b", JsonDocument.Parse(call.Body!).RootElement.GetProperty("actorId").GetString());
            });
        Assert.Null(fixture.Client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("get")]
    [InlineData("respond")]
    [InlineData("stream")]
    public async Task Operation_WhenCallerMissing_ShouldRejectBeforeBackend(string operation)
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() => InvokeAsync(fixture, operation, null));
        Assert.Equal(401, error.StatusCode);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("get")]
    [InlineData("respond")]
    [InlineData("stream")]
    public async Task Operation_WhenCallerExpired_ShouldRejectBeforeBackend(string operation)
    {
        using var fixture = new Fixture();
        var expired = new WorkflowExecutionContext("review", "issuer", "user", fixture.Clock.GetUtcNow(), "assertion");
        await Assert.ThrowsAsync<WorkflowDelegationException>(() => InvokeAsync(fixture, operation, expired));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RunAsync_WhenBackendDenies_ShouldPreserveStatusAndNotReplay(int status)
    {
        using var fixture = new Fixture(status: (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Runner.RunAsync(new(), fixture.Caller("user"), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("workflow_backend_auth_denied", error.ErrorCode);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task StreamAsync_WhenPolling_ShouldAcquireForEverySnapshot()
    {
        using var fixture = new Fixture();
        await using var enumerator = fixture.Runner.StreamAsync("run", fixture.Caller("user"), TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var next = enumerator.MoveNextAsync().AsTask();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await next);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(2, fixture.Provider.Acquisitions);
    }

    [Fact]
    public async Task RunAsync_WhenStatic_ShouldPreserveClaimedIdentityAndToken()
    {
        using var fixture = new Fixture(delegated: false);
        await fixture.Runner.RunAsync(new() { SenderId = "claimed" }, TestContext.Current.CancellationToken);
        var sent = Assert.Single(fixture.Calls);
        Assert.Equal("Bearer static-token", sent.Authorization);
        Assert.Equal("claimed", JsonDocument.Parse(sent.Body!).RootElement.GetProperty("senderId").GetString());
        Assert.Equal(0, fixture.Provider.Acquisitions);
    }

    private static async Task InvokeAsync(Fixture fixture, string operation, WorkflowExecutionContext? caller)
    {
        var ct = TestContext.Current.CancellationToken;
        switch (operation)
        {
            case "run": await fixture.Runner.RunAsync(new(), caller, ct); break;
            case "get": await fixture.Runner.GetAsync("run", caller, ct); break;
            case "respond": await fixture.Runner.RespondAsync("run", new() { PortId = "approval" }, caller, ct); break;
            case "stream": await foreach (var _ in fixture.Runner.StreamAsync("run", caller, ct)) { } break;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Join(Path.GetTempPath(), "workflow-runner-tests", Guid.NewGuid().ToString("N"));
        public ManualWorkflowTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        public List<(string? Authorization, string? Body)> Calls { get; } = [];
        public TestProvider Provider { get; }
        public HttpClient Client { get; }
        public MafDurableHttpWorkflowRunner Runner { get; }
        public Fixture(bool delegated = true, HttpStatusCode status = HttpStatusCode.OK)
        {
            Provider = new(Clock);
            Client = new(new RecordingHttpMessageHandler(async (request, ct) =>
            {
                Calls.Add((request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
                return new(status)
                {
                    Content = new StringContent($$"""{"workflowId":"workflow","runId":"run","status":"{{(Calls.Count < 2 ? "running" : "completed")}}","events":[],"pendingInputs":[]}""",
                        Encoding.UTF8, "application/json")
                };
            }));
            Runner = new("review", new WorkflowBackendConfig
            {
                BaseUrl = "https://workflow.example", ApiTokenSecret = delegated ? null : "raw:static-token",
                Authentication = new() { Mode = delegated ? "entra-obo" : "static" }
            }, new RuntimeEventStore(_path, NullLogger<RuntimeEventStore>.Instance),
                NullLogger<MafDurableHttpWorkflowRunner>.Instance, Provider, Client, Clock);
        }
        public WorkflowExecutionContext Caller(string user) => new("review", "issuer", user, Clock.GetUtcNow().AddMinutes(5), "assertion");
        public void Dispose()
        {
            Runner.Dispose();
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class TestProvider(TimeProvider clock) : IWorkflowAccessTokenProvider
    {
        public int Acquisitions { get; private set; }
        public Task<WorkflowAccessToken> GetAsync(string backendId, WorkflowAuthenticationConfig auth,
            WorkflowExecutionContext context, CancellationToken ct)
        {
            Acquisitions++;
            return Task.FromResult(new WorkflowAccessToken("token-" + context.Subject, clock.GetUtcNow().AddMinutes(1), clock.GetUtcNow().AddSeconds(30)));
        }
    }
}

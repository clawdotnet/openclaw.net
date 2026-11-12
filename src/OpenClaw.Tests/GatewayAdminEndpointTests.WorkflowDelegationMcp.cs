using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Gateway.Mcp;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Theory]
    [InlineData("entra-obo", false)]
    [InlineData("token-exchange", false)]
    [InlineData("entra-obo", true)]
    [InlineData("token-exchange", true)]
    public async Task WorkflowDelegationMcp_WhenUserChanges_ShouldUseCurrentRequest(string mode, bool legacySession)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, mode, legacySession);
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-a", true));
        await using var client = await CreateWorkflowMcpClientAsync(harness.Client);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var tool in tools.Where(tool => tool.Name is "openclaw.run_workflow" or "openclaw.get_workflow_run" or "openclaw.respond_workflow"))
        {
            var schema = tool.JsonSchema.GetRawText();
            foreach (var forbidden in new[] { "assertion", "executionContext", "accessToken", "callerContextFactory" })
                Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
        }

        var started = await client.CallToolAsync("openclaw.run_workflow",
            new Dictionary<string, object?> { ["workflowId"] = "review", ["input"] = "review", ["senderId"] = "forged" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(started.IsError == true, WorkflowMcpText(started));
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-b", true));
        if (legacySession)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.CallToolAsync("openclaw.get_workflow_run",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1" },
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, failure.StatusCode);
            Assert.Single(server.BackendRequests);
            await using var newSession = await CreateWorkflowMcpClientAsync(harness.Client);
            var result = await newSession.CallToolAsync("openclaw.respond_workflow",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1", ["portId"] = "approval" },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.IsError == true, WorkflowMcpText(result));
            Assert.Equal("user-b", server.BackendRequests.Last().Response!.ActorId);
            return;
        }
        var fetched = await client.CallToolAsync("openclaw.get_workflow_run",
            new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(fetched.IsError == true, WorkflowMcpText(fetched));
        var responded = await client.CallToolAsync("openclaw.respond_workflow",
            new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1", ["portId"] = "approval", ["actorId"] = "forged" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(responded.IsError == true, WorkflowMcpText(responded));
        Assert.Collection(server.BackendRequests,
            call => { Assert.Equal("Bearer delegated-user-a", call.Authorization); Assert.Equal("user-a", call.StartRequest!.SenderId); },
            call => Assert.Equal("Bearer delegated-user-b", call.Authorization),
            call => { Assert.Equal("Bearer delegated-user-b", call.Authorization); Assert.Equal("user-b", call.Response!.ActorId); });
    }

    [Theory]
    [InlineData(false, null, "workflow_user_auth_required")]
    [InlineData(true, "access_denied", "workflow_delegation_denied")]
    [InlineData(true, "invalid_client", "workflow_token_exchange_failed")]
    public async Task WorkflowDelegationMcp_WhenDenied_ShouldReturnToolError(bool eligible, string? error, string expected)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        server.OAuthError = error;
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user", eligible));
        await using var client = await CreateWorkflowMcpClientAsync(harness.Client);
        var result = await client.CallToolAsync("openclaw.get_workflow_run",
            new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal($"An error occurred invoking 'openclaw.get_workflow_run': {expected}", WorkflowMcpText(result));
        Assert.Empty(server.BackendRequests);
        if (!eligible) Assert.Empty(server.ExchangeRequests);
    }

    [Fact]
    public async Task WorkflowDelegationMcp_WhenNoHttpContext_ShouldFailClosed()
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        var tools = ActivatorUtilities.CreateInstance<OpenClawMcpTools>(harness.App.Services);
        var error = await Assert.ThrowsAsync<McpException>(() => tools.GetWorkflowRun("review", "run-1", TestContext.Current.CancellationToken));
        Assert.Equal("workflow_user_auth_required", error.Message);
        Assert.Empty(server.ExchangeRequests);
        Assert.Empty(server.BackendRequests);
    }

    [Fact]
    public async Task WorkflowDelegationMcp_WhenUsersCallConcurrently_ShouldIsolateCredentials()
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        using var httpA = harness.App.GetTestClient();
        using var httpB = harness.App.GetTestClient();
        httpA.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-a", true));
        httpB.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-b", true));
        await using var a = await CreateWorkflowMcpClientAsync(httpA);
        await using var b = await CreateWorkflowMcpClientAsync(httpB);
        var arguments = new Dictionary<string, object?> { ["workflowId"] = "review", ["input"] = "review" };
        var results = await Task.WhenAll(
            a.CallToolAsync("openclaw.run_workflow", arguments, cancellationToken: TestContext.Current.CancellationToken).AsTask(),
            b.CallToolAsync("openclaw.run_workflow", arguments, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.All(results, result => Assert.False(result.IsError == true, WorkflowMcpText(result)));
        Assert.Equal(2, server.BackendRequests.Count);
        Assert.All(server.BackendRequests, call => Assert.Equal($"Bearer delegated-{call.StartRequest!.SenderId}", call.Authorization));
        Assert.Equal(new[] { "user-a", "user-b" }, server.BackendRequests.Select(call => call.StartRequest!.SenderId).Order());
    }

    private static Task<McpClient> CreateWorkflowMcpClientAsync(HttpClient httpClient)
        => McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "mcp") },
                httpClient, ownsHttpClient: false),
            cancellationToken: TestContext.Current.CancellationToken);

    private static string WorkflowMcpText(CallToolResult result)
        => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}

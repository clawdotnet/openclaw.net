using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Fact]
    public async Task FractalMemoryWorkflows_EnforceAuthMutationScopeAndCsrf()
    {
        var provider = Substitute.For<IStructuredMemoryProvider, IStructuredMemoryWorkflowProvider>();
        var workflows = (IStructuredMemoryWorkflowProvider)provider;
        workflows.ExecuteWorkflowAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StructuredMemoryWorkflowResult { Success = true }));
        await using var harness = await CreateHarnessAsync(true,
            config => { config.Memory.Fractal.Enabled = true; config.Memory.Fractal.AllowWrites = true; },
            configureServices: (services, _) => services.AddSingleton(provider));

        using var anonymous = await harness.Client.PostAsync("/admin/memory/fractal/workflows/read", JsonContent("""{"path":"projects/demo"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var viewer = CreateOperatorToken(harness, OperatorRoleNames.Viewer, "fractal-viewer");
        using var preview = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/fractal/workflows/doctor") { Content = JsonContent("{}") };
        preview.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        using var previewResponse = await harness.Client.SendAsync(preview);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        using var repair = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/fractal/workflows/doctor") { Content = JsonContent("""{"repair":true}""") };
        repair.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        using var repairResponse = await harness.Client.SendAsync(repair);
        Assert.Equal(HttpStatusCode.Forbidden, repairResponse.StatusCode);

        var (cookie, csrf) = await LoginAsync(harness.Client, harness.AuthToken);
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/fractal/workflows/node_create") { Content = JsonContent("""{"path":"projects/demo"}""") };
        noCsrf.Headers.Add("Cookie", cookie);
        using var noCsrfResponse = await harness.Client.SendAsync(noCsrf);
        Assert.Equal(HttpStatusCode.Unauthorized, noCsrfResponse.StatusCode);
        using var write = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/fractal/workflows/node_create") { Content = JsonContent("""{"path":"projects/demo"}""") };
        write.Headers.Add("Cookie", cookie);
        write.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrf);
        using var writeResponse = await harness.Client.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);
        Assert.Equal(2, workflows.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData("doctor", "{\"repair\":true}", HttpStatusCode.Forbidden)]
    [InlineData("import", "{\"path\":\"research/note\",\"sourceName\":\"note.md\",\"content\":\"note\",\"apply\":true}", HttpStatusCode.Forbidden)]
    [InlineData("read", "[]", HttpStatusCode.BadRequest)]
    [InlineData("read", "{", HttpStatusCode.BadRequest)]
    [InlineData("read", "{}", HttpStatusCode.BadRequest)]
    [InlineData("update", "{\"path\":\"projects/demo\",\"section\":\"Objective\",\"content\":\"hi\"}", HttpStatusCode.BadRequest)]
    [InlineData("unlisted_tool", "{}", HttpStatusCode.NotFound)]
    public async Task FractalMemoryWorkflows_RejectInvalidAndDisabledWrites(string operation, string json, HttpStatusCode expected)
    {
        var provider = Substitute.For<IStructuredMemoryProvider, IStructuredMemoryWorkflowProvider>();
        await using var harness = await CreateHarnessAsync(true,
            config => config.Memory.Fractal.Enabled = true,
            configureServices: (services, _) => services.AddSingleton(provider));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/memory/fractal/workflows/{operation}") { Content = JsonContent(json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(provider.ReceivedCalls());
    }

    [Fact]
    public async Task FractalMemoryWorkflows_HttpClientPreservesHashAndSourceLinks()
    {
        var provider = Substitute.For<IStructuredMemoryProvider, IStructuredMemoryWorkflowProvider>();
        var workflows = (IStructuredMemoryWorkflowProvider)provider;
        using var data = JsonDocument.Parse("""{"hash":"v2","content":"original notes"}""");
        workflows.ExecuteWorkflowAsync("read", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StructuredMemoryWorkflowResult
            {
                Success = true, Data = data.RootElement.Clone(),
                Resources = [new() { Uri = "memory://document/projects%2Fdemo/state.md", Name = "projects/demo/state.md" }]
            }));
        await using var harness = await CreateHarnessAsync(true,
            config => config.Memory.Fractal.Enabled = true,
            configureServices: (services, _) => services.AddSingleton(provider));
        using var client = new OpenClaw.Client.OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);
        using var args = JsonDocument.Parse("""{"path":"projects/demo"}""");
        var response = await client.ExecuteFractalMemoryWorkflowAsync("read", args.RootElement, TestContext.Current.CancellationToken);
        Assert.True(response.Success);
        Assert.Equal("v2", response.Data!.Value.GetProperty("hash").GetString());
        Assert.Single(response.Resources);
    }
}

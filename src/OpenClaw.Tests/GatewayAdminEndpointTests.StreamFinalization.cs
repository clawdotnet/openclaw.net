using System.Net;
using System.Net.Http.Headers;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatCompletions_StreamingErrorDrainsRuntimeAndTerminatesOnce(bool hasDone)
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var finalized = false;
        async IAsyncEnumerable<AgentStreamEvent> Events()
        {
            await Task.Yield();
            yield return new AgentStreamEvent { Type = AgentStreamEventType.Error, Content = "budget exceeded" };
            if (hasDone) yield return new AgentStreamEvent { Type = AgentStreamEventType.Done };
            finalized = true;
        }
        harness.Runtime.AgentRuntime.RunStreamingAsync(Arg.Any<Session>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>()).Returns(_ => Events());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            { Content = JsonContent("""{"messages":[{"role":"user","content":"hello"}],"stream":true}""") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("budget exceeded", body);
        Assert.Equal(2, body.Split("data: [DONE]", StringSplitOptions.None).Length);
        Assert.True(finalized);
    }
}

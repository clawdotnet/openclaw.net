using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Trait("Category", "WorkflowPublished")]
public sealed class WorkflowDelegationPublishedTests
{
    [Theory]
    [InlineData("entra-obo")]
    [InlineData("token-exchange")]
    public async Task PublishedGateway_DelegatesRestAndMcp(string mode)
    {
        var executable = Environment.GetEnvironmentVariable("OPENCLAW_WORKFLOW_GATEWAY_EXE");
        if (string.IsNullOrEmpty(executable))
            Assert.Skip("Set OPENCLAW_WORKFLOW_GATEWAY_EXE to a published Linux gateway executable.");
        Assert.True(Path.IsPathFullyQualified(executable) && File.Exists(executable), "Published gateway executable must exist at an absolute path.");
        Assert.True(OperatingSystem.IsLinux(), "The published TLS trust-bundle matrix runs on Linux.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        await using var server = await WorkflowDelegationTestServer.StartAsync(ct);
        var directory = Path.Join(Path.GetTempPath(), "workflow-published-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Join(directory, "gateway.json");
        var storage = Path.Join(directory, "memory");
        var config = new GatewayConfig
        {
            Port = FindAvailablePort(),
            Llm = new() { Provider = "ollama", Model = "llama3.2" },
            Memory = new() { StoragePath = storage }
        };
        config.Tooling.EnableBrowserTool = false;
        config.Plugins.Enabled = false;
        config.Skills.Enabled = false;
        config.Security.Oidc.Authority = server.Authority.ToString();
        config.Security.Oidc.Audience = "gateway-api";
        config.Workflows.Enabled = true;
        config.Workflows.Backends["review"] = new()
        {
            BaseUrl = server.BackendBaseUrl.ToString(),
            Authentication = new()
            {
                Mode = mode, TokenEndpoint = server.TokenEndpoint.ToString(), ClientId = "client",
                ClientSecretRef = "env:WORKFLOW_PUBLISHED_SECRET", Scopes = ["workflow.read"],
                UserAssertionProfile = "entra-delegated"
            }
        };
        var secret = Guid.NewGuid().ToString("N");
        var userA = server.IssueUserToken("published-a", true);
        var userB = server.IssueUserToken("published-b", true);
        var denied = server.IssueUserToken("published-service", false);
        var output = new ConcurrentQueue<string>();
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(configPath,
                $$"""{"OpenClaw":{{JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig)}}}""", ct);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(configPath);
            start.Environment["SSL_CERT_FILE"] = server.TrustBundlePath;
            start.Environment["WORKFLOW_PUBLISHED_SECRET"] = secret;
            process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.Port}/") };
            http.DefaultRequestHeaders.Authorization = new("Bearer", userA);
            await WaitForReadyAsync(http, process, output, [secret, userA, userB, denied], ct);

            using var started = await http.PostAsync("api/integration/workflows/review/runs",
                Json("""{"input":"review","senderId":"forged"}"""), ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            http.DefaultRequestHeaders.Authorization = new("Bearer", userB);
            using var fetched = await http.GetAsync("api/integration/workflows/review/runs/run-1", ct);
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            using var responded = await http.PostAsync("api/integration/workflows/review/runs/run-1/responses",
                Json("""{"portId":"approval","actorId":"forged"}"""), ct);
            Assert.Equal(HttpStatusCode.OK, responded.StatusCode);

            http.DefaultRequestHeaders.Authorization = new("Bearer", userA);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress, "mcp") },
                http, ownsHttpClient: false), cancellationToken: ct);
            var run = await mcp.CallToolAsync("openclaw.run_workflow",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["input"] = "review", ["senderId"] = "forged" }, cancellationToken: ct);
            Assert.False(run.IsError == true);
            http.DefaultRequestHeaders.Authorization = new("Bearer", userB);
            var get = await mcp.CallToolAsync("openclaw.get_workflow_run",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1" }, cancellationToken: ct);
            Assert.False(get.IsError == true);
            var respond = await mcp.CallToolAsync("openclaw.respond_workflow",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1", ["portId"] = "approval", ["actorId"] = "forged" }, cancellationToken: ct);
            Assert.False(respond.IsError == true);
            http.DefaultRequestHeaders.Authorization = new("Bearer", denied);
            using var rejected = await http.GetAsync("api/integration/workflows/review/runs/run-1", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var mcpRejected = await mcp.CallToolAsync("openclaw.get_workflow_run",
                new Dictionary<string, object?> { ["workflowId"] = "review", ["runId"] = "run-1" }, cancellationToken: ct);
            Assert.True(mcpRejected.IsError);
            Assert.Contains("workflow_user_auth_required", Assert.IsType<TextContentBlock>(Assert.Single(mcpRejected.Content)).Text, StringComparison.Ordinal);
            Assert.Equal(6, server.BackendRequests.Count);
            Assert.Equal(2, server.ExchangeRequests.Count);
            Assert.Equal(new[] { "published-a", "published-b", "published-b", "published-a", "published-b", "published-b" },
                server.BackendRequests.Select(call => call.Authorization.Replace("Bearer delegated-", "", StringComparison.Ordinal)));
            Assert.All(server.BackendRequests.Where(call => call.StartRequest is not null),
                call => Assert.Equal("published-a", call.StartRequest!.SenderId));
            Assert.All(server.BackendRequests.Where(call => call.Response is not null),
                call => Assert.Equal("published-b", call.Response!.ActorId));
            foreach (var exchange in server.ExchangeRequests)
            {
                Assert.Equal(mode == "entra-obo" ? "urn:ietf:params:oauth:grant-type:jwt-bearer" : "urn:ietf:params:oauth:grant-type:token-exchange",
                    exchange.Form["grant_type"]);
                Assert.Equal("workflow.read", exchange.Form["scope"]);
                if (mode == "entra-obo") Assert.Equal(secret, exchange.Form["client_secret"]);
                else Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client:" + secret)), exchange.Authorization);
            }
            var responses = new[] { await started.Content.ReadAsStringAsync(ct), await fetched.Content.ReadAsStringAsync(ct),
                await responded.Content.ReadAsStringAsync(ct), await rejected.Content.ReadAsStringAsync(ct),
                JsonSerializer.Serialize(run), JsonSerializer.Serialize(get), JsonSerializer.Serialize(respond) };
            AssertNoCredentials(string.Join('\n', responses), [secret, userA, userB, denied, "delegated-published-a", "delegated-published-b"]);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                process.Dispose();
            }
            try
            {
                var logs = string.Join('\n', output);
                Assert.DoesNotContain("NotSupportedException", logs, StringComparison.Ordinal);
                Assert.DoesNotContain("MissingMetadataException", logs, StringComparison.Ordinal);
                AssertNoCredentials(logs, [secret, userA, userB, denied, "delegated-published-a", "delegated-published-b", "env:WORKFLOW_PUBLISHED_SECRET"]);
                if (Directory.Exists(storage))
                    foreach (var file in Directory.EnumerateFiles(storage, "*.json*", SearchOption.AllDirectories))
                        AssertNoCredentials(await File.ReadAllTextAsync(file, CancellationToken.None),
                            [secret, userA, userB, denied, "delegated-published-a", "delegated-published-b"]);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private static void AssertNoCredentials(string text, string[] credentials)
    {
        foreach (var credential in credentials)
            Assert.False(text.Contains(credential, StringComparison.Ordinal), "Gateway output persisted or exposed a credential.");
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForReadyAsync(HttpClient client, Process process, ConcurrentQueue<string> output, string[] credentials, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 150 && !process.HasExited; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("health", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Connection refusal is expected before Kestrel starts listening.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
        var diagnostic = string.Join('\n', output);
        foreach (var credential in credentials) diagnostic = diagnostic.Replace(credential, "[REDACTED]", StringComparison.Ordinal);
        Assert.Fail("Published gateway did not become ready: " + diagnostic);
    }
}

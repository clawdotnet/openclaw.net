using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenClaw.StrategosWorkflowHost.Configuration;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyMcpServerTests
{
    [Fact]
    public async Task Mcp_Endpoint_Advertises_The_Four_Ontology_Tools_When_Enabled()
    {
        using var host = BuildTestHost(ontologyEnabled: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var toolNames = await ListToolNamesAsync(host);

        Assert.Contains("ontology_explore", toolNames);
        Assert.Contains("ontology_query", toolNames);
        Assert.Contains("ontology_action", toolNames);
        Assert.Contains("ontology_validate", toolNames);
    }

    [Fact]
    public async Task Mcp_Endpoint_Also_Advertises_The_Traversal_Tool()
    {
        // AddOntologyTools() registers the four discovered tools plus the DR-15
        // instance-anchored traversal tool. The plan predicted exactly four; pinning the
        // real count here means a future SDK/package bump that adds or drops a tool fails
        // loudly instead of silently changing what the gateway advertises.
        using var host = BuildTestHost(ontologyEnabled: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var toolNames = await ListToolNamesAsync(host);

        Assert.Contains("ontology_traverse", toolNames);
        Assert.Equal(5, toolNames.Count);
    }

    [Fact]
    public async Task Mcp_Endpoint_Is_Absent_When_Ontology_Disabled()
    {
        using var host = BuildTestHost(ontologyEnabled: false);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var client = host.GetTestClient();
        var response = await client.GetAsync("/mcp", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_Endpoint_Still_Serves_Tools_When_A_Manifest_Path_Is_Configured()
    {
        // ManifestOutputPath is bound but inert until the manifest writer lands; asserting
        // the host boots with it set keeps the "configured" path exercised from day one.
        var manifestPath = Path.Combine(Path.GetTempPath(), $"openclaw-mcpapp-{Guid.NewGuid():N}.json");

        using var host = BuildTestHost(ontologyEnabled: true, manifestOutputPath: manifestPath);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var toolNames = await ListToolNamesAsync(host);

        Assert.Contains("ontology_explore", toolNames);
    }

    /// <summary>
    /// Issues an MCP <c>tools/list</c> against the test host's <c>/mcp</c> endpoint and
    /// returns the advertised tool names. The streamable-HTTP transport answers with an
    /// SSE frame (<c>event: message</c> / <c>data: {...}</c>), not a bare JSON body, so
    /// the payload is lifted out of the <c>data:</c> line.
    /// </summary>
    private static async Task<IReadOnlySet<string>> ListToolNamesAsync(IHost host)
    {
        var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { },
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractSseData(body);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ExtractSseData(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                return trimmed["data:".Length..].Trim();
            }
        }

        // Not an SSE frame — the transport answered with a plain JSON body.
        return body;
    }

    /// <summary>
    /// Builds a minimal web host that wires the ontology + MCP server through the exact
    /// same <see cref="OntologyServerBootstrap"/> entry points <c>Program.cs</c> uses, so
    /// the test exercises the shipping code path rather than a copy of it. Postgres,
    /// Wolverine, and the workflow endpoints are deliberately absent — none of them are
    /// reachable from <c>/mcp</c>.
    /// </summary>
    internal static IHost BuildTestHost(bool ontologyEnabled, string? manifestOutputPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Strategos:Ontology:Enabled"] = ontologyEnabled ? "true" : "false",
            ["Strategos:Ontology:Port"] = "5098",
        };

        if (manifestOutputPath is not null)
        {
            settings["Strategos:Ontology:ManifestOutputPath"] = manifestOutputPath;
        }

        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(settings));
                web.ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    OntologyServerBootstrap.AddOntologyMcpServer(services, context.Configuration);
                });
                web.Configure((context, app) =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/health", () => "ok");
                        OntologyServerBootstrap.MapOntologyMcpEndpoint(endpoints, context.Configuration);
                    });
                });
            })
            .Build();
    }
}

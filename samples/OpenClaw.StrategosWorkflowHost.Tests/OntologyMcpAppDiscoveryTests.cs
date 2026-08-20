using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Tests;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

/// <summary>
/// Cross-process verification of the MCP App wiring. The gateway's real
/// <c>McpAppDiscovery</c> / <c>McpAppRegistry</c> live in <c>OpenClaw.McpApp</c>, which pins
/// ModelContextProtocol 2.0.0 — binary-incompatible with the sidecar's pinned 1.3.0 (see
/// csproj pin note and ledger ruling R5), so this test does NOT take that dependency. It
/// instead proves the two halves the gateway depends on: (1) the sidecar writes a manifest
/// whose shape satisfies the gateway's discovery validation rules, and (2) the sidecar's
/// running <c>/mcp</c> server enumerates exactly the tools the gateway would register.
/// </summary>
public class OntologyMcpAppDiscoveryTests
{
    [Fact]
    public void Sidecar_Manifest_Satisfies_Gateway_Discovery_Rules()
    {
        // The gateway's McpAppDiscovery rejects a manifest with no 'id', and requires 'url'
        // when transport is 'http'. We reproduce those rules here so a malformed manifest
        // fails this test loudly rather than only at gateway runtime.
        var manifestDir = Path.Combine(Path.GetTempPath(), $"openclaw-discovery-{Guid.NewGuid():N}");
        try
        {
            OntologyAppManifestWriter.Write(
                OntologyAppManifestWriter.ExpandPath(manifestDir),
                OntologyAppManifest.Build(new OntologyOptions { Port = 5098 }));

            var path = Path.Combine(manifestDir, "openclaw.mcpapp.json");
            Assert.True(File.Exists(path), $"Expected manifest at {path}");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Rule: 'id' is required.
            Assert.True(root.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()));
            Assert.Equal("strategos-ontology", id.GetString());

            // Rule: transport must be 'stdio' or 'http'.
            var transport = root.GetProperty("transport").GetString();
            Assert.Contains(transport, new[] { "stdio", "http" });

            // Rule: 'http' transport requires 'url'.
            if (transport == "http")
            {
                Assert.True(root.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()));
            }

            // The gateway connects to this URL; it must land on the sidecar's /mcp.
            Assert.Equal("http://127.0.0.1:5098/mcp", root.GetProperty("url").GetString());
        }
        finally
        {
            if (Directory.Exists(manifestDir)) Directory.Delete(manifestDir, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_Would_Discover_Five_Tools_From_Running_Sidecar()
    {
        // End-to-end: boot the sidecar's /mcp (the same code Program.cs runs), point a
        // manifest at its resolved address, then enumerate tools — exactly what the gateway's
        // McpAppRegistry does after McpAppDiscovery finds the file. Asserts the five tool
        // names the README advertises.
        using var host = OntologyMcpServerTests.BuildTestHost(ontologyEnabled: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        // The test client connects in-process (no real port), but we still record a manifest
        // whose url mirrors what the gateway would resolve — the loopback /mcp the sidecar
        // advertises. The tool enumeration below exercises the live /mcp regardless.
        var baseAddress = (host.GetTestClient().BaseAddress?.ToString() ?? "http://localhost").TrimEnd('/');

        var manifestDir = Path.Combine(Path.GetTempPath(), $"openclaw-discovery-live-{Guid.NewGuid():N}");
        try
        {
            var manifest = OntologyAppManifest.Build(new OntologyOptions { Port = 5098 });
            manifest["url"] = $"{baseAddress}/mcp";
            OntologyAppManifestWriter.Write(OntologyAppManifestWriter.ExpandPath(manifestDir), manifest);

            Assert.True(File.Exists(Path.Combine(manifestDir, "openclaw.mcpapp.json")));

            // Now act as the gateway: connect to the sidecar's /mcp and enumerate tools.
            var toolNames = await ListToolNamesAsync(host);

            Assert.Contains("ontology_explore", toolNames);
            Assert.Contains("ontology_query", toolNames);
            Assert.Contains("ontology_action", toolNames);
            Assert.Contains("ontology_validate", toolNames);
            Assert.Contains("ontology_traverse", toolNames);
            Assert.Equal(5, toolNames.Count);
        }
        finally
        {
            if (Directory.Exists(manifestDir)) Directory.Delete(manifestDir, recursive: true);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

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
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

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

        return body;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Conformance tests for gateway HTTP endpoints (P0).
/// Validates AOT-safe typed models serialize correctly.
/// </summary>
public class EndpointConformanceTests
{
    [Fact]
    public void HealthResponse_RoundTrips_Via_SourceGen()
    {
        var health = new HealthResponse { Status = "ok", Uptime = 12345 };
        var json = JsonSerializer.Serialize(health, CoreJsonContext.Default.HealthResponse);
        var deserialized = JsonSerializer.Deserialize(json, CoreJsonContext.Default.HealthResponse);

        Assert.NotNull(deserialized);
        Assert.Equal("ok", deserialized.Status);
        Assert.Equal(12345, deserialized.Uptime);
    }

    [Fact]
    public void HealthResponse_UsesCorrectPropertyNames()
    {
        var health = new HealthResponse { Status = "ok", Uptime = 99 };
        var json = JsonSerializer.Serialize(health, CoreJsonContext.Default.HealthResponse);

        // CoreJsonContext uses camelCase
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"uptime\"", json);
        Assert.DoesNotContain("Status", json);
    }

    [Fact]
    public void HealthResponse_MatchesExpectedShape()
    {
        var health = new HealthResponse { Status = "ok", Uptime = 42 };
        var json = JsonSerializer.Serialize(health, CoreJsonContext.Default.HealthResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("ok", status.GetString());
        Assert.True(root.TryGetProperty("uptime", out var uptime));
        Assert.Equal(42, uptime.GetInt64());
    }

    [Fact]
    public void IntegrationToolPresetsResponse_DeserializesToolCollections_Via_SourceGen()
    {
        const string json = """
            {
              "items": [
                {
                  "presetId": "web",
                  "description": "Built-in preset 'web'.",
                  "surface": "web",
                  "effectiveAutonomyMode": "interactive",
                  "requireToolApproval": true,
                  "allowedTools": ["session_search", "profile_read"],
                  "approvalRequiredTools": ["process", "automation"]
                }
              ]
            }
            """;

        IntegrationToolPresetsResponse? response = null;
        var exception = Record.Exception(() =>
            response = JsonSerializer.Deserialize(json, CoreJsonContext.Default.IntegrationToolPresetsResponse));

        Assert.Null(exception);
        Assert.NotNull(response);
        var preset = Assert.Single(response.Items);
        Assert.Contains("session_search", preset.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("profile_read", preset.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("process", preset.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("automation", preset.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);
        Assert.True(preset.AllowedTools.Contains("SESSION_SEARCH"));
        Assert.True(preset.ApprovalRequiredTools.Contains("PROCESS"));
    }

    [Fact]
    public void ReadOnlyStringSetJsonConverter_WritesNullSet_AsNull()
    {
        var converter = CreateReadOnlyStringSetConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Record.Exception(() => converter.Write(writer, null!, CoreJsonContext.Default.Options));
        writer.Flush();

        Assert.Null(exception);
        Assert.Equal("null", Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static JsonConverter<IReadOnlySet<string>> CreateReadOnlyStringSetConverter()
    {
        var converterType = typeof(ResolvedToolPreset).Assembly.GetType(
            "OpenClaw.Core.Models.ReadOnlyStringSetJsonConverter",
            throwOnError: true)!;
        return Assert.IsAssignableFrom<JsonConverter<IReadOnlySet<string>>>(
            Activator.CreateInstance(converterType, nonPublic: true));
    }
}

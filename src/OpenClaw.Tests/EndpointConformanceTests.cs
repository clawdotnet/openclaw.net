using System.Text.Json;
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
}

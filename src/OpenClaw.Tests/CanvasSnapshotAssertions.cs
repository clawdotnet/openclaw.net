using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

internal static class CanvasSnapshotAssertions
{
    public static void AssertV09Snapshot(
        string snapshotJson,
        int expectedFrameCount,
        int expectedComponentCount,
        string expectedDataModelFragment,
        string? expectedDiagnosticContains = null)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("dataModelJson", out var dataModelJson));
        Assert.Equal(JsonValueKind.String, dataModelJson.ValueKind);
        Assert.Contains(expectedDataModelFragment, dataModelJson.GetString() ?? string.Empty, StringComparison.Ordinal);

        Assert.True(root.TryGetProperty("components", out var components));
        Assert.Equal(JsonValueKind.Array, components.ValueKind);
        Assert.Equal(expectedComponentCount, components.GetArrayLength());

        Assert.True(root.TryGetProperty("frameCount", out var frameCount));
        Assert.Equal(expectedFrameCount, frameCount.GetInt32());

        Assert.True(root.TryGetProperty("diagnostics", out var diagnostics));
        Assert.Equal(JsonValueKind.Array, diagnostics.ValueKind);

        if (!string.IsNullOrWhiteSpace(expectedDiagnosticContains))
        {
            var values = diagnostics.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty)
                .ToArray();
            Assert.Contains(values, value => value.Contains(expectedDiagnosticContains, StringComparison.OrdinalIgnoreCase));
        }
    }
}
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Adapters.Nacos;

/// <summary>
/// Parses the prose envelope emitted by the upstream Nacos MCP Router's
/// <c>search_mcp_server</c> tool. The envelope wraps a JSON object keyed by
/// server name between two Chinese-language markers; this helper extracts the
/// JSON, drops everything else, and returns candidates ranked by their
/// position in the dictionary.
/// </summary>
public static class RouterCandidateParser
{
    private static readonly Regex JsonBlock = new(
        Regex.Escape(RouterProseContract.SearchListMarker) + @"(\{.*?\})\r?\n"
        + Regex.Escape(RouterProseContract.SearchStepMarker),
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<CapabilityCandidate> Parse(string prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return Array.Empty<CapabilityCandidate>();

        var match = JsonBlock.Match(prose);
        if (!match.Success) return Array.Empty<CapabilityCandidate>();

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<CapabilityCandidate>();

            var results = new List<CapabilityCandidate>();
            var rank = 1;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) { rank++; continue; }

                string name = property.Name;
                string description = "";
                if (property.Value.TryGetProperty("description", out var descNode)
                    && descNode.ValueKind == JsonValueKind.String)
                {
                    description = descNode.GetString() ?? "";
                }

                results.Add(new CapabilityCandidate(name, description, rank));
                rank++;
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<CapabilityCandidate>();
        }
    }
}

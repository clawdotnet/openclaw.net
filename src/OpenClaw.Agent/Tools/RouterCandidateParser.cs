using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Parses the prose envelope emitted by the upstream Nacos MCP Router's
/// <c>search_mcp_server</c> tool. The envelope wraps a JSON object keyed by
/// server name between two Chinese-language markers; this helper extracts the
/// JSON, drops everything else, and returns up to 5 candidates ranked by their
/// position in the dictionary.
/// </summary>
public static class RouterCandidateParser
{
    private const int MaxCandidates = 5;
    private static readonly Regex JsonBlock = new(
        @"### 1\. 当前可用的mcp server列表为：(\{.*?\})\r?\n### 2\.",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<RouterCandidate> Parse(string prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return Array.Empty<RouterCandidate>();

        var match = JsonBlock.Match(prose);
        if (!match.Success) return Array.Empty<RouterCandidate>();

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<RouterCandidate>();

            var results = new List<RouterCandidate>(MaxCandidates);
            var rank = 1;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (rank > MaxCandidates) break;
                if (property.Value.ValueKind != JsonValueKind.Object) { rank++; continue; }

                string name = property.Name;
                string description = "";
                if (property.Value.TryGetProperty("description", out var descNode)
                    && descNode.ValueKind == JsonValueKind.String)
                {
                    description = descNode.GetString() ?? "";
                }

                results.Add(new RouterCandidate(name, description, 1.0 / rank));
                rank++;
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<RouterCandidate>();
        }
    }
}

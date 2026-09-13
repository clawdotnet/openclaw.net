using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

public sealed class ResolveCapabilityTool : ITool
{
    private readonly McpServerToolRegistry _registry;

    public ResolveCapabilityTool(McpServerToolRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "resolve_capability";

    public string Description =>
        "Resolve an intent into a Router-bound server+tool+schema. Zero LLM round-trips.";

    public string ParameterSchema =>
        "{\"type\":\"object\",\"required\":[\"task_description\"],\"properties\":{" +
        "\"task_description\":{\"type\":\"string\"}," +
        "\"key_words\":{\"type\":\"string\"}," +
        "\"selection_policy\":{\"type\":\"string\",\"enum\":[\"first\",\"exact_name\"]}" +
        "}}";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var request = ParseRequest(argumentsJson);
        var client = _registry.GetClientByServerId("nacos-mcp-router");
        if (client is null)
            return JsonFail(ResolveCapabilityFailureCodes.RouterUnavailable, Array.Empty<RouterCandidate>());

        var searchText = await CallToolAsStringAsync(client, "search_mcp_server",
            new Dictionary<string, JsonElement>
            {
                ["task_description"] = JsonSerializer.SerializeToElement(request.TaskDescription, ResolveCapabilitySerializerContext.Default.String),
                ["key_words"] = JsonSerializer.SerializeToElement(request.KeyWords ?? "", ResolveCapabilitySerializerContext.Default.String),
            }, ct);

        var candidates = RouterCandidateParser.Parse(searchText);
        if (candidates.Count == 0)
            return JsonFail(ResolveCapabilityFailureCodes.NoCandidates, Array.Empty<RouterCandidate>());

        var picked = PickCandidates(candidates, request).ToList();
        if (picked.Count == 0)
            return JsonFail(ResolveCapabilityFailureCodes.SelectionPolicyNoMatch, Array.Empty<RouterCandidate>());

        var tried = new List<RouterCandidate>();
        foreach (var candidate in picked)
        {
            tried.Add(candidate);
            var addText = await CallToolAsStringAsync(client, "add_mcp_server",
                new Dictionary<string, JsonElement>
                {
                    ["mcp_server_name"] = JsonSerializer.SerializeToElement(candidate.Name, ResolveCapabilitySerializerContext.Default.String),
                }, ct);

            if (TryExtractTool(addText, out var toolName, out var schema))
                return JsonBinding(candidate, toolName, schema, tried);
        }

        return JsonFail(ResolveCapabilityFailureCodes.AllAddsFailed, tried);
    }

    private static IEnumerable<RouterCandidate> PickCandidates(
        IReadOnlyList<RouterCandidate> candidates,
        ResolveCapabilityRequest request)
    {
        if (request.SelectionPolicy == ResolveCapabilitySelectionPolicy.ExactName)
        {
            return candidates.Where(c =>
                string.Equals(c.Name, request.TaskDescription, StringComparison.OrdinalIgnoreCase));
        }
        return new[] { candidates[0] };
    }

    private static bool TryExtractTool(string addResponse, out string toolName, out string schema)
    {
        toolName = "";
        schema = "";
        if (string.IsNullOrEmpty(addResponse) || !addResponse.Contains("安装完成"))
            return false;

        var match = Regex.Match(
            addResponse, @"tool 列表为: (\[.*\])", RegexOptions.Singleline);
        if (!match.Success) return false;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return false;
            if (!first.TryGetProperty("name", out var nameNode)
                || nameNode.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            toolName = nameNode.GetString() ?? "";
            schema = first.TryGetProperty("inputSchema", out var s)
                ? s.GetRawText()
                : "{}";
            return !string.IsNullOrEmpty(toolName);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static ResolveCapabilityRequest ParseRequest(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;
        var task = root.GetProperty("task_description").GetString()
            ?? throw new ArgumentException("task_description is required");
        string? keywords = root.TryGetProperty("key_words", out var kw) ? kw.GetString() : null;
        var policy = ResolveCapabilitySelectionPolicy.First;
        if (root.TryGetProperty("selection_policy", out var sp) && sp.ValueKind == JsonValueKind.String)
        {
            policy = sp.GetString() switch
            {
                "exact_name" => ResolveCapabilitySelectionPolicy.ExactName,
                _ => ResolveCapabilitySelectionPolicy.First,
            };
        }
        return new ResolveCapabilityRequest(task, keywords, policy);
    }

    private static async Task<string> CallToolAsStringAsync(
        McpClient client, string toolName, Dictionary<string, JsonElement> args, CancellationToken ct)
    {
        var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
            RequestMethods.ToolsCall,
            new CallToolRequestParams { Name = toolName, Arguments = args },
            cancellationToken: ct);
        var parts = new List<string>();
        foreach (var c in response.Content ?? []) if (c is TextContentBlock t) parts.Add(t.Text);
        return string.Join("\n", parts);
    }

    private static string JsonFail(string code, IReadOnlyList<RouterCandidate> tried) =>
        JsonSerializer.Serialize(new ResolveCapabilityFailure(code, tried), ResolveCapabilitySerializerContext.Default.ResolveCapabilityFailure);

    private static string JsonBinding(RouterCandidate chosen, string tool, string schema, IReadOnlyList<RouterCandidate> tried) =>
        JsonSerializer.Serialize(new ResolveCapabilityBinding(chosen.Name, tool, schema, tried), ResolveCapabilitySerializerContext.Default.ResolveCapabilityBinding);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ResolveCapabilityBinding))]
[JsonSerializable(typeof(ResolveCapabilityFailure))]
[JsonSerializable(typeof(RouterCandidate))]
[JsonSerializable(typeof(string))]
internal sealed partial class ResolveCapabilitySerializerContext : JsonSerializerContext;

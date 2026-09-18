using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;
namespace OpenClaw.Adapters.Nacos;

public sealed class NacosCapabilityProvider(McpServerToolRegistry registry, string serverId = "nacos-mcp-router", IReadOnlySet<string>? retrySafeTargets = null) : ICapabilityProvider
{
    public static NacosCapabilityProvider FromSettings(McpServerToolRegistry registry, IReadOnlyDictionary<string, JsonElement> settings)
    {
        var retrySafe = new HashSet<string>(StringComparer.Ordinal);
        if (settings.TryGetValue("nacos", out var options) && options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty("retrySafeTargets", out var targets) && targets.ValueKind == JsonValueKind.Array)
            foreach (var target in targets.EnumerateArray())
                if (target.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(target.GetString()))
                    retrySafe.Add(target.GetString()!);
        return new(registry, retrySafeTargets: retrySafe);
    }
    public string Id => "nacos";
    private McpClient Client => registry.GetClientByServerId(serverId)
        ?? throw new ToolOutcomeException("Nacos Router is unavailable.", "failed", CapabilitySlotFailureCodes.ProviderUnavailable, "Router not registered");
    public async Task<IReadOnlyList<CapabilityCandidate>> DiscoverAsync(ResolveCapabilityRequest request, CancellationToken ct)
    {
        var result = await CallToolAsync(Client, "search_mcp_server", new()
        {
            ["task_description"] = JsonSerializer.SerializeToElement(request.TaskDescription, WireJson.Default.String),
            ["key_words"] = JsonSerializer.SerializeToElement(request.KeyWords ?? "", WireJson.Default.String)
        }, ct);
        if (!result.Reached || result.IsError) throw Unavailable();
        return RouterCandidateParser.Parse(result.Text);
    }
    public async Task<CapabilityTarget?> BindAsync(string target, string? tool, CancellationToken ct)
    {
        var result = await CallToolAsync(Client, "add_mcp_server", new()
        {
            ["mcp_server_name"] = JsonSerializer.SerializeToElement(target, WireJson.Default.String)
        }, ct);
        if (!result.Reached) throw Unavailable();
        if (result.IsError || !TryExtractTool(result.Text, out var discoveredTool, out var schema)) return null;
        // A pinned tool must be present in the actual tool list, not just the first result.
        if (tool is not null && tool != discoveredTool)
        {
            var match = Regex.Match(result.Text, Regex.Escape(RouterProseContract.AddToolListMarker) + @"(\[.*\])", RegexOptions.Singleline);
            using var list = JsonDocument.Parse(match.Groups[1].Value);
            var found = list.RootElement.EnumerateArray().FirstOrDefault(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == tool);
            if (found.ValueKind != JsonValueKind.Object) return null;
            schema = found.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";
        }
        return new CapabilityTarget(target, new BoundTool(this, target, tool ?? discoveredTool, schema), retrySafeTargets?.Contains(target + "/" + (tool ?? discoveredTool)) == true) { ToolId = tool ?? discoveredTool };
    }
    private static ToolOutcomeException Unavailable() => new("Capability provider unavailable", "failed", CapabilitySlotFailureCodes.ProviderUnavailable, "Router transport unavailable");
    private sealed class BoundTool(NacosCapabilityProvider provider, string server, string remoteName, string schema) : IToolWithContext
    {
        public string Name => $"capability:nacos:{Uri.EscapeDataString(server)}:{Uri.EscapeDataString(remoteName)}";
        public string Description => $"Invoke {server}/{remoteName} through the configured Nacos adapter";
        public string ParameterSchema => schema;
        public ValueTask<string> ExecuteAsync(string args, CancellationToken ct) => ExecuteAsync(args, null!, ct);
        public async ValueTask<string> ExecuteAsync(string args, ToolExecutionContext context, CancellationToken ct)
        {
            var result = await CallToolAsync(provider.Client, "use_tool", new()
            {
                ["mcp_server_name"] = JsonSerializer.SerializeToElement(server, WireJson.Default.String),
                ["mcp_tool_name"] = JsonSerializer.SerializeToElement(remoteName, WireJson.Default.String),
                ["params"] = JsonSerializer.SerializeToElement(args, WireJson.Default.String)
            }, ct, context);
            if (!result.Reached) throw Unavailable();
            var text = StripUseToolShell(result.Text);
            if (result.IsError || text.StartsWith(RouterProseContract.UseFailureMarker, StringComparison.Ordinal))
                throw new ToolOutcomeException(text, "failed", CapabilitySlotFailureCodes.ExecutionFailed, text);
            return text;
        }
    }
    internal static bool TryExtractTool(string addResponse, out string toolName, out string schema)
    {
        toolName = "";
        schema = "";
        if (string.IsNullOrEmpty(addResponse) || !addResponse.Contains(RouterProseContract.AddSuccessMarker))
            return false;

        var match = Regex.Match(
            addResponse, Regex.Escape(RouterProseContract.AddToolListMarker) + @"(\[.*\])", RegexOptions.Singleline);
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

    internal readonly record struct ToolCallOutcome(bool Reached, bool IsError, string Text);

    /// <summary>
    /// Invokes a Router tool and normalises failures instead of letting them
    /// escape: transport/protocol exceptions become <c>Reached=false</c>, a
    /// protocol-level error result becomes <c>IsError=true</c>, and only
    /// caller-initiated cancellation propagates.
    /// </summary>
    internal static async Task<ToolCallOutcome> CallToolAsync(
        McpClient client, string toolName, Dictionary<string, JsonElement> args, CancellationToken ct, ToolExecutionContext? context = null)
    {
        try
        {
            var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall,
                new CallToolRequestParams { Name = toolName, Arguments = args, Meta = context is null ? null : new System.Text.Json.Nodes.JsonObject { ["userId"] = context.Session.AuthenticatedUserId ?? context.Session.SenderId, ["sessionId"] = context.Session.Id } },
                cancellationToken: ct);
            var parts = new List<string>();
            foreach (var c in response.Content ?? []) if (c is TextContentBlock t) parts.Add(t.Text);
            return new ToolCallOutcome(true, response.IsError ?? false, string.Join("\n", parts));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Transport-level timeout/abort, not caller cancellation.
            return new ToolCallOutcome(false, true, "");
        }
        catch (McpException)
        {
            return new ToolCallOutcome(false, true, "");
        }
        catch (HttpRequestException)
        {
            return new ToolCallOutcome(false, true, "");
        }
    }

    internal static string StripUseToolShell(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("[TextContent(", StringComparison.Ordinal))
            return text;

        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var markerIndex = text.IndexOf("text=", i, StringComparison.Ordinal);
            if (markerIndex < 0)
                break;
            var valueStart = markerIndex + "text=".Length;
            if (valueStart >= text.Length)
                break;
            var quote = text[valueStart];
            if (quote is not ('\'' or '"'))
            {
                i = markerIndex + 1;
                continue;
            }

            var sb = new StringBuilder();
            var j = valueStart + 1;
            while (j < text.Length)
            {
                var ch = text[j];
                if (ch == '\\' && j + 1 < text.Length)
                {
                    var next = text[j + 1];
                    switch (next)
                    {
                        case '\'': sb.Append('\''); j += 2; continue;
                        case '"': sb.Append('"'); j += 2; continue;
                        case '\\': sb.Append('\\'); j += 2; continue;
                        case 'n': sb.Append('\n'); j += 2; continue;
                        case 't': sb.Append('\t'); j += 2; continue;
                        case 'r': sb.Append('\r'); j += 2; continue;
                        default:
                            if (next == 'u' && j + 6 < text.Length &&
                                ushort.TryParse(text.AsSpan(j + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                j += 6;
                                continue;
                            }
                            sb.Append(next);
                            j += 2;
                            continue;
                    }
                }
                if (ch == quote)
                    break;
                sb.Append(ch);
                j++;
            }

            if (sb.Length > 0)
                parts.Add(sb.ToString());
            i = j + 1;
        }

        return parts.Count == 0 ? text : string.Join("\n", parts);
    }

}
[JsonSerializable(typeof(string))]
internal sealed partial class WireJson : JsonSerializerContext;

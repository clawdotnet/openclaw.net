using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Executes capability slots (issue #231) for meta-DAG tool_call steps.
/// Static bindings auto-add the pinned server once per executor instance
/// (idempotent runtime cache), then proxy every call through the Router's
/// use_tool. Dynamic bindings resolve the intent through the shared
/// <see cref="ResolveCapabilityTool"/> core, then use_tool the bound tool.
/// Dynamic bindings are cached per session via the injected
/// <see cref="CapabilityBindingCache"/> (TTL + reload invalidation, issue #232).
/// Plain-text Router failures and protocol errors are normalised into
/// <see cref="CapabilitySlotFailureCodes"/> so the meta failure-branch
/// machinery routes them deterministically. Never calls an LLM.
/// </summary>
public sealed class CapabilitySlotExecutor
{
    private const string RouterServerId = "nacos-mcp-router";

    private readonly McpServerToolRegistry _registry;
    private readonly CapabilityBindingCache _bindingCache;

    // Runtime-level add cache: a server is added at most once per executor
    // instance; the Router's own add is idempotent, so a re-add on failure
    // is always safe (the cache only records successes).
    private readonly ConcurrentDictionary<string, byte> _addedServers = new(StringComparer.Ordinal);

    public CapabilitySlotExecutor(McpServerToolRegistry registry, CapabilityBindingCache bindingCache)
    {
        _registry = registry;
        _bindingCache = bindingCache;
    }

    /// <summary>
    /// Executes a capability slot. <paramref name="toolArgsJson"/> carries the
    /// inner tool's arguments (the resolved step tool_args); the executor
    /// serialises them onto the Router's <c>params</c> wire field.
    /// <paramref name="sessionId"/> scopes the dynamic binding cache; a null or
    /// empty id skips caching (resolve fresh, store nothing).
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        MetaCapabilityRefDefinition capabilityRef, string toolArgsJson, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);

        var client = _registry.GetClientByServerId(RouterServerId);
        if (client is null)
        {
            return Fail(CapabilitySlotFailureCodes.RouterUnavailable,
                $"Nacos MCP Router '{RouterServerId}' is not registered; configure the Router server before executing capability slots.",
                toolArgsJson);
        }

        string server;
        string tool;
        if (capabilityRef.Binding == "static")
        {
            var pinned = capabilityRef.Static!;
            server = pinned.McpServerName;
            tool = pinned.ToolName;
            var addFailure = await EnsureAddedAsync(client, server, ct);
            if (addFailure is not null)
                return addFailure;
        }
        else
        {
            var intent = capabilityRef.Intent!;
            var request = new ResolveCapabilityRequest(
                intent.TaskDescription,
                intent.Keywords.Count > 0 ? string.Join(",", intent.Keywords) : null,
                capabilityRef.SelectionPolicy == "exact_name"
                    ? ResolveCapabilitySelectionPolicy.ExactName
                    : ResolveCapabilitySelectionPolicy.First);

            var intentKey = CapabilityBindingCache.ComputeIntentKey(
                request.TaskDescription, request.KeyWords, request.SelectionPolicy.ToString());
            if (!string.IsNullOrEmpty(sessionId) &&
                _bindingCache.TryGet(sessionId, intentKey, out var cachedServer, out var cachedTool))
            {
                server = cachedServer;
                tool = cachedTool;
            }
            else
            {
                var (binding, failure) = await ResolveCapabilityTool.ResolveCoreAsync(_registry, request, ct);
                if (binding is null)
                {
                    var code = failure!.FailureCode == ResolveCapabilityFailureCodes.RouterUnavailable
                        ? CapabilitySlotFailureCodes.RouterUnavailable
                        : CapabilitySlotFailureCodes.ResolveFailed;
                    return Fail(code, $"capability resolve failed: {failure.FailureCode}", toolArgsJson);
                }

                server = binding.Server;
                tool = binding.Tool;
                if (!string.IsNullOrEmpty(sessionId))
                    _bindingCache.Set(sessionId, intentKey, server, tool);
            }
        }

        return await UseToolAsync(client, server, tool, toolArgsJson, ct);
    }

    private async Task<ToolExecutionResult?> EnsureAddedAsync(McpClient client, string server, CancellationToken ct)
    {
        if (_addedServers.ContainsKey(server))
            return null;

        var add = await ResolveCapabilityTool.CallToolAsync(client, "add_mcp_server",
            new Dictionary<string, JsonElement>
            {
                ["mcp_server_name"] = JsonSerializer.SerializeToElement(server, CapabilitySlotSerializerContext.Default.String),
            }, ct);
        if (!add.Reached)
        {
            return Fail(CapabilitySlotFailureCodes.RouterUnavailable,
                $"add_mcp_server never reached the Router for '{server}'.", "{}");
        }
        if (add.IsError)
        {
            return Fail(CapabilitySlotFailureCodes.AddFailed,
                $"add_mcp_server reported a protocol error for '{server}': {add.Text}", "{}");
        }
        if (!add.Text.Contains(RouterProseContract.AddSuccessMarker, StringComparison.Ordinal))
        {
            return Fail(CapabilitySlotFailureCodes.AddFailed,
                $"add_mcp_server failed for '{server}': {add.Text}", "{}");
        }

        _addedServers[server] = 1;
        return null;
    }

    private static async Task<ToolExecutionResult> UseToolAsync(
        McpClient client, string server, string tool, string toolArgsJson, CancellationToken ct)
    {
        var use = await ResolveCapabilityTool.CallToolAsync(client, "use_tool",
            new Dictionary<string, JsonElement>
            {
                ["mcp_server_name"] = JsonSerializer.SerializeToElement(server, CapabilitySlotSerializerContext.Default.String),
                ["mcp_tool_name"] = JsonSerializer.SerializeToElement(tool, CapabilitySlotSerializerContext.Default.String),
                // Upstream declares `params` as a JSON-encoded string and
                // json.loads it before dispatch; the slot's tool_args ride along
                // as that encoded string.
                ["params"] = JsonSerializer.SerializeToElement(toolArgsJson, CapabilitySlotSerializerContext.Default.String),
            }, ct);
        if (!use.Reached)
        {
            return Fail(CapabilitySlotFailureCodes.RouterUnavailable,
                $"use_tool never reached the Router for '{server}.{tool}'.", toolArgsJson);
        }

        var text = StripUseToolShell(use.Text);
        if (use.IsError)
        {
            return Fail(CapabilitySlotFailureCodes.UseToolFailed,
                $"use_tool reported a protocol error for '{server}.{tool}': {text}", toolArgsJson);
        }
        // The live Router reports some use failures as plain text, not MCP
        // protocol errors; the pinned prose marker normalises them here.
        if (text.StartsWith(RouterProseContract.UseFailureMarker, StringComparison.Ordinal))
        {
            return Fail(CapabilitySlotFailureCodes.UseToolFailed,
                $"use_tool failed for '{server}.{tool}': {text}", toolArgsJson);
        }

        return Completed(text, toolArgsJson, server, tool);
    }

    /// <summary>
    /// Strips the Python repr shell the live Router wraps around use_tool
    /// results (<c>str(response.content)</c>), e.g.
    /// <c>[TextContent(type='text', text='{...}', annotations=None, meta=None)]</c>.
    /// Plain text passes through unchanged; multiple blocks are joined.
    /// </summary>
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

    private static ToolExecutionResult Fail(string code, string message, string arguments) => new()
    {
        Invocation = new ToolInvocation
        {
            ToolName = "capability",
            Arguments = arguments,
            Result = message,
            ResultStatus = ToolResultStatuses.Failed,
            FailureCode = code,
            FailureMessage = message
        },
        ResultText = message,
        ResultStatus = ToolResultStatuses.Failed,
        FailureCode = code,
        FailureMessage = message
    };

    private static ToolExecutionResult Completed(string text, string arguments, string server, string tool) => new()
    {
        Invocation = new ToolInvocation
        {
            ToolName = $"capability:{server}.{tool}",
            Arguments = arguments,
            Result = text,
            ResultStatus = ToolResultStatuses.Completed
        },
        ResultText = text,
        ResultStatus = ToolResultStatuses.Completed
    };
}

[JsonSerializable(typeof(string))]
internal sealed partial class CapabilitySlotSerializerContext : JsonSerializerContext;

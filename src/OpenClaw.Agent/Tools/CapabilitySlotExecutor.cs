using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// Wipes the runtime-level "already added" cache so the next slot execution
    /// re-runs <c>add_mcp_server</c>. Invoked by
    /// <c>McpWorkspaceWatcherService</c> after a mcp.json reload or a Nacos
    /// config-change event (issue #238) so statically bound slots re-bind
    /// against the post-reload server registry.
    /// </summary>
    public void ClearRuntimeCache() => _addedServers.Clear();

    /// <summary>
    /// Diagnostic surface for tests and observability — number of servers
    /// currently known to be added on this executor instance.
    /// </summary>
    internal int AddedServerCount => _addedServers.Count;

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

        var bindingSw = Stopwatch.StartNew();
        var trajectory = BuildTrajectory(capabilityRef);

        var client = _registry.GetClientByServerId(RouterServerId);
        if (client is null)
        {
            trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
            return Fail(CapabilitySlotFailureCodes.RouterUnavailable,
                $"Nacos MCP Router '{RouterServerId}' is not registered; configure the Router server before executing capability slots.",
                toolArgsJson, trajectory);
        }

        string server;
        string tool;
        if (capabilityRef.Binding == "static")
        {
            var pinned = capabilityRef.Static!;
            server = pinned.McpServerName;
            tool = pinned.ToolName;
            var addFailure = await EnsureAddedAsync(client, server, trajectory, ct);
            if (addFailure is not null)
            {
                trajectory.Server = server;
                trajectory.Tool = tool;
                trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
                return addFailure;
            }
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
                trajectory.CacheHit = true;
            }
            else
            {
                var (binding, failure, candidates) = await ResolveCapabilityTool.ResolveCoreAsync(_registry, request, ct);
                if (binding is null)
                {
                    var code = failure!.FailureCode == ResolveCapabilityFailureCodes.RouterUnavailable
                        ? CapabilitySlotFailureCodes.RouterUnavailable
                        : CapabilitySlotFailureCodes.ResolveFailed;
                    trajectory.Candidates = ToTrajectoryCandidates(candidates);
                    trajectory.Attempted = ToTrajectoryCandidates(failure.TriedCandidates);
                    trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
                    return Fail(code, $"capability resolve failed: {failure.FailureCode}", toolArgsJson, trajectory);
                }

                server = binding.Server;
                tool = binding.Tool;
                trajectory.Candidates = ToTrajectoryCandidates(candidates);
                trajectory.Attempted = ToTrajectoryCandidates(binding.TriedCandidates);
                if (!string.IsNullOrEmpty(sessionId))
                    _bindingCache.Set(sessionId, intentKey, server, tool);
            }
        }

        trajectory.Server = server;
        trajectory.Tool = tool;
        trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
        return await UseToolAsync(client, server, tool, toolArgsJson, trajectory, ct);
    }

    private static CapabilityBindingTrajectory BuildTrajectory(MetaCapabilityRefDefinition capabilityRef)
    {
        var trajectory = new CapabilityBindingTrajectory { Binding = capabilityRef.Binding };
        if (capabilityRef.Binding == "dynamic" && capabilityRef.Intent is { } intent)
        {
            trajectory.TaskDescription = intent.TaskDescription;
            trajectory.KeyWords = intent.Keywords.Count > 0 ? string.Join(",", intent.Keywords) : null;
            trajectory.SelectionPolicy = capabilityRef.SelectionPolicy == "exact_name" ? "exact_name" : "first";
            trajectory.IntentKey = CapabilityBindingCache.ComputeIntentKey(
                intent.TaskDescription, trajectory.KeyWords, capabilityRef.SelectionPolicy == "exact_name" ? "ExactName" : "First");
        }
        return trajectory;
    }

    private static List<CapabilityBindingCandidate> ToTrajectoryCandidates(IReadOnlyList<RouterCandidate> candidates)
        => candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();

    private async Task<ToolExecutionResult?> EnsureAddedAsync(McpClient client, string server, CapabilityBindingTrajectory trajectory, CancellationToken ct)
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
                $"add_mcp_server never reached the Router for '{server}'.", "{}", trajectory);
        }
        if (add.IsError)
        {
            return Fail(CapabilitySlotFailureCodes.AddFailed,
                $"add_mcp_server reported a protocol error for '{server}': {add.Text}", "{}", trajectory);
        }
        if (!add.Text.Contains(RouterProseContract.AddSuccessMarker, StringComparison.Ordinal))
        {
            return Fail(CapabilitySlotFailureCodes.AddFailed,
                $"add_mcp_server failed for '{server}': {add.Text}", "{}", trajectory);
        }

        _addedServers[server] = 1;
        return null;
    }

    private static async Task<ToolExecutionResult> UseToolAsync(
        McpClient client, string server, string tool, string toolArgsJson, CapabilityBindingTrajectory trajectory, CancellationToken ct)
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
                $"use_tool never reached the Router for '{server}.{tool}'.", toolArgsJson, trajectory);
        }

        var text = StripUseToolShell(use.Text);
        if (use.IsError)
        {
            return Fail(CapabilitySlotFailureCodes.UseToolFailed,
                $"use_tool reported a protocol error for '{server}.{tool}': {text}", toolArgsJson, trajectory);
        }
        // The live Router reports some use failures as plain text, not MCP
        // protocol errors; the pinned prose marker normalises them here.
        if (text.StartsWith(RouterProseContract.UseFailureMarker, StringComparison.Ordinal))
        {
            return Fail(CapabilitySlotFailureCodes.UseToolFailed,
                $"use_tool failed for '{server}.{tool}': {text}", toolArgsJson, trajectory);
        }

        return Completed(text, toolArgsJson, server, tool, trajectory);
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

    private static ToolExecutionResult Fail(string code, string message, string arguments, CapabilityBindingTrajectory? trajectory = null) => new()
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
        FailureMessage = message,
        BindingTrajectory = trajectory
    };

    private static ToolExecutionResult Completed(string text, string arguments, string server, string tool, CapabilityBindingTrajectory? trajectory = null) => new()
    {
        Invocation = new ToolInvocation
        {
            ToolName = $"capability:{server}.{tool}",
            Arguments = arguments,
            Result = text,
            ResultStatus = ToolResultStatuses.Completed
        },
        ResultText = text,
        ResultStatus = ToolResultStatuses.Completed,
        BindingTrajectory = trajectory
    };
}

[JsonSerializable(typeof(string))]
internal sealed partial class CapabilitySlotSerializerContext : JsonSerializerContext;

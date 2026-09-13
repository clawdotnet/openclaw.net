using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace OpenClaw.Tests;

/// <summary>
/// Mock Nacos MCP Router that emulates the three upstream MCP tools
/// (<c>search_mcp_server</c>, <c>add_mcp_server</c>, <c>use_tool</c>) documented in
/// epic #228 / child #229. The fixture is intentionally small and
/// deterministic so the Gateway / MetaSkill integration tests can exercise
/// the full Router round trip without needing a live Nacos + Router
/// environment.
///
/// Behaviour:
/// <list type="bullet">
///   <item><description><c>search_mcp_server</c> scores every registered candidate by
///   a simple token-overlap heuristic and returns the top N (max 5).</description></item>
///   <item><description><c>add_mcp_server</c> records the requested server in the
///   in-memory book-keeping dictionary. It returns the cached tool name
///   (or <c>get_current_weather</c>) so the MetaSkill can template
///   the downstream <c>use_tool</c> call.</description></item>
///   <item><description><c>use_tool</c> requires the server to be added first; if
///   not, it throws so the meta-skill <c>on_failure</c> branch fires. The
///   <see cref="FailNextUseTool"/> test hook forces the next invocation
///   to throw regardless of state.</description></item>
/// </list>
/// </summary>
[McpServerToolType]
public sealed class FakeNacosRouterMcpTools
{
    /// <summary>Default tool name returned by <c>add_mcp_server</c> when the
    /// caller did not pass an explicit one. Mirrors the real Router payload
    /// shape so the MetaSkill template stays valid in PoC mode.</summary>
    public const string DefaultToolName = "get_current_weather";

    /// <summary>Default upstream weather payload returned by <c>use_tool</c>.</summary>
    public const string DefaultToolPayload = "{\"tempC\":21,\"condition\":\"clear\"}";

    private static readonly IReadOnlyList<RouterCandidate> Catalog = new[]
    {
        new RouterCandidate("weather-mcp", DefaultToolName, "Current weather + 3-day forecast for a city."),
        new RouterCandidate("weather-historical-mcp", "get_history", "Historical daily weather archive."),
        new RouterCandidate("air-quality-mcp", "get_aqi", "Air quality index by city."),
        new RouterCandidate("geo-mcp", "geocode", "Forward and reverse geocoding."),
        new RouterCandidate("calendar-mcp", "list_events", "Calendar events near a location."),
        new RouterCandidate("maps-mcp", "search_place", "POI search via map provider."),
        new RouterCandidate("transit-mcp", "next_departures", "Next transit departures near a coordinate."),
        new RouterCandidate("news-mcp", "headlines_by_topic", "Top headlines filtered by topic.")
    };

    private readonly ConcurrentDictionary<string, RouterBinding> _bindings = new(StringComparer.Ordinal);
    private int _failNextUseTool;

    /// <summary>Snapshot of all <c>add_mcp_server</c> calls, in insertion order.</summary>
    public IReadOnlyList<RouterBinding> Bindings => _bindings.Values.OrderBy(b => b.AddedAtUtc).ToList();

    /// <summary>Total <c>use_tool</c> invocations that succeeded.</summary>
    public int SuccessfulUseToolCalls => _bindings.Values.Sum(b => b.UseToolCalls);

    /// <summary>Forces the next <c>use_tool</c> call to throw so tests can
    /// validate the MetaSkill <c>on_failure</c> path even when the binding
    /// exists. The counter is decremented after every invocation.</summary>
    public void FailNextUseTool() => Interlocked.Increment(ref _failNextUseTool);

    [McpServerTool(Name = "search_mcp_server", ReadOnly = true), Description("Search the Nacos MCP catalog and return the top-N matching MCP servers.")]
    public string SearchMcpServer(
        [Description("Free-text query, e.g. 'weather forecast'")] string query,
        [Description("Maximum number of candidates to return (default 5, hard cap 5).")] int? limit = null)
    {
        var requested = limit is > 0 ? Math.Min(limit.Value, 5) : 5;
        var tokens = Tokenize(query);

        var scored = Catalog
            .Select(c => new { Candidate = c, Score = ScoreCandidate(c, tokens) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Name, StringComparer.Ordinal)
            .Take(requested)
            .Select(x => new
            {
                name = x.Candidate.Name,
                description = x.Candidate.Description,
                primary_tool = x.Candidate.PrimaryTool,
                score = x.Score
            })
            .ToList();

        var payload = new
        {
            query,
            limit = requested,
            total_candidates = Catalog.Count,
            candidates = scored
        };
        return JsonSerializer.Serialize(payload);
    }

    [McpServerTool(Name = "add_mcp_server", ReadOnly = true), Description("Bind a Nacos MCP server into the local session. Idempotent.")]
    public string AddMcpServer(
        [Description("Logical Nacos MCP server name, e.g. 'weather-mcp'.")] string mcp_server_name,
        [Description("Optional explicit tool to call later. Defaults to the catalog's primary_tool.")] string? tool_name = null)
    {
        if (string.IsNullOrWhiteSpace(mcp_server_name))
            throw new ArgumentException("mcp_server_name is required.", nameof(mcp_server_name));

        var match = Catalog.FirstOrDefault(c =>
            string.Equals(c.Name, mcp_server_name, StringComparison.OrdinalIgnoreCase));

        var binding = new RouterBinding(
            mcp_server_name,
            tool_name ?? match?.PrimaryTool ?? DefaultToolName,
            DateTimeOffset.UtcNow,
            0,
            match is not null);

        _bindings.AddOrUpdate(
            mcp_server_name,
            static (_, value) => value,
            static (_, existing, value) => existing with
            {
                PrimaryTool = value.PrimaryTool,
                AddedAtUtc = value.AddedAtUtc,
                IsKnown = value.IsKnown
            },
            binding);

        var payload = new
        {
            mcp_server_name,
            tool_name = binding.PrimaryTool,
            status = "added"
        };
        return JsonSerializer.Serialize(payload);
    }

    [McpServerTool(Name = "use_tool", ReadOnly = true), Description("Invoke a tool on a previously added Nacos MCP server.")]
    public string UseTool(
        [Description("Server name that was passed to add_mcp_server.")] string mcp_server_name,
        [Description("Tool name to invoke.")] string tool_name,
        [Description("JSON object with the tool's parameters.")] string? @params = null)
    {
        if (Interlocked.Exchange(ref _failNextUseTool, 0) > 0)
            throw new InvalidOperationException(
                $"FakeNacosRouterMcpTools: use_tool forced to fail for '{mcp_server_name}/{tool_name}'.");

        if (string.IsNullOrWhiteSpace(mcp_server_name) || string.IsNullOrWhiteSpace(tool_name))
            throw new ArgumentException("mcp_server_name and tool_name are required.");

        if (!_bindings.TryGetValue(mcp_server_name, out var binding))
            throw new InvalidOperationException(
                $"Server '{mcp_server_name}' was not added via add_mcp_server; call add_mcp_server first.");

        // Bump the per-binding counter atomically so concurrent tests can
        // assert the call was actually issued.
        _bindings[mcp_server_name] = binding with { UseToolCalls = binding.UseToolCalls + 1 };

        var parsedParams = string.IsNullOrWhiteSpace(@params)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(@params)
              ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var payload = new
        {
            mcp_server_name,
            tool_name,
            params_received = parsedParams,
            result = DefaultToolPayload,
            status = "ok"
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.ToLowerInvariant().Split(
                [' ', ',', ';', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double ScoreCandidate(RouterCandidate candidate, string[] tokens)
    {
        if (tokens.Length == 0)
            return 0;

        var haystack = $"{candidate.Name} {candidate.Description} {candidate.PrimaryTool}"
            .ToLowerInvariant();
        var hits = tokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
        return (double)hits / tokens.Length;
    }

    private sealed record RouterCandidate(string Name, string PrimaryTool, string Description);

    /// <summary>Snapshot of a single <c>add_mcp_server</c> invocation.</summary>
    public sealed record RouterBinding(
        string ServerName,
        string PrimaryTool,
        DateTimeOffset AddedAtUtc,
        int UseToolCalls,
        bool IsKnown);
}
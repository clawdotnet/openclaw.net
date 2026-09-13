# Nacos MCP Router PoC

Status: **mock-verified integration foundation; live acceptance pending** for
[#229](https://github.com/clawdotnet/openclaw.net/issues/229). This guide does not
claim that the Nacos deployment, discovery quality, or model-token baseline has
been validated. The resolver/schema/cache work in #230–#234 depends on that evidence.

## Contract observations

The reference is the upstream Python Router at commit
[`0ee95f4f353d6f66184dafdb3e0ffd342c4edb09`](https://github.com/nacos-group/nacos-mcp-router-python/blob/0ee95f4f353d6f66184dafdb3e0ffd342c4edb09/src/nacos_mcp_router/router.py).
These are source observations, **not a live deployment capture**:

| Tool | Arguments | Returned text |
| --- | --- | --- |
| `search_mcp_server` | `task_description`, `key_words` (comma-separated string) | Prose containing a JSON object keyed by server name; entries have `name` and `description`, no score |
| `add_mcp_server` | `mcp_server_name` | Prose containing a tool list with `name`, `description`, `inputSchema` |
| `use_tool` | `mcp_server_name`, `mcp_tool_name`, `params` (JSON-encoded string) | String representation of downstream MCP content |

Do not substitute `tool_name` for `mcp_tool_name`, assume search is a bare JSON
array, fabricate scores, or assume every failure sets MCP `isError`. The Python
Router returns some initialization/install/unhealthy/use failures as ordinary
text. Such responses cannot safely drive generic protocol-level fallback without
a Router-specific normalization contract. The mock suite distinguishes these
plain-text results from explicit MCP protocol failures.

`use_tool`'s `params` argument is declared by upstream as a JSON-encoded
string and decoded server-side via `json.loads(arguments["params"])` before
dispatch to the inner MCP tool. Callers MUST serialize the inner object to a
string before invoking — passing a nested object causes `TypeError`, which the
Router catches and returns as the plain text `failed to use tool: <tool_name>`.
SKILL.md authors: use `params: '{"key": "value"}'` (inline JSON string), never
a YAML mapping.

The server ID `nacos-mcp-router` retains its hyphens in default tool names.
Configure `toolNamePrefix` explicitly to obtain the underscore names below.

## Opt-in configuration

Keep Nacos and Router on the same host when Nacos binds only to loopback. Do not
change an existing deployment's networking or authentication for this example.
With credentials provided by your existing secret environment, start the approved
Router version using the upstream transport spelling:

```sh
: "${NACOS_ADDR:?Set the existing Nacos address}"
: "${NACOS_USERNAME:?Set your Nacos username}"
: "${NACOS_PASSWORD:?Supply through your secret environment}"
: "${NACOS_ROUTER_VERSION:?Pin the Router version tested against your deployment}"
export TRANSPORT_TYPE=streamable_http
uvx "nacos-mcp-router@${NACOS_ROUTER_VERSION}"
```

Set `NACOS_ROUTER_VERSION` to an explicitly validated package version, not an
unverified moving `latest`. Inspect startup output for the actual endpoint.
Merge this entry into `<storagePath>/mcp/mcp.json`; preserve existing servers:

```json
{
  "enabled": true,
  "servers": {
    "nacos-mcp-router": {
      "enabled": true,
      "transport": "http",
      "url": "http://127.0.0.1:8000/mcp",
      "toolNamePrefix": "nacos_mcp_router_"
    }
  }
}
```

Register a test server named `weather-mcp` exposing `get_weather` with required
string argument `city` using the deployment's supported Nacos console/API. Verify
its registration and `add_mcp_server` output before running the static example.
The referenced Windows compose deployment and architecture document are not in
this repository, so this guide deliberately does not invent administrator-init
commands or a version-specific registration payload.

The examples stay under `examples/skills/` and are not bundled or enabled by
default. Copy the two example directories into an isolated gateway workspace's
`skills/` directory, or add their parent as an extra skills directory in that
temporary configuration. Invoke `meta_invoke` with:

```json
{"skill":"nacos-router-weather","input":"Oslo"}
```

The static example binds on each invocation, then calls `use_tool`; it adds no
LLM turn. Its final output is the raw tool result. The exploration example lets
the model discover/bind/use and serves as a separate token baseline. Confirm the
registered tool names rather than assuming the mock's weather schema exists.

## Reproducible local checks

From the repository root:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests
```

The tests start a real in-process HTTP MCP endpoint. They check exact three-tool
registration, source-shaped discovery text, sequential bind/use execution in both
runtimes, repeated execution without adding backend tools, and protocol-error
fallback. They use no external credentials, model calls, Nacos server, or Docker.

For a provisioned Router with a registered weather server:

```sh
export OPENCLAW_NACOS_LIVE=1
export OPENCLAW_NACOS_ROUTER_URL=http://127.0.0.1:8000/mcp
dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~LiveRouter
```

Unset `OPENCLAW_NACOS_LIVE` for normal CI; the live check is skipped.

## Capability resolver (issue #230)

The `resolve_capability` native tool turns the model-driven three-step
chain into a deterministic code path. The model only needs to emit an
intent; binding happens in code.

Inputs:

- `task_description` (required) — the same shape the Router `search_mcp_server` accepts.
- `key_words` (optional) — comma-separated string, same wire shape as the Router.
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`); a name with no exact match fails with `failure_code: "all_adds_failed"` and an empty `tried`.

Output (success):

```json
{
  "server": "weather-mcp",
  "tool": "get_weather",
  "schema": "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}",
  "tried": [
    {"name": "weather-mcp", "description": "...", "score": 1.0}
  ]
}
```

`schema` is the upstream tool schema delivered as a JSON-encoded string — parse it before use.

Output (failure — Router failure prose is returned as JSON, not thrown):

```text
{ "failure_code": "no_candidates", "tried": [] }
{ "failure_code": "all_adds_failed", "tried": [{"name":"...","description":"...","score":0.5}, ...] }
{ "failure_code": "router_unavailable", "tried": [] }
```

Behaviour contract:

1. The tool never invokes `use_tool`; downstream DAG nodes execute the bound tool.
2. The tool never calls any LLM; round-trips are zero (test: `chat.ReceivedCalls()` empty).
3. The tool never throws on Router prose failures; they are normalised to a `failure_code`.
4. `tried` lists the candidates the resolver actually attempted to add, not all returned candidates.
5. `score` is `1.0 / rank` so the field is monotonic in the upstream's deterministic top-N ordering (upstream does not return scores; this avoids fabricating them).

## Remaining live acceptance and downstream decisions

Before closing #229 or proceeding with the dependent runtime changes:

1. Capture the real three tool schemas, success/error responses, and Router
   package version from the intended Nacos 3.2.4 deployment. Redact credentials.
2. Record the actual weather-server registration payload and reproducible startup
   commands from that deployment; the issue's Windows path is not portable.
3. Run both examples five times with the same city, model, fresh session, and
   configuration. Read actual session input/output usage; take the median of the
   five per-run totals. Record discovery quality separately. Do not use invented
   fixture token counts as a model measurement.
4. Decide how resolver results get ranking/version metadata (upstream search
   provides neither score nor version), and how prose failures become typed
   failures before implementing fallback, retries, caching, or replay.

| Measurement | Static binding | Model-driven exploration |
| --- | --- | --- |
| Live recall / selected server | Not measured | Not measured |
| Median input + output tokens (5 runs) | Not measured | Not measured |
| Router version / deployment | Not provided | Not provided |

No cache, capability slots, retry policy, or binding replay is implemented by this
PoC. Those remain separately tracked by #230–#234.

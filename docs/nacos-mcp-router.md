# Nacos MCP Router PoC

Status: **live wire-contract verified (2026-09-14); model-token baseline pending** for
[#229](https://github.com/clawdotnet/openclaw.net/issues/229). The wire contract
(envelopes, tool schemas, failure prose) was captured from a real Router 0.2.2
against a local Nacos 3.2.4 test bed; discovery quality and the model-token
baseline have not been measured. The resolver/schema/cache work in #230–#234
depends on that remaining evidence.

## Contract observations

The reference is the upstream Python Router at commit
[`0ee95f4f353d6f66184dafdb3e0ffd342c4edb09`](https://github.com/nacos-group/nacos-mcp-router-python/blob/0ee95f4f353d6f66184dafdb3e0ffd342c4edb09/src/nacos_mcp_router/router.py).
Originally source observations, re-verified on 2026-09-14 against a live capture
from Router **0.2.2** (`@latest` at capture time) on a local Nacos 3.2.4 test bed
— the envelope shapes below matched verbatim:

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

Live-capture findings (2026-09-14):

- **Dependency pin**: 0.2.2 declares `mcp>=1.9.4` with no upper bound; current
  mcp 2.x renamed the `streamablehttp_client` import and the Router crashes on
  startup. Run with `--with "mcp<2"` (see the opt-in command below). Report the
  break upstream if it still exists when you read this.
- **`use_tool` wraps its result in a Python repr**: the returned text is
  `str(response.content)` of the downstream MCP result, e.g.
  `[TextContent(type='text', text='{...}', annotations=None, meta=None)]`.
  Consumers that wrap `use_tool` must strip this shell before presenting the
  payload to a model. `resolve_capability` never calls `use_tool`, so #230 is
  unaffected.
- **Registration hard requirements** (the Router silently skips anything else):
  the registry entry must have a **non-empty `description`** (keyword search is
  a normalized substring match against it, so bilingual descriptions serve both
  Chinese intents and English test keywords) and the local server config must be
  **`mcpServers`-wrapped**: `{"mcpServers": {"<name>": {"command": ..., "args": [...]}}}`.
  A flat `{"command", "args"}` config installs fail with the plain text
  `failed to install mcp server: <name>`.

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
uvx --with "mcp<2" "nacos-mcp-router@${NACOS_ROUTER_VERSION}"
```

`--with "mcp<2"` upper-bounds the mcp SDK: Router 0.2.2 declares `mcp>=1.9.4`
unbounded, and mcp 2.x breaks its import (verified 2026-09-14).

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

Register a test server named `weather-mcp` using the deployment's Nacos
console/API. Verified on the referenced deployment (2026-09-14): a console
registration with a non-empty bilingual description and a stdio local config
wrapped as `{"mcpServers": {"weather-mcp": {"command": "uvx", "args": ["mcp-server-time"]}}}`.
`mcp-server-time` is a test-bed stand-in so the full `add_mcp_server` chain runs;
replace it with a real weather server. Verify its registration and
`add_mcp_server` output before running the static example — a live add success
envelope is `1. <name>安装完成, tool 列表为: [{name, description, inputSchema}]...`.

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
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`); a name with no exact match fails with `failure_code: "selection_policy_no_match"` and an empty `tried`.

Output (success):

```json
{
  "server": "weather-mcp",
  "tool": "get_weather",
  "schema": "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}",
  "tried": [
    {"name": "weather-mcp", "description": "...", "rank": 1}
  ]
}
```

`schema` is the upstream tool schema delivered as a JSON-encoded string — parse it before use.

Output (failure — Router failure prose is returned as JSON, not thrown):

```text
{ "failure_code": "no_candidates", "tried": [] }
{ "failure_code": "selection_policy_no_match", "tried": [] }
{ "failure_code": "all_adds_failed", "tried": [{"name":"...","description":"...","rank":1}, ...] }
{ "failure_code": "router_unavailable", "tried": [] }
```

Behaviour contract:

1. The tool never invokes `use_tool`; downstream DAG nodes execute the bound tool.
2. The tool never calls any LLM; round-trips are zero (test: `chat.ReceivedCalls()` empty).
3. The tool never throws on Router failures; prose failures, transport failures, and protocol-level `isError` results are all normalised to a `failure_code`.
   - search that fails to reach the Router (transport) or reports `isError` → `router_unavailable`.
   - add that reports `isError` fails only that candidate and continues rotation; `isError` wins over prose inspection, so a "安装完成" message inside an error result cannot bind a tool.
   - add that fails to reach the Router stops rotation → `router_unavailable` with the candidates attempted so far.
   - caller cancellation still propagates as `OperationCanceledException`.
4. `tried` lists the candidates the resolver actually attempted to add, not all returned candidates.
5. `rank` is the candidate's position in the upstream's deterministic top-N ordering. Upstream search returns no scores, so none are fabricated.

## Remaining live acceptance and downstream decisions

Before closing #229 or proceeding with the dependent runtime changes:

1. ~~Capture the real three tool schemas, success/error responses, and Router
   package version from the intended Nacos 3.2.4 deployment. Redact credentials.~~
   **Done 2026-09-14**: schemas, search/add/use envelopes and failure prose
   captured from Router 0.2.2 on the local Nacos 3.2.4 test bed; matches the
   pinned contract. No credentials in this guide or the captures.
2. ~~Record the actual weather-server registration payload and reproducible startup
   commands from that deployment; the issue's Windows path is not portable.~~
   **Done 2026-09-14**: registration payload and startup command recorded above
   (`--with "mcp<2"` pin included).
3. Run both examples five times with the same city, model, fresh session, and
   configuration. Read actual session input/output usage; take the median of the
   five per-run totals. Record discovery quality separately. Do not use invented
   fixture token counts as a model measurement.
4. Ranking metadata is the upstream positional `rank` (see above); version
   metadata remains open (upstream search provides none). Prose failures are
   now typed via the #230 `failure_code` envelope; fallback, retries, caching,
   or replay build on that.

| Measurement | Static binding | Model-driven exploration |
| --- | --- | --- |
| Live recall / selected server | Weather intent only: search found `weather-mcp` | Not measured |
| Median input + output tokens (5 runs) | Not measured | Not measured |
| Router version / deployment | 0.2.2 (`@latest`, requires `mcp<2`) / local Nacos 3.2.4, streamable_http :8000 | same |

No cache, capability slots, retry policy, or binding replay is implemented by this
PoC. Those remain separately tracked by #230–#234.

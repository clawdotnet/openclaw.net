# Nacos MCP Router PoC

Status: **live wire-contract verified (2026-09-14); model-token baseline measured
(2026-09-14)** for
[#229](https://github.com/clawdotnet/openclaw.net/issues/229); **capability
slots implemented (2026-09-14)** for #231; **node-level degradation
and retry implemented (2026-09-14)** for #233; **binding trajectory
observability and offline replay implemented (2026-09-14)** for
[#234](https://github.com/clawdotnet/openclaw.net/issues/234); **Nacos event
subscription implemented (2026-09-14)** for
[#238](https://github.com/clawdotnet/openclaw.net/issues/238). The wire contract
(envelopes, tool schemas, failure prose) was captured from a real Router 0.2.2
against a local Nacos 3.2.4 test bed, and both example skills were measured five
times against a live model (MiniMax-M2.1 via an OpenAI-compatible endpoint) with
fresh sessions; the medians and the discovery-quality caveats are recorded
below. The resolver (#230), capability slots (#231), binding trajectory
replay (#234), and Nacos event subscription (#238) are implemented on this
evidence.

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
replace it with a real weather server. Verified 2026-09-14: with this config the
live chain `search → add → use_tool` completed for `weather-mcp` (add success
envelope `1. <name>安装完成, tool 列表为: [{name, description, inputSchema}]...`,
then `use_tool` returned the backend tool result).

The examples stay under `examples/skills/` and are not bundled or enabled by
default. Copy the two example directories into an isolated gateway workspace's
`skills/` directory, or add their parent as an extra skills directory in that
temporary configuration. Invoke `meta_invoke` with:

```json
{"skill":"nacos-router-weather","input":"Oslo"}
```

Both examples are capability slots (see "Capability slots" below). The static
example auto-adds the pinned server once per runtime, then calls `use_tool` on
every invocation; the dynamic example resolves the intent through the Router's
search/add chain in code, then calls `use_tool`. Neither adds an LLM turn, and
the final output is the raw tool result. Confirm the registered tool names
rather than assuming the mock's weather schema exists.

## Reproducible local checks

From the repository root:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilitySlot|FullyQualifiedName~NacosRouterIntegrationTests
```

The tests start a real in-process HTTP MCP endpoint. They check exact three-tool
registration, source-shaped discovery text, capability-slot execution in both
runtimes (static: one cached `add`, then `use_tool` per call; dynamic:
`search → add → use`), typed failure codes (`capability_add_failed`,
`capability_use_tool_failed`, `capability_resolve_failed`), fallback routing,
and protocol-error handling. They use no external credentials, model calls,
Nacos server, or Docker.

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
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`); a name with no exact match fails with `failure_code: "selection_policy_no_match"` and an empty `tried`. Under `first`, candidates are attempted in the upstream's deterministic top-N order and a failed add rotates to the next candidate; the first successful add wins.

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
4. `tried` lists the candidates the resolver actually attempted to add, in rank order, not all returned candidates. Rotation applies to every attempted candidate: a failed add (prose or protocol) moves to the next one, and only a dead transport stops the rotation.
5. `rank` is the candidate's position in the upstream's deterministic top-N ordering. Upstream search returns no scores, so none are fabricated.

## Capability slots (issue #231)

A meta-skill `tool_call` step can declare a `capability_ref` instead of a
`tool`. The runtime executes the slot deterministically — the model never sees
the Router's tool schemas and adds no LLM round-trips.

```yaml
steps:
  - id: query
    kind: tool_call
    capability_ref:
      binding: static                # or: dynamic
      static:                        # static only
        mcp_server_name: weather-mcp
        tool_name: get_weather
      # intent:                      # dynamic only
      #   task_description: weather city
      #   keywords: [weather, city]
      selection_policy: first        # dynamic only: first | exact_name
      fallback: fallback_notice      # folded into on_failure
    tool_args:
      city: "{{ input }}"
```

Binding modes:

- **static** — the executor auto-calls `add_mcp_server` once per executor
  instance (an idempotent cache records successes only; a failed add is
  retried on the next call), then proxies every invocation through `use_tool`.
- **dynamic** — the executor resolves the intent through the same
  `resolve_capability` core as #230 (`search → add`, no LLM, no `use_tool`
  inside the resolver), then calls `use_tool` on the bound tool. Successful
  bindings are cached per session, keyed by a SHA-256 hash of the normalised
  intent (task_description + keywords + selection_policy); later slots in the
  same session reuse the binding until its TTL (default 300 s) expires or the
  workspace MCP config reloads. `resolve_capability` itself is never cached.
- **degradation** — a failed slot routes to the step's `fallback` (folded into
  `on_failure` at parse time; declaring both `capability_ref.fallback` and a
  step-level `on_failure` is rejected as `invalid_capability_ref`). `use_tool`
  failures retry per the step's `retry` policy (`max_attempts` + `backoff_ms`)
  before the fallback fires, and the failed step records its `failure_code` in
  the run's step results.

Semantics shared with the resolver:

- `selection_policy` is `first` (default) or `exact_name`, the same enum as
  `resolve_capability`.
- `tool_args` are the *inner* tool's arguments; the executor serialises them to
  the Router's `params` JSON-string wire field, so SKILL.md authors write a
  YAML mapping, never an inline JSON string.
- `fallback` is folded into the step's `on_failure` at parse time and validates
  through the existing failure-branch rules.
- The `use_tool` repr shell (see above) is stripped before the payload reaches
  the model.

Failure codes (all in `failure_code`):

| Code | Scenario |
| --- | --- |
| `capability_not_configured` | runtime has no capability slot executor |
| `capability_router_unavailable` | Router client missing or unreachable |
| `capability_add_failed` | static `add_mcp_server` failed (prose or protocol) |
| `capability_use_tool_failed` | `use_tool` failed, including `failed to use tool:` prose |
| `capability_resolve_failed` | dynamic resolution failed (`no_candidates`, `selection_policy_no_match`, `all_adds_failed`, ...) |

Schema validation rejects `top_k` / `prefer_version` with
`capabilityref_reserved_field` until the resolver supports them.

Examples: `examples/skills/nacos-router-weather` (static),
`examples/skills/nacos-router-weather-dynamic` (dynamic), and
`examples/skills/nacos-router-weather-retry` (dynamic + `retry` policy).
Each adds an `emit_text` fallback step and stays opt-in, not bundled by default.

## Binding trajectory observability (issue #234)

Every capability slot execution records a binding trajectory on the run's
step evidence (under `stepResults[].executionEvidence.capabilityBinding` in
`meta-runs --json`):

- `binding` — `static` or `dynamic`;
- intent fields (dynamic): `taskDescription`, `keyWords`, `selectionPolicy`
  (wire values `first` / `exact_name`) and `intentKey` (SHA-256 of the
  normalised intent, the session cache key);
- `cacheHit` — whether the session binding cache supplied the binding;
- `server` / `tool` — the bound pair (null when binding failed);
- `elapsedMs` — the binding phase only (excludes `use_tool`);
- `candidates` — the search Top-N in upstream rank order, and `attempted` —
  the candidates the resolver actually tried to add, as `name` + `rank`.

Upstream search returns no scores (#230), so the trajectory records the
positional `rank` — none are fabricated. Failure trajectories are recorded
too (empty candidates, null server/tool, the step's `failure_code`).

The exported run JSON deserialises through `CoreJsonContext` and replays
offline through `OpenClaw.Testing`: `CapabilityBindingReplayFixture.FromMetaRun`
extracts the recorded trajectory, and `CapabilityBindingReplay` re-executes
the slot against a deterministic router harness and asserts the same binding
(same-input → same-binding; cache-hit fixtures are reproduced by seeding the
cache with the recorded binding). See
`src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` for the loop.

## Nacos event subscription (issue #238)

The Gateway subscribes to Nacos configuration changes for the MCP workspace
file and funnels them into the existing reload path. A
`NacosConfigSubscriptionService` (built on the `RedNb.Nacos.All 2.0.0` SDK's
long-polling listener) registers for the `openclaw-mcp.json` dataId in
`DEFAULT_GROUP` at startup; on change it calls the
`McpWorkspaceWatcherService` reload trigger, which re-runs the workspace
reload and clears **both** caches — the session binding cache (#232) and the
runtime-level static "already added" cache (#231) — so the next slot
execution re-resolves and re-adds from scratch. `McpWorkspaceWatcherService`
is the single convergence point: file change, startup reload, and Nacos
events all run the same invalidation fan-out.

The subscription is opt-in through the `Nacos` configuration section
(`ServerAddr`, `DataId`, `Group`, `LongPollingTimeoutMs`, and
`Username`/`Password` from the existing Nacos credential source). When
`ServerAddr` is absent, the service degrades to a no-op and the TTL/reload
fallback (#232) remains the only invalidation path; when Nacos is
unreachable, listener registration fails over to that same fallback and
gateway startup never blocks on Nacos. Credentials are read from
configuration only — nothing is hardcoded or committed.

Runtime requirement (JIT builds): the RedNb SDK serialises its gRPC
payloads with reflection-based System.Text.Json, and `PublishAot=true`
disables that process-wide — the SDK injects
`System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false` into
*every* gateway runtimeconfig, including plain JIT runs, which makes the
SDK throw `JsonSerializerIsReflectionDisabled`. `OpenClaw.Gateway.csproj`
therefore re-enables the switch in JIT builds only
(`JsonSerializerIsReflectionEnabledByDefault` conditioned on no
`RuntimeIdentifier`); the gateway's own paths use source-generated
contexts and are unaffected. NativeAOT builds keep the switch off — the
SDK's payload types are trimmed there, so under NativeAOT the
subscription degrades to the TTL/reload fallback (tracked as a
follow-up issue).

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
3. ~~Run both examples five times with the same city, model, fresh session, and
   configuration. Read actual session input/output usage; take the median of the
   five per-run totals. Record discovery quality separately. Do not use invented
   fixture token counts as a model measurement.~~
   **Done 2026-09-14**: MiniMax-M2.1 (OpenAI-compatible endpoint) × Oslo ×
   5 runs each, live Router 0.2.2, fresh `Session` per run, real session
   usage counters. Static example: 0 input / 0 output tokens × 5 (the DAG is
   deterministic — zero LLM confirmed live). Exploration example: median
   input **17479** / output **1181** tokens per run in the traced batch; an
   earlier untraced batch hit the 12-iteration cap with medians 29763 / 1870,
   so treat the cost as a range whose spread comes from the model's loop
   behaviour (repeated re-searches), not from the Router.
   Discovery quality was **0/5** for environmental reasons: the test-bed
   `weather-mcp` backend is `mcp-server-time` (its real tools are
   `get_current_time`/`convert_time` while its description promises weather),
   and `cn.pianam.mcp/weather-mcp-china` add intermittently returned
   `failed to install mcp server` during the measurement windows although
   serial Python probes against the same Router before and after succeeded
   (10/10). The cause is not yet pinned — Router-side registry/install
   suspicion; the Router's warning traceback would settle it. The model never
   fabricated weather: every run reported the failure per SKILL.md, and it
   self-corrected after one hallucinated `use_tool` call and one
   `meta_invoke` misuse. Tool-call traces per run are kept next to the
   measurement driver (throwaway, not in the repo).
4. Ranking metadata is the upstream positional `rank` (see above); version
   metadata remains open (upstream search provides none). Prose failures are
   now typed via the #230 `failure_code` envelope; fallback routing, candidate
   rotation, retry, and caching build on that (#231/#232/#233). Binding
   trajectory observability and offline replay are implemented (#234).
5. ~~Subscribe to Nacos config change events and wire them into binding
   invalidation (dual cache clear + workspace reload).~~
   **Done 2026-09-14**: `RedNb.Nacos.All 2.0.0` long-polling subscription on
   the mcp.json dataId; onChange clears the session binding cache and the
   runtime added-server cache and triggers the workspace watcher reload.
   Graceful no-op without `Nacos:ServerAddr`; the TTL/reload fallback stays
   active. Hand-verified live on the local Nacos 3.2.4 test bed (JIT):
   publish via `POST /nacos/v3/admin/cs/config` → `Nacos config change
   received …; triggering MCP workspace reload` plus the dual-cache clear
   logged at **+310 ms** (DoD: ≤ 2 s).

| Measurement | Static binding | Model-driven exploration |
| --- | --- | --- |
| Live recall / selected server | `weather-mcp` bound; pinned `get_weather` does not exist on the test-bed backend (`mcp-server-time` has `get_current_time`/`convert_time`) → plain-text tool error | 0/5: `weather-mcp` exposes time tools only; `weather-mcp-china` add failed during the measurement window (intermittent, cause not yet pinned) |
| Median input + output tokens (5 runs) | 0 + 0 (no LLM turn) | 17479 + 1181 (traced batch; untraced batch at the iteration cap: 29763 + 1870) |
| Router version / deployment | 0.2.2 (`@latest`, requires `mcp<2`) / local Nacos 3.2.4, streamable_http :8000 | same |

Capability slots (#231) and the capability resolver (#230) are implemented. The
static slot's auto-add cache is runtime-scoped idempotency; session-level
binding caching (#232) and node-level degradation with retry (#233) are
implemented. Binding trajectory observability and offline replay (#234) are
implemented: every slot records its full binding path (intent → candidates →
selected server/tool → cache hit → elapsed) on the run's step evidence, and
`OpenClaw.Testing` replays the exported JSON with same-binding assertions.
Nacos event subscription (#238) is implemented: config changes to the
mcp.json dataId converge on `McpWorkspaceWatcherService` and clear both
caches before the next slot execution.

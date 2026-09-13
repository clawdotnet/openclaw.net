# Nacos MCP Router Integration (PoC, Issue #229)

This page describes the proof-of-concept integration between the OpenClaw.NET
Gateway and the [Nacos MCP Router](https://www.nacos.io/) (issue
[#229](https://github.com/clawdotnet/openclaw.net/issues/229), child #1 of epic
[#228](https://github.com/clawdotnet/openclaw.net/issues/228)). The Router
sits between the Gateway and a fleet of Nacos-registered MCP servers and
exposes three stable MCP tools — `search_mcp_server`, `add_mcp_server`,
and `use_tool` — that the Gateway can compose as native meta-skill steps.

> **Status (2026-09-13).** T0 (probing the live Router contract) is
> **BLOCKED-EXTERNAL** in this commit: the local Nacos 3.2.4 compose stack at
> `E:/GitHub/RedNb.Nacos/deploy/docker-compose` (Docker, ports 8080/8848/9848)
> and the `nacos-mcp-router` HTTP endpoint at `127.0.0.1:8000/mcp` are not
> reachable from the sandboxed test environment. All other PoC steps
> (sample `mcp.json`, skill examples, mock Router fixture, integration tests,
> docs) land against the in-process
> [`FakeNacosRouterMcpTools`](../src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs)
> fixture. The Notes section at the bottom records the exact commands to run
> once the live environment is available.

## What the Router does for the Gateway

| Nacos / Router concept | OpenClaw.NET counterpart |
| --- | --- |
| MCP Server metadata (name / version / description) | `capabilityRef` payload that the future Capability Resolver will resolve (see epic #228, child #230) |
| MCP Server `description` | MetaSkill `intent` / `task_description` / `key_words` (see [`meta-skills.md`](meta-skills.md)) |
| Nacos `namespace` / `group` | DDD bounded context identifier (planned; not yet wired in this PoC) |
| `search_mcp_server` | Capability Resolver pre-flight (planned; the PoC currently routes this step to the Router directly) |
| `add_mcp_server` | Bind the upstream server into the local session before invoking tools |
| `use_tool` | The DAG step that actually calls the upstream tool |

In other words, the Router is **the indirection layer** the Gateway needs
to discover and dispatch to Nacos-registered MCP servers without baking
Nacos SDK calls into the Runtime.

## Sample `mcp.json` for the live Router

Drop the snippet below into `<workspace>/.openclaw/mcp/mcp.json`
(workspace MCP config — see [`McpConfigStore.cs`](../src/OpenClaw.Gateway/Mcp/McpConfigStore.cs))
or into the bundled `mcp.json` to enable the Router. All four environment
variables are placeholders — set them via `.env` or your secret manager,
never commit the values.

```json
{
  "enabled": true,
  "servers": {
    "nacos-mcp-router": {
      "enabled": true,
      "transport": "http",
      "url": "http://127.0.0.1:8000/mcp",
      "startupTimeoutSeconds": 30,
      "requestTimeoutSeconds": 60,
      "headers": {
          "X-Nacos-Addr":      "env:NACOS_ADDR",
          "X-Nacos-Username":  "env:NACOS_USERNAME",
          "X-Nacos-Password":  "env:NACOS_PASSWORD",
          "X-Nacos-Transport": "env:TRANSPORT_TYPE"
        }
      }
    }
  }
}
```

`NACOS_ADDR` is the upstream Nacos 3.2.4 address (typically
`http://127.0.0.1:8848`), `NACOS_USERNAME` / `NACOS_PASSWORD` are the
Nacos admin credentials (never commit them), and `TRANSPORT_TYPE` is
typically `http` for the bundled Router. The Router itself speaks MCP
streamable-HTTP at `/mcp`, so the Gateway's MCP server tool registry can
discover exactly three prefixed tools:

- `nacos-mcp-router_search_mcp_server`
- `nacos-mcp-router_add_mcp_server`
- `nacos-mcp-router_use_tool`

(The `nacos-mcp-router.` prefix is the server id with the dot replaced by
an underscore — see [`McpServerToolRegistry.ResolveToolName`](../src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs).)

## Authoring a Router-backed MetaSkill

Two PoC skills ship in [`examples/skills/`](../examples/skills/):

- [`nacos-router-weather/SKILL.md`](../examples/skills/nacos-router-weather/SKILL.md)
  — DAG: `bind` → `query` → `answer`, with `query_fallback` as the
  `on_failure` substitute for the upstream `use_tool` call.
- [`nacos-router-weather-explore/SKILL.md`](../examples/skills/nacos-router-weather-explore/SKILL.md)
  — DAG: `search` → `bind` → `query` → `answer`. Use it to benchmark
  per-step token usage against the static Resolver that epic #228 will
  ship (children #230 / #231).

The DAG constraints to remember when authoring a Router-backed MetaSkill:

1. **Do not share `on_failure` substitutes.** Two different steps must
   not point at the same `on_failure: <step_id>` target — the validator
   in `TryValidateMetaPlan` rejects it as "fallback step X is shared by
   A and B".
2. **Do not depend directly on a fallback-only step.** A regular step
   must depend on the original step (`query`), not its substitute
   (`query_fallback`). The runtime mirrors the fallback output back
   into `outputs.query` via `failureAliases` so the dependent step sees
   either path.
3. **Tool-name prefix.** The tool name in `tool_call` steps must use
   the server-id-prefixed local name (`nacos-mcp-router_use_tool`),
   not the remote name (`use_tool`). The `McpNativeTool` wrapper maps
   between them.

See [`docs/meta-skill-orchestration.md`](meta-skill-orchestration.md) and
[`docs/meta-skills.md`](meta-skills.md) for the broader MetaSkill
authoring guide.

## Token measurement recipe

To compare the PoC (live Router) against the planned Runtime Resolver,
replay the same five intents through both paths and record the
`input_tokens + output_tokens` from the meta-run replay:

| Intent | Static Resolver (planned) | Router PoC (this skill) |
| --- | --- | --- |
| "Weather forecast for Beijing" | tbd in #231 | `nacos-router-weather` (3-step DAG) |
| "Air quality index in Shanghai" | tbd in #231 | `nacos-router-weather` (3-step DAG) |
| "Tomorrow's sunrise time" | tbd in #231 | `nacos-router-weather` (3-step DAG) |
| "Compare today vs tomorrow" | tbd in #231 | `nacos-router-weather-explore` (4-step DAG) |
| "Pin a route from X to Y" | tbd in #231 | not in PoC scope (transit-mcp, post-#231) |

Run each intent five times; record median + p95. The PoC adds roughly
one extra round trip (the `add_mcp_server` bind) and one extra tool call
(the `use_tool` invocation) compared to a future direct capability call.

## Acceptance criteria coverage

| # | Criterion | Where it lives |
| --- | --- | --- |
| 1 | Mock Router exposes exactly three `nacos-mcp-router_*` tools and they are registered through `McpServerToolRegistry` | `NacosRouterIntegrationTests.RegistryAgainstFakeRouter_DiscoversExactlyThreeNacosRouterTools` |
| 2 | MetaSkill DAG `bind → query → answer` runs end-to-end with `completed` step results | `NacosRouterIntegrationTests.MetaSkill_FullHappyPath_BindQueryAnswer_AllStepsCompleted` |
| 3 | DAG validation wires `on_failure` so a failing `query` step activates the substitute branch | `NacosRouterIntegrationTests.MetaSkill_OutputContractFailure_TriggersOnFailureFallbackBranch` |
| 4 | Re-calling `RegisterToolsAsync` does not duplicate the three entries | covered by `RegistryAgainstFakeRouter_DiscoversExactlyThreeNacosRouterTools` |
| 5 | `dotnet test` passes (all existing tests still green) | run via `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj` |
| 6 | `OPENCLAW_NACOS_LIVE=1` live integration probe | documented below; not run because the live environment is BLOCKED-EXTERNAL |
| 7 | T4 documentation references the live Router workflow and tags T0 with `BLOCKED-EXTERNAL` | this page |

## Live integration probe (gated on `OPENCLAW_NACOS_LIVE`)

The tests added in [`NacosRouterIntegrationTests.cs`](../src/OpenClaw.Tests/NacosRouterIntegrationTests.cs)
all run against the in-process mock fixture. A live probe is gated on the
environment variable `OPENCLAW_NACOS_LIVE`:

```bash
# Boot the stack (see /e/GitHub/RedNb.Nacos/deploy/docker-compose/README.md)
cd /e/GitHub/RedNb.Nacos/deploy/docker-compose
umask 077
printf 'NACOS_AUTH_TOKEN=%s\nNACOS_AUTH_IDENTITY_VALUE=%s\n' \
    "$(openssl rand -base64 48 | tr -d '\n')" \
    "$(openssl rand -hex 24)" > .env
docker compose up -d

# Start the Router
uvx nacos-mcp-router@latest \
    --nacos-addr      http://127.0.0.1:8848 \
    --nacos-username  nacos \
    --nacos-password  "$NACOS_PASSWORD" \
    --transport-type  http

# Run the live probe
OPENCLAW_NACOS_LIVE=1 dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "FullyQualifiedName~NacosRouterLiveProbe"
```

The live probe test will be added in a follow-up commit once the stack is
reachable; it will reuse the same `McpServerToolRegistry` + AgentRuntime
plumbing as the mock tests and assert that:

- `search_mcp_server` returns Nacos-registered servers (not the mock catalog)
- `add_mcp_server` + `use_tool` round trip succeeds against the live Router

## Notes

- **T0 status: BLOCKED-EXTERNAL.** The Docker daemon and local
  `127.0.0.1:8080/8848/9848/8000` ports are not reachable from the
  test sandbox. All assertions in this commit land against the
  in-process fixture; the live probe is documented but not run.
- **No real credentials.** Sample `mcp.json` and tests use placeholder
  env-var references (`env:NACOS_*`); the actual `NACOS_PASSWORD` value
  must stay in the local `.env` file or your secret manager.
- **Out of scope for #229.** The Capability Resolver (`resolve_capability`),
  the capabilityRef schema (children #230 / #231), the Nacos registry
  sync (child #232), and the `meta-runs` binding (child #234) are
  follow-on issues. This PoC only demonstrates that the Gateway can
  already orchestrate Router-backed MCP tools as native MetaSkill DAG
  steps.
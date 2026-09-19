# Capability resolution

OpenClaw.NET owns deterministic capability resolution and execution. The default provider is `local`; Nacos is optional. A resolved handle is never an authorization grant.

## Ownership

| Layer | Responsibility |
| --- | --- |
| `OpenClaw.Core` | Provider, candidate, binding, invalidation, trajectory, and status contracts; no vendor SDK types |
| `OpenClaw.Agent` | Provider selection, deterministic ranking, policy-governed execution, bounded caches, circuit protection |
| `OpenClaw.Adapters.Nacos` | Router search/add/use protocol and failure normalization over the existing MCP transport |
| `OpenClaw.Adapters.Nacos.Events` | Optional Nacos SDK, configuration, listener lifecycle, generic invalidation events; JIT and NativeAOT |
| AgentQi | Ecosystem documentation, catalog curation/trust assessment, setup and operational UX |

Catalog trust is input to runtime policy, never permission to bypass local authorization, approvals, hooks, or audit. Providers implement `ICapabilityProvider`; change adapters implement `ICapabilityChangeSource` and publish through `ICapabilityInvalidationSink`.

## Use without Nacos

A `tool_call` meta-skill step can bind a registered local tool. For example, with `memory_search` available:

```yaml
- id: read
  kind: tool_call
  capability_ref:
    provider: local
    binding: static
    static:
      target: memory_search
      tool_name: memory_search
  tool_args:
    query: user-preferences
```

Dynamic selection uses `binding: dynamic` and `intent: { task_description: memory_search, keywords: [memory] }` instead of `static`. Both AgentRuntime and MafAgentRuntime execute the same capability path, without an LLM discovery call. Omitting `provider` selects `local`.

The `resolve_capability` tool accepts `task_description`, `provider`, `keywords` (array), and `selection_policy` (`first` or `exact_name`). Legacy comma-separated `key_words` is accepted. Candidates are ordered by provider rank, then ordinal name, and capped at five. Failed bindings rotate to the next candidate; a provider transport failure stops resolution. Exact-name matching is case-insensitive against the task description. Unknown providers fail explicitly. `top_k` and `prefer_version` are rejected; absent upstream version/score metadata is never invented. Intent `type` is a provider hint and cache dimension, not an enforced compatibility constraint.

The tool returns provider, server, tool, schema, schema fingerprint, and attempted candidates. It only resolves; meta-skill slots resolve and execute. Both discovery/binding and the selected tool pass through the shared executor. Route allowlists must permit `resolve_capability` and the selected tool name. Nacos targets use `capability:nacos:<server>:<tool>` for policy/audit, while trajectories retain the logical remote tool name.

## Optional adapter builds

| Build | Providers | SDK events |
| --- | --- | --- |
| Default Gateway | local | absent |
| `-p:OpenClawEnableNacos=true` | local, nacos | absent; suitable for NativeAOT |
| Above plus `-p:OpenClawEnableNacosEvents=true` | local, nacos | explicit SDK adapter; JIT or NativeAOT |

The default Gateway dependency graph has no Nacos package reference. The optional event adapter uses RedNb.Nacos.DependencyInjection 2.1.0 and its generated protocol JSON metadata. It supports NativeAOT without enabling reflection serialization. Use `-p:PublishAot=false` for JIT publishing. The SDK stays absent unless explicitly selected.

Configure the Router as MCP server `nacos-mcp-router`, and add `provider: nacos` to capability references. See the [Router contract and deployment guide](nacos-mcp-router.md). Optional event settings live under the generic extension bag:

```json
{
  "adapterSettings": {
    "nacos": {
      "enabled": true,
      "serverAddr": "127.0.0.1:8848",
      "dataId": "openclaw-mcp.json",
      "group": "DEFAULT_GROUP",
      "longPollingTimeoutMs": 10000,
      "reconnectDelayMs": 5000
    }
  }
}
```

Supply optional username/password through your deployment's secret configuration. Missing address or disabled settings produce no subscription. Setup is asynchronous; each attempt has a timeout and failures retry indefinitely with exponential backoff capped at 60 seconds. `active` means listener registered; the SDK owns subsequent long-poll reconnect and this status is not remote health attestation. Events invalidate bindings; they do not import untrusted config content or replace the workspace file.

Migration from the experimental `nacos` branch: move the top-level `nacos` settings to `adapterSettings.nacos`, select the build flags explicitly, and add `provider: nacos`. `static.target` is preferred; `mcp_server_name` remains a loader alias. Vendor-specific failure names become `provider_unavailable`, `all_bindings_failed`, `capability_provider_unavailable`, `capability_binding_failed`, and `capability_execution_failed`.

## Cache and reliability

Each Gateway owns a bounded 1,024-binding cache. Dynamic entries expire after 300 seconds and are scoped by provider, authenticated channel/user, session, normalized intent, binding mode, and generation. Static entries share the same capacity bound and are scoped by security identity; they persist until eviction or invalidation. Registries/caches are per host/workspace; embeddings must not share them across independent workspace security domains.

Workspace reload and adapter events advance the generation and clear bindings. In-flight old-generation resolution cannot repopulate or start execution from stale entries. An invalidation after execution starts cannot undo an external action. Binding fills are serialized per cache key; unrelated sessions and tool execution remain concurrent. Cache hits retain candidate evidence and always recheck authorization.

Three failed executions open a provider/target/security-scoped circuit for 30 seconds. Success resets it; blocked actions do not count. After cooldown, executions may probe recovery (there is no single-probe half-open guarantee). Unknown tools are never retried automatically. Retry requires the target to explicitly declare retry safety and the step to request retries. Local implementations can opt in with `IRetrySafeCapabilityTool`; custom Nacos composition can supply an operator-controlled allowlist. Default Nacos targets are not retry-safe. Fallbacks continue to use the existing meta-skill failure branches.

## Evidence and offline replay

Step evidence records provider, generation, intent, candidates/attempts, selected target, schema/fingerprint, cache hit, and binding duration. These are execution observations, not catalog trust assertions. The authenticated `GET /api/integration/capabilities` endpoint (`integration.read`) exposes provider IDs, event setup status, cache generation, and count for AgentQi without SDK details or secrets.

`CapabilityBindingReplayFixture.FromMetaRun` exports a version-2 replay fixture with separate copies of recorded observations and expected results. `CapabilityBindingReplay.RunAsync` uses a recorded provider and a no-execution callback: it never contacts a registry, invokes a remote tool, or calls an LLM. It checks selection, attempts, cache behavior, provider, and schema fingerprint. This verifies binding consistency; it does not reproduce remote business results, unavailable alternative schemas, or provider health. Version-1 fixtures must be regenerated from retained run evidence.

## Validation and remaining acceptance

Conformance tests cover both runtimes with the local provider, permission denial before discovery, authorization on cache hits, generation races, provider/security isolation, bounded expiry, circuit cooldown, and replay divergence. Router tests cover captured protocol envelopes and typed failures; listener tests cover retry, cancellation, and disposal.

Live Nacos/Router acceptance is reproducible through [the isolated acceptance harness](../eng/nacos-live/README.md). It runs authenticated Nacos 3.2.4 and Router 0.2.2, exercises both runtimes, and checks real event invalidation and rebinds in managed and NativeAOT processes with JSON reflection disabled. The dedicated CI lane provisions its own services; ordinary unit tests remain independent of them. Historical contributor token measurements remain separately attributed in the Router guide. This proves the tested deployment contract, not arbitrary registry recall or production availability. AgentQi catalog/trust and operational screen design belong in the downstream ecosystem/product backlog.

Nacos target retries require an explicit operator allowlist under `adapterSettings.nacos.retrySafeTargets`, for example `["weather-mcp/get_weather"]`. Only list operations known to be safe to repeat; other targets execute once regardless of a skill's retry count. The retry weather example requires this setting.

Sessions retain the latest 100 meta-run records, including their complete replay evidence. Export records before they age out if longer retention is required.

# Nacos Live Acceptance and Gateway Integration

This document describes how the .NET 10 live acceptance harness, the managed and NativeAOT smoke executable, and the Gateway's optional Nacos adapters fit together. It is the current implementation guide; Router protocol history and capability semantics remain in [the Nacos MCP Router guide](nacos-mcp-router.md) and [capability resolution](capability-resolution.md).

The acceptance harness and weather fixture are .NET applications. The pinned Router is the separate `NacosMcpRouter` 1.0.0 .NET global tool. This flow does not use Python.

## Component boundaries

| Component | Owns | Does not own |
| --- | --- | --- |
| `eng/nacos-live` | Temporary Nacos, model assets, weather fixture, registration, Router process, evidence, and reverse-order cleanup | Gateway service composition or production Nacos lifecycle |
| `eng/NacosLiveSmoke` | AOT/JIT smoke of the capability provider and Nacos event adapter using the real Router | Starting the full Gateway host; the smoke composes the relevant adapters directly |
| `OpenClaw.Gateway` | Optional DI registration of the Nacos capability provider and event source | Vendor-specific capability policy or Nacos configuration import |
| `OpenClaw.Adapters.Nacos` | `search_mcp_server`, `add_mcp_server`, and `use_tool` calls and Router failure normalization | Nacos SDK event subscription |
| `OpenClaw.Adapters.Nacos.Events` | Background configuration listener and generic capability invalidation | Replacing workspace MCP configuration with remote config content |
| `NacosMcpRouter` | Nacos MCP search, installation, and downstream tool proxying | OpenClaw authorization, approvals, hooks, and audit policy |

The Gateway always registers the local capability provider. The Nacos provider is added only when the `OPENCLAW_NACOS` build symbol is enabled in [`ToolServicesExtensions`](../src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs). Nacos configuration events are a separate optional adapter, enabled by `OPENCLAW_NACOS_EVENTS`.

## Build and configuration boundary

| Gateway build | Providers | Nacos event SDK |
| --- | --- | --- |
| Default | `local` | Not referenced |
| `-p:OpenClawEnableNacos=true` | `local`, `nacos` | Not referenced; supports NativeAOT |
| Add `-p:OpenClawEnableNacosEvents=true` | `local`, `nacos` | Referenced explicitly; supports JIT and NativeAOT |

The events flag requires `OpenClawEnableNacos=true`; the Gateway project fails the build otherwise. Feature variants use separate intermediate output paths. Nacos remains optional and the default Gateway dependency graph has no Nacos SDK.

The runtime provider defaults to `local`; capability references must select `provider: nacos`. Event setup reads the generic `adapterSettings.nacos` object. A typical configuration shape is:

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

Credentials can be supplied through the configured secret resolver. Missing/disabled event settings do not create a listener. Listener setup retries with bounded exponential backoff; `active` means that registration completed, not that Nacos is remotely healthy. A matching config change publishes a generic invalidation signal and advances the capability-cache generation. The listener hashes content for duplicate detection and does not retain or apply that content.

## End-to-end acceptance flow

```mermaid
sequenceDiagram
    participant Runner as .NET acceptance runner
    participant Nacos as Authenticated Nacos 3.2.4
    participant Fixture as Streamable HTTP weather fixture
    participant Router as NacosMcpRouter 1.0.0
    participant Smoke as Managed / NativeAOT smoke
    participant Events as Gateway Nacos event adapter
    participant Cache as Capability binding cache

    Runner->>Nacos: Start, initialize admin, authenticate
    Runner->>Fixture: Start loopback /mcp and /health
    Runner->>Nacos: Release MCP metadata over HTTP
    Runner->>Nacos: Register fixture endpoint over gRPC
    Runner->>Router: Start with isolated Nacos/model/data settings
    Smoke->>Nacos: Publish initial config and wait until readable
    Smoke->>Events: Subscribe; wait for active status
    Smoke->>Router: list/search/add/use through capability path
    Router->>Fixture: get_weather(city=Oslo)
    Fixture-->>Smoke: Labelled deterministic Oslo payload
    Smoke->>Nacos: Publish changed config
    Nacos-->>Events: Config change callback
    Events->>Cache: Invalidate bindings and advance generation
    Smoke->>Router: Resolve and execute static/dynamic bindings again
    Runner->>Runner: Write JSON/TRX evidence and clean up owned processes
```

`NacosLiveAcceptance` downloads the pinned Nacos 3.2.4 archive and verifies its SHA-256 before extraction. It enables Nacos authentication, binds the server to loopback, generates temporary credentials, initializes the administrator, and logs in before continuing. The Nacos HTTP Console port is fixed at `8080`: Router 1.0.0 defaults its Console API client to `127.0.0.1:8080` and exposes no acceptance-time override. The Nacos main and gRPC ports, Router HTTP port, and fixture HTTP port are selected dynamically. Ensure local port `8080` is available.

The runner downloads the four embedding assets required by the Router. Defaults are ModelScope (`https://www.modelscope.cn`), repository `sentence-transformers/all-MiniLM-L6-v2`, and branch `master`; other endpoints default to `main`. Assets are validated before Router startup. Temporary service data and generated credentials stay in a run-specific temporary directory; process logs and reports are written to the selected evidence directory. Cleanup stops only processes owned by this run and removes its temporary data.

The fixture is loopback-only Streamable HTTP at `/mcp`, with `/health` for readiness. It exposes only `get_weather(city)`, accepts Oslo, and returns deterministic data labelled `acceptance fixture` (including `temperature_c: 7.5`). This is test data, not a live weather observation. Registration releases server/tool/endpoint metadata through Nacos HTTP APIs and registers the live endpoint through gRPC.

## What the smoke verifies

The smoke registers the production `NacosEventRegistration`, waits for its listener status to become `active`, and loads the real Router through `McpServerToolRegistry`. It requires exactly three Router tools. It then executes both static and dynamic capability bindings, repeats each to prove cache reuse, publishes a new Nacos config revision, waits for invalidation within 2,000 ms, verifies the cache is empty, and binds both modes again.

Each weather result must contain Oslo and a numeric temperature. The smoke also asserts zero LLM calls, disabled JSON reflection, and that the process mode matches its report name (`nativeAot: false` for managed and `true` for NativeAOT). The Gateway itself is built separately in the dedicated workflow; the smoke executable is an adapter-level integration check, not a full Gateway-host boot test.

The runner additionally executes `src/OpenClaw.Tests` with the `LiveRouter` filter. Those tests cover both `AgentRuntime` and `MafAgentRuntime` against the live Router. Normal test runs do not require Nacos; the live cases are opt-in through `OPENCLAW_NACOS_LIVE`.

## Run and evidence

Prerequisites are .NET SDK 10, Java 17 or newer, the NativeAOT toolchain for the host, network access to NuGet/Nacos/model assets, and the pinned Router global tool:

```powershell
dotnet tool install --global NacosMcpRouter --version 1.0.0
```

Use the complete host-specific publish and acceptance commands in [`eng/nacos-live/README.md`](../eng/nacos-live/README.md). The dedicated [Nacos live workflow](../.github/workflows/nacos-live.yml) builds the Gateway with both Nacos flags, builds managed and NativeAOT smoke executables, runs harness unit tests, then runs the isolated acceptance.

Success requires the `NACOS_LIVE_ACCEPTANCE_PASS` marker and produces:

| Artifact | Meaning |
| --- | --- |
| `managed.json`, `native.json` | Runtime mode, reflection setting, binding/cache checks, and invalidation latency |
| `acceptance.json` | Nacos/Router versions, authenticated setup, fixture label, dual-runtime result, overall pass |
| `live-runtimes.trx` | Filtered live Router test results |
| `nacos.log`, `router.log`, `fixture.log`, and smoke/test logs | Process diagnostics for the run |

A successful Gateway publish or NativeAOT compile by itself is not an acceptance pass. Review logs for secrets and environment-specific data before sharing them.

## Guarantees and limits

The acceptance proves the tested Nacos 3.2.4 / Router 1.0.0 contract, authenticated registration and config subscription, static/dynamic capability execution in managed and NativeAOT smoke processes, cache invalidation/rebinding, and the listed runtime tests. It does not prove arbitrary Nacos search recall, external weather availability, production uptime, or that every registered MCP server is safe to invoke. Gateway policy and authorization remain authoritative regardless of registry metadata.

For provider-neutral cache, authorization, failure, and replay semantics, see [capability resolution](capability-resolution.md). For the Router wire contract and historical captures, see [Nacos MCP Router](nacos-mcp-router.md).

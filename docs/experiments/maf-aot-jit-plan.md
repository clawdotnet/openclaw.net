# MAF Optional Orchestrator Experiment

## Background
This branch exists to evaluate Microsoft Agent Framework (MAF) as an optional orchestration backend inside OpenClaw.NET without turning that experiment into a rewrite. OpenClaw.NET still owns the runtime, gateway, policy, hosting, observability, deployment, and compatibility layers. MAF is being tested only as an alternate agent-execution loop that can sit behind those existing OpenClaw surfaces.

This is not a decision to replace OpenClaw.NET. The current native path remains the default and the reference behavior for the branch. The experiment exists because OpenClaw already supports both `aot` and `jit` runtime lanes, and orchestration viability may differ sharply between them. JIT is the best-case lane for richer framework compatibility. AOT is the constrained lane where publish, trimming, runtime reflection, and tool-binding behavior must be evaluated honestly rather than assumed.

Experiment matrix:

| Runtime Mode | Orchestrator | Purpose |
|---|---|---|
| `jit` | `native` | baseline |
| `jit` | `maf` | richer integration test |
| `aot` | `native` | baseline |
| `aot` | `maf` | constrained viability test |

## Architectural Boundary
OpenClaw.NET continues to own:

- gateway surfaces
- session hosting
- policy and approvals
- plugin compatibility
- security hardening
- observability
- deployment and runtime mode handling

MAF is evaluated only for:

- agent reasoning and orchestration
- agent and thread abstractions
- tool invocation coordination
- workflow composition where it is useful

The branch keeps those boundaries explicit in both code and configuration. The native `AgentRuntime` remains intact behind a factory seam, and `Runtime.Orchestrator=native` stays the default.

## Success Criteria
- `Runtime.Orchestrator=maf` can be enabled without destabilizing `Runtime.Orchestrator=native`.
- Gateway, session, approval, policy, and hosting surfaces remain OpenClaw-owned.
- At least one tool invocation can execute through the MAF backend in the JIT lane.
- Tracing and operator diagnostics remain coherent and continue to report runtime ownership from OpenClaw.
- The JIT lane can run a realistic orchestration flow without replacing the native path.
- The AOT lane either supports a constrained subset or records precise blockers and unsupported areas without forcing parity.

## Non-Goals
- no rewrite
- no replacement of the current native runtime path
- no requirement for feature parity between AOT and JIT
- no immediate merge target
- no immediate removal of Semantic Kernel support or any other integration
- no repo rename

## Proposed Branch Architecture
The branch uses additive seams instead of a top-level runtime rewrite.

Existing or new orchestration seams:

- `src/OpenClaw.Agent/IAgentRuntimeFactory.cs`
- `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs`
- `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
- `src/OpenClaw.Agent/AgentSystemPromptBuilder.cs`
- `src/OpenClaw.Agent/LlmExecutionEstimateBuilder.cs`

Experimental adapter project:

- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntimeFactory.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentFactory.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafToolAdapter.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafSessionStateStore.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafTelemetryAdapter.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafCapabilities.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafServiceCollectionExtensions.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafOptions.cs`

The adapter project is conditional, isolated, and intentionally marked non-AOT-compatible and non-trimmable for the first pass. Native OpenClaw startup continues to work without that project being referenced.

## Runtime/Config Selection Model
Configuration model:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "aot|jit|auto",
      "Orchestrator": "native|maf"
    }
  }
}
```

Rules:

- `native` remains the default orchestrator.
- `maf` is opt-in and supported in the MAF-enabled artifacts.
- `auto` runtime mode resolution remains unchanged.
- `maf + jit` and `maf + aot` are both supported in the MAF-enabled artifacts.
- selecting `maf` without building the adapter continues to fail fast with a clear startup error.

## Experiment Phases
### Phase 1 — Repository/Branch Prep
- create the experiment branch
- inspect current orchestration seams
- add this planning document
- identify additive extension points instead of replacing the native loop

### Phase 2 — Orchestrator Abstraction
- introduce `IAgentRuntimeFactory` selection on top of the existing `IAgentRuntime` seam
- keep `AgentRuntime` as the native implementation
- extract shared tool execution into `OpenClawToolExecutor`
- expose runtime orchestrator selection through diagnostics and admin surfaces
- preserve native behavior as the default and baseline

### Phase 3 — JIT + MAF Spike
- register the adapter only when `OpenClawEnableMafExperiment=true`
- create a `MafAgentRuntime` backed by `ChatClientAgent`
- adapt tool execution through `MafToolAdapter` and the shared `OpenClawToolExecutor`
- persist MAF session state in a sidecar store under the OpenClaw memory root
- preserve provider policy and usage accounting by routing MAF model calls through OpenClaw-owned execution services
- validate at least one published-binary tool path and confirm admin/runtime observability remains coherent

### Phase 4 — AOT + MAF Spike
- attempt NativeAOT publish with the experiment adapter included
- validate startup viability separately from feature parity
- validate at least one published-binary tool path through an external surface, not only in-process tests
- verify runtime/admin observability still reports `maf`, the effective runtime mode, provider usage, and coherent runtime events
- record concrete blockers if trimming, publish, or runtime behavior fails
- do not weaken security defaults or force workarounds just to claim parity

### Phase 5 — Comparison and Findings
- compare native vs MAF in all four matrix cells
- summarize fit, friction, maintainability, overhead, and coupling risk
- record the readiness conclusion and supported artifact model

## Evaluation Rubric
Score or narrate each matrix cell against:

- build and publish viability
- architectural fit
- maintainability
- observability
- policy and approval compatibility
- plugin and tool compatibility
- performance and overhead
- future coupling risk

The comparison should treat `native` as the baseline and call out any MAF-only compromises explicitly.

## Exit Outcomes
Historical experiment exit options:

1. adopt MAF as an optional JIT backend only
2. adopt MAF optionally in both modes with clearly documented constraints
3. keep MAF experimental only because the fit, risk, or maintenance profile is not strong enough

Current branch conclusion:

1. MAF is now supported as an optional backend in both `jit` and `aot` within the MAF-enabled artifacts, with `native` retained as the default orchestrator.

## Suggested Implementation Order
1. Keep `Runtime.Orchestrator=native` as the default and preserve current `auto|aot|jit` behavior.
2. Route gateway runtime creation through `IAgentRuntimeFactory`.
3. Share tool execution, approval, and hook behavior through `OpenClawToolExecutor`.
4. Keep the MAF adapter project conditional and isolated behind `OpenClawEnableMafExperiment=true`.
5. Validate `jit + native` after every seam refactor before enabling `jit + maf`.
6. Use the JIT lane to prove basic chat, at least one tool, session continuity, and coherent diagnostics.
7. Attempt the AOT lane only after the JIT lane is understood, and document blockers instead of forcing parity.
8. Record findings before deciding whether the adapter should progress past this branch.

## Current Branch Status
This branch now carries the full comparison set required to exit the experiment honestly. In addition to the original HTTP/tool, approval happy-path, plugin, streaming, streamed-tool, `/v1/responses` streamed-tool, and `/v1/responses` failure matrices, it now also includes published-binary four-cell coverage for restart/resume with MAF sidecar restore and fallback (`docs/experiments/maf-aot-jit-restart-findings.md`), negative approval outcomes across websocket and HTTP admin decisions (`docs/experiments/maf-aot-jit-approval-outcome-findings.md`), and file-bound `Plugins:Entries:*:Config` parity (`docs/experiments/maf-aot-jit-plugin-config-findings.md`). The current branch position is no longer “AOT subset candidate only”; the readiness conclusion is tracked in `docs/experiments/maf-aot-jit-readiness.md`: MAF is now supported as an optional backend in both `jit` and `aot` within the MAF-enabled artifacts, while `native` remains the default orchestrator and standard artifacts continue to fail fast on `Runtime.Orchestrator=maf`.

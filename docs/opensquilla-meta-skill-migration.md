# OpenSquilla Meta-Skill Migration Notes

This note summarizes the current OpenClaw.NET implementation level for the OpenSquilla-style meta-skill path. It focuses on what is already aligned and what still needs a dedicated migration step.

## Current status

OpenClaw.NET already implements the core OpenSquilla-style meta-skill orchestration skeleton:

- `kind: meta` skills with a `composition.steps` DAG
- `depends_on` ordering and dependency-cycle validation
- `llm_classify` branching through `options` + `route`
- `user_input` pause/resume behavior with session checkpoint restoration
- `final_text_mode: auto | raw | structured | step:<id>`
- structured execution envelopes for automation and diagnostics

That means the current runtime can support the basic pattern of assembling a skill graph, classifying a branch, then executing tools or model steps.

The OpenSquilla reference implementation under `E:\GitHub\opensquilla\src\opensquilla\skills\meta` shows the baseline:

- `parser.py` treats `on_failure` as a first-class failure-branch contract. It validates that the target step exists, is not self-referential, does not create nested failover chains, and is owned by only one primary step.
- `types.py` and the parser layer treat `output_choices`, `tool_allowlist`, and `clarify` schema as strong typed contracts, not only runtime conventions.

OpenClaw.NET now has first-class coverage for the closest local equivalents: explicit failure substitution, step-level retry/timeout policy, JSON intermediate-output validation, the P0 OpenSquilla-native DSL/Jinja compatibility layer described below, and the initial P1 runtime parity slice for `skill_exec`, meta-run persistence, and dedicated meta policy gating. This shipped slice is implemented end-to-end and validated by the OpenClaw test project (`1907 passed, 0 failed, 0 skipped`). The remaining OpenSquilla meta-policy surface is still wider than the current OpenClaw.NET implementation.

## Newly completed parity

- Native OpenSquilla DSL fields are now first-class parser/runtime contracts: `output_choices`, composition `tool_args`, step `tool_args`, `tool_allowlist`, `clarify`, `when`, and route arrays.
- `user_input.clarify` now validates typed chat/form input and normalizes successful multi-field results to canonical JSON text.
- Jinja rendering now uses `Jinja2.NET 1.4.1` with OpenSquilla-compatible `xml_escape`, `slugify`, `truncate`, and `tojson` filters.
- Runtime parity hardening now also covers stale checkpoint rejection, completion-routing parity for continued non-tool failures, and preserved `user_input_required` pause traces across resume boundaries.
- `skill_exec` now has a first-class parser/runtime contract for `entrypoint`, `args`, `cwd`, and `parse_mode`, and executes script resources through the tool execution layer with path validation instead of model-delegated chat behavior.
- Meta runs now append minimal persisted run records to the session model for completed, failed, and paused executions, with a dedicated local operator surface for inspection, replay preview, audit reconstruction, and proposal review/provenance tracking.
- Dedicated meta-layer policy gating now exists via `SkillsConfig.MetaSkill.Enabled`, which keeps meta skills installed while hiding them from the prompt index, suppressing routing hints, and rejecting explicit `meta_invoke` execution.
- Product-surface Phase 1 is now in place: `openclaw skills catalog` and a read-only `openclaw skills proposals` alias (list/show only) are available, and proposal list/detail JSON now emit additive entry metadata (`entrypoint`, `readOnlyAlias`) so consumers can distinguish operator entry paths safely.
- Product-surface Phase 2 has started with a minimal creator scaffold entrypoint: `openclaw skills create` now generates `standard` or `meta` `SKILL.md` scaffolds, supports additive JSON output (`name`, `slug`, `kind`, `path`, `created`, `overwrote`), supports proposal draft contracts via `--proposal-draft` (text and JSON), emits additive draft quality summaries (`proposalDraft.quality`), validates that proposal drafts are `meta`-only, emits machine-readable JSON error codes for `--json` failure paths (for example, `invalid_proposal_draft_kind`), and enforces conflict protection with optional `--force` overwrite semantics.
- Product-surface Phase 2 error-contract hardening now also covers runtime failure branches for `--json` callers in the meta-runs operator plane (for example, session/run/proposal not found and invalid lifecycle-transition rejections in replay/reconstruct/proposals list/show/change/rollback), so these branches emit the shared machine-readable error schema instead of plain text.
- The shared error-contract layer now also covers `skills inspect` / `skills install` source-inspection failures for `--json` callers (`inspect_failed`), plus install-time operational failures (`install_failed`), so non-parameter failure paths continue converging on one machine-readable schema.
- The `skills` top-level unknown-subcommand branch now also emits the same machine-readable error schema for `--json` callers (`unknown_subcommand`) while preserving existing text + help behavior for non-JSON invocations.
- With this pass, the current `skills` command surface now emits the shared machine-readable error schema across the primary failure classes: parameter validation, runtime not-found branches, inspect/install runtime failures, and unknown subcommands.
- Phase 3 proposal pipeline hardening has now started in-product: proposal mutation commands (`accept`/`dismiss`/`rollback`/`change`) require `OPENCLAW_OPERATOR_ID` and emit `permission_denied` for `--json` callers when the operator boundary is missing.
- Proposal mutation/show JSON contracts now include additive `audit` fields (`schemaVersion`, `actorId`, `changedAtUtc`, `transitionAction`) sourced from durable proposal transition metadata, preserving backward compatibility with existing lifecycle/provenance fields.
- Product-level Phase 3 acceptance now includes an end-to-end command slice (`skills create --proposal-draft --json -> meta-runs proposals dismiss -> rollback -> change -> show`) validated by `RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState`.
- Proposal accept governance now uses a structured validation profile (`opensquilla-authoring-v1`) on both `accept` and `change --to accept`, with grouped checks across `structure`, `trigger`, `runtime`, and `safety`, machine-readable gate failure payloads (`gate.profileId`, `gate.passed`, `gate.failedChecks`), and durable acceptance-gate metadata snapshots persisted on successful accepts (`meta_run_proposal_accept_gate_profile`, `meta_run_proposal_accept_gate_passed`, `meta_run_proposal_accept_gate_failed_checks`, `meta_run_proposal_accept_gate_checked_at_utc`).

## Validation status

The current implementation was validated with both focused meta-skill regressions and the full OpenClaw test project:

- focused P1 regression slices: `skill_exec` parser/runtime parity (`7 passed`), meta-run persistence (`4 passed`), and dedicated meta policy gating (`3 passed`)
- full test project: `1907 passed, 0 failed, 0 skipped`

That means the migration note below reflects the currently shipped and verified OpenClaw.NET behavior, not a planned or partial parity layer.

## Acceptance matrix

> This table aligns the OpenSquilla user/author docs with the current OpenClaw.NET implementation.

| Check | OpenSquilla requirement | OpenClaw.NET status | Evidence | Conclusion |
| --- | --- | --- | --- | --- |
| MetaSkill definition | `SKILL.md` should declare `kind: meta`, `triggers`, and `composition.steps` | Supported | [SkillLoader.cs](../src/OpenClaw.Core/Skills/SkillLoader.cs#L261)；[SkillModels.cs](../src/OpenClaw.Core/Skills/SkillModels.cs#L100) | Complete |
| Natural vs explicit triggering | Support natural-language activation and explicit meta-skill invocation | Supported via `triggers` + `meta_invoke` + priority resolution | [MetaSkillResolver.cs](../src/OpenClaw.Core/Skills/MetaSkillResolver.cs#L1)；[MetaInvokeTool.cs](../src/OpenClaw.Core/Skills/MetaInvokeTool.cs#L1) | Complete |
| Preconditions (runtime and tests) | The OpenSquilla docs call for structural validation, trigger checks, runtime tests, and safety-boundary review before trusting a MetaSkill | Structure/trigger parsing and runtime test coverage are in place across the core execution path | OpenSquilla user/author docs；[SkillLoader.cs](../src/OpenClaw.Core/Skills/SkillLoader.cs#L261)；[MetaSkillResolver.cs](../src/OpenClaw.Core/Skills/MetaSkillResolver.cs#L1)；[SkillTests.cs](../src/OpenClaw.Tests/SkillTests.cs#L240) | Complete |
| Review workflow object (governance) | High-risk meta changes should have a dedicated review workflow with traceable governance records | The current baseline still relies on runtime constraints and regression coverage; there is no dedicated meta-skill review workflow object yet | OpenSquilla user/author docs；[SkillTests.cs](../src/OpenClaw.Tests/SkillTests.cs#L240) | Partial |
| Risk metadata | `metadata.opensquilla.risk` / `capabilities` should act as authoring constraints | Meta skill loading now applies explicit risk/capability policy gates | [SkillLoader.cs](../src/OpenClaw.Core/Skills/SkillLoader.cs#L2218)；[SkillTests.cs](../src/OpenClaw.Tests/SkillTests.cs#L1185) | Complete |
| MetaSkill nesting | The authoring guide says a MetaSkill cannot compose another MetaSkill | Runtime preflight now rejects meta->meta composition | [AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L1988)；[MafAgentRuntime.cs](../src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs#L721)；[AgentRuntimeTests.cs](../src/OpenClaw.Tests/AgentRuntimeTests.cs#L1590) | Complete |
| Final text mode | `auto` / `raw` / `structured` / `step:<id>` | Supported | [AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L3035)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L3047) | Complete |
| DAG, routing, failure fallback | `depends_on`, `route`, and `on_failure` should execute as a real DAG | Supported | [SkillModels.cs](../src/OpenClaw.Core/Skills/SkillModels.cs#L151)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L2888)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L3549) | Complete |
| Step coverage | `agent`, `llm_chat`, `llm_classify`, `user_input`, `tool_call`, `skill_exec` | Supported | OpenSquilla authoring docs；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L2257)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L2584)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L2696)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L3433) | Complete |
| Clarify semantics | The docs describe `form` / `chat`, fields, `skip_if`, timeout, cancel, and normalization behavior | `form` / `chat`, fields, timeout, cancel, defaults, typed validation, and `skip_if` are now supported | [SkillLoader.cs](../src/OpenClaw.Core/Skills/SkillLoader.cs#L1477)；[AgentRuntime.cs](../src/OpenClaw.Agent/AgentRuntime.cs#L2696)；[MafAdapterTests.cs](../src/OpenClaw.Tests/MafAdapterTests.cs#L1707) | Complete |
| Meta runs and proposals | The docs require inspect, replay, reconstruct, and proposal lifecycle support | Implemented and documented as complete in this note | [opensquilla-meta-skill-migration.md](opensquilla-meta-skill-migration.md#L1)；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L37)；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L1907) | Complete |
| Quality gating (creator draft) | Authoring flow should include baseline structure/description checks and reject low-quality drafts | `skills create --proposal-draft` now enforces a blocking gate and returns `proposal_draft_quality_gate_failed` on low-quality drafts | OpenSquilla user/author docs；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L1404)；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L1714)；[SkillCommandsTests.cs](../src/OpenClaw.Tests/SkillCommandsTests.cs#L703) | Complete |
| Quality gating (pre-accept) | The docs expect review-time validation before acceptance | Accept and change-to-accept now run the structured `opensquilla-authoring-v1` profile before lifecycle mutation; grouped checks cover `structure`/`trigger`/`runtime`/`safety`, JSON failures include `gate.profileId` + `gate.failedChecks`, and successful accepts persist durable gate snapshot metadata for audit reconstruction | OpenSquilla user/author docs；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L658)；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L1067)；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L3030)；[SkillCommandsTests.cs](../src/OpenClaw.Tests/SkillCommandsTests.cs#L3750)；[SkillCommandsMetaGovernanceTests.cs](../src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs#L80) | Complete |
| Stable catalog | The docs describe a stable built-in MetaSkill catalog | Productized stable catalog mode is available via `openclaw skills catalog --stable --kind meta` (bundled first-party meta skills) with text/JSON contracts | OpenSquilla user docs；[SkillCommands.cs](../src/OpenClaw.Cli/SkillCommands.cs#L1490)；[SkillCommandsMetaGovernanceTests.cs](../src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs#L12) | Complete |
| Disable model-visible meta behavior | MetaSkills can be kept installed while hidden from model prompting and explicit invocation can be rejected | Supported via runtime policy | [SkillModels.cs](../src/OpenClaw.Core/Skills/SkillModels.cs#L1)；[MafAgentRuntime.cs](../src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs#L680)；[OpenClawToolExecutor.cs](../src/OpenClaw.Agent/OpenClawToolExecutor.cs#L483) | Complete |
| Auditability | MetaSkill runs should be auditable, replayable, and recoverable | Supported through history/evidence/checkpoint coverage and regression tests | [opensquilla-meta-skill-migration.md](opensquilla-meta-skill-migration.md#L1)；[AgentRuntimeTests.cs](../src/OpenClaw.Tests/AgentRuntimeTests.cs#L1752)；[MafAdapterTests.cs](../src/OpenClaw.Tests/MafAdapterTests.cs#L896) | Complete |

### Migration judgment

- The core runtime and operational surface have been migrated.
- The pre-accept quality gate alignment item is now closed with the `opensquilla-authoring-v1` profile, grouped checks, machine-readable gate diagnostics, and durable gate metadata snapshots.
- Remaining concrete gaps are product-level: broaden failure/authorization/conflict E2E coverage, and introduce a dedicated meta-skill review workflow object (the matrix row for this remains partial).

## Proposal review overlay (2026-06-13)

The derived proposal layer now includes operator review commands while preserving evidence-first semantics:

- `openclaw skills meta-runs proposals accept <session-id> --proposal <id>` and `... dismiss ...` record operator review decisions only.
- Review decisions do not execute tools, models, replay, resume, or proposal lifecycle transitions.
- Same-action replays are idempotent success; opposite-action requests are rejected as conflicts.
- `meta-runs proposals` and `meta-runs proposals show` expose additive review state (`reviewStatus`, `reviewedAtUtc`, and detail `review` object) alongside existing derived evidence fields.

## What is already aligned

### 1. DAG composition

OpenClaw.NET declares meta-skill steps under `composition.steps` and validates the graph before execution:

- duplicate step IDs
- missing dependencies
- self-dependencies
- dependency cycles
- invalid `llm_classify` route targets

This gives the meta path a fail-fast contract instead of accepting broken graphs and failing later during execution.

### 2. Step kinds

The current runtime supports these core orchestration kinds:

- `agent`
- `skill_exec`
- `tool_call`
- `llm_chat`
- `llm_classify`
- `user_input`

These cover the main execution boundary for OpenSquilla-style orchestration in OpenClaw.NET.

### 3. Structured outputs and diagnostics

When `final_text_mode: structured` is enabled, the runtime returns a structured payload with:

- `skill`
- `final_text`
- `error` / `error_code`
- `steps[]` with status, duration, failure code, and continuation metadata

Meta steps also support:

- `on_failure` substitute branches, validated in both parser and runtime paths
- `retry` and `timeout_seconds` for bounded tool and model execution
- `output_contract` / `output_schema` for required-property validation on JSON intermediate results

This helps automated tests, log triage, and operational diagnostics inspect meta-skill runs without parsing free-form final text.

## Migration checklist

When porting an OpenSquilla meta skill to OpenClaw.NET, use this order:

1. Express the orchestration graph with `composition.steps` and `depends_on`.
2. Use `llm_classify` for branch selection instead of ad-hoc string parsing.
3. Use `on_failure` when a failed step should activate a substitute step and mirror the substitute output back to the primary step ID for downstream dependencies.
4. Use `with.continue_on_error` only when failure should not stop the DAG and no substitute-branch semantics are needed.
5. Configure `retry.max_attempts`, optional `retry.backoff_ms`, and `timeout_seconds` for tool or model steps that need bounded execution.
6. Use `output_contract` / `output_schema` with `format: json` and `required_properties` when downstream steps depend on structured intermediate output.
7. Prefer `final_text_mode: structured` when callers need a machine-readable result envelope.
8. Use `user_input` as the pause/resume boundary for interactive flows.

## Remaining migration gaps

The current OpenClaw.NET meta path now covers DAG execution, fail-fast validation, explicit failure substitution, bounded step execution, structured results, the P0 native DSL compatibility layer, typed `user_input.clarify`, shared Jinja rendering filters, `skill_exec` subprocess entrypoint execution, minimal persisted meta-run records, dedicated meta policy gating, the final checkpoint/routing hardening needed for runtime parity inside this slice, and wave-based parallel scheduling for independent ready `tool_call` steps in both Agent and MAF runtimes. It is not yet a full drop-in replacement for every OpenSquilla-native meta-skill contract.

| Gap | Why it matters | Current status |
| --- | --- | --- |
| Meta run history detail, replay, and proposals CLI | OpenSquilla exposes `skills meta runs ...`, dry-run replay, and proposal list/show/accept commands for audit and operations. | OpenClaw.NET now persists minimal per-run records in session state and exposes a local `openclaw skills meta-runs <session-id>` inspection surface with default run summaries, optional `--verbose` per-step trace output, `--run <run-id>` filtering, machine-readable `--json` output, a preview-only `meta-runs replay` availability check that reports a minimal replay plan with an operator-facing summary such as `auditable_not_replayable` when retained step traces exist or `metadata_only_not_replayable` when only run-level metadata remains, a separate `meta-runs reconstruct` command that builds an audit replay result from persisted run history plus optional checkpoint evidence without re-executing tools or models, and a read-only `meta-runs proposals` / `meta-runs proposals show` surface that derives candidate proposal summaries from persisted meta-run evidence for paused or failed runs only. The `proposals show` detail surface now expands an additive run-level `evidence` summary (`timelineStepIds`, `errorCode`, `error`, `finalText`), persisted step-level evidence (`steps[]` with kind/status/failure/duration/continued metadata), and a structured checkpoint summary (`checkpoint.pendingStepId`, pending/blocked step sets, `promptPresent`, output step IDs, and failure-alias step IDs) while keeping the layer read-only. For compatibility, earlier top-level detail fields remain emitted as legacy mirrors, but operators should prefer grouped `evidence` and `checkpoint` fields. Stable replay-preview, reconstruct, and derived-proposal contract strings resolve through shared session-model constants. Durable proposal lifecycle, provenance parity, and any future accept/reject workflow still belong to the planned `LearningProposal`-backed migration rather than this derived layer. |
| Full `skill_exec` stdin/replay ergonomics | OpenSquilla `skill_exec` includes richer subprocess ergonomics around stdin-heavy workflows and the surrounding operator tooling. | OpenClaw.NET now executes skill entrypoints as validated subprocesses, supports stdin passthrough, persists replay-safe execution evidence (`input_mode`, `stdin_bytes`, `parse_mode`, `command` preview), emits evidence-backed reconstruct timeline notes for `skill_exec`, reports machine-readable replay requirements (`skill_exec_inputs` / `skill_exec_inputs_not_persisted`) when required inputs are absent, and now provides additive operator-first replay/reconstruct diagnostics (`operatorSummary`, `triageHints`) in both JSON and text outputs. |
| Built-in MetaSkill catalog and creator/proposal flow | OpenSquilla documents built-in workflows such as `meta-web-research-to-report`, `meta-document-to-decision`, and `meta-skill-creator`, plus proposal inspection and auto-enable audit. | OpenClaw.NET now has a Phase-1 product surface, a Phase-2 baseline (`skills create` scaffold generation + proposal draft contracts + command-surface JSON failure schema hardening), a blocking creator quality gate with explicit threshold/error-code output, and a delivered Phase-3 proposal pipeline closure slice (mutation permission boundary, action-aware lifecycle governance, additive audit payloads, and E2E acceptance coverage). The remaining product-side migration work is broader failure/authorization coverage and any later catalog depth beyond the current surface. |

## Recommendation

Treat the current OpenClaw.NET meta-skill path as a shipped, validated OpenSquilla-style implementation for:

- DAG orchestration
- explicit failure substitution
- bounded step execution
- JSON intermediate-output validation
- structured execution results
- native OpenSquilla DSL parity for `output_choices`, `tool_args`, `tool_allowlist`, `clarify`, `when`, and route arrays
- shared Jinja rendering with `xml_escape`, `slugify`, `truncate`, and `tojson`
- validated `skill_exec` entrypoint execution for script resources with parser/runtime safety checks
- minimal persisted meta-run history in session state
- dedicated runtime-level meta policy gating
- pause/resume checkpoint safety and continued-failure routing parity inside the current meta runtime model

For deeper OpenSquilla parity, prioritize the still-missing product surface by direct operational impact:

1. **Broaden product-level acceptance slices.** Expand E2E coverage from the current create -> proposal lifecycle -> show chain to include additional failure/authorization/conflict scenarios (including invalid-transition failures), with JSON error-contract assertions and non-drift checks for lifecycle/audit/provenance state after failed mutations.
   Prefer expanding by failure-matrix dimensions so each added slice keeps both contract and non-drift assertions.
2. **Continue catalog depth hardening.** Extend creator and catalog policy checks beyond the current blocking threshold if future product rules require it.

## P1 Acceptance Checklist (DoD)

Use this checklist for P1 milestone sign-off, layered as capability, contract, and regression evidence.

### A. P1-1: Meta run history replay and operations

- [x] `meta-runs` inspection surface exists (summary, `--run`, `--verbose`, `--json`).
- [x] `meta-runs replay` (preview-only) and `meta-runs reconstruct` (audit reconstruction, non-executing) are available.
- [x] Derived evidence views exist for `meta-runs proposals` and `meta-runs proposals show`.
- [x] Durable lifecycle actions exist for `proposals accept` / `proposals dismiss` / `proposals rollback` / `proposals change`.
- [x] Same-action idempotency and opposite-action conflict rejection are enforced, with no partial JSON on JSON failure paths.
- [x] List/detail outputs expose additive review and lifecycle state (`reviewStatus`, `reviewedAtUtc`, `review`, `lifecycle`, `provenanceHistory`).
- [x] Help text covers the new commands (skills help and top-level CLI help).

- [x] Durable proposal lifecycle migration to the `LearningProposal` domain store.
- [x] Full proposal provenance and lifecycle semantics at the domain layer.

Verification commands (passing examples):

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Accept|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Dismiss|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalReviewCommands"`
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`

### B. P1-2: skill_exec stdin and operator ergonomics

- [x] Support stdin-heavy `skill_exec` contracts and execution path.
- [x] Provide inspection/replay operational visibility around `skill_exec` runs.
- [x] Add machine-readable failure contracts and regression tests for stdin/replay branches.
- [x] Add additive operator-first replay/reconstruct diagnostics (`operatorSummary`, `triageHints`) for failure clustering and triage ordering.

Suggested sign-off threshold:

- P1-1 is complete.
- Remaining migration work is P2-only (product-level catalog/creator flow).

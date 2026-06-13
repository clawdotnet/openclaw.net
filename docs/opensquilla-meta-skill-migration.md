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
- Meta runs now append minimal persisted run records to the session model for completed, failed, and paused executions, establishing an audit/replay foundation without a separate operator surface yet.
- Dedicated meta-layer policy gating now exists via `SkillsConfig.MetaSkill.Enabled`, which keeps meta skills installed while hiding them from the prompt index, suppressing routing hints, and rejecting explicit `meta_invoke` execution.

## Validation status

The current implementation was validated with both focused meta-skill regressions and the full OpenClaw test project:

- focused P1 regression slices: `skill_exec` parser/runtime parity (`7 passed`), meta-run persistence (`4 passed`), and dedicated meta policy gating (`3 passed`)
- full test project: `1907 passed, 0 failed, 0 skipped`

That means the migration note below reflects the currently shipped and verified OpenClaw.NET behavior, not a planned or partial parity layer.

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

The current OpenClaw.NET meta path now covers DAG execution, fail-fast validation, explicit failure substitution, bounded step execution, structured results, the P0 native DSL compatibility layer, typed `user_input.clarify`, shared Jinja rendering filters, `skill_exec` subprocess entrypoint execution, minimal persisted meta-run records, dedicated meta policy gating, and the final checkpoint/routing hardening needed for runtime parity inside this slice. It is not yet a full drop-in replacement for every OpenSquilla-native meta-skill contract.

| Gap | Why it matters | Current status |
| --- | --- | --- |
| Meta run history detail, replay, and proposals CLI | OpenSquilla exposes `skills meta runs ...`, dry-run replay, and proposal list/show/accept commands for audit and operations. | OpenClaw.NET now persists minimal per-run records in session state and exposes a local `openclaw skills meta-runs <session-id>` inspection surface with default run summaries, optional `--verbose` per-step trace output, `--run <run-id>` filtering, machine-readable `--json` output, a preview-only `meta-runs replay` availability check that reports a minimal replay plan with an operator-facing summary such as `auditable_not_replayable` when retained step traces exist or `metadata_only_not_replayable` when only run-level metadata remains, a separate `meta-runs reconstruct` command that builds an audit replay result from persisted run history plus optional checkpoint evidence without re-executing tools or models, and a read-only `meta-runs proposals` / `meta-runs proposals show` surface that derives candidate proposal summaries from persisted meta-run evidence for paused or failed runs only. The `proposals show` detail surface now expands an additive run-level `evidence` summary (`timelineStepIds`, `errorCode`, `error`, `finalText`), persisted step-level evidence (`steps[]` with kind/status/failure/duration/continued metadata), and a structured checkpoint summary (`checkpoint.pendingStepId`, pending/blocked step sets, `promptPresent`, output step IDs, and failure-alias step IDs) while keeping the layer read-only. For compatibility, earlier top-level detail fields remain emitted as legacy mirrors, but operators should prefer grouped `evidence` and `checkpoint` fields. Stable replay-preview, reconstruct, and derived-proposal contract strings resolve through shared session-model constants. Durable proposal lifecycle, provenance parity, and any future accept/reject workflow still belong to the planned `LearningProposal`-backed migration rather than this derived layer. |
| Full `skill_exec` stdin/replay ergonomics | OpenSquilla `skill_exec` includes richer subprocess ergonomics around stdin-heavy workflows and the surrounding operator tooling. | OpenClaw.NET now executes skill entrypoints as validated subprocesses, but the current slice still rejects `stdin` and does not yet expose replay-oriented operator workflows around those runs. |
| True parallel step scheduling | OpenSquilla can execute independent steps concurrently up to scheduler limits. | OpenClaw.NET preserves DAG ordering but currently executes ready steps through the runtime loop rather than a parallel scheduler. |
| Built-in MetaSkill catalog and creator/proposal flow | OpenSquilla documents built-in workflows such as `meta-web-research-to-report`, `meta-document-to-decision`, and `meta-skill-creator`, plus proposal inspection and auto-enable audit. | The current OpenClaw.NET path focuses on runtime orchestration. The broader product catalog and proposal workflow are not migrated. |

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

For deeper OpenSquilla parity, prioritize the still-missing surfaces by direct operational impact:

1. **P1: Meta run history replay and operations.** A local CLI inspection surface now exists with default run summaries, optional `--verbose` per-step trace output, `--run <run-id>` filtering, `--json` output, a preview-only replay availability check, a separate audit reconstruction command, and now a read-only derived `meta-runs proposals` view over paused/failed run evidence. The `meta-runs proposals show` detail path also expands a run-level `evidence` summary, step-level evidence, and structured checkpoint metadata for operator review without implying an accept/reject lifecycle. The grouped `evidence` / `checkpoint` objects are the preferred operator-facing shape; duplicated top-level detail fields remain only as compatibility mirrors. Stable preview/reconstruct/proposal contract strings come from shared session-model constants rather than duplicated CLI-local literals. The remaining gap in this operator area is durable proposal lifecycle management, which should migrate to `LearningProposal` storage rather than over-extending the derived layer.
2. **P1: `skill_exec` stdin and operator ergonomics.** Extend the new subprocess path to cover stdin-heavy workflows and the surrounding inspection/replay surfaces if migrated skills depend on them.
3. **P2: True parallel step scheduling.** Preserve DAG correctness while allowing independent steps to run concurrently. This improves performance and better matches OpenSquilla behavior, but most flows can be migrated without it.
4. **P2: Product-level catalog, creator, and proposal flow.** Add built-in MetaSkills, `meta-skill-creator`, proposal inspection, and auto-enable audit only if OpenClaw.NET needs product-level OpenSquilla parity rather than runtime portability alone.

## P1 Acceptance Checklist (DoD)

Use this checklist for P1 milestone sign-off, layered as capability, contract, and regression evidence.

### A. P1-1: Meta run history replay and operations

- [x] `meta-runs` inspection surface exists (summary, `--run`, `--verbose`, `--json`).
- [x] `meta-runs replay` (preview-only) and `meta-runs reconstruct` (audit reconstruction, non-executing) are available.
- [x] Derived evidence views exist for `meta-runs proposals` and `meta-runs proposals show`.
- [x] Review-only actions exist for `proposals accept` / `proposals dismiss` (no tool/model/replay/resume execution).
- [x] Same-action idempotency and opposite-action conflict rejection are enforced, with no partial JSON on JSON failure paths.
- [x] List/detail outputs expose additive review state (`reviewStatus`, `reviewedAtUtc`, `review`).
- [x] Help text covers the new commands (skills help and top-level CLI help).

Verification commands (passing examples):

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Accept|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Dismiss|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalReviewCommands"`
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`

Remaining gap (P1-1 not fully closed):

- [ ] Durable proposal lifecycle migration to the `LearningProposal` domain store (current implementation is review overlay).
- [ ] Full proposal provenance and lifecycle semantics at the domain layer (avoid long-term overgrowth of the derived layer).

### B. P1-2: skill_exec stdin and operator ergonomics

- [ ] Support stdin-heavy `skill_exec` contracts and execution path (currently rejected).
- [ ] Provide inspection/replay operational visibility around `skill_exec` runs.
- [ ] Add machine-readable failure contracts and regression tests for stdin/replay branches.

Suggested sign-off threshold:

- P1-1 can be considered substantially complete.
- Overall P1 should remain partial until P1-2 is complete.

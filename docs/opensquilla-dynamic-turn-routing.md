# OpenSquilla Dynamic Turn Routing

中文版本： [zh-CN/opensquilla-dynamic-turn-routing.md](zh-CN/opensquilla-dynamic-turn-routing.md)

OpenClaw.NET now includes an optional OpenSquilla-style dynamic turn-routing surface that can classify each user turn into `T0` through `T3`, then project that decision onto the existing OpenClaw model-profile, tool-filter, and system-prompt seams.

This is not a second routing stack. The implementation reuses the existing session-scoped route fields and model selection pipeline:

- `Session.ModelProfileId`
- `Session.PreferredModelTags`
- `Session.RouteAllowedTools`
- `Session.SystemPromptOverride`
- `Session.RouteModelTier`
- `Session.RouteReason`

The result is a turn-scoped routing layer that stays compatible with the repository's NativeAOT-first architecture and optional-dependency discipline.

## What It Does

At the start of each turn, OpenClaw can resolve a `TurnRoutingDecision` that contains:

- a tier name such as `T0`, `T1`, `T2`, or `T3`
- an optional model profile override
- an optional tool allowlist for the turn
- optional preferred profile tags
- an optional prompt suffix used as route instructions
- a machine-readable reason string

The decision contract also carries additive routing directives used by profile selection and runtime shaping:

- `DirectModelFallbackProfileId`
- `ReasoningLevel`
- `ResponsePolicy`
- `ImageCapableModelProfileId`
- `CacheContinuitySafeguardsEnabled`
- `CacheContinuityMaxConversationTurns`
- `CacheContinuityResetOnProfileSwitch`

That decision is applied only for the current turn. After the turn completes, the previous session route state is restored.

One field is intentionally sticky: `Session.RouteModelTier` is retained across turns so `EnableStickyTier` policy behavior can be applied consistently.

This gives the runtime a cheap place to narrow prompt scope and tool scope before the normal model call happens.

## Supported Runtime Paths

Dynamic turn routing is currently wired into both supported orchestrators:

- the native `AgentRuntime`
- the Microsoft Agent Framework adapter `MafAgentRuntime`

That matters because `MafAgentRuntime` is a separate orchestration path and does not flow through `NativeAgentRuntimeFactory`. The routing policy had to be wired explicitly into the MAF adapter so both runtimes now honor the same turn-scoped routing contract.

## Architecture Shape

The implementation is intentionally split across three layers.

### Core contracts

`OpenClaw.Core` contains only configuration and validation contracts:

- `DynamicTurnRoutingConfig`
- `DynamicTurnRoutingAssetsConfig`
- `DynamicTurnRoutingPolicyConfig`
- `DynamicTurnRoutingTierMap`
- `DynamicTurnRoutingTierTarget`

This keeps the core runtime free of ONNX and tokenizer dependencies.

### Runtime abstraction

`OpenClaw.Agent` contains the runtime-facing abstraction:

- `ITurnRoutingPolicy`
- `TurnRoutingRequest`
- `TurnRoutingDecision`
- `NoopTurnRoutingPolicy`

The native runtime and the MAF runtime both consume this abstraction and apply route-scoped session overrides before they build tool declarations, system prompt text, and chat options for the current turn.

### Optional ONNX implementation

`OpenClaw.Routing.Onnx` contains the optional ONNX-backed implementation boundary.

The gateway composes that implementation only when `OpenClaw:DynamicTurnRouting:Enabled=true`.

This preserves the repository's boundary rules:

- `OpenClaw.Core` stays small and AOT-friendly
- `OpenClaw.Agent` depends only on a routing interface
- `OpenClaw.Gateway` decides whether to compose the ONNX implementation

## Configuration

The feature is configured under `OpenClaw:DynamicTurnRouting`.

OpenClaw supports modern routing input through `Assets` and `Policy`.

At startup, the gateway can optionally import OpenSquilla-style bundle directories through `BundlePath`, then normalize bundle values and direct overrides into one internal resolved routing model before constructing the ONNX policy.

Preferred modern shape:

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Enabled": true,
      "BundlePath": "models/routing/opensquilla-v4-compat",
      "Policy": {
        "EnableStickyTier": true,
        "EnableMarginUpgrade": true,
        "EnableUnderRoutingSafety": true
      }
    }
  }
}
```

`Policy.Tiers` is the only supported tier mapping location.

## Tier Mapping Model

Each tier target can currently shape the turn in four ways:

- `ModelProfileId`: chooses which existing OpenClaw profile should serve the turn
- `AllowedTools`: narrows the declared tool list for that turn
- `PreferredTags`: biases profile selection toward matching profile tags
- `PromptMode` and `DisableTools`: appends a compact route instruction suffix to the system prompt

The built-in prompt suffix behavior is intentionally small:

- `minimal`: `Respond directly with minimal reasoning.`
- `compact`: `Keep the reply short and skip planning.`
- `DisableTools=true`: `Respond directly and do not call tools.`

## Validation Rules

Startup validation now checks the modern routing shape.

The validator currently fails fast when:

- a configured tier points at a profile id that is not present in `OpenClaw:Models:Profiles`
- classifier assets imply embeddings without a tokenizer path
- bundle-based configuration resolves a tier-to-profile mapping that is invalid

For bundle asset discovery failures, startup remains available and runtime falls back to `T2` with a machine-readable reason instead of hard-failing process boot.

## Current Runtime Behavior

The current implementation has two distinct parts:

1. the turn-scoped routing contract and runtime wiring are implemented and tested
2. the optional ONNX policy now executes a real local inference path when its assets are present

What is implemented today:

- native runtime applies route-scoped model, prompt, and tool overrides per turn
- MAF runtime applies the same route-scoped overrides per turn
- route state is restored after the turn, except `Session.RouteModelTier` which is intentionally sticky across turns
- gateway composition enables a noop policy by default and an ONNX-backed policy when configured
- config validation rejects tier-to-profile mappings that reference unknown profiles
- `LocalOnnxEmbeddingGenerator` loads a Hugging Face-style BPE `tokenizer.json`, tokenizes the user turn, runs the embedding ONNX model, and converts the result into a fixed-size embedding vector
- `PromptFeatureExtractor` combines lightweight prompt heuristics with that embedding vector
- `OnnxTurnRoutingPolicy` feeds the resulting feature vector into the classifier ONNX model and maps the predicted class back onto the configured tier target

Current fallback and compatibility behavior:

- when classifier assets are missing, the policy falls back to `T2`
- when local inference throws at runtime, the policy also falls back to `T2` with a machine-readable reason
- the classifier path accepts common integer label outputs as well as float logits and applies `argmax` for the latter

The feature support today is best understood as:

- architecture and runtime integration: implemented
- operator config surface: implemented
- local embedding plus classifier inference path: implemented
- fallback semantics: implemented

This is still an experimental routing capability, not a tuned production classifier. The runtime wiring is stable, but the prompt features and threshold defaults are intentionally conservative and are not claimed to match OpenSquilla-equivalent accuracy.

Why this remains experimental today:

- The current quality baseline is still below typical production classifier expectations. See [turn-routing-quality.grid-report.json](testing/turn-routing-quality.grid-report.json) (`best.MacroF1` is around `0.65` in the current grid output).
- The small 10-sample offline snapshots are useful smoke evidence, but they are not enough for production-quality claims and do not by themselves validate ONNX fallback execution paths. See [phase-a-offline-validation-10sample-report.json](testing/phase-a-offline-validation-10sample-report.json) and [phase-a-offline-validation-10sample-report-t1.json](testing/phase-a-offline-validation-10sample-report-t1.json).
- Tokenizer/model-family compatibility and bundle-shape interoperability still require explicit operator validation before rollout.

For production promotion criteria, use the Stage B launch gate table in this document as the minimum bar.

The remaining limitations are narrower now:

- the tokenizer loader now supports Hugging Face-style `tokenizer.json` files for both BPE and WordPiece models, but it still does not cover every tokenizer family or every pre-tokenizer shape
- the embedding path still assumes common transformer input names and embedding outputs that are either direct vectors (rank-1 / rank-2) or rank-3 hidden-state tensors that can be mean-pooled; arbitrary ONNX embedding graphs are not guaranteed to work unchanged
- the classifier path still assumes a 4-tier output contract (`T0`..`T3`) and a feature-vector shape that matches the classifier model's training run
- prompt features, post-processing rules, and threshold defaults remain intentionally conservative and are not yet tuned enough to claim OpenSquilla-equivalent quality

## Native And MAF Semantics

Both runtimes now share the same high-level semantics:

- resolve a turn-routing decision before the model call
- apply turn-scoped session overrides
- build tools and system prompt from the routed session view
- execute the turn
- restore the prior session route state

For the MAF adapter there is one extra nuance: if a routing decision returns an empty `AllowedTools` array, the runtime does not overwrite an already-populated manual `Session.RouteAllowedTools` allowlist. This preserves existing MAF route-filter behavior when no explicit per-turn allowlist is supplied by the router.

## Alignment With OpenSquilla `squilla-router.md` (2026-06)

Alignment should be understood in two layers.

### 1. Aligned: architecture and routing contract

- both systems classify each turn and project the decision onto model/policy surfaces
- OpenClaw `TurnRoutingDecision` already carries the key decision fields discussed in OpenSquilla docs: tier, model override, tool scope, reasoning level, response policy, and machine-readable reason
- gateway composition remains config-first and optional: `NoopTurnRoutingPolicy` when disabled, ONNX-backed routing policy when enabled
- both native and MAF orchestrator paths are wired into dynamic turn routing

### 2. Not fully aligned: model bundle format and inference pipeline

The current OpenSquilla `squilla_router` model directory (`src/opensquilla/squilla_router/models/v4.2_phase3_inference`) is still not drop-in compatible with OpenClaw routing today, but the exact gap is narrower than earlier drafts suggested.

Re-assessed reasons:

- **bundle contract mismatch is now partial, not absolute**: OpenClaw still prefers the compat shape (`manifest.json`, `classifier.onnx`, `embeddings.onnx`, `tokenizer.json`, `runtime-config.json`), but its bundle loader already resolves asset paths and embedding dimensions from nested manifest metadata when those keys exist. The remaining issue is that OpenSquilla v4.2 ships a different native directory layout (`artifact_manifest.json`, `inference_manifest.json`, `bge_onnx/model.onnx`, `mlp/model.onnx`, `lgbm_main.bin`) instead of an OpenClaw-ready compat bundle.
- **tokenizer mismatch is no longer a WordPiece blocker by itself**: OpenClaw now supports Hugging Face-style `tokenizer.json` files for both BPE and WordPiece models. The remaining incompatibility is broader: not every tokenizer family or pre-tokenizer shape is supported, so OpenSquilla tokenizer assets still need target-bundle validation.
- **classifier pipeline mismatch remains real**: OpenClaw's ONNX path still assumes one embedding model plus one 4-class ONNX classifier, while OpenSquilla v4.2 is a native multi-stage router with MLP, LightGBM, and post-process fusion. Native v4.2 inference therefore still cannot be consumed as-is through the current `OnnxTurnRoutingPolicy`.
- **tier vocabulary mismatch is mostly naming, but still needs an adapter contract**: OpenSquilla talks about `c0..c3` (with legacy `t0..t3` aliases), while OpenClaw projects decisions into `T0..T3`. This is easy to map in a compat export, but it is still a translation step rather than true drop-in parity.

Summary: architecture-level alignment is strong, bundle loading is more flexible than before, but native OpenSquilla v4.2 model-package interoperability still requires either a compat export or a dedicated adapter layer.

## Plan To Use OpenSquilla Router Models In OpenClaw

Based on the current repository state, this section is more accurately described as a two-track plan: first stabilize the compat bundle that already exists in-tree, then decide whether high-fidelity native parity is actually needed.

### Stage A: Stabilize the existing compat bundle (reference implementation already exists)

Goal: avoid rewriting the main OpenClaw router and instead promote the already-existing compat asset shape from a reference artifact into a repeatable export, validation, and operator workflow.

The repository already contains a concrete reference bundle at `models/routing/opensquilla-v4-compat/`. It already includes:

- `classifier.onnx`
- `embeddings.onnx`
- `tokenizer.json`
- `runtime-config.json`
- `manifest.json`

And this reference bundle is not just a placeholder:

- `manifest.json` already freezes the tier mapping `c0/c1/c2/c3 -> T0/T1/T2/T3`
- `runtime-config.json` already declares the current reference embedding size as `512`
- `tokenizer.json` is currently a `WordPiece` + `BertPreTokenizer` asset shape, which is a combination the current loader already supports and the test suite already covers

So the practical Stage A path is now:

1. Treat `models/routing/opensquilla-v4-compat` as the baseline reference artifact rather than redefining the compat shape from scratch
2. Add a repeatable export step in OpenSquilla that regenerates this same bundle contract reliably
3. Point `OpenClaw:DynamicTurnRouting:BundlePath` to that compat directory and map `Policy.Tiers` to OpenClaw model profiles
4. Validate offline first (fallback ratio, tier distribution, cost profile), then expand

This is the shortest path now because the repository already answers the question of what the compat bundle should look like. The remaining work is export automation, validation, and operator hardening.

### Stage A export contract (derived from the current reference bundle)

The export contract below is no longer just a theoretical suggestion. It should be read as the reproducible shape of the checked-in `models/routing/opensquilla-v4-compat` reference bundle.

Suggested export directory:

```text
<export-root>/
  manifest.json
  classifier.onnx
  embeddings.onnx
  tokenizer.json
  runtime-config.json
```

Contract expectations:

- `manifest.json`: at minimum include `classifierModelPath`, `embeddingModelPath`, `tokenizerPath`, and `runtimeConfigPath`
- `runtime-config.json`: at minimum include embedding dimensions (`dimensions` or `embeddingDimensions`); the current reference bundle uses `512`
- `classifier.onnx`: emit 4 classes corresponding to `T0/T1/T2/T3`
- `tokenizer.json`: emit a tokenizer shape that has already been validated against the current OpenClaw loader and supported pre-tokenizer set; the current reference bundle uses `WordPiece` + `BertPreTokenizer`

Suggested fixed tier mapping:

- `c0 -> T0`
- `c1 -> T1`
- `c2 -> T2`
- `c3 -> T3`

### Stage A file responsibilities in the compat bundle

For operator troubleshooting, the three key JSON files under `models/routing/opensquilla-v4-compat` have distinct runtime responsibilities:

- `manifest.json`
  - Purpose: asset index entry for the bundle. It typically provides `classifierModelPath`, `embeddingModelPath`, `tokenizerPath`, and `runtimeConfigPath`.
  - Runtime behavior: Gateway reads this first and resolves relative paths; if values are missing, it falls back to default bundle filenames such as `classifier.onnx`, `embeddings.onnx`, `tokenizer.json`, and `runtime-config.json`.
  - Failure signal: bad or missing paths increase `classifier_unavailable` risk and often show up as repeated fallback to `T2`.

- `tokenizer.json`
  - Purpose: tokenizer config used before local embedding inference. `LocalOnnxEmbeddingGenerator` uses it to encode turn text into model inputs.
  - Runtime behavior: if the file is missing or incompatible with the active tokenizer loader, ONNX routing degrades to fallback (`T2`) with a machine-readable reason.
  - Operational guidance: prefer a tokenizer format known to be compatible with the current loader; if using WordPiece variants, validate on your target bundle first.

- `runtime-config.json`
  - Purpose: runtime metadata, most importantly embedding dimension (`dimensions` / `embeddingDimensions` / `embeddingSize`).
  - Runtime behavior: Gateway reads embedding dimensions from this file first and writes them into resolved routing assets; if unavailable, it falls back to `manifest.json`, then to default `384`.
  - Failure signal: dimension mismatch vs real embedding output can degrade feature assembly quality and routing outcomes.

Quick troubleshooting checklist (recommended order):

1. If `classifier_unavailable` persists with repeated fallback to `T2`, verify paths in `manifest.json` first (existence + readability)
2. If startup is fine but inference fallback is frequent, validate `tokenizer.json` compatibility with the active tokenizer loader
3. If routing quality drifts or feature behavior looks unstable, verify `runtime-config.json` dimensions against real embedding output
4. If all three look correct but failures continue, compare `classifier.onnx` input shape against assembled feature vector dimensions

5-minute minimal self-check commands (PowerShell):

```powershell
$bundle = "models/routing/opensquilla-v4-compat"

# 1) File presence
Get-Item "$bundle/manifest.json", "$bundle/tokenizer.json", "$bundle/runtime-config.json" |
  Select-Object FullName, Length, LastWriteTime

# 2) JSON parse check
Get-Content "$bundle/manifest.json" -Raw | ConvertFrom-Json | Out-Null
Get-Content "$bundle/tokenizer.json" -Raw | ConvertFrom-Json | Out-Null
Get-Content "$bundle/runtime-config.json" -Raw | ConvertFrom-Json | Out-Null

# 3) Resolve runtime-config dimension field (any one of these keys)
$rc = Get-Content "$bundle/runtime-config.json" -Raw | ConvertFrom-Json
$dims = $rc.embeddingDimensions
if (-not $dims) { $dims = $rc.dimensions }
if (-not $dims) { $dims = $rc.embeddingSize }
"resolved embedding dims = $dims"
```

Checksum consistency note:

- If you modify any bundle JSON (especially `runtime-config.json`), update the corresponding entry in `manifest.json` -> `checksums`; otherwise verification tooling may report a stale or mismatched bundle.
- Quick SHA256 command:

```powershell
(Get-FileHash "models/routing/opensquilla-v4-compat/runtime-config.json" -Algorithm SHA256).Hash.ToLowerInvariant()
```

### Stage A.1: Offline sample validation notes

To preserve evidence for the “Stage A first, make it usable” milestone, keep both 10-sample snapshots for later comparison:

- [balanced 10-sample report](testing/phase-a-offline-validation-10sample-report.json): 5 simple + 5 complex samples, gold / predicted are both `T0:5` and `T2:5`, fallback is 0
- [T1-inclusive 10-sample report](testing/phase-a-offline-validation-10sample-report-t1.json): 3 `T0`, 3 `T1`, 2 `T2`, and 2 `T3` samples, gold / predicted match exactly, fallback is 0

Both reports now include a `costDistribution` section with unit-cost proxy totals (`T0=1`, `T1=2`, `T2=4`, `T3=8`) for offline comparison.

Note: both reports are based on the code-level heuristic routing rule. They are not measurements of actual ONNX fallback execution in this session, so fallback should be read as “not triggered,” not “ONNX fallback was verified.”

### Stage B: High-fidelity native adapter (optional)

Goal: preserve OpenSquilla v4.2 multi-head semantics as-is.

1. Add a dedicated optional implementation boundary (for example `OpenClaw.Routing.OpenSquillaV4`)
2. Parse OpenSquilla-native bundle assets (`inference_manifest.json`, `router.runtime.yaml`, `bge_onnx`, `mlp`, `lgbm_*`)
3. Recreate v4.2 fusion/postprocess logic in that layer and map result to `TurnRoutingDecision`
4. Keep `OpenClaw.Core` and `OpenClaw.Agent` interface-only; composition still decided by gateway

This path is more expensive but yields higher behavioral parity.

### Stage B launch gate table (milestone-ready)

| Dimension | Metric | Go threshold | Observation window | If not met | Hard rollback trigger |
|---|---|---|---|---|---|
| Availability | Total fallback rate | <= 0.5% | 14 consecutive days (10% production canary) | Extend canary by 7 days and inspect bundle/model load path | Any 30-minute window > 2.0%: rollback to Stage A |
| Availability | classifier_unavailable | <= 0.1% | 14 consecutive days | Validate bundle naming, paths, and asset completeness first | Any 30-minute window > 0.5%: rollback |
| Stability | classifier_runtime_error | <= 0.2% | 14 consecutive days | Investigate ONNX inference, shape, and tokenizer compatibility | Any 15-minute window > 1.0%: rollback |
| Quality | Labeled-set Macro-F1 | >= 0.90 (N >= 500) | Weekly rolling evaluation for 2 weeks | Do not scale to full traffic; continue threshold/tier-map tuning | 2 consecutive weekly runs < 0.88: rollback |
| Quality | High-cost-tier recall (T2/T3 recall) | >= 0.92 | Weekly rolling evaluation for 2 weeks | Keep only low traffic canary; no scale-out | 2 consecutive weekly runs < 0.90: rollback |
| Distribution | Tier drift vs Stage A baseline | Absolute drift per tier <= 5pp, T3 drift <= +2pp | 14 consecutive days | Freeze scale-out and analyze prompt/workload drift | Any 24-hour window with T3 drift > +5pp and no quality gain: rollback |
| Cost | Cost per 1k turns (unit-cost proxy) | <= +8% vs Stage A | 14 consecutive days | Rate-limit and retune up/down-tier thresholds | Any 24-hour window > +15%: rollback |
| Performance | Added routing latency (p95) | <= +80ms vs Stage A | 14 consecutive days | Optimize model loading and cache behavior | > +150ms for 6 consecutive hours: rollback |
| Consistency | Native/MAF decision parity | >= 99.5% (same input, same config) | Daily reconciliation for 14 days | Block scale-out and close parity gaps first | Any day < 99.0%: rollback |
| Ops risk | Sev1/Sev2 routing incidents | Sev1 = 0, Sev2 <= 1/week | 14 consecutive days | Stop scale-out and run incident review | Any Sev1: immediate rollback |

Stage B rollout decision rules:

1. Scale from 10% to 50% only when all Go thresholds are met and no hard rollback trigger fires.
2. Observe 50% traffic for another 7 days; request full rollout only if all thresholds still hold.
3. If any hard rollback trigger fires, switch back to Stage A immediately and freeze Stage B changes for at least 72 hours before re-review.

Stage B rollback runbook (short form):

1. Config rollback: switch routing strategy back to Stage A compatibility path (keep current BundlePath and tier mapping).
2. Traffic rollback: reduce Stage B traffic to 0% within 5 minutes.
3. Evidence capture: retain 24-hour before/after snapshots for fallback, tier distribution, cost, latency, and native/MAF parity.
4. Re-entry gate: do not re-enter 10% canary until root cause is identified and validated.

## Current Integration Risks

- **Risk 1: native OpenSquilla bundle shape still does not match the OpenClaw compat contract**. Assets can exist but still fail composition because the shipped v4.2 directory layout is not the same thing as an OpenClaw-ready bundle.
- **Risk 2: tokenizer compatibility is asset-specific, not family-guaranteed**. WordPiece is no longer automatically incompatible, but tokenizer and pre-tokenizer variants still need validation against the current loader.
- **Risk 3: classifier pipeline mismatch**. Even with a readable tokenizer and embeddings, OpenSquilla v4.2's multi-head fusion path does not map directly onto the current single-classifier ONNX runtime.
- **Risk 4: native/MAF semantic gap**. Native runtime currently consumes more routing fields than MAF runtime (for example direct fallback, reasoning level, response policy).

Recommended order:

1. treat the existing `models/routing/opensquilla-v4-compat` bundle as the baseline and verify end-to-end load, routing, and fallback behavior
2. turn that baseline into a repeatable OpenSquilla-side export flow, including checksum and metadata generation
3. validate the exported tokenizer on the target bundle, whether it is BPE or WordPiece, before widening traffic
4. decide whether Stage B parity is necessary for your operating envelope

Common historical failure cases and troubleshooting:

- Signal: long-running `classifier_unavailable`
  - Check first whether the files referenced by `BundlePath` actually match the OpenClaw compat contract and are readable
- Signal: repeated fallback to `T2` after inference exceptions
  - Check whether embedding input names and output shape still match current `LocalOnnxEmbeddingGenerator` assumptions
- Signal: tokenizer load failure
  - Check whether the tokenizer exceeds the specific shapes the current loader already supports; the current reference bundle uses `WordPiece` + `BertPreTokenizer`, so a failure here is more likely to come from another tokenizer family or an uncovered pre-tokenizer combination
- Signal: native/MAF behavior mismatch
  - If this still appears, confirm whether you are depending on extra routing fields that are currently consumed only by native runtime

Reference `runtime-config.json` sample:

```json
{
  "schemaVersion": 1,
  "embeddingDimensions": 512,
  "classifier": {
    "numClasses": 4,
    "classLabels": ["T0", "T1", "T2", "T3"]
  },
  "notes": {
    "tokenizerFamily": "WordPiece",
    "sourceModelDir": "opensquilla/squilla_router/models/v4.2_phase3_inference",
    "sourceRuntimeConfig": "opensquilla/squilla_router/models/v4.2_phase3_inference/inference_manifest.json"
  }
}
```

## CLI Surface

Routing CLI details are documented in [cli/routing.md](cli/routing.md).

OpenClaw remains config-first for dynamic routing (`OpenClaw:DynamicTurnRouting`), and the routing CLI commands are a management facade over that same config-driven surface.

The preferred modern routing shape is `BundlePath` + `Policy.Tiers`.

## Operator Guidance

Use this feature when you want:

- a future-proof seam for cheap turn classification before model selection
- route-scoped tool narrowing for read-only or lightweight turns
- compact prompt instructions for simpler requests
- a clean architecture boundary for local classifier experiments

Do not treat it yet as a finished OpenSquilla-equivalent local router if you need:

- real learned tier classification in production
- native OpenSquilla v4.2 multi-stage inference parity
- tuned prompt feature extraction and probability thresholds

If you need stronger claims, run the offline routing evaluation baseline first and compare the report against a known sample set before widening the operating envelope.

For now, think of it as a stable routing contract plus optional ONNX composition boundary that is ready for a stronger classifier implementation.

## Related Docs

- [LOCAL_MODELS.md](LOCAL_MODELS.md): local asset and operator guidance
- [ARCHITECTURE_BOUNDARIES.md](ARCHITECTURE_BOUNDARIES.md): why ONNX routing stays outside `OpenClaw.Core`
- [MODEL_PROFILES.md](MODEL_PROFILES.md): how routed profile ids flow through normal model selection
- [integrations/microsoft-agent-framework.md](integrations/microsoft-agent-framework.md): MAF runtime path and adapter context

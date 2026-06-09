# Dynamic Routing Evaluation

This document defines the offline baseline check for dynamic turn routing.

The goal is not to prove production-grade accuracy. The goal is to keep the current routing heuristics measurable so we can compare conservative baselines, spot regressions, and avoid overstating capability.

## Baseline Sets

The routing regression script compares three strategies against the same sample set:

- `alwaysT2` - a conservative baseline that always promotes to `T2`
- `ruleOnly` - the rule layer without classifier help
- `classifierPlusRules` - the combined route decision used by the current bundle

## Sample Schema

Each sample in `tests/routing-eval/*.json` should include:

- `id` - stable sample identifier
- `prompt` - representative user request
- `expectedTier` - ground-truth tier for the sample
- `ruleOnlyTier` - expected outcome from rule-only routing
- `classifierTier` - raw classifier tier for reference
- `combinedTier` - final tier after classifier plus rules
- `riskFlags` - optional tags describing why the sample matters
- `rationale` - short human-readable justification
- `source` - provenance string for the sample

## How To Run

```powershell
./scripts/test-dynamic-routing-regression.ps1
```

Optional parameters:

- `-SamplesPath tests/routing-eval`
- `-OutputRoot artifacts/testing/dynamic-routing`
- `-Strict` to fail the script when the gate conditions are not met

The script writes both `report.json` and `report.md` under a timestamped subdirectory in `artifacts/testing/dynamic-routing/`.

## Curated Artifacts

Curated routing-quality snapshots are kept under `docs/testing/` for documentation and review workflows:

- `docs/testing/turn-routing-quality.grid-report.json`
- `docs/testing/turn-routing-quality.policy-suggestion.json`
- `docs/testing/turn-routing-quality.report.json`
- `docs/testing/turn-routing-quality.weight-sensitivity-report.json`
- `docs/testing/phase-a-offline-validation-report.json`
- `docs/testing/phase-a-offline-validation-10sample-report.json`
- `docs/testing/phase-a-offline-validation-10sample-report-t1.json`
- `docs/testing/phase-a-offline-validation-10sample.jsonl`
- `docs/testing/phase-a-offline-validation-10sample-t1.jsonl`

## Gate

The default gate checks that `classifierPlusRules` does not regress on the sample set:

- accuracy must not fall below `ruleOnly`
- under-routing risk must not exceed `alwaysT2`

The report also includes per-tier F1 so threshold changes can be compared against the same sample corpus over time.

## Practical Use

Use this baseline before tuning routing thresholds or changing feature extraction:

1. Update or extend the sample set.
2. Run the regression script.
3. Compare the new report to the previous artifact.
4. Only widen claim language after the offline metrics remain stable.

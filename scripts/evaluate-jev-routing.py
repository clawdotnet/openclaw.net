#!/usr/bin/env python3
"""Summarize Jev or Laya routing journals; optional human labels measure tier quality, not task success."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import sys


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.laya_service.calibration import metrics


TIERS = ("T0", "T1", "T2", "T3")


def read_jsonl(path):
    with Path(path).open(encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ValueError(f"{path}:{line_number}: invalid JSON") from exc
                if not isinstance(row, dict):
                    raise ValueError(f"{path}:{line_number}: expected an object")
                yield row


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)] if ordered else None


def quality(rows, labels, predict):
    confusion = {tier: {predicted: 0 for predicted in TIERS} for tier in TIERS}
    under = over = correct = high_risk_count = high_risk_retained = 0
    for row in rows:
        label = labels[row["decision_id"]]
        expected, predicted = label["expected_tier"], predict(row)
        confusion[expected][predicted] += 1
        correct += predicted == expected
        under += TIERS.index(predicted) < TIERS.index(expected)
        over += TIERS.index(predicted) > TIERS.index(expected)
        if label.get("high_risk", False):
            high_risk_count += 1
            high_risk_retained += TIERS.index(predicted) >= max(2, TIERS.index(expected))
    per_tier = {}
    for tier in TIERS:
        tp = confusion[tier][tier]
        fp = sum(confusion[other][tier] for other in TIERS if other != tier)
        fn = sum(confusion[tier][other] for other in TIERS if other != tier)
        per_tier[tier] = {
            "support": sum(confusion[tier].values()),
            "f1": 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else 0,
        }
    return {
        "samples": len(rows), "accuracy": correct / len(rows),
        "under_routing_rate": under / len(rows), "over_routing_rate": over / len(rows),
        "high_risk_samples": high_risk_count,
        "high_risk_capability_retention": high_risk_retained / high_risk_count if high_risk_count else None,
        "macro_f1": sum(item["f1"] for item in per_tier.values()) / len(TIERS),
        "per_tier": per_tier, "confusion": confusion,
    }


def summarize(rows, label_rows=()):
    if not rows:
        raise ValueError("The journal contains no decisions.")
    by_id = {}
    for row in rows:
        identifier = row.get("decision_id")
        if not identifier or identifier in by_id:
            raise ValueError("Every decision must have a unique nonempty decision_id.")
        if row.get("baseline_tier") not in TIERS or row.get("applied_tier") not in TIERS:
            raise ValueError(f"Invalid baseline/applied tier for {identifier}.")
        if row.get("proposed_tier") is not None and row["proposed_tier"] not in TIERS:
            raise ValueError(f"Invalid proposed tier for {identifier}.")
        latency = row.get("latency_ms")
        cost = row.get("estimated_cost_usd", 0)
        if not isinstance(latency, (int, float)) or not math.isfinite(latency) or latency < 0:
            raise ValueError(f"Invalid latency for {identifier}.")
        if cost is not None and (not isinstance(cost, (int, float)) or not math.isfinite(cost) or cost < 0):
            raise ValueError(f"Invalid cost for {identifier}.")
        by_id[identifier] = row
    proposed = [row for row in rows if row.get("proposed_tier") is not None]
    completed = [row for row in rows if row.get("input_tokens") is not None]
    latencies = [row["latency_ms"] for row in rows]
    report = {
        "decisions": len(rows), "responses_with_usage": len(completed),
        "eligible_proposals": len(proposed), "proposal_coverage": len(proposed) / len(rows),
        "modes": dict(Counter(row.get("mode", "unknown") for row in rows)),
        "providers": dict(Counter(row.get("provider", "jev") for row in rows)),
        "models": dict(Counter(row.get("model", "unreported") for row in rows)),
        "rubric_versions": dict(Counter(row.get("rubric_version", "unknown") for row in rows)),
        "reasons": dict(Counter(row.get("reason", "unknown") for row in rows)),
        "proposed_tiers": dict(Counter(row["proposed_tier"] for row in proposed)),
        "proposal_disagreement_with_baseline": sum(row["proposed_tier"] != row["baseline_tier"] for row in proposed) / len(proposed) if proposed else None,
        "added_latency_ms": {"p50": percentile(latencies, .50), "p95": percentile(latencies, .95), "max": max(latencies)},
        "reported_input_tokens": sum(row["input_tokens"] for row in completed),
        "estimated_reported_decision_cost_usd": round(sum(row.get("estimated_cost_usd") or 0 for row in rows), 8),
        "quality": None,
        "limitations": [
            "Decision cost excludes failed calls without usage, downstream models, retries, and cache effects.",
            "Tier labels do not measure task success or establish calibrated confidence.",
            "With ONNX disabled, baseline tier T2 is a bookkeeping default; the actual configured model is unchanged.",
            "The proposal includes confidence gates and safety floors; missing proposals fall back to the baseline.",
            "Compare model/rubric cohorts separately before tuning thresholds.",
        ],
    }
    labels = {}
    for label in label_rows:
        identifier = label.get("decision_id")
        if identifier not in by_id or identifier in labels or label.get("expected_tier") not in TIERS:
            raise ValueError("Labels must reference unique journal decision IDs and expected_tier T0 through T3.")
        if "high_risk" in label and not isinstance(label["high_risk"], bool):
            raise ValueError("high_risk must be a JSON boolean.")
        labels[identifier] = label
    if labels:
        labeled = [row for row in rows if row["decision_id"] in labels]
        report["quality"] = {
            "labeled_decisions": len(labeled), "label_coverage": len(labeled) / len(rows),
            "baseline": quality(labeled, labels, lambda row: row["baseline_tier"]),
            "always_t2": quality(labeled, labels, lambda row: "T2"),
            "jev_with_fallback": quality(labeled, labels, lambda row: row.get("proposed_tier") or row["baseline_tier"]),
        }
    # Evaluate raw tier distributions, separately from the safeguards/fallback policy.
    # Never pool calibration across checkpoint, rubric, or temperature artifacts.
    cohorts = {}
    for row in rows:
        if row["decision_id"] not in labels or not row.get("probabilities"):
            continue
        metadata = row.get("metadata") or {}
        identity = json.dumps([row.get("provider", "jev"), row.get("model"), row.get("rubric_version"),
                               metadata.get("checkpoint"), metadata.get("revision"), metadata.get("calibration_id"),
                               metadata.get("schema_hash")])
        cohorts.setdefault(identity, []).append({
            "answer": {"type": "choice", "probabilities": row["probabilities"]},
            "label": labels[row["decision_id"]]["expected_tier"]})
    report["calibration_quality"] = [{"cohort": json.loads(key), **metrics(values)} for key, values in cohorts.items()]
    if report["quality"]:
        report["quality"]["decision_with_fallback"] = report["quality"]["jev_with_fallback"]
        if any(row.get("provider") == "laya" for row in rows):
            del report["quality"]["jev_with_fallback"]
    return report


def plot(report, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    cohorts = report["calibration_quality"]
    if not cohorts:
        raise ValueError("Reliability plots require labeled probabilities.")
    figure, axes = plt.subplots(len(cohorts), 2, figsize=(10, 4 * len(cohorts)), squeeze=False)
    for index, cohort in enumerate(cohorts):
        bins = cohort["reliability_bins"]
        axes[index, 0].plot([0, 1], [0, 1], "--", color="gray")
        axes[index, 0].plot([b["mean_top_probability"] for b in bins], [b["accuracy"] for b in bins], "o-")
        provider, model, rubric, checkpoint, revision, calibration_id, schema = cohort["cohort"]
        title = f"{provider} / {checkpoint or model or 'unknown'}\n{rubric or 'unknown rubric'} | revision {(revision or 'n/a')[:8]} | calibration {(calibration_id or 'n/a')[:8]}"
        axes[index, 0].set(xlabel="Mean top probability", ylabel="Accuracy", xlim=(0, 1), ylim=(0, 1))
        axes[index, 0].set_title(title, fontsize=9)
        curve = [point for point in cohort["risk_coverage"] if point["error_rate"] is not None]
        axes[index, 1].plot([p["coverage"] for p in curve], [p["error_rate"] for p in curve], "o-")
        axes[index, 1].set(xlabel="Coverage at entropy-confidence threshold", ylabel="Error rate", xlim=(0, 1), ylim=(0, 1))
    figure.tight_layout()
    figure.savefig(path, dpi=150)
    plt.close(figure)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("journal", help="Path to a snapshot of jev-decisions.jsonl")
    parser.add_argument("--labels", help="Optional JSONL with decision_id, expected_tier, and high_risk")
    parser.add_argument("--output", help="Write report JSON here instead of stdout")
    parser.add_argument("--plot", help="Optional reliability/risk-coverage PNG (requires matplotlib)")
    args = parser.parse_args()
    try:
        report = summarize(list(read_jsonl(args.journal)), list(read_jsonl(args.labels)) if args.labels else [])
        if args.plot:
            plot(report, args.plot)
        rendered = json.dumps(report, indent=2, allow_nan=False) + "\n"
        if args.output:
            Path(args.output).write_text(rendered, encoding="utf-8")
        else:
            sys.stdout.write(rendered)
    except (OSError, ValueError, TypeError, KeyError, ImportError) as exc:
        parser.exit(2, f"{exc}\n")


if __name__ == "__main__":
    main()

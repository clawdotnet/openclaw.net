"""Post-hoc temperature fitting and held-out evaluation; never modifies Laya weights.

Inputs are local observations from evaluate.py. Fit and validation cases must be disjoint.
"""
import argparse
import copy
import json
import math
from pathlib import Path
from .protocol import CHECKPOINTS, SDK_VERSION, Rejected, canonical, distribution, file_hash, is_hex


def temperature_scale(probabilities, temperature):
    logits = [math.log(max(p, 1e-9)) / temperature for p in probabilities]
    peak = max(logits)
    values = [math.exp(value - peak) for value in logits]
    total = sum(values)
    return [value / total for value in values]


def confidence(probabilities, kind):
    if kind == "noul":
        return max(probabilities)
    return max(0.0, 1 + sum(p * math.log(max(p, 1e-12)) for p in probabilities) / math.log(len(probabilities)))


def bucket(kind, count):
    return f"{kind}:{count}"


def metrics(rows):
    count = len(rows)
    if not count:
        raise ValueError("No labeled predictions.")
    bins = [[] for _ in range(10)]
    nll = brier = correct_count = 0
    ranked = []
    for row in rows:
        keys, probabilities = distribution(row["answer"])
        expected = keys.index(row["label"])
        predicted = max(range(len(probabilities)), key=probabilities.__getitem__)
        correct = int(predicted == expected)
        top = probabilities[predicted]
        bins[min(9, int(top * 10))].append((top, correct))
        correct_count += correct
        nll -= math.log(max(probabilities[expected], 1e-9))
        brier += sum((p - int(i == expected)) ** 2 for i, p in enumerate(probabilities))
        ranked.append((confidence(probabilities, row["answer"]["type"]), correct))
    reliability = []
    ece = 0
    for i, values in enumerate(bins):
        if values:
            mean = sum(v[0] for v in values) / len(values)
            accuracy = sum(v[1] for v in values) / len(values)
            ece += len(values) / count * abs(mean - accuracy)
            reliability.append({"lower": i / 10, "upper": (i + 1) / 10,
                                "samples": len(values), "mean_top_probability": mean, "accuracy": accuracy})
    # Include all ties; threshold curves must not pretend a tie can be selectively accepted.
    coverage = []
    for threshold in (0.0, 0.2, 0.4, 0.6, 0.8, 0.9, 0.95, 0.99):
        selected = [correct for conf, correct in ranked if conf >= threshold]
        coverage.append({"confidence_threshold": threshold, "coverage": len(selected) / count,
                         "error_rate": 1 - sum(selected) / len(selected) if selected else None})
    return {"samples": count, "accuracy": correct_count / count, "nll": nll / count,
            "brier": brier / count, "ece": ece, "reliability_bins": reliability, "risk_coverage": coverage}


def transform(answer, temperature):
    result = copy.deepcopy(answer)
    keys, values = distribution(answer)
    values = temperature_scale(values, temperature)
    result["confidence"] = confidence(values, answer["type"])
    if answer["type"] == "noul":
        result["noul"] = values[1]
    else:
        result["probabilities"] = dict(zip(keys, values))
        if answer["type"] == "choice":
            result["choice"] = keys[max(range(len(values)), key=values.__getitem__)]
        else:
            result["score"] = sum(int(key) * probability for key, probability in zip(keys, values))
    return result


class Calibration:
    def __init__(self, path=None, model=None):
        self.data = None
        self.identifier = "uncalibrated"
        if path:
            data = json.loads(Path(path).read_text())
            if (data.get("version") != 1 or data.get("sdk_version") != SDK_VERSION or
                data.get("model") != model or not is_hex(data.get("schema_hash"), 64) or
                not isinstance(data.get("temperatures"), dict) or not data["temperatures"] or
                not isinstance(data.get("validation"), dict)):
                raise ValueError("Invalid calibration artifact or model identity.")
            for checkpoint, values in data["temperatures"].items():
                if checkpoint not in CHECKPOINTS or not isinstance(values, dict) or not values:
                    raise ValueError("Invalid calibration checkpoint.")
                for key, temperature in values.items():
                    if not isinstance(key, str) or not isinstance(temperature, (int, float)) or not math.isfinite(temperature) or not 0.1 <= temperature <= 10:
                        raise ValueError("Invalid calibration temperature.")
            self.data = data
            self.identifier = file_hash(path)

    def check(self, schema, checkpoint, questions):
        if not self.data:
            return
        if self.data["schema_hash"] != schema:
            raise Rejected("calibration_schema_mismatch")
        values = self.data["temperatures"].get(checkpoint, {})
        for question in questions.values():
            count = 2 if question["type"] == "noul" else len(question["criteria"])
            if bucket(question["type"], count) not in values:
                raise Rejected("calibration_bucket_missing")

    def apply(self, checkpoint, answers):
        if not self.data:
            return answers
        result = {}
        for name, answer in answers.items():
            keys, _ = distribution(answer)
            result[name] = transform(answer, self.data["temperatures"][checkpoint][bucket(answer["type"], len(keys))])
        return result


def load_observations(path):
    rows, seen = [], set()
    for line in Path(path).read_text().splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        identity = (row["case_id"], row["question_id"])
        if identity in seen or not all(isinstance(v, str) and v for v in identity):
            raise ValueError("Duplicate or empty observation identity.")
        seen.add(identity)
        if not is_hex(row.get("case_fingerprint"), 64) or row.get("checkpoint") not in CHECKPOINTS or not is_hex(row.get("schema_hash"), 64) or row.get("sdk_version") != SDK_VERSION or row.get("source_calibration") != "raw":
            raise ValueError("Observations must contain raw outputs and complete provenance.")
        if row["answer"].get("type") not in ("choice", "score", "noul"):
            raise ValueError("Unsupported answer type.")
        keys, _ = distribution(row["answer"])
        if row["label"] not in keys:
            raise ValueError("Label is outside the question's options.")
        rows.append(row)
    if not rows:
        raise ValueError("Empty observations.")
    return rows


def fit(training, validation, minimum=20):
    if {r["case_id"] for r in training} & {r["case_id"] for r in validation}:
        raise ValueError("Calibration and validation case IDs overlap.")
    if {r["case_fingerprint"] for r in training} & {r["case_fingerprint"] for r in validation}:
        raise ValueError("Calibration and validation contain identical requests under different IDs.")
    identities = {(r["model"], r["schema_hash"], r["sdk_version"]) for r in training + validation}
    if len(identities) != 1:
        raise ValueError("Use one model revision, SDK version, and question schema per artifact.")
    model, schema, sdk = identities.pop()
    if not model.startswith("laya@") or not is_hex(model[5:], 40):
        raise ValueError("Unpinned model.")
    def group(row):
        keys, _ = distribution(row["answer"])
        return row["checkpoint"], bucket(row["answer"]["type"], len(keys))
    groups = {group(r) for r in training}
    if groups != {group(r) for r in validation}:
        raise ValueError("Training and validation must cover the same checkpoint/question buckets.")
    result = {"version": 1, "model": model, "schema_hash": schema, "sdk_version": sdk,
              "temperatures": {}, "validation": {}}
    for checkpoint, key in sorted(groups):
        train = [r for r in training if group(r) == (checkpoint, key)]
        test = [r for r in validation if group(r) == (checkpoint, key)]
        if min(len(train), len(test)) < minimum or len({r["label"] for r in train}) < 2:
            raise ValueError("Each bucket needs at least 20 calibration and 20 held-out observations, and multiple calibration labels.")
        def loss(temperature):
            total = 0
            for row in train:
                keys, values = distribution(row["answer"])
                total -= math.log(max(temperature_scale(values, temperature)[keys.index(row["label"])], 1e-9))
            return total
        temperature = min((10 ** (-1 + i / 80) for i in range(161)), key=loss)
        result["temperatures"].setdefault(checkpoint, {})[key] = temperature
        changed = [{**r, "answer": transform(r["answer"], temperature)} for r in test]
        result["validation"].setdefault(checkpoint, {})[key] = {
            "calibration_samples": len(train), "raw": metrics(test), "calibrated": metrics(changed)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fit", required=True, help="Raw calibration observations JSONL")
    parser.add_argument("--validate", required=True, help="Disjoint held-out observations JSONL")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        result = fit(load_observations(args.fit), load_observations(args.validate))
        Path(args.output).write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
        print(json.dumps({"calibration_id": file_hash(args.output), "validation": result["validation"]}))
    except (OSError, ValueError, KeyError, TypeError) as exc:
        parser.exit(2, str(exc) + "\n")


if __name__ == "__main__":
    main()

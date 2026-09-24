"""Evaluate labeled local JSONL cases without recording state in observations.

Each case: {case_id, model, state, questions, rubric_version, labels, language?}.
Labels: choice key, score index, or a JSON boolean for noul. Separate datasets
must be used for calibration fitting and validation.
"""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import Request, build_opener, ProxyHandler, HTTPRedirectHandler
from .protocol import DEFAULT_REVISION, canonical, distribution, read_json, validate_request


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def evaluate(dataset, endpoint, output):
    url = urlparse(endpoint)
    if url.scheme != "http" or url.hostname != "127.0.0.1" or url.username or url.password or url.query or url.fragment or url.path != "/v1/decisions":
        raise ValueError("Use the local http://127.0.0.1:PORT/v1/decisions endpoint.")
    opener = build_opener(ProxyHandler({}), NoRedirect())
    rows, seen = [], set()
    for line in Path(dataset).read_text().splitlines():
        if not line.strip():
            continue
        case = read_json(line)
        identifier = case.pop("case_id")
        labels = case.pop("labels")
        if not isinstance(identifier, str) or not identifier or identifier in seen:
            raise ValueError("Case IDs must be nonempty and unique.")
        seen.add(identifier)
        if "questions" not in case:
            rubric = read_json((Path(__file__).parent / "rubrics/openclaw-laya-tiers-v1.json").read_text())
            case.update(rubric)
        case.setdefault("model", "laya@" + DEFAULT_REVISION)
        questions = validate_request(case, case["model"])
        if set(labels) != set(questions):
            raise ValueError("Provide one label for every question.")
        request = Request(endpoint, data=canonical(case).encode(), headers={"Content-Type": "application/json"})
        with opener.open(request, timeout=30) as response:
            result = read_json(response.read(262145).decode())
        metadata = result["metadata"]
        if result["model"] != case["model"] or metadata["truncated"]:
            raise ValueError("Model identity mismatch or truncated prediction.")
        for name, answer in result["raw_answers"].items():
            label = labels[name]
            if answer["type"] == "noul":
                if not isinstance(label, bool):
                    raise ValueError("Noul labels must be JSON booleans.")
                label = str(label).lower()
            else:
                label = str(label)
            keys, _ = distribution(answer)
            if label not in keys:
                raise ValueError("Label is outside the question's options.")
            rows.append({"case_id": identifier, "case_fingerprint": hashlib.sha256(canonical(case).encode()).hexdigest(), "question_id": name, "model": result["model"],
                         "checkpoint": metadata["checkpoint"], "schema_hash": metadata["schema_hash"],
                         "sdk_version": metadata["sdk_version"], "source_calibration": "raw",
                         "answer": answer, "label": label})
    if not rows:
        raise ValueError("Empty dataset.")
    Path(output).write_text("".join(canonical(row) + "\n" for row in rows))
    return len(rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset")
    parser.add_argument("--endpoint", default="http://127.0.0.1:8099/v1/decisions")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        print(json.dumps({"observations": evaluate(args.dataset, args.endpoint, args.output)}))
    except (ValueError, KeyError, TypeError, OSError) as exc:
        parser.exit(2, f"Evaluation failed ({type(exc).__name__}); no new observations written.\n")


if __name__ == "__main__":
    main()

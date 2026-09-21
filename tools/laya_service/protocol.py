"""Small, dependency-free local decision protocol and artifact validation."""
import hashlib
import json
import math
import re
from pathlib import Path

SDK_VERSION = "0.3.4"
DEFAULT_REVISION = "1c5edc17a7acd8701df6fc341c0d179f1c62c982"
CHECKPOINTS = ("english", "multilingual", "typed-decisions")
MODEL_FILES = ("model.safetensors", "rl_agent_config.json", "encoder/config.json",
               "tokenizer/tokenizer.json", "tokenizer/tokenizer_config.json")


class Rejected(ValueError):
    """A safe reason code, never request text or an underlying exception message."""


def canonical(value):
    # Preserve dictionary order: candidate order changes Laya's inputs and predictions.
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def schema_hash(questions):
    return hashlib.sha256(canonical(questions).encode()).hexdigest()


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_hex(value, length):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{%d}" % length, value) is not None


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise Rejected("duplicate_json_key")
        result[key] = value
    return result


def read_json(text):
    def invalid_constant(_):
        raise Rejected("nonfinite_json")
    return json.loads(text, object_pairs_hook=unique_object, parse_constant=invalid_constant)


def validate_request(request, model):
    if not isinstance(request, dict) or set(request) - {"model", "state", "questions", "rubric_version", "language"}:
        raise Rejected("invalid_request")
    if request.get("model") != model:
        raise Rejected("model_version_mismatch")
    if not isinstance(request.get("state"), (str, dict, list)) or len(canonical(request["state"])) > 32000:
        raise Rejected("invalid_state")
    rubric = request.get("rubric_version")
    if not isinstance(rubric, str) or not re.fullmatch(r"[a-zA-Z0-9_.-]{1,80}", rubric):
        raise Rejected("invalid_rubric")
    lang = request.get("language")
    if lang is not None and (not isinstance(lang, str) or not re.fullmatch(r"[a-zA-Z0-9-]{1,35}", lang)):
        raise Rejected("invalid_language")
    questions = request.get("questions")
    if not isinstance(questions, dict) or not 1 <= len(questions) <= 16:
        raise Rejected("invalid_questions")
    for name, question in questions.items():
        if not isinstance(name, str) or not re.fullmatch(r"[a-zA-Z0-9_.-]{1,80}", name) or not isinstance(question, dict):
            raise Rejected("invalid_question")
        if set(question) - {"type", "instructions", "criteria"}:
            raise Rejected("invalid_question")
        if not isinstance(question.get("instructions"), str) or not 1 <= len(question["instructions"]) <= 2000:
            raise Rejected("invalid_instructions")
        kind, criteria = question.get("type"), question.get("criteria")
        if kind == "choice":
            if not isinstance(criteria, dict) or not 2 <= len(criteria) <= 20 or any(not isinstance(k, str) or not 1 <= len(k) <= 80 for k in criteria):
                raise Rejected("invalid_choices")
            values = criteria.values()
        elif kind == "score":
            if not isinstance(criteria, list) or not 2 <= len(criteria) <= 10:
                raise Rejected("invalid_score")
            values = criteria
        elif kind == "noul":
            if criteria is not None and (not isinstance(criteria, dict) or set(criteria) != {"false", "true"}):
                raise Rejected("invalid_noul")
            values = criteria.values() if criteria else []
        else:
            raise Rejected("invalid_question_type")
        if any(v is not None and (not isinstance(v, str) or len(v) > 2000) for v in values):
            raise Rejected("invalid_criteria")
    return questions


def distribution(answer):
    if answer["type"] == "noul":
        probability = answer["noul"]
        values, keys = [1 - probability, probability], ["false", "true"]
    else:
        keys, values = list(answer["probabilities"]), list(answer["probabilities"].values())
    if len(values) < 2 or any(not isinstance(p, (float, int)) or not math.isfinite(p) or not 0 <= p <= 1 for p in values) or abs(sum(values) - 1) > 0.002:
        raise Rejected("invalid_model_probabilities")
    total = sum(values)
    return keys, [p / total for p in values]

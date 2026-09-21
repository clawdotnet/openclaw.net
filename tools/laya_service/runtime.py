"""Preloaded, offline Laya runtime with strict provenance and no silent truncation."""
import json
import os
from pathlib import Path
from .calibration import Calibration
from .compat import ensure_complete, select_checkpoint
from .protocol import (CHECKPOINTS, MODEL_FILES, SDK_VERSION, Rejected, file_hash,
                       is_hex, schema_hash, validate_request)


def load_manifest(path):
    path = Path(path).resolve()
    data = json.loads(path.read_text())
    if data.get("version") != 1 or not is_hex(data.get("revision"), 40) or not isinstance(data.get("checkpoints"), dict) or not data["checkpoints"]:
        raise ValueError("Invalid checkpoint manifest.")
    for name, checkpoint in data["checkpoints"].items():
        if name not in CHECKPOINTS or not isinstance(checkpoint, dict):
            raise ValueError("Invalid checkpoint entry.")
        directory = (path.parent / checkpoint["path"]).resolve()
        if not directory.is_relative_to(path.parent):
            raise ValueError("Checkpoint path must remain inside the manifest directory.")
        if set(checkpoint["sha256"]) != set(MODEL_FILES):
            raise ValueError("Incomplete checkpoint manifest.")
        for filename, expected in checkpoint["sha256"].items():
            target = (directory / filename).resolve()
            if not target.is_relative_to(path.parent) or not target.is_file() or not is_hex(expected, 64) or file_hash(target) != expected:
                raise ValueError("Checkpoint file missing or hash mismatch.")
        checkpoint["resolved_path"] = str(directory)
    return data


class Runtime:
    def __init__(self, manifest, calibration=None, device="cpu", checkpoint="auto", threads=4):
        # Set before importing ML libraries. All required assets were validated locally.
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", USE_TF="0", TOKENIZERS_PARALLELISM="false")
        import laya
        import torch
        if laya.__version__ != SDK_VERSION:
            raise ValueError("Unsupported Laya SDK version; install the pinned requirements.")
        data = load_manifest(manifest)
        self.model = "laya@" + data["revision"]
        self.revision = data["revision"]
        self.calibration = Calibration(calibration, self.model)
        self.checkpoint = checkpoint
        if checkpoint != "auto" and checkpoint not in data["checkpoints"]:
            raise ValueError("Requested checkpoint is not installed.")
        torch.set_num_threads(threads)
        names = data["checkpoints"] if checkpoint == "auto" else [checkpoint]
        self.agents = {}
        warmup = {"ready": {"type": "choice", "instructions": "What is the message?",
                             "criteria": {"greeting": "a greeting", "other": "anything else"}}}
        for name in names:
            agent = laya.load(data["checkpoints"][name]["resolved_path"], device=device)
            ensure_complete(agent, "Hello", warmup)
            agent.predict("Hello", warmup)
            self.agents[name] = agent

    def health(self):
        return {"ready": True, "model": self.model, "calibration_id": self.calibration.identifier,
                "sdk_version": SDK_VERSION, "checkpoints": {name: str(agent.device) for name, agent in self.agents.items()}}

    def predict(self, request):
        questions = validate_request(request, self.model)
        selected = select_checkpoint(request["state"], request.get("language"))
        if self.checkpoint != "auto":
            if self.checkpoint in ("english", "typed-decisions") and selected != "english":
                raise Rejected("language_checkpoint_conflict")
            selected = self.checkpoint
        if selected not in self.agents:
            raise Rejected("checkpoint_not_installed")
        agent = self.agents[selected]
        schema = schema_hash(questions)
        self.calibration.check(schema, selected, questions)
        ensure_complete(agent, request["state"], questions)
        result = agent.predict(request["state"], questions)
        raw = result["answers"]
        return {"model": self.model, "answers": self.calibration.apply(selected, raw), "raw_answers": raw,
                "usage": result["usage"],
                "metadata": {"checkpoint": selected, "revision": self.revision,
                             "calibration_id": self.calibration.identifier, "schema_hash": schema,
                             "rubric_version": request["rubric_version"], "device": agent.device.type,
                             "sdk_version": SDK_VERSION, "truncated": False}}

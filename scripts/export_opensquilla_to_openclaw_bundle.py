#!/usr/bin/env python3
"""
Export OpenSquilla router assets into an OpenClaw-compatible bundle layout.

Input (default):
  E:/GitHub/opensquilla/src/opensquilla/squilla_router/models/v4.2_phase3_inference

Output:
  <out_dir>/
    manifest.json
    classifier.onnx
    embeddings.onnx
    tokenizer.json
    runtime-config.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

try:
    import onnxruntime as ort
except Exception:
    ort = None


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def load_json(path: Path) -> Dict[str, Any]:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def save_json(path: Path, obj: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
        f.write("\n")


def pick_existing(*candidates: Path) -> Optional[Path]:
    for p in candidates:
        if p.exists():
            return p
    return None


def inspect_onnx(path: Path) -> Dict[str, Any]:
    info: Dict[str, Any] = {"path": str(path), "ok": False}
    if ort is None:
        info["error"] = "onnxruntime not installed"
        return info
    try:
        sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        inputs = []
        for i in sess.get_inputs():
            inputs.append(
                {
                    "name": i.name,
                    "shape": list(i.shape) if i.shape is not None else None,
                    "type": i.type,
                }
            )
        outputs = []
        for o in sess.get_outputs():
            outputs.append(
                {
                    "name": o.name,
                    "shape": list(o.shape) if o.shape is not None else None,
                    "type": o.type,
                }
            )
        info["ok"] = True
        info["inputs"] = inputs
        info["outputs"] = outputs
        return info
    except Exception as e:
        info["error"] = str(e)
        return info


def get_embedding_dim_from_config(config_json: Path) -> Optional[int]:
    if not config_json.exists():
        return None
    data = load_json(config_json)
    for k in ("hidden_size", "embedding_size", "dim"):
        v = data.get(k)
        if isinstance(v, int) and v > 0:
            return v
    return None


def get_tokenizer_family(tokenizer_json: Path) -> Optional[str]:
    if not tokenizer_json.exists():
        return None
    data = load_json(tokenizer_json)
    model = data.get("model")
    if isinstance(model, dict):
        t = model.get("type")
        if isinstance(t, str):
            return t
    return None


def normalize_shape_last_dim(shape: Optional[List[Any]]) -> Optional[int]:
    if not shape:
        return None
    last = shape[-1]
    if isinstance(last, int):
        return last
    return None


def infer_num_classes(classifier_info: Dict[str, Any]) -> Optional[int]:
    outputs = classifier_info.get("outputs") or []
    for out in outputs:
        shape = out.get("shape")
        last = normalize_shape_last_dim(shape)
        if isinstance(last, int) and last > 1:
            return last
    return None


def export_bundle(
    source_dir: Path,
    out_dir: Path,
    classifier_src: Optional[Path],
    embedding_src: Optional[Path],
    tokenizer_src: Optional[Path],
    runtime_src: Optional[Path],
    force: bool,
    allow_wordpiece: bool,
    expected_classes: int,
    require_onnxruntime: bool,
) -> int:
    if not source_dir.exists():
        print(f"[ERROR] source_dir not found: {source_dir}", file=sys.stderr)
        return 2

    classifier = classifier_src or pick_existing(
        source_dir / "mlp" / "model.onnx",
        source_dir / "classifier.onnx",
    )
    embedding = embedding_src or pick_existing(
        source_dir / "bge_onnx" / "model.onnx",
        source_dir / "embeddings.onnx",
    )
    tokenizer = tokenizer_src or pick_existing(
        source_dir / "bge_onnx" / "tokenizer.json",
        source_dir / "tokenizer.json",
    )
    runtime_config_in = runtime_src or pick_existing(
        source_dir / "inference_manifest.json",
        source_dir / "runtime-config.json",
    )

    missing = []
    if classifier is None:
        missing.append("classifier onnx")
    if embedding is None:
        missing.append("embedding onnx")
    if tokenizer is None:
        missing.append("tokenizer.json")
    if runtime_config_in is None:
        missing.append("runtime config source")
    if missing:
        print("[ERROR] missing required source assets: " + ", ".join(missing), file=sys.stderr)
        return 2

    tokenizer_family = get_tokenizer_family(tokenizer)
    if tokenizer_family and tokenizer_family.lower() == "wordpiece" and not allow_wordpiece:
        print(
            "[ERROR] tokenizer family is WordPiece but allow_wordpiece is false. "
            "OpenClaw current loader is BPE-oriented.",
            file=sys.stderr,
        )
        return 3

    if out_dir.exists():
        if not force:
            print(f"[ERROR] out_dir already exists, use --force to overwrite: {out_dir}", file=sys.stderr)
            return 2
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    dst_classifier = out_dir / "classifier.onnx"
    dst_embedding = out_dir / "embeddings.onnx"
    dst_tokenizer = out_dir / "tokenizer.json"
    dst_runtime = out_dir / "runtime-config.json"
    dst_manifest = out_dir / "manifest.json"

    shutil.copy2(classifier, dst_classifier)
    shutil.copy2(embedding, dst_embedding)
    shutil.copy2(tokenizer, dst_tokenizer)

    emb_dim = get_embedding_dim_from_config(source_dir / "bge_onnx" / "config.json")
    if emb_dim is None:
        emb_dim = 384

    classifier_info = inspect_onnx(dst_classifier)
    embedding_info = inspect_onnx(dst_embedding)

    if require_onnxruntime and ort is None:
        print("[ERROR] --require-onnxruntime set but onnxruntime is unavailable", file=sys.stderr)
        return 4

    classes = infer_num_classes(classifier_info)
    if classes is not None and classes != expected_classes:
        print(
            f"[ERROR] classifier classes mismatch: got {classes}, expected {expected_classes}",
            file=sys.stderr,
        )
        return 5

    runtime_out = {
        "schemaVersion": 1,
        "embeddingDimensions": emb_dim,
        "classifier": {
            "numClasses": expected_classes,
            "classLabels": ["T0", "T1", "T2", "T3"],
        },
        "notes": {
            "tokenizerFamily": tokenizer_family or "unknown",
            "sourceModelDir": str(source_dir),
            "sourceRuntimeConfig": str(runtime_config_in),
            "warning": (
                "WordPiece tokenizer may require OpenClaw tokenizer loader extension"
                if (tokenizer_family or "").lower() == "wordpiece"
                else ""
            ),
        },
        "inspect": {
            "classifier": classifier_info,
            "embedding": embedding_info,
        },
    }
    save_json(dst_runtime, runtime_out)

    manifest_out = {
        "schemaVersion": 1,
        "bundleName": "opensquilla-v4-compat",
        "classifierModelPath": "classifier.onnx",
        "embeddingModelPath": "embeddings.onnx",
        "tokenizerPath": "tokenizer.json",
        "runtimeConfigPath": "runtime-config.json",
        "tierMap": {
            "c0": "T0",
            "c1": "T1",
            "c2": "T2",
            "c3": "T3",
        },
        "checksums": {
            "classifier.onnx": sha256_file(dst_classifier),
            "embeddings.onnx": sha256_file(dst_embedding),
            "tokenizer.json": sha256_file(dst_tokenizer),
            "runtime-config.json": sha256_file(dst_runtime),
        },
    }
    save_json(dst_manifest, manifest_out)

    print("[OK] exported OpenClaw-compatible bundle:")
    print(f"  out_dir: {out_dir}")
    print(f"  classifier: {dst_classifier.name}")
    print(f"  embedding:  {dst_embedding.name}")
    print(f"  tokenizer:  {dst_tokenizer.name} (family={tokenizer_family or 'unknown'})")
    print(f"  runtime:    {dst_runtime.name}")
    print(f"  manifest:   {dst_manifest.name}")

    if (tokenizer_family or "").lower() == "wordpiece":
        print(
            "[WARN] tokenizer is WordPiece. OpenClaw current tokenizer loader may reject it. "
            "Either export BPE tokenizer or extend OpenClaw tokenizer loader."
        )

    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Export OpenSquilla router assets to OpenClaw bundle format")
    parser.add_argument(
        "--source-dir",
        type=Path,
        default=Path("E:/GitHub/opensquilla/src/opensquilla/squilla_router/models/v4.2_phase3_inference"),
        help="OpenSquilla source model directory",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        required=True,
        help="Output directory for OpenClaw-compatible bundle",
    )
    parser.add_argument("--classifier-src", type=Path, default=None, help="Override classifier ONNX source path")
    parser.add_argument("--embedding-src", type=Path, default=None, help="Override embedding ONNX source path")
    parser.add_argument("--tokenizer-src", type=Path, default=None, help="Override tokenizer.json source path")
    parser.add_argument("--runtime-src", type=Path, default=None, help="Override runtime config source path")
    parser.add_argument("--expected-classes", type=int, default=4, help="Expected classifier class count")
    parser.add_argument("--allow-wordpiece", action="store_true", help="Allow WordPiece tokenizer export")
    parser.add_argument("--force", action="store_true", help="Overwrite output directory if exists")
    parser.add_argument(
        "--require-onnxruntime",
        action="store_true",
        help="Fail if onnxruntime is unavailable (enables strict shape/class checks)",
    )

    args = parser.parse_args()

    return export_bundle(
        source_dir=args.source_dir,
        out_dir=args.out_dir,
        classifier_src=args.classifier_src,
        embedding_src=args.embedding_src,
        tokenizer_src=args.tokenizer_src,
        runtime_src=args.runtime_src,
        force=args.force,
        allow_wordpiece=args.allow_wordpiece,
        expected_classes=args.expected_classes,
        require_onnxruntime=args.require_onnxruntime,
    )


if __name__ == "__main__":
    raise SystemExit(main())

"""Download only selected, pinned Laya checkpoints and create an integrity manifest."""
import argparse
import json
from pathlib import Path
from .protocol import CHECKPOINTS, DEFAULT_REVISION, MODEL_FILES, file_hash, is_hex


def prepare(destination, revision, checkpoints):
    from huggingface_hub import snapshot_download
    if not is_hex(revision, 40):
        raise ValueError("Use an immutable 40-character Hugging Face commit revision.")
    destination = Path(destination).resolve()
    manifest_path = destination / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {
        "version": 1, "revision": revision, "checkpoints": {}}
    if manifest["revision"] != revision:
        raise ValueError("Use a separate destination for a different model revision.")
    destination.mkdir(parents=True, exist_ok=True)
    for checkpoint in checkpoints:
        if checkpoint not in CHECKPOINTS:
            raise ValueError("Unknown checkpoint.")
        prefix = "" if checkpoint == "english" else checkpoint + "/"
        snapshot_download("convaiinnovations/laya", revision=revision,
                          allow_patterns=[prefix + name for name in MODEL_FILES],
                          local_dir=destination / "hub")
        directory = destination / "hub" / prefix
        # Perform the SDK's tokenizer compatibility normalization during setup,
        # before hashing. The SDK must not rewrite an integrity-checked file at startup.
        tokenizer = directory / "tokenizer/tokenizer_config.json"
        config = json.loads(tokenizer.read_text())
        if config.get("tokenizer_class") in (None, "TokenizersBackend"):
            config["tokenizer_class"] = "PreTrainedTokenizerFast"
            config.pop("backend", None)
            config.pop("is_local", None)
        if isinstance(config.get("extra_special_tokens"), list):
            config["extra_special_tokens"] = {f"extra_{i}": value for i, value in enumerate(config["extra_special_tokens"])}
        tokenizer.write_text(json.dumps(config, indent=2) + "\n")
        manifest["checkpoints"][checkpoint] = {
            "path": str(directory.relative_to(destination)),
            "sha256": {name: file_hash(directory / name) for name in MODEL_FILES}}
    temporary = manifest_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n")
    temporary.replace(manifest_path)
    # Keep the model's provenance and upstream license beside downloaded assets.
    source = Path(__file__).parent
    (destination / "LAYA-NOTICE.md").write_text((source / "THIRD_PARTY_NOTICES.md").read_text())
    (destination / "LAYA-LICENSE.txt").write_text((source / "licenses/laya-APACHE-2.0.txt").read_text())
    return manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", required=True)
    parser.add_argument("--revision", default=DEFAULT_REVISION)
    parser.add_argument("--checkpoint", choices=CHECKPOINTS, action="append", required=True)
    args = parser.parse_args()
    try:
        print(prepare(args.destination, args.revision, args.checkpoint))
    except (ValueError, OSError) as exc:
        parser.exit(2, str(exc) + "\n")


if __name__ == "__main__":
    main()

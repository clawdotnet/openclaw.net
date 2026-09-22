"""Download only selected, pinned Laya checkpoints and create an integrity manifest."""
import argparse
import json
from pathlib import Path
from .protocol import CHECKPOINTS, DEFAULT_REVISION, MODEL_FILES, file_hash, is_hex


DEFAULT_FILE_HASHES = {
    "english": {
        "model.safetensors": "891102d372688fc2a094dac56a384bc537b87c63f21f9f3dac0be2b7cbc8d86c",
        "rl_agent_config.json": "ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd",
        "encoder/config.json": "bf3ab80598fdccf414855a2ce80f22859e4492d06ca8a62ddd1cfb63972f8979",
        "tokenizer/tokenizer.json": "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30",
        "tokenizer/tokenizer_config.json": "50044de60daaa73df97d262e15a40d4faf0160e7d742df64b377877a1320dd12",
    },
    "multilingual": {
        "model.safetensors": "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204",
        "rl_agent_config.json": "25061739243b617ad88d1219ba6f8a9c86c5881ca28df024fa2d9b3b2fcc30c6",
        "encoder/config.json": "83f6916d13ef0f556ac461f28308dc2bffa7ebeadee8ec9e2db5812020ea5bb4",
        "tokenizer/tokenizer.json": "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f",
        "tokenizer/tokenizer_config.json": "6c6b2d8e3c84ce0e671c129cd6b374b235d6f9863042a5836358d00a89bbb5a1",
    },
    "typed-decisions": {
        "model.safetensors": "4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e",
        "rl_agent_config.json": "ebf0cd524d92342a6be5e48e9fca3d7c2babfb5a56ccd79d2171ef5d8c7f7be8",
        "encoder/config.json": "5268d24ad3b77c8151de5dcb0762ba4391619aad9ab0bda33e36fb083cfeae6d",
        "tokenizer/tokenizer.json": "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30",
        "tokenizer/tokenizer_config.json": "08d4cf3ac4dca381759441b85b91a6d40e688471dcd33d15d6649eb0a9a854d1",
    },
}


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
        if revision == DEFAULT_REVISION:
            for name, expected in DEFAULT_FILE_HASHES[checkpoint].items():
                if file_hash(directory / name) != expected:
                    raise ValueError("Downloaded checkpoint does not match the pinned upstream digest.")
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

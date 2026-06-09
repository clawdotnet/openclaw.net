# Routing CLI

Routing CLI commands manage the dynamic turn-routing configuration surface in a running OpenClaw operator environment.

OpenClaw remains config-first for routing (`OpenClaw:DynamicTurnRouting`). The commands below are management helpers over that same config-driven surface.

Current status:

- Runtime wiring is implemented in both native and MAF orchestrator paths.
- The ONNX router is optional and remains experimental for classifier quality.
- `BundlePath` + `Policy.Tiers` is the preferred modern routing shape.

```bash
openclaw routing onboard [--router recommended|openrouter-mix|disabled] [--config <path>]
openclaw routing configure router [--router recommended|openrouter-mix|disabled] [--margin-upgrade-threshold <n>] [--r1-rescue-threshold <n>] [--under-routing-safety-threshold <n>] [--deep-turn-threshold <n>] [--config <path>]
openclaw routing configure providers --tier <T0|T1|T2|T3> [--model-profile <id>] [--fallback-profile <id>] [--reasoning-level <level>] [--response-policy <policy>] [--image-model-profile <id>] [--allowed-tools <csv>] [--preferred-tags <csv>] [--prompt-mode <full|minimal|compact>] [--disable-tools <true|false>] [--config <path>]
openclaw routing providers [--config <path>]
openclaw routing status [--config <path>]
openclaw routing diagnostics <on|off> [--config <path>]
```

Router mode behavior:

- `recommended`: enables dynamic turn routing and keeps existing tier mappings.
- `openrouter-mix`: enables routing and appends OpenRouter-oriented preferred tags to tiers (`cost`, `fast`, `tools`, `reasoning`).
- `disabled`: turns dynamic turn routing off.

Examples:

```bash
openclaw routing onboard --router recommended
openclaw routing configure router --router openrouter-mix
openclaw routing configure router --router disabled
openclaw routing configure providers --tier T2 --model-profile frontier-tools --allowed-tools read_file,run_in_terminal
openclaw routing diagnostics on
```

Notes:

- These commands do not create a second routing engine.
- If ONNX assets are missing or runtime inference fails, the runtime falls back to `T2` with a machine-readable reason.

Operator warning:

- Point `BundlePath` at an explicit OpenClaw compat export such as `models/routing/opensquilla-v4-compat`, not directly at the raw OpenSquilla `v4.2_phase3_inference` directory.
- OpenClaw's loader is compat-first but not rigid: it can resolve nested manifest asset paths and embedding dimensions when bundle metadata provides them.
- Tokenizer support now includes Hugging Face-style BPE and WordPiece `tokenizer.json` files, but compatibility is still asset-specific and depends on supported pre-tokenizer shapes.
- The main remaining interoperability gap is the native inference pipeline: OpenClaw currently expects one embedding model plus one 4-class ONNX classifier, while OpenSquilla v4.2 uses a multi-stage fused router.

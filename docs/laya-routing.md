# Local Laya decision routing

OpenClaw can use Laya as an optional, locally hosted decision provider. Both native and Microsoft Agent Framework runtimes use the shared decision-routing policy. The default is disabled. Laya produces typed decisions; it is not a chat model and should not be added to `Models.Profiles` as a generator.

**Laya is developed by Nandakishor Mukkunnoth (Nandakishor M), ConvAI Innovations, and upstream contributors.** Its model, SDK, and research are their work. This integration's local service, compatibility checks, and evaluation tooling are maintained separately. See the [author's article](https://laya.convaiinnovations.com/), [source repository](https://github.com/NandhaKishorM/laya), [model card](https://huggingface.co/convaiinnovations/laya), and retained [attribution and license](../tools/laya_service/THIRD_PARTY_NOTICES.md). The earlier [confidence-aware routing paper](https://arxiv.org/abs/2510.01237) motivates escalation pathways; this implementation changes model selection only.

## Prepare the optional service

Run these commands from the repository root with Python 3.12. Python/PyTorch remain outside the .NET gateway and its NativeAOT binary. Keep model assets and calibration datasets outside the repository.

```bash
python3.12 -m venv /path/to/laya-venv
/path/to/laya-venv/bin/python -m pip install -r tools/laya_service/requirements.txt
/path/to/laya-venv/bin/python -m tools.laya_service.download \
  --destination /path/to/laya-models \
  --checkpoint english --checkpoint multilingual
```

The downloader pins Hugging Face revision `1c5edc17a7acd8701df6fc341c0d179f1c62c982`, selects only the named checkpoint files, prepares tokenizer compatibility, and creates a manifest of SHA-256 hashes. It copies attribution and the upstream Apache-2.0 license beside the assets. Use a new destination when changing revision. The SDK is pinned to `laya==0.3.4`; dependency versions are the tested Python 3.12 combination. On Linux CUDA hosts, install the appropriate PyTorch wheel for the GPU/driver. CUDA was not verified by the macOS smoke test.

For English-only use, download only `english`. Non-English requests then retain the gateway's baseline when the required checkpoint is unavailable. Do not assume unknown or mixed scripts are English. The compatibility layer handles Armenian, omitted by the released SDK, and other non-Latin minority scripts before inference. An explicit language hint helps short Latin-script inputs; an English hint conflicting with non-Latin text is rejected.

Start a persistent service:

```bash
/path/to/laya-venv/bin/python -m tools.laya_service \
  --manifest /path/to/laya-models/manifest.json --device cpu --port 8099
```

Use `--device mps` on supported Apple Silicon or `--device cuda` on a configured NVIDIA host. `--threads 4` is the default CPU thread limit. `--checkpoint multilingual` can force all requests to the multilingual checkpoint. The workflow-specific `typed-decisions` checkpoint must be downloaded and explicitly selected; automatic routing does not select it merely because question names resemble its training tasks.

All installed/selected checkpoints load and warm up before the socket opens. Startup verifies every asset hash. Runtime sets Hugging Face/Transformers offline mode and loads only local paths; it never downloads weights. `GET http://127.0.0.1:8099/health` reports readiness, model identity, calibration ID, checkpoints, and actual devices, including SDK CPU fallback. Restart the service after changing its manifest or calibration.

The service binds only to `127.0.0.1`, rejects browser Origin and unexpected Host headers, caps request size and connections, and admits one inference at a time without a waiting queue. Protect the host as you would any local process. The gateway bypasses proxies and redirects for Laya, accepts only literal loopback endpoints, and never falls back from Laya to hosted Jev. Selected conversation text still crosses a local HTTP process boundary.

## Start in shadow mode

Retain the existing `Policy.Tiers` model-profile mappings. Enable one decision provider at a time:

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Jev": { "Mode": "disabled" },
      "Laya": {
        "Mode": "shadow",
        "Endpoint": "http://127.0.0.1:8099/v1/decisions",
        "Model": "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982",
        "CalibrationId": "",
        "Language": "",
        "TimeoutMs": 1500,
        "MaxConcurrentRequests": 1,
        "DiagnosticsPath": "routing/laya-decisions.jsonl"
      }
    }
  }
}
```

Restart the gateway. `openclaw routing status --config /path/to/appsettings.json` shows configured provider modes and identities. `DynamicTurnRouting.Enabled` still controls the independent ONNX baseline. Shadow mode returns that baseline unchanged but adds bounded evaluation latency.

The shorter `openclaw-laya-tiers-v1` rubric is [shared by the gateway and evaluation tool](../tools/laya_service/rubrics/openclaw-laya-tiers-v1.json). It asks for T0–T3/abstain, risk, and tool need. Before prediction the service checks the exact tokenizer budget for state, question instructions, and every option. It rejects any input the SDK would truncate or rewrite, rather than deciding from silently incomplete text. The English checkpoint has a 512-token total budget per question, multilingual 1,024 by default, including questions and options. Up to 20 choice options and 16 questions are accepted, subject to those stricter token limits.

The adapter checks model revision, SDK version, calibration identity, rubric, device, checkpoint, and explicit absence of truncation. Errors, overload, deadline expiry, and uncertainty retain the baseline. The shared policy preserves redaction, explicit model selections, deterministic floors, sticky tiers, and tool permissions. It does not authorize actions. Disabling a router does not weaken existing permissions.

Metadata-only journals include provider, checkpoint/revision, schema hash, calibration ID, actual device, rubric, probabilities, latency, and applied/proposed routes. They omit state, prompts, and credentials. Laya's metered API cost is zero; this does not represent the cost of local compute. Rotate/protect these files using normal log retention controls.

## Evaluate and calibrate

The base weights run without training, but this is not evidence of production routing quality. In a local smoke test the new rubric still underpredicted tool need for repository work. The [author's benchmarks](https://github.com/NandhaKishorM/laya/blob/42626c348753fbb17572a813127df2278a1ec527/BENCHMARKS.md) also distinguish the fine-tuned checkpoint from weak base-model performance on typed decisions. Do not transfer Jev confidence thresholds or advertise the local timing as a quality benchmark.

Prepare representative, human-labeled calibration and held-out validation JSONL files. Each line has a unique case ID, state, and labels for every question. The tool supplies the shared routing rubric and pinned model unless an explicit schema/model is provided:

```json
{"case_id":"cal-001","state":{"current_request":"Rewrite hello in uppercase.","recent_conversation":[]},"labels":{"tier":"T0","high_risk":false,"requires_tools":false}}
```

Include languages, multi-turn references, ambiguity, consequential tasks, and tool-dependent requests. Keep validation cases and their IDs separate from fitting data. Twenty observations per checkpoint/question-type/option-count bucket is the tooling minimum, not a sufficient production sample size. Include multiple labels in each calibration bucket. Do not derive tool/risk labels mechanically from model outputs.

```bash
/path/to/laya-venv/bin/python -m tools.laya_service.evaluate /path/to/calibration-cases.jsonl --output /path/to/calibration-raw.jsonl
/path/to/laya-venv/bin/python -m tools.laya_service.evaluate /path/to/validation-cases.jsonl --output /path/to/validation-raw.jsonl
/path/to/laya-venv/bin/python -m tools.laya_service.calibration \
  --fit /path/to/calibration-raw.jsonl --validate /path/to/validation-raw.jsonl \
  --output /path/to/calibration.json
```

Observations retain raw pre-adapter probabilities, labels, and request hashes for detecting duplicate cases, without state. Protect these local evaluation files as potentially sensitive metadata. Calibration fits an additional scalar temperature to those probabilities, separately by checkpoint, primitive, and option count. It validates disjoint IDs, exact-request fingerprints, and provenance, reports held-out NLL, Brier score, expected calibration error, reliability bins, and risk-versus-coverage curves, and prints the artifact's SHA-256 ID. It does not retrain weights or automatically promote a provider. Inspect held-out results, especially under-routing and important language cohorts; reject a calibration that worsens acceptable risk or coverage. Temperature scaling cannot fix bad rankings or missing knowledge, and may require subsequent task-specific fine-tuning.

Restart the service with `--calibration /path/to/calibration.json`, put its printed SHA-256 in `Laya.CalibrationId`, and continue shadow evaluation. The service rejects a different question schema, candidate order, or a checkpoint/bucket absent from the calibration. After acceptable held-out task results, set `Laya.Mode=active` and configure confidence/margin thresholds based on that evaluation. Active mode requires a calibration ID; the response must match it. No production calibration artifact is shipped.

The existing journal report now supports both providers:

```bash
python3 scripts/evaluate-decision-routing.py /path/to/laya-decisions.snapshot.jsonl \
  --labels /path/to/tier-labels.jsonl --output /path/to/report.json
```

Tier-label rows use `decision_id`, `expected_tier`, and optional `high_risk`, as described in [Jev routing](jev-routing.md). Calibration metrics are separated by provider/model/rubric/checkpoint/revision/calibration/schema cohort. They assess raw tier distributions separately from policy safeguards and fallback. Add `--plot /path/to/reliability.png` when `matplotlib` is installed to render reliability and risk/coverage plots. Unlabeled journals make no accuracy claim. The legacy `evaluate-jev-routing.py` command remains available.

## Rollback and verification

Set `Laya.Mode=disabled` and restart the gateway, or use `openclaw routing configure router --router disabled` to disable ONNX and both decision providers. Remove environment overrides that would re-enable them. Stop the independently managed Python service when it is no longer needed.

```bash
python3 -B -m unittest discover -s tests/laya-service -v
python3 -B -m unittest discover -s tests/routing-eval -p test_jev_report.py
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter 'FullyQualifiedName~LayaRoutingTests|FullyQualifiedName~JevRoutingTests'
```

The Python unit tests require only the standard library; they do not download weights or make hosted inference calls. Real local inference is a separate operator smoke check. This implementation covers turn routing, local serving, compatibility improvements, and calibration tooling. Skill selection, memory reranking, and workflow escalation remain later integrations requiring their own quality evaluations.

Development verification on September 21, 2026 included real English inference on Apple MPS, multilingual inference on CPU (including Armenian checkpoint selection), a compiled macOS arm64 NativeAOT client calling the local service, and a synthetic calibration/evaluation round trip. These verify the integration, not production quality or a general latency guarantee. No production routing mode or model weights were changed by that verification.

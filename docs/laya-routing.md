# Local Laya Decision Routing

OpenClaw can use Laya as an optional, locally hosted decision provider. Native and Microsoft Agent Framework runtimes share the decision-routing policy. The default is disabled. Laya produces typed decisions; it is not a chat model and must not be added to `Models.Profiles` as a generator.

**Laya is developed by Nandakishor Mukkunnoth (Nandakishor M), ConvAI Innovations, and upstream contributors.** Its model, SDK, and research are their work. OpenClaw separately maintains the local service, adapter, and evaluation tools. See the [author's article](https://laya.convaiinnovations.com/), [source repository](https://github.com/NandhaKishorM/laya), [model card](https://huggingface.co/convaiinnovations/laya), and retained [attribution and license](../tools/laya_service/THIRD_PARTY_NOTICES.md). The earlier [confidence-aware routing paper](https://arxiv.org/abs/2510.01237) motivates escalation pathways; this integration changes model selection only.

## Runtime Boundary

The standalone service targets .NET 10 and uses `NLaya`/`NLaya.TorchSharp` 1.0.0 with `TorchSharp-cpu` 0.107.0. The Gateway communicates with it over the loopback HTTP contract and does not reference NLaya or TorchSharp. The service is a JIT deployment; NativeAOT support is not claimed. CPU is the verified runtime with the checked-in dependency set. `cuda` and `mps` are accepted only when their matching TorchSharp backend is installed; this project does not bundle those backends.

Keep model assets, calibration observations, and reports outside the repository. From the repository root, download the pinned revision and selected checkpoint files:

```bash
dotnet run --project tools/laya_service -c Release -- download \
  --destination /path/to/laya-models \
  --revision 1c5edc17a7acd8701df6fc341c0d179f1c62c982 \
  --checkpoint english --checkpoint multilingual
```

The downloader retrieves only the allowlisted model/config/tokenizer assets for the requested checkpoints, writes their SHA-256 values to `manifest.json`, and copies upstream attribution and license notices. The default revision is `1c5edc17a7acd8701df6fc341c0d179f1c62c982`. Use a fresh destination when changing revisions. Startup verifies manifest paths and every asset hash before loading local files; serving never downloads weights. The [retained upstream license](../tools/laya_service/licenses/laya-APACHE-2.0.txt) and [third-party notices](../tools/laya_service/THIRD_PARTY_NOTICES.md) travel with the service.

Start the local service:

```bash
dotnet run --project tools/laya_service -c Release -- serve \
  --manifest /path/to/laya-models/manifest.json --device cpu --port 8099 --threads 4
```

`--checkpoint auto` is the default. `english` and `multilingual` are routed by language; `typed-decisions` must be downloaded and explicitly selected. `--device` accepts `cpu`, `cuda`, or `mps`; an unavailable backend fails startup rather than silently falling back. `GET http://127.0.0.1:8099/health` reports readiness and model/runtime identity. Restart after changing the manifest or calibration.

The service binds only to `127.0.0.1`, rejects browser Origin and unexpected Host headers, caps request size and connections, and admits one inference at a time without a waiting queue. The Gateway bypasses proxies and redirects, accepts only literal loopback endpoints, and never falls back from Laya to hosted Jev. Selected conversation text still crosses a local HTTP process boundary.

## Gateway Configuration

Retain existing `Policy.Tiers` model-profile mappings and enable one decision provider at a time:

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

Restart the Gateway. `openclaw routing status --config /path/to/appsettings.json` shows configured provider modes and identities. `DynamicTurnRouting.Enabled` still controls the independent ONNX baseline. Shadow mode returns that baseline unchanged but adds bounded evaluation latency.

The `openclaw-laya-tiers-v1` rubric is [shared by the Gateway and evaluator](../tools/laya_service/rubrics/openclaw-laya-tiers-v1.json). The adapter reports `sdk_version: "1.0.0"` and `runtime: "NLaya"`; Gateway metadata checks both before accepting a decision. The service checks tokenizer budgets for state, question instructions, and options, and rejects inputs that would be truncated. The English checkpoint budget is 512 tokens per question and multilingual is 1,024 by default; protocol limits are up to 20 choice options and 16 questions, subject to those budgets.

Errors, overload, deadline expiry, and uncertainty retain the baseline. Shared policy preserves redaction, explicit model selections, deterministic floors, sticky tiers, and tool permissions. It does not authorize actions. Metadata-only journals omit state, prompts, and credentials; protect and rotate them using normal log retention controls.

## Evaluate and Calibrate

Model execution and passing unit tests do not establish production routing quality. Do not transfer Jev confidence thresholds or interpret local runtime as a quality benchmark. Prepare representative human-labeled calibration and held-out validation JSONL files. Each case has a unique ID, state, and labels for every question; when questions/model are omitted, the evaluator supplies the shared rubric and pinned model:

```json
{"case_id":"cal-001","state":{"current_request":"Rewrite hello in uppercase.","recent_conversation":[]},"labels":{"tier":"T0","high_risk":false,"requires_tools":false}}
```

Include languages, multi-turn references, ambiguity, consequential tasks, and tool-dependent requests. Keep validation cases separate from fitting cases. Twenty observations per checkpoint/question-type/option-count bucket is only the tooling minimum. Include multiple labels in every training bucket and do not derive risk/tool labels mechanically from model outputs.

```bash
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/calibration-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/calibration-raw.jsonl
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/validation-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/validation-raw.jsonl
dotnet run --project tools/laya_service -c Release -- calibrate \
  --fit /path/to/calibration-raw.jsonl --validate /path/to/validation-raw.jsonl \
  --output /path/to/calibration.json
```

The evaluator sends requests only to a literal `http://127.0.0.1:PORT/v1/decisions` endpoint, disables proxies and redirects, and writes state-free observations with raw answers, labels, provenance, and canonical request fingerprints. Treat these files as potentially sensitive metadata. Calibration fits scalar temperatures by checkpoint and `type:option-count`, validates disjoint IDs/fingerprints and homogeneous model/schema/runtime identity, and reports held-out NLL, Brier, ECE, reliability bins, and risk/coverage. The output is UTF-8 JSON with a final newline and `version: 2`, bound to model revision, schema hash, `sdk_version: "1.0.0"`, and `runtime: "NLaya"`. The CLI prints the exact artifact SHA-256.

Restart with `--calibration /path/to/calibration.json` and set the printed SHA-256 in `Laya.CalibrationId`. The service rejects v1 artifacts, a different question schema, or missing checkpoint/buckets. Continue shadow evaluation and inspect held-out results before considering active mode. Temperature scaling cannot repair ranking errors or missing knowledge. No production calibration artifact ships with this repository.

## Routing Journal Report

The .NET report command handles both Jev and Laya journals. Labels use `decision_id`, `expected_tier`, and optional boolean `high_risk`. Calibration is reported separately by provider/model/rubric/checkpoint/revision/calibration/schema cohort. Unlabeled journals make no accuracy claim; reliability plots require labeled probability data.

```bash
dotnet run --project tools/laya_service -c Release -- report \
  /path/to/jev-decisions.snapshot.jsonl \
  --labels /path/to/tier-labels.jsonl --output /path/to/report.json \
  --plot /path/to/reliability.png
```

## Rollback and Verification

Set `Laya.Mode=disabled` and restart the Gateway, or run `openclaw routing configure router --router disabled` to disable ONNX and both decision providers. Remove environment overrides that would re-enable them, and stop the independently managed .NET service when it is no longer needed.

```bash
dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release \
  -p:OpenClawSkipDashboardBuild=true --filter FullyQualifiedName~LayaRoutingTests
```

These tests use fakes and fixtures; they do not download weights or exercise real model quality. Real inference and checkpoint parity are opt-in operator checks in an environment with model assets and the required device backend. Skill selection, memory reranking, and workflow escalation remain separate integrations requiring their own evaluations.

For an opt-in real-model check, download all three checkpoints, start the service once per checkpoint by setting `--checkpoint english`, `--checkpoint multilingual`, or `--checkpoint typed-decisions`, then evaluate the same labeled parity dataset against each service:

```bash
dotnet run --project tools/laya_service -c Release -- serve \
  --manifest /path/to/laya-models/manifest.json --checkpoint english --device cpu
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/parity-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/english-observations.jsonl
```

Repeat with `multilingual` and `typed-decisions` (and distinct output paths). Compare each observation to an approved reference for exact typed-answer/category matches and probability deltas within a tolerance chosen before the run for the same device/runtime. Record checkpoint, revision, runtime, device, and tolerance with the results. The repository does not supply real-model golden outputs or a production acceptance threshold; this check is not part of ordinary CI and is not evidence of production routing quality.

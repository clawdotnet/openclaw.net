# Jev decision routing

OpenClaw.NET can evaluate TypeSafe Jev decisions alongside its existing router. This is an opt-in pilot: the shipped mode is `disabled`, no key is embedded, and no request is sent until an operator enables it. Native and Microsoft Agent Framework runtimes use the same policy. Skill selection, memory reranking, and heartbeat triage are future consumers of the decision client; this change implements turn routing only.

## Start with shadow mode

Set `TYPESAFE_API_KEY` in the gateway process environment through your normal secret manager. Add the following to your gateway configuration, retaining your existing `DynamicTurnRouting.Enabled`, `BundlePath`, and tier mappings:

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Jev": {
        "Mode": "shadow",
        "Endpoint": "https://api.typesafe.ai/v1/systemone",
        "ApiKeyRef": "env:TYPESAFE_API_KEY",
        "Model": "jev-1.13.0",
        "TimeoutMs": 1500,
        "MaxStateChars": 12000,
        "HistoryMessages": 4,
        "MaxConcurrentRequests": 8,
        "CircuitFailureThreshold": 3,
        "CircuitBreakSeconds": 30,
        "MinConfidence": 0.80,
        "DowngradeMinConfidence": 0.95,
        "MinProbabilityMargin": 0.15,
        "HighRiskThreshold": 0.20,
        "InputUsdPerMillionTokens": 0.042,
        "DiagnosticsPath": "routing/jev-decisions.jsonl"
      }
    }
  }
}
```

Restart the gateway after configuration changes. Alternatively set `OpenClaw__DynamicTurnRouting__Jev__Mode=shadow` in its environment. `openclaw routing status --config <path>` shows the configured Jev mode and model without exposing the key; it reads the file, not a running process's environment overrides.

`DynamicTurnRouting.Enabled` continues to control the ONNX baseline. With it false, shadow mode compares against the unchanged session/default model route. With it true, shadow mode compares against ONNX. Jev is a decorator, not a chat provider: do not put its model ID in `Models.Profiles`.

Shadow mode returns the baseline decision unchanged, including tool scope, prompts, fallback, reasoning and model selection. It awaits a bounded evaluation before returning, so it adds latency even though it does not apply its proposed route. It does not launch untracked background tasks. The deadline covers credential resolution and HTTP evaluation; there are no retries on the agent's critical path. Full concurrency slots cause immediate fallback instead of queueing. Consecutive failures open a short circuit breaker.

## State and decisions

The versioned `openclaw-tiers-v1` rubric asks three questions in one request:

- `tier`: choose T0, T1, T2, T3, or abstain.
- `high_risk`: whether the task involves consequential or sensitive operations.
- `requires_tools`: whether completing it requires external information or actions.

The state contains the current request and up to four whole recent user/assistant messages. System prompts and previous tool-result summaries are excluded. The configured redaction pipeline runs before sending text. This reduces common secret exposure but is not a guarantee that all sensitive content is removed: enabling either mode sends selected conversation text to the configured hosted endpoint. Use only where that data handling is acceptable. Requests with media, exact tool continuations, empty text, or oversized current text retain the baseline without calling Jev. Character limits are not an exact token counter; provider validation failures also fall back.

If relevant history is omitted by the message/character budget, or a compaction/tool summary is excluded, the policy will not downgrade the baseline tier. Confidence and top-two probability margin gate proposals, with stricter confidence for downgrades. These defaults are experimental starting values, not measured correctness guarantees. Confidence statistics are not interchangeable with ONNX probabilities, so its probability thresholds are not copied into Jev.

Shared deterministic rules preserve the existing risk, architecture/planning, conversation-depth, and sticky-tier floors. Jev's risk and tool-need answers can raise a floor. Existing explicit session model/profile selections prevent Jev proposals from overriding that selection beyond baseline behavior. `abstain`, uncertainty, unknown tier mappings, mismatched model versions, invalid typed answers, missing credentials, HTTP errors, and timeout all retain the baseline. Caller cancellation still propagates.

## Evaluate before activating

Diagnostics append to `Memory.StoragePath/routing/jev-decisions.jsonl`, unless another path is configured. An empty `DiagnosticsPath` disables the file; structured summary logs remain. Each record includes a decision ID, session ID, rubric/model version, baseline/proposed/applied tiers and profile IDs, probabilities, confidence, risk/tool signals, reason, truncation flag, latency, usage, and estimated decision cost. Conversation text, credentials, HTTP response bodies, and exception messages are not recorded. Protect and rotate this journal using the deployment's normal log retention policy; it has no automatic rotation.

The tier/profile fields describe routing preferences before the existing model selector resolves capabilities and provider fallback. They are not proof of which provider ultimately completed the turn. With ONNX disabled, baseline `T2` is a bookkeeping default, not evidence that the configured model has T2 capability.

Copy a completed journal snapshot and summarize it:

```bash
python3 scripts/evaluate-jev-routing.py /path/to/jev-decisions.snapshot.jsonl --output /tmp/jev-report.json
```

For representative decisions, use existing session records to assign human labels in a separate JSONL file:

```json
{"decision_id":"<ID from journal>","expected_tier":"T2","high_risk":true}
```

```bash
python3 scripts/evaluate-jev-routing.py /path/to/jev-decisions.snapshot.jsonl --labels /path/to/labels.jsonl --output /tmp/jev-quality.json
```

Without labels the report includes coverage, failures, disagreement, latency and estimated decision spend; it makes no accuracy claim. With labels it compares the baseline, always-T2, and Jev with safeguards/fallback using accuracy, confusion matrices, per-tier F1, under-routing, and high-risk capability retention. Label coverage and per-tier sample counts matter. Separate model/rubric cohorts before calibrating thresholds.

Go beyond tier agreement before activating: evaluate representative tasks against the actual configured candidate models, score completion quality, and compare total cost including Jev calls, downstream input/output, cache loss from model switches, retries, and repair turns. Include multi-turn references, ambiguous requests, non-English requests, adversarial text, consequential tasks, and outages. The existing heuristic routing tests do not establish production model quality. Failed requests without returned usage may still be billed, so journal spend is incomplete in those cases. Jev usage is reported separately and is not added to the session's generative token budget.

## Controlled activation and rollback

After calibration, change `Jev.Mode` to `active` and restart. Each configured `Policy.Tiers.T0` through `T3` may map to an existing `Models.Profiles` entry. Unmapped tiers fall back. Profiles and fallback chains must satisfy the model selector's tools, media, structured-output and other capability requirements.

Active mode changes only the proposed model profile, its direct fallback, and reasoning level, plus routing tier/reason. It preserves the baseline's tool restrictions, response policy, tags, and prompt behavior. It grants no tool permissions and does not replace governance, approvals, or the model selector. Baseline restrictions can still prevent a proposed model from using tools; evaluate that configuration explicitly.

The shared decision client and policy also support [local Laya routing](laya-routing.md). Enable only one decision provider at a time. Jev retains its own credentials, model identity, pricing, and rubric.

For Jev-only rollback set `Jev.Mode=disabled` and restart. To turn ONNX, Jev, and Laya off, run:

```bash
openclaw routing configure router --router disabled --config /path/to/appsettings.json
```

Restart afterward and remove any environment override that would re-enable Jev.

## Implementation and verification

- `OpenClaw.Routing.Decisions`: reusable Choice/Score/Noul HTTP transport, source-generated JSON, bounded response parsing, policy, and observer.
- `TurnRoutingGuardrails`: deterministic rules shared with ONNX; existing ONNX probability handling remains unchanged.
- `TurnRoutingServices`: optional gateway composition; no hosted client is registered when disabled.
- `JevRoutingTests`: HTTP contract, shadow isolation, active scope, redaction, floors, malformed data, timeout, cancellation, overload, concurrency and circuit recovery.

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -p:OpenClawSkipDashboardBuild=true --filter 'FullyQualifiedName~JevRoutingTests|FullyQualifiedName~TurnRoutingPolicyTests|FullyQualifiedName~RoutingCommandsTests'
python3 -m unittest discover -s tests/routing-eval -p test_jev_report.py
```

The transport follows the [TypeSafe API](https://docs.typesafe.ai/api). The model pin and initial estimated price come from its [model reference](https://docs.typesafe.ai/models), checked September 20, 2026. See [confidence semantics](https://docs.typesafe.ai/confidence) and [known model limitations](https://docs.typesafe.ai/model-jaggedness/jev-1.13) before tuning policy.

Jev is developed by TypeSafe AI; see [Diogo Almeida's introduction](https://typesafe.ai/blog/introducing-system-one-models-and-jev). OpenClaw's adapters are maintained separately. Laya credit is recorded in its [provider guide](laya-routing.md).

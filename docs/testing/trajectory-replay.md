# Turn a captured exchange into an offline regression

> **Release availability:** Included in v0.3.0 and later; not included in v0.2.0. See the [roadmap](../ROADMAP.md).

`OpenClaw.Testing` can import one user exchange from the gateway's version 1 JSONL trajectory export and replay its provider responses and tool outputs through the real runtime. This tests orchestration, tool dispatch, approval behavior, and output assertions without repeating external actions.

The new sample is an executable entry point:

```sh
dotnet run --project samples/OpenClaw.TrajectoryReplay -- \
  trajectory.jsonl exported-session-id 0 "expected answer text"
```

Choose the **session ID as it appears in the export** and the user prompt's `turnIndex`. With anonymized exports, use the anonymized ID. The exchange ends at the next prompt. The expected answer is an independent assertion supplied by the test author, not inferred from the capture. The sample exits nonzero when the assertion fails or replay diverges.

The sample creates isolated temporary memory, uses only recorded provider/tool adapters, and removes its temporary memory afterward. It does not connect to model providers or register real tools. The existing `openclaw test run` command keeps its existing behavior; this sample and the SDK API explicitly select real-runtime replay.

## Import, inspect, and store a fixture

```csharp
using var reader = File.OpenText("trajectory.jsonl");
var fixture = await TrajectoryReplayImporter.ImportAsync(
    reader, exportedSessionId, promptTurnIndex, projectRedactionPipeline, cancellationToken);
var sanitizedJson = JsonSerializer.Serialize(
    fixture, ScenarioJsonContext.Default.TrajectoryReplayFixture);
```

Supply an `IRedactionPipeline` configured for the secrets and private data in your application. Import always applies baseline secret redaction as well, recursively redacts JSON argument/result values, and replaces common credential fields. It discards exported session, sender, channel, call IDs, timestamps, governance records, and evidence bundles. Replay assigns fresh call IDs.

**Inspect the sanitized fixture before sharing or committing it.** Generic patterns cannot recognize every password, personal detail, path, or proprietary value in free text. The `anonymized` flag in the source is not treated as proof that the input is safe. Credential replacement can change JSON value types; fixtures model sanitized behavior rather than the original sensitive inputs.

Use `ScenarioJsonContext.Default.TrajectoryReplayFixture` for deserialization too. Fixtures carry schema version 1. Keep fixture files separate from the directory consumed by `JsonScenarioLoader`, which loads `AgentScenario` documents.

## Run against the runtime

`RuntimeScenarioRunner.RunReplayAsync` accepts a fixture, independent `ScenarioOracleDefinition` assertions, and a factory receiving an `IChatClient` and `IReadOnlyList<ITool>`. Construct the runtime with those supplied replay adapters and isolated memory. Do not add real tools, network-capable hooks, or live providers to a replay factory.

Each replay gets a private snapshot and fresh consumption state. Tool arguments are compared as JSON values, so property ordering does not matter. Independent calls in the same batch can execute in either order. The provider verifies that the runtime returned each recorded tool result before advancing. Missing, extra, mismatched, or repeated calls fail replay, even if a later final answer matches an assertion. Failures remain latched if runtime error handling catches an adapter exception.

The runner evaluates emitted runtime evidence and requires at least one outcome assertion. It does not treat imported records as proof that the runtime executed them.

## Supported boundary

- One complete text exchange: a user prompt, zero or more assistant tool batches, and a final assistant response.
- Completed tool results, including legacy records with a result but no `resultStatus`.
- Sequential batches and parallel calls with distinct name/argument combinations.
- Version 1 gateway exports, bounded to 8 Mi characters and 10,000 records.

Malformed JSON, unmatched calls/results, duplicate prompt selection, incomplete exchanges, unsupported schemas, failed/denied/unknown tool outcomes, and ambiguous identical calls within one batch are rejected. Import does not reconstruct prior conversation context, original tool schemas, model reasoning, token usage, timings, approval decisions, images, or audio. Configure approvals in the test explicitly; they are denied by default. Recorded model outputs test the runtime, not the quality of a live model's next response.

Automatic capture and structured-failure/media replay are implemented as described below. Durable action reconciliation is a separate opt-in runtime feature; offline replay never resumes or repeats a live external action.

## Automatic capture and expanded outcomes

Set `OpenClaw:Memory:RegressionCaptureEnabled=true` to have the gateway scan persisted sessions every 30 seconds and write complete exchanges to `Memory.StoragePath/regression-captures`. `RegressionCaptureMaxFiles` defaults to 100 (1–10,000). Capture is off by default. Files use deterministic content hashes, omit original identity/timestamps, apply configured and baseline structured credential redaction, and use private atomic writes. Identical exchanges deduplicate. At capacity capture stops adding files; archive/delete captures explicitly to free space. This is periodic capture of retained histories, not a lossless event log: exchanges compacted or deleted before a scan can be missed. Partial exchanges are skipped. Capture failures are logged without failing runtime execution.

Each file imports with session ID `capture` and prompt turn index `0`. Review captured text for application-specific sensitive information before committing fixtures. Binary media is not collected, credentials are not resolved, and general text redaction still depends on application redactors.

Replay now accepts `failed` and `blocked` tool outcomes as well as completed results. The stub raises a structured `ToolOutcomeException`; the actual executor emits the recorded result, failure code, and failure message. Unknown/denied outcome strings and inconsistent completed-with-failure records remain rejected. Independent outcome assertions are still required.

Multimodal input markers for image, audio, video, document, and file HTTP(S) URLs now verify the actual `UriContent` and media type delivered by the runtime. The fake provider never fetches these references or reads media bytes. This tests message composition and subsequent recorded reasoning/tool behavior, not visual/audio model accuracy. Local paths, inline binary, and unresolved channel file IDs are rejected. Use reviewed synthetic media references such as `https://media.invalid/fixture` when publishing fixtures; URLs can themselves contain sensitive identifiers. Output image/audio generation is not represented by session history and is not captured by this format.

Replay validates recorded conversation composition and result handling. A stubbed approval_required, governance_denied, or other pre-execution failure does not test the original approval or governance gate. Validate those gates separately with policy scenarios against the real runtime configuration. Automatic capture reads detached persisted snapshots, skips system turns and unsupported multi-answer exchanges, and stops scanning at capacity. It is periodic and best effort, not a lossless audit log.

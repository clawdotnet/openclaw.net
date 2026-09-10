# Turn a captured exchange into an offline regression

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

Automatic capture during production execution and replay of structured failures/media remain follow-on work. Durable action reconciliation remains a separate roadmap item; this feature never resumes or repeats a live external action.

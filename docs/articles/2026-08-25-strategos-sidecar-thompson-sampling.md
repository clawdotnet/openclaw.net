# OpenClaw × Strategos: From Sidecar Sample to Thompson Sampling Feedback Loop

> Companion documents:  
> - Design spec: [`2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../../superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md) (P0 sidecar sample)  
> - Design spec: [`2026-08-21-thompson-sampling-sidecar-feedback-design.md`](../../superpowers/specs/2026-08-21-thompson-sampling-sidecar-feedback-design.md) (P2 Thompson Sampling feedback)  
> - Parent design: [`OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md)

## 1. Why a sidecar

OpenClaw deliberately keeps heavyweight workflow engines out of its core—`OpenClaw.Core` and `OpenClaw.Gateway` (AOT-published) only handle orchestration, gatewaying, and model adaptation; persistence, event sourcing, sagas, compensation, and approval gates live in external backends. The gateway calls backends through `IAgentWorkflowRunner` instances registered in `AgentWorkflowRegistry`. The only supported `Kind` is `maf-durable-http`—full delegation to an external HTTP service (see [`docs/workflow-backends.md`](../../workflow-backends.md)).

This boundary keeps the OpenClaw binary from being forced to abandon AOT when introducing Wolverine/Marten/Postgres. The cost is that "durable, recoverable, approval-gated" workflows must be served by an external process. Before P0, the only example of that external process was [`samples/OpenClaw.DurableAgentReview`](../../../samples/OpenClaw.DurableAgentReview/Program.cs)—an in-memory mock that already shaped the three-endpoint contract but offered no event sourcing, no restart recovery, and no saga.

P0's goal is to put real persistence into the same endpoint shape: create the sample [`samples/OpenClaw.StrategosWorkflowHost/`](../../../samples/OpenClaw.StrategosWorkflowHost/) hosting a real Strategos saga runtime (Wolverine + Marten + PostgreSQL), speaking the same `maf-durable-http` contract—zero gateway changes, only redirecting `BaseUrl` from 5095 to 5097. P0 only proves the integration works; it does not deliver `Kind: strategos-http` aliases, Evidence cross-linking, Thompson Sampling, or host AOT—those are P1/P2/P3.

P2 closes the Thompson Sampling learning loop on top of the same sidecar: the gateway feeds each step's success/failure back into the sidecar's agent selector, and the selector biases future picks toward historically successful agents. This article focuses on the design and implementation of that feedback loop.

## 2. Overall topology

```
                       ┌──────────────────────────────────────┐
                       │ OpenClaw.Gateway  (AOT)               │
                       │                                      │
                       │  AgentWorkflowRegistry (Kind dispatch)│
                       │   ├─ Kind=maf-durable-http           │
                       │   │    → MafDurableHttpWorkflowRunner│
                       │   │       ├─ RuntimeEventStore(JSONL)│
                       │   │       └─ RuntimeEventWebhook(P2) │
                       │   └─ Kind=strategos-http (P1 landed) │
                       │        → StrategosHttpWorkflowRunner │
                       │           └─ composes MafDurableHttp │
                       │              (only re-tags Kind)      │
                       │                                      │
                       │  BaseUrl = http://127.0.0.1:5097     │
                       └────────────┬─────────────────────────┘
                                    │  POST /api/workflows/.../run
                                    │  GET  /api/workflows/.../status/{runId}
                                    │  POST /api/workflows/.../respond/{runId}
                                    ▼
        ┌───────────────────────────────────────────────────────────┐
        │ OpenClaw.StrategosWorkflowHost  (JIT sample)              │
        │                                                           │
        │  ┌────────────────┐    ┌────────────────────┐              │
        │  │ 3 endpoints    │───▶│ Strategos Saga     │              │
        │  │ (contract)     │    │ (Roslyn source-gen)│              │
        │  └────────────────┘    │  + Wolverine        │              │
        │                        │                     │              │
        │                  ┌─────┴──────────┐         │              │
        │                  │ IWorkflowStep  │ ── IChatClient (P2 wrap)│
        │                  │ reviewers (DI) │         │              │
        │                  └────────────────┘         │              │
        │                  ┌────────────────────────┐ │              │
        │                  │ Marten (Postgres)      │◀┘              │
        │                  │ event stream + agg.    │                 │
        │                  └────────────────────────┘                 │
        │                                                           │
        │  ┌──────────────────────┐  ┌──────────────────────────┐    │
        │  │ SelectorBackedChat    │  │ GatewayEventReceiver     │    │
        │  │ Client (P2 IChatClient│  │ POST /runtime-events     │    │
        │  │ decorator)            │  │ (P2)                     │    │
        │  └──────────┬───────────┘  └──────────┬───────────────┘    │
        │             │                         │                    │
        │  ┌──────────▼───────────┐  ┌──────────▼───────────────┐    │
        │  │ ThompsonSampling     │  │ AgentOutcomeMapper       │    │
        │  │ AgentSelector +      │◀─│ (P2 pure function)       │    │
        │  │ InMemoryBeliefStore  │  └──────────────────────────┘    │
        │  └──────────────────────┘                                  │
        │                                                            │
        │  ┌──────────────────────┐                                  │
        │  │ RunIdAgentSelection   │ (P2 (runId, stepName) cache)    │
        │  │ Cache (P2)           │                                  │
        │  └──────────────────────┘                                  │
        └────────────────────────────────────────────────────────────┘
                                    │
                  ┌─────────────────┼─────────────────┐
                  ▼                 ▼                 ▼
            ┌──────────┐     ┌──────────────┐   ┌──────────────┐
            │ Mode=Mock│     │ Mode=Direct  │   │ Mode=Gateway │
            │ (fixed   │     │ (any OpenAI  │   │ →127.0.0.1:  │
            │  verdict)│     │ -compatible  │   │   18789/v1   │
            │ default  │     │ endpoint)    │   │              │
            └──────────┘     └──────────────┘   └──────────────┘
```

Key invariants:

- **Zero gateway changes.** `AgentWorkflowRegistry` continues to accept `maf-durable-http`. The three endpoints (`POST .../run`, `GET .../status/{runId}`, `POST .../respond/{runId}`) match byte-for-byte. The host runs as **JIT, non-AOT**—sidestepping the unfinished Wolverine/Marten AOT story.
- **`runId == saga WorkflowId`.** The adapter parses `run_xxx` into a saga id, which doubles as the Marten stream identity.
- **`PendingInputs` only populate when `waiting_for_input`**, matching `MafDurableHttpWorkflowRunner` expectations.
- **Provider keys never leave the gateway.** In `Mode=Gateway`, provider keys, presets, and TokenJuice stay single-source-of-truth inside the gateway; the host only holds the credential needed to call the gateway's v1 endpoint.
- **Off by default.** Thompson Sampling is off on both sides (`Strategos:Selector:Enabled=false`, `OpenClaw:RuntimeEvents:Webhook:Url=""`). When off, behavior matches P0 exactly.

## 3. P0: real persistence in the three-endpoint shape

### 3.1 The review workflow's Strategos DSL

The workflow body [`Workflows/ReviewWorkflow.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Workflows/ReviewWorkflow.cs) is an event-sourced saga that demonstrates the full topology of workflow primitives Strategos offers:

```csharp
[Workflow("durable-agent-review", Persistence = PersistenceMode.EventSourced)]
public static partial class DurableAgentReviewWorkflowDefinition
{
    public static WorkflowDefinition<ReviewState> Definition =>
        Workflow<ReviewState>
            .Create("durable-agent-review")
            .StartWith<PlanExecutor>()
            .Fork(
                path => path.Then<SecurityReviewer>(),
                path => path.Then<ArchitectureReviewer>(),
                path => path.Then<CostReviewer>())
            .Join<AggregateReviews>()
            .Then<AssessConfidence>(step => step
                .RequireConfidence(0.85)
                .OnLowConfidence(alt => alt.Then<RequestHumanReview>()))
            .AwaitApproval<Operator>(approval => approval
                .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                .WithTimeout(TimeSpan.FromHours(4))
                .OnTimeout(esc => esc.EscalateTo<Admin>(a => a
                    .WithContextFrom(s => "Escalated after approval timeout."))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())
            .OnFailure(flow => flow.Then<NotifyFailure>())
            .Finally<EmitAuditTrace>();
}
```

Capabilities demonstrated here:

- **Fork / Join**: three reviewers run in parallel, each returning its own verdict, joined back at `AggregateReviews`.
- **Confidence gate**: `AssessConfidence` returns aggregate confidence; `RequireConfidence(0.85)` is declared on the step-config. `MockReviewChatClient` outputs `Confidence=0.8`, deterministically triggering the low-confidence branch.
- **Human in the loop**: `AwaitApproval<Operator>` pauses the saga and persists `Request...ApprovalEvent`; the gateway's `respond` endpoint resumes the saga.
- **Timeout escalation**: `OnTimeout` routes to `EscalateTo<Admin>`, which opens another `AwaitApproval<Admin>`. Note this lives inside the approval builder (`IApprovalEscalationBuilder`), not as a top-level `Then<>` step.
- **Compensation**: `Compensate<RevertApprovedAction>()` declared on the step-config. When triggered, the saga enters a compensation phase and the adapter maps it to `failed`.
- **Failure fallback**: `OnFailure` must come before `Finally`. `NotifyFailure` writes the failure reason.
- **Audit trail**: `Finally<EmitAuditTrace>()` writes `ExecutionResult`.

### 3.2 Event-sourced state

[`Workflows/ReviewState.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Workflows/ReviewState.cs) implements `IEventSourcedState<ReviewState>` with the Marten single-stream aggregation convention:

```csharp
[WorkflowState]
public sealed record ReviewState : IEventSourcedState<ReviewState>
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public string UserRequest { get; init; } = "";
    public string Plan { get; init; } = "";
    public IReadOnlyList<ReviewVerdict> Reviews { get; init; } = [];
    public string? AggregatedSummary { get; init; }
    public double AggregateConfidence { get; init; }
    public HumanDecision? Decision { get; init; }
    public string? ExecutionResult { get; init; }
    public string? FailureReason { get; init; }
    public string CurrentPhase { get; init; } = "NotStarted";

    public static ReviewState Create(DurableAgentReviewStarted started) => started.InitialState;

    public ReviewState Apply(PlanExecutorCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingPlan" };
    // ...fold for each step Completed

    public ReviewState ApplyEvent(IProgressEvent evt) => evt switch
    {
        PlanExecutorCompleted c => c.UpdatedState,
        SecurityReviewerCompleted c => c.UpdatedState,
        // ...
        _ => this,
    };
}
```

`{StepClassName}Completed` events are emitted by the Roslyn source generator (naming rule verified from `EventsEmitter.cs` and `EventSourcedAuditState.Create(EventSourcedHappyStarted)`). Each event carries an `UpdatedState` snapshot—the fold adopts that snapshot verbatim and stamps `CurrentPhase`.

### 3.3 Reviewer steps: exposing the IChatClient boundary for P2

[`Steps/SecurityReviewer.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Steps/SecurityReviewer.cs) is the natural injection point for P2 Thompson Sampling:

```csharp
public sealed class SecurityReviewer(IChatClient chat) : IWorkflowStep<ReviewState>
{
    public async Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "Return ONLY JSON: {role,verdict,summary,confidence}."),
            new ChatMessage(ChatRole.User, PromptBuilders.Security(state.Plan, state.UserRequest)),
        };
        // ← (runId, stepName) ride along — P2 decorator uses it for outcome correlation
        var correlation = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["runId"] = state.Id.ToString(),
                ["stepName"] = context.StepName,
            },
        };
        var response = await chat.GetResponseAsync<ReviewVerdict>(
            messages, options: correlation, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var verdict) || verdict is null)
            throw new InvalidOperationException("LLM did not return a security verdict.");
        var stamped = verdict with { Role = "security" };
        return StepResult<ReviewState>.WithConfidence(
            state with { Reviews = state.Reviews.Append(stamped).ToList() },
            stamped.Confidence);
    }
}
```

Three design decisions stand out:

1. **`IChatClient` is injected via the primary constructor.** The source generator registers each step as `AddTransient<{Step}>()`; DI resolves `IChatClient` per call.
2. **`ChatOptions.AdditionalProperties` carries `(runId, stepName)`**—the correlation key for P2 outcome attribution. The gateway does not know which agent the sidecar picked; it can only tell the sidecar `(runId, stepName)`, so the sidecar must remember the agentId itself.
3. **`StepResult.WithConfidence`** propagates the verdict's confidence to the saga so `RequireConfidence` can evaluate.

`ArchitectureReviewer` and `CostReviewer` mirror the same structure. During the fork, the three reviewers only see the pre-fork state and append their own verdicts without interfering.

### 3.4 Contract adapter: three endpoints ↔ saga commands and state

[`Adapters/DurableHttpAdapter.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs) translates the HTTP shape into Marten event-stream reads and Wolverine command publishes:

```csharp
public sealed class DurableHttpAdapter(IMessageBus bus, IDocumentStore store)
{
    public async Task<AgentWorkflowRunResult> StartRunAsync(
        string workflowName, AgentWorkflowRequest request, CancellationToken ct)
    {
        var runId = $"run_{Guid.NewGuid():N}";
        if (!TryParseSagaId(runId, out var sagaId))
            throw new InvalidOperationException("Invalid runId format.");
        var initial = new ReviewState { Id = sagaId, WorkflowId = sagaId, UserRequest = request.Input };
        var cmd = new StartDurableAgentReviewCommand(sagaId, initial);
        await bus.PublishAsync(cmd, ct);  // Wolverine transactional outbox
        return new AgentWorkflowRunResult { /* ... */ };
    }

    public async Task<AgentWorkflowRunSnapshot?> GetStatusAsync(
        string workflowName, string runId, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId)) return null;
        await using var query = store.QuerySession();
        var state = await query.LoadAsync<ReviewState>(sagaId);  // inline snapshot
        if (state is null) return null;
        var status = PhaseStatusMap.ToOpenClawStatus(state.CurrentPhase);
        var events = await query.Events.FetchStreamAsync(sagaId);  // audit stream
        // ...
    }
}
```

Read path uses `LoadAsync` (inline snapshot, fast) plus `FetchStreamAsync` (audit stream, complete). The state mapping [`Adapters/PhaseStatusMap.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/PhaseStatusMap.cs) is a pure function—`"AwaitingApproval" -> "waiting_for_input"` etc., translating Strategos internal phases into OpenClaw `AgentWorkflowStatuses` constants (lowercase `queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`).

Write path uses `IMessageBus.PublishAsync`: Wolverine's outbox pattern guarantees that commands and state changes commit in the same transaction—after saga startup the gateway needs no idempotency.

Approval resumption flows through `ResumeOperatorApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision Decision, string? SelectedOptionId, string? Instructions)`—`[SagaIdentity]` routes by `WorkflowId` to the correct saga instance; `ApprovalDecision` is the `Strategos.Models` enum (`Approved/Rejected/Deferred`).

### 3.5 LLM modes: zero-key first run

[`Configuration/LlmMode.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Configuration/LlmMode.cs) resolves `LlmOptions` into one of three `IChatClient` factories:

| Mode | Network egress | Credential source | When to use |
|---|---|---|---|
| `Mock` (default) | none | none | First contributor run; CI kill-restart |
| `DirectOpenAI` | Provider API | `env:OPENAI_API_KEY` | Skip OpenClaw, talk to provider directly |
| `BackThroughGateway` | `127.0.0.1:18789/v1` | `env:OPENCLAW_GATEWAY_KEY` | Full chain, provider keys stay in gateway |

`OPENCLAW_GATEWAY_KEY` is the gateway authentication credential (lets the host through the gateway's door), **not** a provider key (provider keys always stay in the gateway). The Mock mode deterministically returns `ReviewVerdict{Verdict="review-required", Confidence=0.8}`—the workflow deterministically reaches `AssessConfidence` (0.8 < 0.85 → `AwaitApproval`), exercising the approval gate even without an LLM.

P0 does not implement concrete OpenAI-compatible `IChatClient` for Direct/Gateway—neither OpenClaw nor Strategos pulls in the OpenAI SDK. Mock mode covers all P0 acceptance criteria; Direct/Gateway implementations are flagged in the README as "to be completed by the user".

### 3.6 Fork concurrency conflicts (verified, critical)

Three reviewers append to the same Marten event stream in parallel, triggering optimistic-concurrency conflicts (`ConcurrentUpdateException` / `EventStreamUnexpectedMaxEventIdException`). [`Program.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Program.cs) must configure retries:

```csharp
opts.OnException(ex => ex is Marten.Exceptions.ConcurrentUpdateException
    || ex.GetType().Name.Contains("EventStreamUnexpected", StringComparison.Ordinal))
    .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
```

Without this configuration, the fork path stalls the saga. This is a real production configuration verified from `EventSourcedHostFixture.cs:86-101`, not a P0 assumption.

### 3.7 Kill-Restart test: the only proof of state recovery

P0's core acceptance test [`KillRestartTests`](../../../samples/OpenClaw.StrategosWorkflowHost.Tests/KillRestartTests.cs) walks the full lifecycle:

```
1. docker compose up -d postgres strategos-host
2. POST /run → get runId, status=queued→running
3. poll GET /status until status=waiting_for_input
4. docker compose kill strategos-host          (simulate crash)
5. docker compose up -d strategos-host         (restart — Marten holds event stream)
6. GET /status/{runId} → status=waiting_for_input  ← critical assertion
7. POST /respond/{runId} {approved:true} → status=completed
8. assert: step 6's Events array contains events from before the crash
```

Step 6 is the critical one—without it, the restart test only proves "the host came up", not "state was recovered". This is the essential difference between P0 and the in-memory mock: an in-memory mock never loses saga state, but killing the process resets everything to zero.

## 4. P2: the Thompson Sampling feedback loop

P2 adds a learning loop on top of P0: the agent selector learns from history and biases future picks toward higher-success agents.

### 4.1 Goals and boundaries

| Goal | Boundary (explicit non-goals) |
|---|---|
| Wrap `Steps/*.cs` agent steps with `IAgentSelector` | Do not rewrite reviewer steps |
| Consume gateway run results via `RecordOutcomeAsync` | Do not change JSONL shape (webhook is a mirror of Append) |
| Close the Thompson Sampling learning loop | Do not introduce `Kind: strategos-http` (P1) |
| Do not break existing workflows | Default off; off-state behavior unchanged |

### 4.2 Three layers + DI wiring

#### 4.2.1 `SelectorBackedChatClient` — the IChatClient decorator

[`Adapters/SelectorBackedChatClient.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/SelectorBackedChatClient.cs) puts "pick agent + route" outside the `IChatClient` boundary, leaving reviewer code untouched:

```csharp
public sealed class SelectorBackedChatClient : IChatClient
{
    private readonly IAgentSelector _selector;
    private readonly RunIdAgentSelectionCache _cache;
    private readonly IChatClient _defaultClient;
    private readonly IReadOnlyDictionary<string, IChatClient> _innerClients;
    private readonly SelectorOptions _options;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await ResolveInnerClientAsync(messages, options, cancellationToken);
        return await inner.GetResponseAsync(messages, options, cancellationToken);
    }

    private async Task<IChatClient> ResolveInnerClientAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var context = BuildContext(messages);
        var selectionResult = await _selector.SelectAgentAsync(context, cancellationToken);

        if (!selectionResult.IsSuccess || selectionResult.Value is null)
        {
            _logger.LogWarning("Selector returned failure: {Error}. Falling back to default client.", selectionResult.Error);
            return _defaultClient;
        }

        var selected = selectionResult.Value.SelectedAgentId;
        if (!_innerClients.TryGetValue(selected, out var inner))
        {
            _logger.LogWarning("Selected agent {AgentId} has no InnerClient registered. Falling back to default client.", selected);
            return _defaultClient;
        }

        // Only cache when both (runId, stepName) are present — otherwise the outcome can never correlate back
        if (TryGetCorrelationKey(options, out var runId, out var stepName))
        {
            _cache.Set(runId, stepName, selected, _options.TaskCategory);
        }

        return inner;
    }
}
```

Three design principles:

1. **All failures fall back to `defaultClient`.** Selector returns `Result.Failure`, the chosen agentId is not registered, or the inner client throws—every path returns `defaultClient` instead of throwing. This preserves P0's "reviewer steps are unchanged" rule: selector failure cannot drag down workflow availability.
2. **No cache write when the correlation key is missing.** If `ChatOptions.AdditionalProperties` lacks `runId` or `stepName`, caching is meaningless (the outcome event will never find a matching entry) so the write is skipped.
3. **`AgentSelectionContext` deliberately omits `WorkflowId`.** The sidecar correlates by `(runId, stepName)`. `WorkflowId` is set to `Guid.Empty` as a placeholder—the Strategos selector only needs "task description + available agents", not the saga identity.

#### 4.2.2 `RunIdAgentSelectionCache` — sidecar-local FIFO cache

[`Adapters/RunIdAgentSelectionCache.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/RunIdAgentSelectionCache.cs) is the core of outcome attribution:

```csharp
public sealed class RunIdAgentSelectionCache
{
    private readonly ConcurrentDictionary<string, CachedSelection> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _evictionLock = new();
    private readonly int _capacity;

    public RunIdAgentSelectionCache(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public CachedSelection Set(string runId, string stepName, string agentId, string taskCategory)
    {
        var key = GetKey(runId, stepName);  // "{runId}{stepName}"
        var selection = new CachedSelection(agentId, taskCategory, DateTimeOffset.UtcNow);

        if (_entries.TryAdd(key, selection))
        {
            lock (_evictionLock)
            {
                _insertionOrder.Enqueue(key);
                EvictIfOverCapacity();
            }
        }
        else
        {
            _entries[key] = selection;  // overwrite
        }
        return selection;
    }

    public CachedSelection? TryGet(string runId, string stepName)
        => _entries.TryGetValue(GetKey(runId, stepName), out var selection) ? selection : null;

    private void EvictIfOverCapacity()
    {
        while (_insertionOrder.Count > _capacity)
        {
            var oldest = _insertionOrder.Dequeue();
            _entries.TryRemove(oldest, out _);
        }
    }
}

public readonly record struct CachedSelection(string AgentId, string TaskCategory, DateTimeOffset SelectedAt);
```

Two design choices:

- **Capacity-driven FIFO eviction** (default 10 000 entries): when over capacity, evict from the head of `_insertionOrder`; `ConcurrentDictionary` keeps the read path lock-free. Thompson Sampling is naturally tolerant of "selections without recorded outcomes"—belief stays at prior and eviction is safe.
- **Key is `{runId}{stepName}` string concatenation.** Simple and avoids a custom `IEqualityComparer<CachedKey>`. `(runId, stepName)` is a unique key; repeated `Set` overwrites the value but preserves position in the queue—preventing a malicious or buggy caller from filling the queue by re-using the same key.

**Why local memory?** A sidecar crash loses historical selections; feedback goes silent—this is intrinsic to Thompson Sampling and acceptable. Persisting to Redis/Postgres would add serialization, expiration, and cross-instance consistency complexity that outweighs the benefit. The selector's intent is "fast trial-and-error, long-arc adaptation"—a single process lifetime is enough to accumulate sufficient samples.

#### 4.2.3 `AgentOutcomeMapper` — pure function

[`Adapters/AgentOutcomeMapper.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/AgentOutcomeMapper.cs) translates `RuntimeEventEntry` into `(agentId, taskCategory, AgentOutcome)`:

```csharp
public sealed class AgentOutcomeMapper
{
    private static readonly HashSet<string> CompletedActions = new(StringComparer.Ordinal)
    {
        "run_completed",
        "run_failed",
    };

    public MappedOutcome? Map(RuntimeEventEntry entry, CancellationToken ct = default)
    {
        if (!string.Equals(entry.Component, "workflow", StringComparison.OrdinalIgnoreCase))
            return null;

        var metadata = entry.Metadata;
        if (metadata is null
            || !metadata.TryGetValue("runId", out var runId)
            || !metadata.TryGetValue("stepName", out var stepName))
        {
            _logger.LogDebug("Skipping runtime event {EventId}: missing runId or stepName in metadata.", entry.Id);
            return null;
        }

        if (!CompletedActions.Contains(entry.Action))
            return null;  // run_started / response_sent — no pass/fail signal

        var cached = _cache.TryGet(runId, stepName);
        if (cached is null)
        {
            _logger.LogDebug("Skipping runtime event {EventId}: no cached selection for runId={RunId} stepName={StepName}.",
                entry.Id, runId, stepName);
            return null;
        }

        var success = string.Equals(entry.Action, "run_completed", StringComparison.OrdinalIgnoreCase);
        var outcome = success ? AgentOutcome.Succeeded() : AgentOutcome.Failed();

        return new MappedOutcome(cached.Value.AgentId, cached.Value.TaskCategory, outcome);
    }
}

public readonly record struct MappedOutcome(string AgentId, string TaskCategory, AgentOutcome Outcome);
```

**Why pure function?** Six unit-test cases are all "given an entry, expect a mapped result or null"—zero state, zero IO. Mapping policy:

- **Component filter**: only `Component == "workflow"` matters; `tool`/`session`/anything else is ignored.
- **Action filter**: `run_started` and `response_sent` carry no success/failure signal and are skipped; `run_completed` and `run_failed` map.
- **Missing correlation key → skip**: returning null (not throwing) when `runId` or `stepName` is absent.
- **Cache miss → skip**: silently skip when the sidecar has no recorded selection (predates sidecar startup or has been evicted).
- **Debug log on skip reasons**: this is a high-frequency hot path; debug-level logging avoids log spam.

#### 4.2.4 `GatewayEventReceiver` — HTTP ingress

[`Adapters/GatewayEventReceiver.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/GatewayEventReceiver.cs) handles the inbound webhook:

```csharp
public sealed class GatewayEventReceiver
{
    private const int DedupCapacity = 10_000;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public async Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // 1. Bearer token check (mismatch → 401)
        if (_expectedBearerToken is not null)
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !FixedTimeEquals(auth.Substring("Bearer ".Length).Trim(), _expectedBearerToken))
            {
                _logger.LogWarning("Rejecting /runtime-events: bearer token mismatch.");
                return Results.Unauthorized();
            }
        }

        // 2. Deserialize
        RuntimeEventEntry? entry;
        try { entry = await JsonSerializer.DeserializeAsync<RuntimeEventEntry>(context.Request.Body, ...); }
        catch (JsonException ex) { return Results.BadRequest(); }

        // 3. Dedup by entry.Id
        if (!_seen.TryAdd(entry.Id, 0)) return Results.Ok();  // already processed
        TrimSeenIfOverCapacity();

        // 4. Map → call RecordOutcomeAsync
        var mapped = _mapper.Map(entry, cancellationToken);
        if (mapped is null) return Results.Ok();  // filtered by mapper

        try
        {
            var outcome = await _selector.RecordOutcomeAsync(
                mapped.Value.AgentId, mapped.Value.TaskCategory, mapped.Value.Outcome, cancellationToken);
            if (!outcome.IsSuccess)
                _logger.LogWarning("RecordOutcomeAsync returned failure for agent {AgentId}: {Error}",
                    mapped.Value.AgentId, outcome.Error);
        }
        catch (Exception ex)
        {
            // selector throws must be swallowed — the HTTP loop must keep moving
            _logger.LogWarning(ex, "RecordOutcomeAsync threw for agent {AgentId}; dropping outcome.", mapped.Value.AgentId);
        }

        return Results.Ok();
    }

    private static bool FixedTimeEquals(string a, string b) { /* constant-time compare, side-channel defense */ }
}
```

Design points:

- **Dedup by `entry.Id`.** The gateway uses `evt_{Guid:N}` (first 20 chars) as id; the sidecar returns 200 OK on a duplicate id without re-processing—preserving the webhook retry contract.
- **FIFO eviction on the dedup set.** When `_seen` exceeds 10 000 entries, remove the surplus; strict LRU is unnecessary (approximate eviction suffices under concurrency).
- **`FixedTimeEquals` for bearer tokens.** Constant-time string compare to defend against side-channel timing attacks that try to infer the token.
- **Selector exceptions are swallowed.** The HTTP loop must keep processing subsequent events; one bad outcome delivery cannot stall the pipeline.
- **Route mounted only when `Enabled=true`.** `SelectorServerBootstrap.MapSelectorEventEndpoint` checks config before `MapPost("/runtime-events", ...)`. When off, that path returns 404 instead of 401—404 tells the gateway "endpoint doesn't exist, retry won't help", saving pointless retries.

### 4.3 Gateway side: `RuntimeEventWebhook` outbound client

[`src/OpenClaw.Gateway/RuntimeEventWebhook.cs`](../../../src/OpenClaw.Gateway/RuntimeEventWebhook.cs) is the mirror client on the gateway, pushing the same `RuntimeEventStore.Append` entry to the sidecar:

```csharp
public sealed class RuntimeEventWebhook
{
    public async Task SendAsync(RuntimeEventEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Url)) return;

        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url);
                if (!string.IsNullOrWhiteSpace(_options.BearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

                var json = JsonSerializer.Serialize(entry, CoreJsonContext.Default.RuntimeEventEntry);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) return;

                var status = (int)response.StatusCode;
                if (status is 401 or 403)
                {
                    // config error, do not retry
                    _logger.LogWarning("RuntimeEventWebhook returned {StatusCode}; webhook will not retry (configuration error).", status);
                    return;
                }
                if (status is >= 500 || status is 429)
                {
                    // server trouble, retry once
                    if (attempts >= 2) { /* give up */ return; }
                    await Task.Delay(_options.RetryDelayMs, cancellationToken);
                    continue;
                }
                // other 4xx: sidecar rejected the payload, retries won't help
                _logger.LogDebug("RuntimeEventWebhook returned {StatusCode} for entry {EventId}; dropping.", status, entry.Id);
                return;
            }
            catch (HttpRequestException ex) { /* connection failed, retry once */ }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
```

**Retry policy table:**

| Failure | Behavior |
|---|---|
| 5xx / 429 | retry once after `RetryDelayMs` (default 1s) |
| 401 / 403 | config error, stop sending |
| Other 4xx | sidecar rejected payload, retries useless |
| `HttpRequestException` | connection failed, retry once |
| 2xx | done |

Webhook failure **does not affect JSONL writes**—`MafDurableHttpWorkflowRunner.RecordEvent` calls `_events.Append(entry)` first, then fire-and-forget pushes the webhook. If the sidecar is down, the durable record survives; manual replay is possible later.

### 4.4 Gateway-side extension: `RecordEvent` adds `stepName` + `score`

[`src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs`](../../../src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs) extends the original `RecordEvent(runId, action, status, summary)` with `stepName` and `score` parameters and emits step-level events as independent webhook triggers:

```csharp
private void RecordEvent(string runId, string action, string status, string summary,
    string? stepName = null, double? score = null)
{
    var metadata = new Dictionary<string, string>
    {
        ["backendId"] = BackendId,
        ["workflowId"] = WorkflowId,
        ["runId"] = runId,
        ["status"] = status
    };
    if (!string.IsNullOrWhiteSpace(stepName)) metadata["stepName"] = stepName;
    if (score.HasValue) metadata["score"] = score.Value.ToString(CultureInfo.InvariantCulture);

    var entry = new RuntimeEventEntry { /* Component = "workflow", Action = action, Metadata = metadata, ... */ };
    _events.Append(entry);

    if (_webhook is not null)
    {
        // fire-and-forget — webhook handles its own retry and exception swallowing
        _ = Task.Run(async () =>
        {
            try { await _webhook.SendAsync(entry).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "RuntimeEventWebhook.SendAsync threw for entry {EventId}.", entry.Id); }
        });
    }
}
```

Step-level events come from `RecordStepEvents`, which strips `stepName` from the saga's `*Completed/*Failed` event types:

```csharp
private void RecordStepEvents(string runId, IReadOnlyList<AgentWorkflowEvent> workflowEvents, string status)
{
    foreach (var evt in workflowEvents)
    {
        if (string.Equals(evt.Type, "status", StringComparison.OrdinalIgnoreCase)) continue;  // skip StreamAsync-injected status
        if (string.IsNullOrWhiteSpace(evt.Type)) continue;
        if (!_lastRecordedStepEventIds.TryAdd(evt.Id, 0)) continue;  // dedup by evt.Id

        var action = ResolveStepAction(evt.Type);
        if (action is null) continue;

        var bareStepName = StripStepSuffix(evt.Type);  // "SecurityReviewerCompleted" → "SecurityReviewer"
        RecordEvent(
            runId,
            action,
            status,
            string.IsNullOrWhiteSpace(evt.Summary) ? $"Step '{bareStepName}' completed." : evt.Summary,
            stepName: bareStepName);
    }
}

private static string? ResolveStepAction(string eventType)
{
    if (eventType.EndsWith("Completed", StringComparison.Ordinal)) return "run_completed";
    if (eventType.EndsWith("Failed", StringComparison.Ordinal) || eventType.EndsWith("Faulted", StringComparison.Ordinal))
        return "run_failed";
    return null;
}
```

**Why split out `stepName`?** The gateway sees the saga's `"{StepClassName}Completed"` event types—for outcome correlation, the sidecar needs the bare `stepName` (`SecurityReviewerCompleted` → `SecurityReviewer`), aligned with `context.StepName` in `Steps/*.cs`.

**Why does the webhook fire from both `RecordStatus` and `RecordStepEvents`?** Workflow-level status changes (`waiting_for_input`/`completed`/`failed`) and step-level events (each reviewer's completion/failure) each produce a `RuntimeEventEntry`. The sidecar only cares about `run_completed`/`run_failed` with a matching correlation key, so workflow-level statuses are filtered out by the mapper (their action doesn't match `CompletedActions`) and don't pollute the belief store.

### 4.5 Wiring (`Program.cs`)

[`samples/OpenClaw.StrategosWorkflowHost/Program.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Program.cs) ties both layers together:

```csharp
// LLM-mode-aware IChatClient. Mock by default; other modes throw at startup (see LlmMode).
var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var llmLogger = NullLogger<LlmMode>.Instance;
var chat = LlmClientFactory.Create(llmOptions, llmLogger);

// Selector surface: when Strategos:Selector:Enabled is true, replaces the
// direct IChatClient registration with a SelectorBackedChatClient that
// routes calls through Thompson Sampling. When disabled (default), this
// is a no-op for the IChatClient registration — same behavior as P0/P2.
SelectorServerBootstrap.AddSelectorServer(opts.Services, builder.Configuration, chat);
if (builder.Configuration.GetValue($"{SelectorOptions.SectionName}:Enabled", false))
{
    opts.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<SelectorBackedChatClient>());
}
else
{
    opts.Services.AddSingleton(chat);
    opts.Services.AddSingleton<IChatClient>(chat);
}
```

`SelectorServerBootstrap.AddSelectorServer` registers the selector singleton, the cache, the mapper, the receiver, and the decorator. `MapSelectorEventEndpoint` only mounts `POST /runtime-events` when `Enabled=true`.

### 4.6 End-to-end call stacks

**Agent selection:**

```
ReviewWorkflow ─▶ SecurityReviewer.ExecuteAsync(state, ct)
                            └─▶ chat.GetResponseAsync<ReviewVerdict>(messages, options, ct)
                                  └─▶ SelectorBackedChatClient.GetResponseAsync
 ├─ Build AgentSelectionContext { WorkflowId=Guid.Empty, StepName="StrategosChat",
 │                                 TaskDescription=first user msg truncated,
 │                                 AvailableAgents=["mock","mock-fast"] }
 ├─ selector.SelectAgentAsync(context)
 │     on failure → log warning, return default inner client
 ├─ RunIdAgentSelectionCache.Set((runId, stepName), agentId, category, ts)
 ├─ Pick agent-specific inner client
 │     "mock"         → MockReviewChatClient
 │     "mock-fast"    → NSubstitute fake
 │     "gpt-4o-mini"  → DirectOpenAI client (if configured)
 └─ inner.GetResponseAsync<ReviewVerdict>(messages, options, ct)
```

**Outcome feedback:**

```
gateway MafDurableHttpWorkflowRunner completes
 └─▶ RecordStepEvents(...)  (each *Completed/*Failed step triggers)
      └─▶ _events.Append(new RuntimeEventEntry {
             Component = "workflow",
             Action    = "run_completed" | "run_failed",
             Metadata  = { runId, stepName="SecurityReviewer", status, backendId, workflowId }
         })
 ├─ (existing) JSONL write — unchanged from today
 └─ (new) RuntimeEventWebhook → POST {sidecar-url}/runtime-events
        Authorization: Bearer <shared token>
        Body = RuntimeEventEntry (CoreJsonContext.Default.RuntimeEventEntry serialized)

sidecar GatewayEventReceiver.HandleAsync(ctx, ct)
 ├─ validate token (401 if mismatch)
 ├─ deserialize RuntimeEventEntry
 ├─ skip duplicate id (in-memory LRU 10k)
 ├─ AgentOutcomeMapper.Map(entry) → (agentId, taskCategory, AgentOutcome)?
 ├─ selector.RecordOutcomeAsync(agentId, category, outcome)
 │     on failure → log warning, drop
 └─ 200 OK
```

## 5. Key design decisions and trade-offs

| # | Decision | Choice | Alternatives | Rationale |
|---|---|---|---|---|
| 1 | Seam for agent selection | `SelectorBackedChatClient` decorates `IChatClient` | (a) rewrite each reviewer step; (b) `IAgentStepExecutor` | Decorator touches zero reviewer code; `IChatClient` boundary naturally separates "model call" from "model choice" |
| 2 | Outcome source | HTTP webhook `POST /runtime-events` | (a) shared JSONL; (b) direct Postgres read | Webhook is a mirror of Append, preserves durable record; direct Postgres couples sidecar to gateway DB |
| 3 | Trigger events | Reuse `Component="workflow"` + `Action ∈ {run_completed, run_failed}` | (a) new `Component="AgentSelection"` events; (b) push all events | Reuse existing event sources; filtering reduces noise |
| 4 | Failure strategy | selector fail → fall back; outcome fail → log + drop | (a) throw; (b) configurable policy | Fault isolation at the boundary: selector failure cannot drag down workflow availability |
| 5 | agentId delivery | Sidecar-local `RunIdAgentSelectionCache` keyed by `(runId, stepName)` | (a) embed agentId in metadata; (b) pass in request body | Gateway doesn't know which agent the sidecar picked; sidecar maintains the correlation |
| 6 | Port | Reuse 8080, add `/runtime-events` route | New port | New port adds deployment surface (health check, ingress, port conflicts) |
| 7 | Auth | Shared bearer token via SecretResolver | mTLS | Sidecar and gateway usually share host/cluster network; token suffices; mTLS is a P3+ project |
| 8 | Default state | Both sides `Enabled=false` / `Url=""` | Default on | Default-on would pollute every existing workflow's belief store, breaking current dev usage |

### 5.1 Why "fail and fall back" is mandatory

The decorator can fail three ways in `ResolveInnerClientAsync` (selector returns `Result.Failure`, the chosen agentId is not registered, or the inner client throws), and all three return `defaultClient` rather than throwing. Reasons:

1. **Workflow availability must not be coupled to selector health.** Thompson Sampling is an optimization layer, not a correctness layer; the workflow must complete when the selector fails.
2. **`defaultClient` is always available.** `MockReviewChatClient` is deterministic and always returns a verdict; even in `DirectOpenAI` mode the mock fallback is the safety net—this is the value of "fallback" semantics.
3. **Audit trail stays complete.** Even when the selector fails end-to-end, the workflow still emits `*Completed` events, and `NotifyFailure` / `EmitAuditTrace` run as usual.

### 5.2 Why "decorator" beats "step wrapper"

| Dimension | `SelectorBackedChatClient` decorator | Wrapper per reviewer step |
|---|---|---|
| Intrusiveness | 0 (reviewer code unchanged) | 5 reviewers × DI refactor |
| Streaming API support | Pass through `GetStreamingResponseAsync` | Must implement separately |
| Config reversibility | Toggle `Enabled` off → reverts to P0 | Must roll back step DI |
| Unit-test coverage | 5 decorator tests + 1 E2E | Per-step × N tests |

The decorator separates policy from mechanism: reviewers know "I want chat"; the decorator knows "I want to pick an agent, then chat". This is OpenClaw's consistent boundary discipline—core/gateway doesn't know about Strategos; reviewers don't know about selectors.

### 5.3 Why JSONL stays the durable record, webhook is a mirror

`MafDurableHttpWorkflowRunner.RecordEvent` calls `_events.Append(entry)` first, then fire-and-forget pushes the webhook. JSONL write failure logs a warning, webhook failure logs a warning, but **they don't affect each other**:

- JSONL write fails: monitoring alerts + bypass-based replay path
- Webhook fails: belief store misses one outcome update, but belief stays at prior and the system doesn't crash

If the order were reversed (webhook first, then Append), webhook failure would also skip the JSONL write—the durable record would be lost. This is the P0/P2 hard constraint: **JSONL is the durable record; webhook is best-effort mirror**.

### 5.4 E2E test: belief really updates

[`tests/Integration/SelectorEndToEndTests.cs`](../../../samples/OpenClaw.StrategosWorkflowHost.Tests/Integration/SelectorEndToEndTests.cs) verifies the closed loop:

```csharp
[Fact]
public async Task Selection_Then_Outcome_Webhook_Updates_Thompson_Belief_By_One()
{
    // Real ThompsonSamplingAgentSelector + InMemoryBeliefStore, randomSeed: 42
    var beliefStore = new InMemoryBeliefStore(beliefLogger);
    var selector = new ThompsonSamplingAgentSelector(beliefStore, new TaskCategoryClassifier(), selectorLogger, randomSeed: 42);
    var cache = new RunIdAgentSelectionCache();

    // Two inner clients: one "good", one "bad"
    var decorator = new SelectorBackedChatClient(selector, cache, goodInner, ..., options, ...);

    // 1. First chat call: selector picks agent, decorator routes, cache records
    var firstResponse = await decorator.GetResponseAsync(...);
    var selected = cache.TryGet("run-e2e-1", "SecurityReviewer");

    // 2. Snapshot belief observation count before
    var beforeBelief = (await beliefStore.GetBeliefAsync(selected.Value.AgentId, "General", ct)).Value;
    var beforeObservations = beforeBelief.ObservationCount;

    // 3. Stand up sidecar test server, fire webhook
    var mapper = new AgentOutcomeMapper(cache, ...);
    var receiver = new GatewayEventReceiver(mapper, selector, expectedBearerToken: "secret", ...);
    using var host = new HostBuilder().ConfigureWebHost(web => { /* MapPost /runtime-events */ }).Build();
    await host.StartAsync();

    var entry = new RuntimeEventEntry { /* Component="workflow", Action="run_completed", ... */ };
    using var req = new HttpRequestMessage(HttpMethod.Post, "/runtime-events") { Content = JsonContent.Create(entry) };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
    using var resp = await client.SendAsync(req, ct);

    // 4. Assert: observation count +1, success outcome pulls mean up
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    var afterBelief = (await beliefStore.GetBeliefAsync(selected.Value.AgentId, "General", ct)).Value;
    Assert.Equal(beforeObservations + 1, afterBelief.ObservationCount);
    Assert.True(afterBelief.Mean >= beforeBelief.Mean);
}
```

The test uses real `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`, with `randomSeed: 42` for reproducibility (matching Strategos's own `ThompsonSamplingSelectorTests.cs:42`). The full chain runs:

1. Decorator picks agent + writes cache → 1 selection
2. HTTP webhook triggers `RecordOutcomeAsync` → 1 outcome update
3. Belief observation count goes 0 → 1; mean rises from a successful outcome

This is P2's core acceptance criterion: **"runtime experience actually enters the selector's mind"**, not merely a log line saying "recorded outcome".

## 6. Test strategy and quality gates

| Test file | Test count | Verification points |
|---|---|---|
| `SelectorBackedChatClientTests.cs` | 5 | Routes to selected inner after pick; falls back on selection failure; falls back when inner is missing; streaming API has same behavior; no cache write when `ChatOptions.AdditionalProperties` is absent |
| `RunIdAgentSelectionCacheTests.cs` | 4 | Write/read; FIFO eviction; concurrent-write safety (`ConcurrentDictionary`); miss returns null |
| `AgentOutcomeMapperTests.cs` | 6 | `run_completed` → success; `run_failed` → failure; `run_started`/`response_sent` → null; non-`workflow` components ignored; missing `runId`/`stepName` returns null; cache miss returns null |
| `GatewayEventReceiverTests.cs` | 5 | Valid entry accepted and `RecordOutcomeAsync` called; token mismatch returns 401; dedup by `id`; non-`workflow` components ignored; `RecordOutcomeAsync` throwing does not interrupt subsequent events |
| `Integration/SelectorEndToEndTests.cs` | 1 | End-to-end closed loop: decorator picks → fake chat → simulated webhook → belief observation count +1 |
| `RuntimeEventWebhookTests.cs` (gateway) | 4 | Triggers when configured; URL empty → skip; 5xx retry; body fields (`component`/`action`/`metadata.runId/stepName`) correct |

**Test-side selector**: Unit tests use `StubAgentSelector` (always returns a fixed agentId, records `RecordOutcomeAsync` calls for assertion). End-to-end tests use real `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`.

**LlmMode integration**: In `Mock` mode the default client is `MockReviewChatClient` and `AvailableAgents=["mock"]`, so the selector always picks `mock`—Thompson Sampling runs but observes no difference (same agent succeeding/failing repeatedly leaves the belief curve static). Integration tests replace inner clients with NSubstitute fakes to drive the algorithm: success of the "good" client vs. failure of the "bad" client becomes the belief comparator.

**Test-side position**: None of the tests depend on Strategos selector implementation details—they test through the `IAgentSelector` interface contract. This means selector implementations can be swapped (`ThompsonSamplingAgentSelector` → `UCB1AgentSelector` → `RandomAgentSelector`) without breaking tests. That is the payoff of interface segregation.

## 7. P1: gateway registry dispatches by Kind (landed)

In P0 the gateway only supported a single `maf-durable-http` Kind. Once the host sample stabilized, P1 promoted `strategos-http` to a first-class backend type—whichever Kind the configuration specifies, the gateway dispatches the matching runner. [`AgentWorkflowRegistry`](../../../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs) already dispatches by Kind:

```csharp
internal sealed class AgentWorkflowRegistry : IDisposable
{
    private readonly Dictionary<string, IAgentWorkflowRunner> _runners;

    public AgentWorkflowRegistry(
        GatewayConfig config,
        RuntimeEventStore events,
        ILoggerFactory loggerFactory,
        RuntimeEventWebhook? webhook = null)
    {
        _runners = new Dictionary<string, IAgentWorkflowRunner>(StringComparer.OrdinalIgnoreCase);
        if (!config.Workflows.Enabled) return;

        foreach (var (backendId, backendConfig) in config.Workflows.Backends)
        {
            if (string.IsNullOrWhiteSpace(backendId) || !backendConfig.Enabled) continue;

            var kind = string.IsNullOrWhiteSpace(backendConfig.Kind)
                ? AgentWorkflowBackendKinds.MafDurableHttp
                : backendConfig.Kind.Trim();

            _runners[backendId.Trim()] = kind switch
            {
                AgentWorkflowBackendKinds.MafDurableHttp => new MafDurableHttpWorkflowRunner(
                    backendId.Trim(), backendConfig, events, webhook,
                    loggerFactory.CreateLogger<MafDurableHttpWorkflowRunner>()),
                AgentWorkflowBackendKinds.StrategosHttp => new StrategosHttpWorkflowRunner(
                    backendId.Trim(), backendConfig, events, webhook,
                    loggerFactory.CreateLogger<StrategosHttpWorkflowRunner>()),
                _ => throw new InvalidOperationException(
                    $"Unsupported workflow backend kind '{kind}' for backend '{backendId}'.")
            };
        }
    }
    // ...
}
```

[`StrategosHttpWorkflowRunner`](../../../src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs) uses composition rather than inheritance—because `MafDurableHttpWorkflowRunner` is `internal sealed` and cannot be subclassed:

```csharp
internal sealed class StrategosHttpWorkflowRunner : IAgentWorkflowRunner, IDisposable
{
    private readonly MafDurableHttpWorkflowRunner _inner;

    public StrategosHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        RuntimeEventWebhook? webhook,
        ILogger<StrategosHttpWorkflowRunner> logger)
    {
        _inner = new MafDurableHttpWorkflowRunner(
            backendId, config, events, webhook,
            NullLogger<MafDurableHttpWorkflowRunner>.Instance);
        BackendId = _inner.BackendId;
        WorkflowId = _inner.WorkflowId;
    }

    public AgentWorkflowBackendSummary GetSummary()
    {
        // Re-tag Kind: inner still speaks maf-durable-http on the wire, but
        // summary reports "strategos-http" so observers see the configured kind.
        var innerSummary = _inner.GetSummary();
        return new AgentWorkflowBackendSummary
        {
            Id = innerSummary.Id,
            Kind = AgentWorkflowBackendKinds.StrategosHttp,  // ← key override
            WorkflowName = innerSummary.WorkflowName,
            DisplayName = innerSummary.DisplayName,
            Enabled = innerSummary.Enabled,
        };
    }

    public Task<AgentWorkflowRunResult> RunAsync(...)
        => _inner.RunAsync(...);
    public Task<AgentWorkflowRunSnapshot> GetAsync(...)
        => _inner.GetAsync(...);
    public Task<AgentWorkflowRunSnapshot> RespondAsync(...)
        => _inner.RespondAsync(...);
    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(...)
        => _inner.StreamAsync(...);
    public void Dispose() => _inner.Dispose();
}
```

### 7.1 P1 design motivation

Why a separate `strategos-http` Kind? The on-the-wire contract is byte-for-byte identical (still the `maf-durable-http` three-endpoint shape), but the **external semantics** differ:

- **Observability**: Summary / events / runtime metadata report `Kind="strategos-http"`, so operators and UIs can distinguish "generic durable-http backend" from "Strategos-persistence backend".
- **Forward evolution**: Strategos-specific protocol features (querying ontology, retrieving Evidence Bundles) can be layered on the `"strategos-http"` runner later without polluting the generic `"maf-durable-http"` path.
- **Zero intrusion**: Already-deployed `maf-durable-http` configurations need no change; adding a new backend type is one extra `switch` arm in the registry.

### 7.2 P1 × P2 collaboration

P2's webhook injection treats both Kinds identically. `AgentWorkflowRegistry` receives `RuntimeEventWebhook?` at construction and passes it to both runners—`MafDurableHttpWorkflowRunner` directly, `StrategosHttpWorkflowRunner` via its inner. This means:

- `Kind=maf-durable-http` + `OpenClaw:RuntimeEvents:Webhook:Url=...` → webhook fires straight to sidecar
- `Kind=strategos-http` + same config → identical webhook behavior, except `GetSummary().Kind` reports `"strategos-http"`

The "seam discipline" preserved by P0/P2 still holds after P1—`StrategosHttpWorkflowRunner` is a **pure tagging layer**, all HTTP/JSONL/webhook behavior comes from `MafDurableHttpWorkflowRunner`.

### 7.3 P1 non-goals

`StrategosHttpWorkflowRunner.GetSummary()` only re-tags the Kind field. It does **not** additionally provide:

- Strategos-specific summary fields (saga type, Wolverine handler list, Marten projections)
- Strategos-specific runtime APIs (query current saga state, list in-flight workflows)
- Strategos-specific Evidence Bundle endpoints (those land with P2a)

These belong to P1.1+ scope—current P1 strictly follows "compose, don't extend", avoiding duplicating maf-durable-http implementation details on the gateway side.

## 8. Future work and boundaries

| Stage | Scope | Relation to this article |
|---|---|---|
| **P1.1** | `StrategosHttpWorkflowRunner` exposes Strategos-specific summary / runtime endpoints | Builds on the P1 tagging layer; does not touch `MafDurableHttpWorkflowRunner` |
| **P2a** (partially landed) | Evidence Bundle ↔ Marten cross-link; align `status` response `OutputPayload`/`Events` with `EmitAuditTrace` step | Adapter layer is the seam; `FetchStreamAsync` is already in place |
| **P2b** (landed) | Ontology MCP App: register the host's ontology MCP server as an OpenClaw MCP App | `OntologyServerBootstrap` + `OntologyAppManifestWriter` |
| **P3** | Host AOT publishing; in-process embedding | Wait for Wolverine/Marten AOT to stabilize |
| **P4+** | mTLS auth replacing bearer tokens; Redis-backed selection cache (cross-process persistence) | Not needed at current scale |

## 9. Lessons

The P0 + P2 combination shows how **seam discipline + feedback loop** introduces learning into workflows without breaking AOT boundaries:

- **Seam discipline.** Gateway only knows `maf-durable-http`; the sidecar speaks the same contract; reviewers only know `IChatClient`; the decorator picks an agent outside that boundary. Every layer is independently replaceable.
- **Failure isolation.** Selector/webhook/cache failures are absorbed by three strategies—fall back to default, silent skip, log-and-drop—and never pollute workflow availability.
- **Mirror, not master.** JSONL is the durable record; webhook is best-effort mirror. The Append-before-Send order guarantees webhook failures do not affect JSONL.
- **Off by default.** Both Thompson Sampling and webhook are off by default. When off, behavior matches P0/P1 exactly—a zero-impact evolution path for contributors.
- **Observable.** Belief observation count, mean, and observation count are all readable; the E2E test asserts `ObservationCount + 1`, turning "runtime experience enters the selector's mind" into a verifiable fact.

This design pattern—**sidecar carries real persistence, decorator carries policy choice, HTTP webhook carries feedback loop, JSONL remains the durable master record**—generalizes to other agent learning scenarios: RAG retriever online learning, planner policy updates, tool-selection Thompson Sampling can all reuse the same framework.

---

## Appendix A: file inventory

### Sidecar (`samples/OpenClaw.StrategosWorkflowHost/`)

| File | Role | Stage |
|---|---|---|
| `Program.cs` | WebApplication + UseWolverine + 3 endpoints + IChatClient registration + Selector wiring | P0 + P2 |
| `Configuration/LlmMode.cs` | Mock/DirectOpenAI/BackThroughGateway modes + IChatClient factory | P0 |
| `Configuration/MockReviewChatClient.cs` | Fixed-verdict mock client | P0 |
| `Configuration/SelectorOptions.cs` | Thompson Sampling config: Enabled/AvailableAgents/TaskCategory/InnerClients/CacheSize/Webhook | P2 |
| `Configuration/SelectorServerBootstrap.cs` | Wiring: register selector/cache/mapper/receiver/decorator + MapSelectorEventEndpoint | P2 |
| `Configuration/OntologyGraphFactory.cs` / `OntologyOptions.cs` / `OntologyServerBootstrap.cs` / `Adapters/OntologyAppManifest*.cs` | P2b MCP App | P2b |
| `Workflows/ReviewState.cs` | Event-sourced state + ApplyEvent fold | P0 |
| `Workflows/ReviewWorkflow.cs` | Strategos DSL: Fork/Join/RequireConfidence/AwaitApproval/Compensate/Finally | P0 |
| `Workflows/Models/ReviewVerdict.cs` / `HumanDecision.cs` | DTOs | P0 |
| `Workflows/ApproverMarker.cs` | `Operator` / `Admin` self-declared markers | P0 |
| `Steps/PlanExecutor.cs` / `SecurityReviewer.cs` / `ArchitectureReviewer.cs` / `CostReviewer.cs` / `AggregateReviews.cs` / `AssessConfidence.cs` / `ExecuteApprovedAction.cs` / `RevertApprovedAction.cs` / `EmitAuditTrace.cs` / `NotifyFailure.cs` / `RequestHumanReview.cs` / `PromptBuilders.cs` | Hand-written `IWorkflowStep<ReviewState>` step classes | P0 |
| `Adapters/DurableHttpAdapter.cs` | Three endpoints ↔ saga commands/state | P0 |
| `Adapters/PhaseStatusMap.cs` | Strategos phase → OpenClaw status (pure function) | P0 |
| `Adapters/PendingInputBuilder.cs` | `AwaitingApproval` → `AgentWorkflowPendingInput` | P0 |
| `Adapters/EvidenceBundleParser.cs` | Evidence parsing (P2a) | P2a |
| `Adapters/SelectorBackedChatClient.cs` | IChatClient decorator: pick agent + route | P2 |
| `Adapters/RunIdAgentSelectionCache.cs` | `(runId, stepName)` in-memory cache | P2 |
| `Adapters/AgentOutcomeMapper.cs` | `RuntimeEventEntry` → `(agentId, category, outcome)` pure function | P2 |
| `Adapters/GatewayEventReceiver.cs` | `POST /runtime-events` endpoint | P2 |
| `tests/SelectorBackedChatClientTests.cs` | Decorator 5 tests | P2 |
| `tests/RunIdAgentSelectionCacheTests.cs` | Cache 4 tests | P2 |
| `tests/AgentOutcomeMapperTests.cs` | Mapper 6 tests | P2 |
| `tests/GatewayEventReceiverTests.cs` | Receiver 5 tests | P2 |
| `tests/Integration/SelectorEndToEndTests.cs` | End-to-end closed-loop test | P2 |

### Gateway (`src/OpenClaw.Gateway/`)

| File | Role | Stage |
|---|---|---|
| `Workflows/MafDurableHttpWorkflowRunner.cs` | Existing `maf-durable-http` backend runner; extended `RecordEvent` with `stepName`/`score`; new `RecordStepEvents` strips stepName from saga `*Completed/*Failed` events | P2 |
| `Workflows/AgentWorkflowRegistry.cs` | Backend-type registry: dispatches by Kind (maf-durable-http / strategos-http) | P0 + P1 |
| `Workflows/StrategosHttpWorkflowRunner.cs` | P1 alias runner (composition, not inheritance) | P1 (landed) |
| `RuntimeEventStore.cs` | JSONL Append and Query (unchanged) | P0 |
| `RuntimeEventWebhook.cs` | Outbound webhook client (5xx retry, 401/403 stop) | P2 |
| `Composition/RuntimeEventWebhookExtensions.cs` | `AddRuntimeEventWebhook` DI wiring | P2 |
| `Composition/CoreServicesExtensions.cs` | `AddRuntimeEventWebhook` invocation site | P2 |

## Appendix B: verification record

P0's 8 Strategos API verification items were fully resolved by 4 parallel research agents plus direct reads of `E:/GitHub/strategos` source:

1. **NuGet packages + namespaces** — `LevelUp.Strategos.*` (MinVer, 2.10.0); C# namespace root `Strategos`.
2. **`Workflow<T>.Create()` builder API** — returns `IWorkflowBuilder<TState>`; `Fork(params Action<IForkPathBuilder>[])`, `Join<T>` on `IForkJoinBuilder`, `RequireConfidence(double)`, `OnLowConfidence(Action<IBranchBuilder>)`, `Compensate<T>` on `IStepConfiguration`, `OnFailure` must precede `Finally`.
3. **Step model** — `AgentStepBase<TState,TResult>` is sealed and not inheritable; steps are hand-written `IWorkflowStep<TState>` (`ExecuteAsync(TState, StepContext, CancellationToken) -> Task<StepResult<TState>>`), primary-constructor DI; `StepContext` is non-generic, no `Services`/`RaiseAsync`.
4. **State / events / reduction** — event-sourced mode uses `IEventSourcedState<TState>.ApplyEvent` + Marten single-stream aggregation; `[Append]`/`[Merge]` are for document mode (**no `[Snapshot]`**).
5. **Wolverine/Marten** — `builder.Host.UseWolverine` + `AddMarten(...).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup()` + generator `Add{Pascal}Workflow()`; `IMessageBus.PublishAsync` for commands; `LoadAsync` + `FetchStreamAsync` for reads (not `AggregateStreamAsync`).
6. **Approval resumption** — `Resume{Point}ApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision, string? SelectedOptionId, string? Instructions)`; `ApprovalDecision` is an enum (`Approved/Rejected/Deferred`).
7. **OpenClaw constants** — `AgentWorkflowStatuses.*` lowercase (`queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`); `AgentWorkflowBackendKinds.MafDurableHttp="maf-durable-http"`; `CoreJsonContext` registers all workflow DTOs; `SecretResolver` supports `env:`/`raw:` (no `ref:`).
8. **`OperationStatusResponse`** — `OpenClaw.Core.Models`, fields `Success`/`Message`/`Error`/`Mode` (mutable `set`). 404 responses reuse this type.

P2 related items:

- **`Strategos.Selection` types** (`AgentSelectionContext`, `AgentSelection`, `AgentOutcome`, `TaskCategoryClassifier`, `TaskCategory`, `InMemoryBeliefStore`, `ThompsonSamplingAgentSelector`) — all consumed from the `LevelUp.Strategos.Infrastructure.Selection` namespace.
- **`IAgentSelector`** interface lives in `Strategos.Abstractions`. Method signatures: `SelectAgentAsync(AgentSelectionContext, CancellationToken) -> Task<Result<AgentSelection>>` and `RecordOutcomeAsync(string agentId, string taskCategory, AgentOutcome outcome, CancellationToken) -> Task<Result<Unit>>`.
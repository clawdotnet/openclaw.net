# MAF AOT/JIT HTTP Tool Findings

Generated on 2026-03-11T10:56:05.677567-07:00 from the published-binary HTTP/tool matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | HTTP turn ms | Provider requests | Tokens in/out | LLM action counts |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| jit | native | 1270 | 254 | 2 | 40/18 | request_completed=2, request_started=2, route_selected=2 |
| jit | maf | 1260 | 240 | 2 | 40/18 | request_completed=2, request_started=2, route_selected=2 |
| aot | native | 1136 | 40 | 2 | 40/18 | request_completed=2, request_started=2, route_selected=2 |
| aot | maf | 1114 | 47 | 2 | 40/18 | request_completed=2, request_started=2, route_selected=2 |

## Runtime Comparisons

- `jit`: maf startup `1260` ms vs native `1270` ms; HTTP turn `240` ms vs `254` ms; provider requests `2` vs `2`; tokens in/out `40/18` vs `40/18`; startup delta `-10` ms; turn delta `-14` ms; event shape matched: `request_completed=2, request_started=2, route_selected=2`.
- `aot`: maf startup `1114` ms vs native `1136` ms; HTTP turn `47` ms vs `40` ms; provider requests `2` vs `2`; tokens in/out `40/18` vs `40/18`; startup delta `-22` ms; turn delta `+7` ms; event shape matched: `request_completed=2, request_started=2, route_selected=2`.

## Notes

- All four cells persisted the `memory` tool result and reported the expected orchestrator and effective runtime mode through `admin/summary`.
- All four cells emitted coherent `llm` runtime events through `admin/events` with correlated `route_selected`, `request_started`, and `request_completed` actions.
- These measurements come from one deterministic fake-provider scenario. They are useful for fit/comparison, not as absolute performance claims.

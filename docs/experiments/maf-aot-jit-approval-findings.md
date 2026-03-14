# MAF AOT/JIT Approval Findings

Generated on 2026-03-11T11:13:52.556133-07:00 from the published-binary websocket approval matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | Approval flow ms | Decision source | Approval events | LLM action counts |
| --- | --- | ---: | ---: | --- | --- | --- |
| jit | native | 1255 | 230 | chat | requested=1 | request_completed=2, request_started=2, route_selected=2 |
| jit | maf | 1282 | 298 | chat | requested=1 | request_completed=2, request_started=2, route_selected=2 |
| aot | native | 1158 | 168 | chat | requested=1 | request_completed=2, request_started=2, route_selected=2 |
| aot | maf | 1150 | 137 | chat | requested=1 | request_completed=2, request_started=2, route_selected=2 |

## Runtime Comparisons

- `jit`: maf startup `1282` ms vs native `1255` ms; approval flow `298` ms vs `230` ms; provider requests `2` vs `2`; approval events `requested=1` vs `requested=1`; startup delta `+27` ms; approval delta `+68` ms.
- `aot`: maf startup `1150` ms vs native `1158` ms; approval flow `137` ms vs `168` ms; provider requests `2` vs `2`; approval events `requested=1` vs `requested=1`; startup delta `-8` ms; approval delta `-31` ms.

## Notes

- All four cells produced a real approval request over the websocket channel, accepted the decision, and completed the protected `memory` tool path.
- Approval history showed `created` and `decision` entries for every run, and runtime events showed `approval.requested` plus coherent `llm` action groups.
- These measurements are comparative smoke metrics from one deterministic scenario, not throughput benchmarks.

# MAF AOT/JIT Responses Failed Findings

Generated on 2026-03-11T16:04:14.275320-07:00 from the published-binary `/v1/responses` failure matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | Failed turn ms | Provider requests | Transport requests | Provider errors | `response.failed` events | Function-call outputs | LLM action counts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| jit | native | 1314 | 235 | 2 | 2 | 1 | 1 | 1 | route_selected=2, stream_completed=1, stream_failed=1, stream_started=2 |
| jit | maf | 1201 | 255 | 2 | 2 | 1 | 1 | 1 | route_selected=2, stream_completed=1, stream_failed=1, stream_started=2 |
| aot | native | 1112 | 62 | 2 | 2 | 1 | 1 | 1 | route_selected=2, stream_completed=1, stream_failed=1, stream_started=2 |
| aot | maf | 1122 | 57 | 2 | 2 | 1 | 1 | 1 | route_selected=2, stream_completed=1, stream_failed=1, stream_started=2 |

## Runtime Comparisons

- `jit`: maf startup `1201` ms vs native `1314` ms; failed responses turn `255` ms vs `235` ms; provider requests `2` vs `2`; transport requests `2` vs `2`; provider errors `1` vs `1`; failed event counts `response.failed=1` vs `response.failed=1`; startup delta `-113` ms; turn delta `+20` ms; LLM event shape matched: `route_selected=2, stream_completed=1, stream_failed=1, stream_started=2`.
- `aot`: maf startup `1122` ms vs native `1112` ms; failed responses turn `57` ms vs `62` ms; provider requests `2` vs `2`; transport requests `2` vs `2`; provider errors `1` vs `1`; failed event counts `response.failed=1` vs `response.failed=1`; startup delta `+10` ms; turn delta `-5` ms; LLM event shape matched: `route_selected=2, stream_completed=1, stream_failed=1, stream_started=2`.

## Notes

- All four cells returned a real `/v1/responses` SSE failure stream with `response.created`, `response.in_progress`, and `response.failed`, and no `response.completed`.
- The failure case is deterministic at the client contract level: the fake upstream starts failing once the tool-result follow-up stream begins, so the failed response still contains both `function_call` and `function_call_output` items.
- `Provider requests` reflects the gateway's logical LLM attempts from `admin/summary`, while `Transport requests` reflects raw fake-upstream HTTP request count and may be higher if the underlying client retries after the failure begins.
- All four cells emitted contiguous `sequence_number` values and recorded coherent `stream_failed` runtime events in `admin/events`.

# MAF AOT/JIT HTTP Stream Findings

Generated on 2026-03-11T11:55:34.745295-07:00 from the published-binary HTTP/SSE stream matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | SSE turn ms | Provider requests | Tokens in/out | Text chunks | LLM action counts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| jit | native | 1261 | 171 | 1 | 2143/4 | 2 | route_selected=1, stream_completed=1, stream_started=1 |
| jit | maf | 1251 | 255 | 1 | 2138/4 | 2 | route_selected=1, stream_completed=1, stream_started=1 |
| aot | native | 1110 | 93 | 1 | 2143/4 | 2 | route_selected=1, stream_completed=1, stream_started=1 |
| aot | maf | 1123 | 92 | 1 | 2138/4 | 2 | route_selected=1, stream_completed=1, stream_started=1 |

## Runtime Comparisons

- `jit`: maf startup `1251` ms vs native `1261` ms; SSE turn `255` ms vs `171` ms; provider requests `1` vs `1`; tokens in/out `2138/4` vs `2143/4`; startup delta `-10` ms; turn delta `+84` ms; stream text matched: `Hello streaming`; event shape matched: `route_selected=1, stream_completed=1, stream_started=1`.
- `aot`: maf startup `1123` ms vs native `1110` ms; SSE turn `92` ms vs `93` ms; provider requests `1` vs `1`; tokens in/out `2138/4` vs `2143/4`; startup delta `+13` ms; turn delta `-1` ms; stream text matched: `Hello streaming`; event shape matched: `route_selected=1, stream_completed=1, stream_started=1`.

## Notes

- All four cells returned a real OpenAI-compatible SSE response with an assistant role chunk, deterministic text deltas, a final `finish_reason=stop` chunk, and a terminal `[DONE]` sentinel.
- All four cells reported the expected orchestrator and effective runtime mode through `admin/summary` and emitted coherent streaming `llm` runtime events through `admin/events`.
- The fake upstream stream does not report token usage, so the input-token figures shown here come from local estimation paths. Native and MAF currently estimate streamed input tokens differently, which makes those numbers useful for internal accounting review but not for direct parity claims.
- These measurements come from one deterministic fake-provider scenario. They are useful for comparison and observability parity, not as absolute performance claims.

# MAF AOT/JIT HTTP Stream Tool Findings

Generated on 2026-03-11T12:29:32.334144-07:00 from the published-binary HTTP/SSE stream-tool matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | Stream-tool turn ms | Provider requests | Tokens in/out | Tool call chunks | Tool result chunks | LLM action counts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| jit | native | 1384 | 305 | 2 | 4332/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| jit | maf | 1272 | 428 | 2 | 4322/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| aot | native | 1123 | 190 | 2 | 4332/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| aot | maf | 1211 | 213 | 2 | 4322/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |

## Runtime Comparisons

- `jit`: maf startup `1272` ms vs native `1384` ms; stream-tool turn `428` ms vs `305` ms; provider requests `2` vs `2`; tokens in/out `4322/5` vs `4332/5`; tool results `1` vs `1`; startup delta `-112` ms; turn delta `+123` ms; final text matched: `Tool streamed: abc`; event shape matched: `route_selected=2, stream_completed=2, stream_started=2`.
- `aot`: maf startup `1211` ms vs native `1123` ms; stream-tool turn `213` ms vs `190` ms; provider requests `2` vs `2`; tokens in/out `4322/5` vs `4332/5`; tool results `1` vs `1`; startup delta `+88` ms; turn delta `+23` ms; final text matched: `Tool streamed: abc`; event shape matched: `route_selected=2, stream_completed=2, stream_started=2`.

## Notes

- All four cells returned a real OpenAI-compatible SSE response that included assistant role setup, streamed tool-call deltas, the additive `openclaw_tool_result` extension chunk, final assistant text, and a terminal `[DONE]` sentinel.
- The stream-tool smoke uses the env-gated `stream_echo` smoke tool (`OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL=1`) so the proof stays isolated from normal built-in tool exposure.
- All four cells issued two upstream streaming requests, proving the same gateway surface can span streamed tool orchestration and final assistant synthesis in both native and MAF lanes.

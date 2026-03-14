# MAF AOT/JIT Responses Stream Tool Findings

Generated on 2026-03-11T14:13:51.224020-07:00 from the published-binary `/v1/responses` stream-tool matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | Responses turn ms | Provider requests | Tokens in/out | Function call items | Function-call outputs | LLM action counts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| jit | native | 1229 | 325 | 2 | 4368/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| jit | maf | 1282 | 383 | 2 | 4358/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| aot | native | 1158 | 155 | 2 | 4368/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |
| aot | maf | 1184 | 161 | 2 | 4358/5 | 1 | 1 | route_selected=2, stream_completed=2, stream_started=2 |

## Runtime Comparisons

- `jit`: maf startup `1282` ms vs native `1229` ms; responses stream-tool turn `383` ms vs `325` ms; provider requests `2` vs `2`; tokens in/out `4358/5` vs `4368/5`; function_call_output items `1` vs `1`; startup delta `+53` ms; turn delta `+58` ms; final text matched: `Tool streamed: abc`; LLM event shape matched: `route_selected=2, stream_completed=2, stream_started=2`.
- `aot`: maf startup `1184` ms vs native `1158` ms; responses stream-tool turn `161` ms vs `155` ms; provider requests `2` vs `2`; tokens in/out `4358/5` vs `4368/5`; function_call_output items `1` vs `1`; startup delta `+26` ms; turn delta `+6` ms; final text matched: `Tool streamed: abc`; LLM event shape matched: `route_selected=2, stream_completed=2, stream_started=2`.

## Notes

- All four cells returned a real Responses API SSE stream with `response.created`, `response.in_progress`, `response.output_item.added`, `response.function_call_arguments.delta`, `response.output_text.delta`, and `response.completed`, and now surface tool results as standard `function_call_output` items keyed by `call_id`.
- Every streamed event now carries a contiguous `sequence_number`; the published matrix observed stable `1..20` numbering in all four cells.
- The additive `response.openclaw_tool_delta` and `response.openclaw_tool_result` events remain in place only for progressive tool-output visibility during internal execution.
- The proof stays isolated behind the env-gated `stream_echo` smoke tool (`OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL=1`).
- The same gateway observability checks apply here as in the chat-completions matrix: provider usage, recent turn linkage, and coherent `route_selected/stream_started/stream_completed` LLM event groups.

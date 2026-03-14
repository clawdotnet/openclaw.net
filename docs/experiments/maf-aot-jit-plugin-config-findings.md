# MAF AOT/JIT HTTP Plugin Config Findings

Generated on 2026-03-11T17:09:59.531812-07:00 from the published-binary HTTP/plugin-config matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | HTTP turn ms | Provider requests | Plugin id | Plugin tools | Diagnostics | LLM action counts |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | --- |
| jit | native | 1196 | 167 | 2 | bridge-note-plugin | 1 | 0 | request_completed=2, request_started=2, route_selected=2 |
| jit | maf | 1210 | 252 | 2 | bridge-note-plugin | 1 | 0 | request_completed=2, request_started=2, route_selected=2 |
| aot | native | 1154 | 54 | 2 | bridge-note-plugin | 1 | 0 | request_completed=2, request_started=2, route_selected=2 |
| aot | maf | 1076 | 45 | 2 | bridge-note-plugin | 1 | 0 | request_completed=2, request_started=2, route_selected=2 |

## Runtime Comparisons

- `jit`: maf startup `1210` ms vs native `1196` ms; HTTP plugin turn `252` ms vs `167` ms; provider requests `2` vs `2`; plugin diagnostics `0` vs `0`; LLM event shape `request_completed=2, request_started=2, route_selected=2` vs `request_completed=2, request_started=2, route_selected=2`; startup delta `+14` ms; turn delta `+85` ms.
- `aot`: maf startup `1076` ms vs native `1154` ms; HTTP plugin turn `45` ms vs `54` ms; provider requests `2` vs `2`; plugin diagnostics `0` vs `0`; LLM event shape `request_completed=2, request_started=2, route_selected=2` vs `request_completed=2, request_started=2, route_selected=2`; startup delta `-78` ms; turn delta `-9` ms.

## Notes

- All four cells loaded the same bridge plugin and completed a real plugin-backed tool invocation with file-bound `Plugins:Entries:*:Config` instead of the earlier env-var shortcut.
- The plugin artifact path came from `api.config.outputDir` in every cell, and the plugin diagnostics stayed empty across all four runs.
- The gateway still emitted the same `admin/summary` plugin report shape and coherent `llm` runtime events through `admin/events` for native and MAF.
- These measurements are comparative smoke metrics from one deterministic scenario, not throughput benchmarks.

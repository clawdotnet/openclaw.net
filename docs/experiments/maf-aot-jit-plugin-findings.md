# MAF AOT/JIT HTTP Plugin Findings

Generated on 2026-03-11T11:30:59.971866-07:00 from the published-binary HTTP/plugin-tool matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | HTTP turn ms | Provider requests | Plugin id | Plugin tools | LLM action counts |
| --- | --- | ---: | ---: | ---: | --- | ---: | --- |
| jit | native | 1737 | 420 | 2 | bridge-note-plugin | 1 | request_completed=2, request_started=2, route_selected=2 |
| jit | maf | 1198 | 205 | 2 | bridge-note-plugin | 1 | request_completed=2, request_started=2, route_selected=2 |
| aot | native | 1163 | 61 | 2 | bridge-note-plugin | 1 | request_completed=2, request_started=2, route_selected=2 |
| aot | maf | 1196 | 66 | 2 | bridge-note-plugin | 1 | request_completed=2, request_started=2, route_selected=2 |

## Runtime Comparisons

- `jit`: maf startup `1198` ms vs native `1737` ms; HTTP plugin turn `205` ms vs `420` ms; provider requests `2` vs `2`; plugin loaded `bridge-note-plugin` vs `bridge-note-plugin`; startup delta `-539` ms; turn delta `-215` ms; event shape matched: `request_completed=2, request_started=2, route_selected=2`.
- `aot`: maf startup `1196` ms vs native `1163` ms; HTTP plugin turn `66` ms vs `61` ms; provider requests `2` vs `2`; plugin loaded `bridge-note-plugin` vs `bridge-note-plugin`; startup delta `+33` ms; turn delta `+5` ms; event shape matched: `request_completed=2, request_started=2, route_selected=2`.

## Notes

- All four cells loaded the same bridge plugin and completed a real plugin-backed tool invocation through the published HTTP surface.
- The plugin wrote its artifact file to disk in every cell, and `admin/summary` reported the plugin load with the expected single tool registration.
- All four cells emitted coherent `llm` runtime events through `admin/events` with correlated `route_selected`, `request_started`, and `request_completed` actions.
- This matrix uses an environment variable for the plugin output path. Undefined plugin-config payloads are now normalized to null to avoid bridge startup failures, but rich file-bound `Plugins:Entries:*:Config` remains a separate compatibility check.
- These measurements come from one deterministic fake-provider scenario. They are comparative smoke metrics, not throughput benchmarks.

# MAF AOT/JIT Restart Findings

Generated on 2026-03-11T16:49:38.751201-07:00 from the published-binary websocket restart/resume matrix.

## Matrix

| Runtime | Orchestrator | Startup ms | Turn 1 ms | Resume turn ms | Fallback turn ms | Provider requests | Session history | Sidecar restore | Sidecar fallback |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| jit | native | 1322 | 405 | 377 | 388 | 4 | 7 | not_applicable | not_applicable |
| jit | maf | 1194 | 452 | 439 | 429 | 4 | 7 | logged | logged |
| aot | native | 1070 | 240 | 228 | 238 | 4 | 7 | not_applicable | not_applicable |
| aot | maf | 1104 | 232 | 241 | 243 | 4 | 7 | logged | logged |

## Runtime Comparisons

- `jit`: maf startup `1194` ms vs native `1322` ms; resume turn `439` ms vs `377` ms; fallback turn `429` ms vs `388` ms; history count `7` vs `7`; MAF sidecar restore `logged` and fallback `logged`.
- `aot`: maf startup `1104` ms vs native `1070` ms; resume turn `241` ms vs `228` ms; fallback turn `243` ms vs `238` ms; history count `7` vs `7`; MAF sidecar restore `logged` and fallback `logged`.

## Notes

- All four cells kept the same logical websocket session id across process restarts and preserved multi-turn continuity at the published-binary surface.
- The MAF cells additionally proved sidecar-backed resume on the second run and canonical-history fallback after deliberate sidecar corruption on the third run.
- Native cells do not use a MAF sidecar, so their restore/fallback columns are intentionally marked `not_applicable`.

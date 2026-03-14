# MAF AOT/JIT Approval Outcome Findings

| Runtime | Orchestrator | Startup ms | WS deny ms | HTTP admin deny ms | WS requester mismatch + admin drain ms | WS timeout ms | Approval actions |
|---|---|---:|---:|---:|---:|---:|---|
| `jit` | `native` | 1200 | 301 | 499 | 590 | 5106 | `requested=4, decision_recorded=3, decision_rejected=1, timed_out=1` |
| `jit` | `maf` | 2295 | 306 | 499 | 569 | 5207 | `requested=4, decision_recorded=3, decision_rejected=1, timed_out=1` |
| `aot` | `native` | 1145 | 136 | 466 | 569 | 5152 | `requested=4, decision_recorded=3, decision_rejected=1, timed_out=1` |
| `aot` | `maf` | 1142 | 141 | 509 | 641 | 5120 | `requested=4, decision_recorded=3, decision_rejected=1, timed_out=1` |

## Findings

- All four cells passed the same negative approval matrix: websocket deny, HTTP admin deny, websocket requester-mismatch followed by admin drain, and websocket timeout.
- Approval history recorded the expected decision sources across all cells: `chat`, `http_admin`, and `timeout`.
- Runtime events exposed the same branch signals in every cell: `approval.requested`, `approval.decision_recorded`, `approval.decision_rejected`, and `approval.timed_out`.
- No note files were written in any negative approval scenario, confirming that denied or timed-out approvals do not leak tool execution.

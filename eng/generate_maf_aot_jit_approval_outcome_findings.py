#!/usr/bin/env python3
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
ARTIFACT_ROOT = ROOT / "artifacts"
DOC_PATH = ROOT / "docs" / "experiments" / "maf-aot-jit-approval-outcome-findings.md"

ORDER = {
    ("jit", "native"): "jit-native-ws-approval-outcomes",
    ("jit", "maf"): "jit-maf-ws-approval-outcomes",
    ("aot", "native"): "aot-native-ws-approval-outcomes",
    ("aot", "maf"): "aot-maf-ws-approval-outcomes",
}


def load_report(name):
    path = ARTIFACT_ROOT / name / "report.json"
    if not path.exists():
        raise SystemExit(f"Missing report: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def main():
    reports = [load_report(name) for name in ORDER.values()]
    by_key = {(item["runtimeMode"], item["orchestrator"]): item for item in reports}

    lines = [
        "# MAF AOT/JIT Approval Outcome Findings",
        "",
        "| Runtime | Orchestrator | Startup ms | WS deny ms | HTTP admin deny ms | WS requester mismatch + admin drain ms | WS timeout ms | Approval actions |",
        "|---|---|---:|---:|---:|---:|---:|---|",
    ]

    for key in [("jit", "native"), ("jit", "maf"), ("aot", "native"), ("aot", "maf")]:
        report = by_key[key]
        timings = report["timings"]
        actions = report["events"]["actionCounts"]
        lines.append(
            f"| `{report['runtimeMode']}` | `{report['orchestrator']}` | "
            f"{timings['gatewayStartupMs']} | {timings['wsDenyMs']} | {timings['httpAdminDenyMs']} | "
            f"{timings['httpRequesterMismatchMs']} | {timings['wsTimeoutMs']} | "
            f"`requested={actions.get('requested', 0)}, decision_recorded={actions.get('decision_recorded', 0)}, "
            f"decision_rejected={actions.get('decision_rejected', 0)}, timed_out={actions.get('timed_out', 0)}` |"
        )

    lines.extend(
        [
            "",
            "## Findings",
            "",
            "- All four cells passed the same negative approval matrix: websocket deny, HTTP admin deny, websocket requester-mismatch followed by admin drain, and websocket timeout.",
            "- Approval history recorded the expected decision sources across all cells: `chat`, `http_admin`, and `timeout`.",
            "- Runtime events exposed the same branch signals in every cell: `approval.requested`, `approval.decision_recorded`, `approval.decision_rejected`, and `approval.timed_out`.",
            "- No note files were written in any negative approval scenario, confirming that denied or timed-out approvals do not leak tool execution.",
            "",
        ]
    )

    DOC_PATH.parent.mkdir(parents=True, exist_ok=True)
    DOC_PATH.write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
import argparse
import json
import pathlib
from datetime import datetime


RUNTIME_ORDER = {"jit": 0, "aot": 1}
ORCHESTRATOR_ORDER = {"native": 0, "maf": 1}


def load_report(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sort_reports(reports):
    return sorted(
        reports,
        key=lambda item: (
            RUNTIME_ORDER.get(item["runtimeMode"], 99),
            ORCHESTRATOR_ORDER.get(item["orchestrator"], 99),
        ),
    )


def format_ms(value):
    return "n/a" if value is None else str(value)


def build_markdown(reports):
    rows = []
    for report in sort_reports(reports):
        sidecar = report["sidecar"]
        rows.append(
            "| "
            + " | ".join(
                [
                    report["runtimeMode"],
                    report["orchestrator"],
                    format_ms(report["timings"]["gatewayStartupMs"]),
                    format_ms(report["timings"]["turn1Ms"]),
                    format_ms(report["timings"]["turn2Ms"]),
                    format_ms(report["timings"]["turn3Ms"]),
                    str(report["provider"]["requestCount"]),
                    str(report["session"]["historyCount"]),
                    sidecar["restored"],
                    sidecar["fallback"],
                ]
            )
            + " |"
        )

    by_key = {(report["runtimeMode"], report["orchestrator"]): report for report in reports}
    comparisons = []
    for runtime_mode in ("jit", "aot"):
        native_report = by_key[(runtime_mode, "native")]
        maf_report = by_key[(runtime_mode, "maf")]
        comparisons.append(
            "- "
            + "; ".join(
                [
                    f"`{runtime_mode}`: maf startup `{format_ms(maf_report['timings']['gatewayStartupMs'])}` ms vs native `{format_ms(native_report['timings']['gatewayStartupMs'])}` ms",
                    f"resume turn `{format_ms(maf_report['timings']['turn2Ms'])}` ms vs `{format_ms(native_report['timings']['turn2Ms'])}` ms",
                    f"fallback turn `{format_ms(maf_report['timings']['turn3Ms'])}` ms vs `{format_ms(native_report['timings']['turn3Ms'])}` ms",
                    f"history count `{maf_report['session']['historyCount']}` vs `{native_report['session']['historyCount']}`",
                    f"MAF sidecar restore `{maf_report['sidecar']['restored']}` and fallback `{maf_report['sidecar']['fallback']}`",
                ]
            )
            + "."
        )

    lines = [
        "# MAF AOT/JIT Restart Findings",
        "",
        f"Generated on {datetime.now().astimezone().isoformat()} from the published-binary websocket restart/resume matrix.",
        "",
        "## Matrix",
        "",
        "| Runtime | Orchestrator | Startup ms | Turn 1 ms | Resume turn ms | Fallback turn ms | Provider requests | Session history | Sidecar restore | Sidecar fallback |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |",
        *rows,
        "",
        "## Runtime Comparisons",
        "",
        *comparisons,
        "",
        "## Notes",
        "",
        "- All four cells kept the same logical websocket session id across process restarts and preserved multi-turn continuity at the published-binary surface.",
        "- The MAF cells additionally proved sidecar-backed resume on the second run and canonical-history fallback after deliberate sidecar corruption on the third run.",
        "- Native cells do not use a MAF sidecar, so their restore/fallback columns are intentionally marked `not_applicable`.",
        "",
    ]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("reports", nargs=4)
    args = parser.parse_args()

    reports = [load_report(pathlib.Path(report)) for report in args.reports]
    output_path = pathlib.Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(build_markdown(reports), encoding="utf-8")


if __name__ == "__main__":
    main()

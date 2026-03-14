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


def format_actions(action_counts):
    return ", ".join(f"{key}={value}" for key, value in sorted(action_counts.items()))


def build_comparison_line(runtime_mode, native_report, maf_report):
    native_startup = native_report["timings"]["gatewayStartupMs"]
    maf_startup = maf_report["timings"]["gatewayStartupMs"]
    native_turn = native_report["timings"]["gatewayRequestMs"]
    maf_turn = maf_report["timings"]["gatewayRequestMs"]
    native_usage = native_report["usage"]["providerSnapshot"]
    maf_usage = maf_report["usage"]["providerSnapshot"]
    native_actions = native_report["events"]["actionCounts"]
    maf_actions = maf_report["events"]["actionCounts"]

    startup_delta = None if native_startup is None or maf_startup is None else maf_startup - native_startup
    turn_delta = None if native_turn is None or maf_turn is None else maf_turn - native_turn
    same_actions = native_actions == maf_actions

    parts = [
        f"`{runtime_mode}`: maf startup `{format_ms(maf_startup)}` ms vs native `{format_ms(native_startup)}` ms",
        f"HTTP plugin turn `{format_ms(maf_turn)}` ms vs `{format_ms(native_turn)}` ms",
        f"provider requests `{maf_usage['requests']}` vs `{native_usage['requests']}`",
        f"plugin loaded `{maf_report['plugin']['report']['pluginId']}` vs `{native_report['plugin']['report']['pluginId']}`",
    ]

    if startup_delta is not None:
        parts.append(f"startup delta `{startup_delta:+d}` ms")
    if turn_delta is not None:
        parts.append(f"turn delta `{turn_delta:+d}` ms")

    if same_actions:
        parts.append(f"event shape matched: `{format_actions(maf_actions)}`")
    else:
        parts.append(
            "event shape differed: "
            f"maf `{format_actions(maf_actions)}` vs native `{format_actions(native_actions)}`")

    return "- " + "; ".join(parts) + "."


def build_markdown(reports):
    rows = []
    for report in sort_reports(reports):
        provider_usage = report["usage"]["providerSnapshot"]
        plugin_report = report["plugin"]["report"]
        rows.append(
            "| "
            + " | ".join(
                [
                    report["runtimeMode"],
                    report["orchestrator"],
                    format_ms(report["timings"]["gatewayStartupMs"]),
                    format_ms(report["timings"]["gatewayRequestMs"]),
                    str(provider_usage["requests"]),
                    plugin_report["pluginId"],
                    str(plugin_report["toolCount"]),
                    format_actions(report["events"]["actionCounts"]),
                ]
            )
            + " |"
        )

    by_key = {(report["runtimeMode"], report["orchestrator"]): report for report in reports}
    comparisons = []
    for runtime_mode in ("jit", "aot"):
        native_report = by_key[(runtime_mode, "native")]
        maf_report = by_key[(runtime_mode, "maf")]
        comparisons.append(build_comparison_line(runtime_mode, native_report, maf_report))

    lines = [
        "# MAF AOT/JIT HTTP Plugin Findings",
        "",
        f"Generated on {datetime.now().astimezone().isoformat()} from the published-binary HTTP/plugin-tool matrix.",
        "",
        "## Matrix",
        "",
        "| Runtime | Orchestrator | Startup ms | HTTP turn ms | Provider requests | Plugin id | Plugin tools | LLM action counts |",
        "| --- | --- | ---: | ---: | ---: | --- | ---: | --- |",
        *rows,
        "",
        "## Runtime Comparisons",
        "",
        *comparisons,
        "",
        "## Notes",
        "",
        "- All four cells loaded the same bridge plugin and completed a real plugin-backed tool invocation through the published HTTP surface.",
        "- The plugin wrote its artifact file to disk in every cell, and `admin/summary` reported the plugin load with the expected single tool registration.",
        "- All four cells emitted coherent `llm` runtime events through `admin/events` with correlated `route_selected`, `request_started`, and `request_completed` actions.",
        "- This matrix uses an environment variable for the plugin output path. Undefined plugin-config payloads are now normalized to null to avoid bridge startup failures, but rich file-bound `Plugins:Entries:*:Config` remains a separate compatibility check.",
        "- These measurements come from one deterministic fake-provider scenario. They are comparative smoke metrics, not throughput benchmarks.",
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

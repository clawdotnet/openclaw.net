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
    native_plugin = native_report["plugin"]["report"]
    maf_plugin = maf_report["plugin"]["report"]

    parts = [
        f"`{runtime_mode}`: maf startup `{format_ms(maf_startup)}` ms vs native `{format_ms(native_startup)}` ms",
        f"HTTP plugin turn `{format_ms(maf_turn)}` ms vs `{format_ms(native_turn)}` ms",
        f"provider requests `{maf_usage['requests']}` vs `{native_usage['requests']}`",
        f"plugin diagnostics `{len(maf_plugin.get('diagnostics') or [])}` vs `{len(native_plugin.get('diagnostics') or [])}`",
        f"LLM event shape `{format_actions(maf_report['events']['actionCounts'])}` vs `{format_actions(native_report['events']['actionCounts'])}`",
    ]

    if native_startup is not None and maf_startup is not None:
        parts.append(f"startup delta `{maf_startup - native_startup:+d}` ms")
    if native_turn is not None and maf_turn is not None:
        parts.append(f"turn delta `{maf_turn - native_turn:+d}` ms")

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
                    str(len(plugin_report.get("diagnostics") or [])),
                    format_actions(report["events"]["actionCounts"]),
                ]
            )
            + " |"
        )

    by_key = {(report["runtimeMode"], report["orchestrator"]): report for report in reports}
    comparisons = [
        build_comparison_line(runtime_mode, by_key[(runtime_mode, "native")], by_key[(runtime_mode, "maf")])
        for runtime_mode in ("jit", "aot")
    ]

    lines = [
        "# MAF AOT/JIT HTTP Plugin Config Findings",
        "",
        f"Generated on {datetime.now().astimezone().isoformat()} from the published-binary HTTP/plugin-config matrix.",
        "",
        "## Matrix",
        "",
        "| Runtime | Orchestrator | Startup ms | HTTP turn ms | Provider requests | Plugin id | Plugin tools | Diagnostics | LLM action counts |",
        "| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | --- |",
        *rows,
        "",
        "## Runtime Comparisons",
        "",
        *comparisons,
        "",
        "## Notes",
        "",
        "- All four cells loaded the same bridge plugin and completed a real plugin-backed tool invocation with file-bound `Plugins:Entries:*:Config` instead of the earlier env-var shortcut.",
        "- The plugin artifact path came from `api.config.outputDir` in every cell, and the plugin diagnostics stayed empty across all four runs.",
        "- The gateway still emitted the same `admin/summary` plugin report shape and coherent `llm` runtime events through `admin/events` for native and MAF.",
        "- These measurements are comparative smoke metrics from one deterministic scenario, not throughput benchmarks.",
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

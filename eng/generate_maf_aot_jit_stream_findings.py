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
    native_stream = native_report["stream"]
    maf_stream = maf_report["stream"]

    startup_delta = None if native_startup is None or maf_startup is None else maf_startup - native_startup
    turn_delta = None if native_turn is None or maf_turn is None else maf_turn - native_turn
    same_actions = native_actions == maf_actions
    same_chunks = native_stream["textChunks"] == maf_stream["textChunks"]

    parts = [
        f"`{runtime_mode}`: maf startup `{format_ms(maf_startup)}` ms vs native `{format_ms(native_startup)}` ms",
        f"SSE turn `{format_ms(maf_turn)}` ms vs `{format_ms(native_turn)}` ms",
        f"provider requests `{maf_usage['requests']}` vs `{native_usage['requests']}`",
        f"tokens in/out `{maf_usage['inputTokens']}/{maf_usage['outputTokens']}` vs `{native_usage['inputTokens']}/{native_usage['outputTokens']}`",
    ]

    if startup_delta is not None:
        parts.append(f"startup delta `{startup_delta:+d}` ms")
    if turn_delta is not None:
        parts.append(f"turn delta `{turn_delta:+d}` ms")

    if same_chunks:
        parts.append(f"stream text matched: `{maf_stream['fullText']}`")
    else:
        parts.append(
            "stream text differed: "
            f"maf `{maf_stream['fullText']}` vs native `{native_stream['fullText']}`")

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
        rows.append(
            "| "
            + " | ".join(
                [
                    report["runtimeMode"],
                    report["orchestrator"],
                    format_ms(report["timings"]["gatewayStartupMs"]),
                    format_ms(report["timings"]["gatewayRequestMs"]),
                    str(provider_usage["requests"]),
                    f"{provider_usage['inputTokens']}/{provider_usage['outputTokens']}",
                    str(report["stream"]["chunkCount"]),
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
        "# MAF AOT/JIT HTTP Stream Findings",
        "",
        f"Generated on {datetime.now().astimezone().isoformat()} from the published-binary HTTP/SSE stream matrix.",
        "",
        "## Matrix",
        "",
        "| Runtime | Orchestrator | Startup ms | SSE turn ms | Provider requests | Tokens in/out | Text chunks | LLM action counts |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |",
        *rows,
        "",
        "## Runtime Comparisons",
        "",
        *comparisons,
        "",
        "## Notes",
        "",
        "- All four cells returned a real OpenAI-compatible SSE response with an assistant role chunk, deterministic text deltas, a final `finish_reason=stop` chunk, and a terminal `[DONE]` sentinel.",
        "- All four cells reported the expected orchestrator and effective runtime mode through `admin/summary` and emitted coherent streaming `llm` runtime events through `admin/events`.",
        "- The fake upstream stream does not report token usage, so the input-token figures shown here come from local estimation paths. Native and MAF currently estimate streamed input tokens differently, which makes those numbers useful for internal accounting review but not for direct parity claims.",
        "- These measurements come from one deterministic fake-provider scenario. They are useful for comparison and observability parity, not as absolute performance claims.",
        "",
    ]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("reports", nargs=4)
    args = parser.parse_args()

    report_paths = [pathlib.Path(report) for report in args.reports]
    reports = [load_report(path) for path in report_paths]
    output_path = pathlib.Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(build_markdown(reports), encoding="utf-8")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Lightweight history explorer for OpenClaw creator workflows.

Parses an OpenClaw.NET Session JSON snapshot (piped via stdin) and returns
structured analytics: turn summaries, tool usage, meta-skill execution patterns,
and skill/tool co-occurrence data.

This script intentionally uses only stdlib so it can run in constrained
skill_exec environments.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from typing import Any


def _parse_session(stdin_data: str) -> dict[str, Any] | None:
    """Parse a JSON session snapshot from stdin."""
    try:
        return json.loads(stdin_data)
    except (json.JSONDecodeError, TypeError):
        return None


def _collect_turns(
    history: list[dict], query: str, window_days: int, top_k: int
) -> dict[str, Any]:
    """Analyze ChatTurn history."""
    if not history:
        return {"count": 0, "roles": {}, "span_days": 0, "recent": [], "query_hits": []}

    roles: dict[str, int] = {}
    timestamps: list[datetime] = []
    recent: list[dict] = []
    query_hits: list[dict] = []
    query_lower = query.lower().strip() if query else ""

    for turn in history:
        role = str(turn.get("role", "")).lower()
        if role:
            roles[role] = roles.get(role, 0) + 1

        ts_str = turn.get("timestamp", "")
        if ts_str:
            try:
                ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                timestamps.append(ts)
            except (ValueError, TypeError):
                pass

        content = str(turn.get("content", ""))
        if query_lower and query_lower in content.lower():
            query_hits.append({
                "role": role,
                "preview": content[:200],
                "timestamp": ts_str,
            })

    # Recent turns (last N by insertion order, which is chronological)
    recent_turns = history[-top_k:] if len(history) > top_k else history
    for turn in recent_turns:
        recent.append({
            "role": turn.get("role", ""),
            "preview": str(turn.get("content", ""))[:150],
            "timestamp": turn.get("timestamp", ""),
            "tool_count": len(turn.get("toolCalls") or []),
        })

    span_days = 0.0
    if len(timestamps) >= 2:
        span_days = (max(timestamps) - min(timestamps)).total_seconds() / 86400.0

    # Filter by window_days
    now = datetime.now(timezone.utc)
    cutoff = now.timestamp() - (window_days * 86400)
    recent_in_window = sum(
        1 for ts in timestamps if ts.timestamp() >= cutoff
    )

    return {
        "count": len(history),
        "in_window": recent_in_window,
        "roles": roles,
        "span_days": round(span_days, 2),
        "recent": recent,
        "query_hits": query_hits[:top_k],
    }


def _collect_tools(history: list[dict], top_k: int) -> dict[str, Any]:
    """Analyze ToolInvocation data across turns."""
    tool_counts: dict[str, int] = {}
    tool_statuses: dict[str, dict[str, int]] = {}
    total_calls = 0

    for turn in history:
        tool_calls = turn.get("toolCalls") or []
        for call in tool_calls:
            name = str(call.get("toolName", "")).strip()
            if not name:
                continue
            total_calls += 1
            tool_counts[name] = tool_counts.get(name, 0) + 1

            status = str(call.get("resultStatus", "unknown"))
            if name not in tool_statuses:
                tool_statuses[name] = {}
            tool_statuses[name][status] = tool_statuses[name].get(status, 0) + 1

    ranked = sorted(tool_counts.items(), key=lambda x: x[1], reverse=True)[:top_k]
    top_tools = [
        {
            "name": name,
            "count": count,
            "statuses": tool_statuses.get(name, {}),
        }
        for name, count in ranked
    ]

    return {
        "total_calls": total_calls,
        "unique_tools": len(tool_counts),
        "top": top_tools,
    }


def _collect_meta_runs(
    meta_run_history: list[dict], top_k: int
) -> dict[str, Any]:
    """Analyze SessionMetaRunRecord history."""
    if not meta_run_history:
        return {"count": 0, "runs": []}

    skill_counts: dict[str, int] = {}
    statuses: dict[str, int] = {}
    runs: list[dict] = []

    for run in meta_run_history:
        name = str(run.get("skillName", ""))
        status = str(run.get("status", "unknown"))
        if name:
            skill_counts[name] = skill_counts.get(name, 0) + 1
        statuses[status] = statuses.get(status, 0) + 1

        step_results = run.get("stepResults") or []
        step_summaries = []
        failures = []
        for step in step_results:
            step_status = step.get("status", "")
            step_summaries.append({
                "id": step.get("id", ""),
                "kind": step.get("kind", ""),
                "status": step_status,
                "failureCode": step.get("failureCode"),
                "durationMs": step.get("durationMs", 0),
            })
            if step_status == "failed":
                failures.append(step.get("failureCode", "unknown"))

        runs.append({
            "runId": run.get("runId", ""),
            "skillName": name,
            "status": status,
            "startedAtUtc": run.get("startedAtUtc", ""),
            "error": run.get("error"),
            "errorCode": run.get("errorCode"),
            "stepCount": len(step_results),
            "failureCount": len(failures),
            "failureCodes": failures,
        })

    ranked = sorted(skill_counts.items(), key=lambda x: x[1], reverse=True)[:top_k]
    top_skills = [{"name": name, "count": count} for name, count in ranked]

    return {
        "count": len(meta_run_history),
        "statuses": statuses,
        "top_skills": top_skills,
        "recent": runs[-top_k:] if len(runs) > top_k else runs,
    }


def _collect_co_occurrences(history: list[dict], top_k: int) -> list[dict]:
    """Detect skill/tool pairs that appear together in the same turn."""
    counts: dict[tuple[str, str], int] = {}

    for turn in history:
        names: list[str] = []
        tool_calls = turn.get("toolCalls") or []
        for call in tool_calls:
            name = str(call.get("toolName", "")).strip()
            if name:
                names.append(name)

        if len(names) < 2:
            continue

        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                a, b = sorted((names[i], names[j]))
                key = (a, b)
                counts[key] = counts.get(key, 0) + 1

    pairs = sorted(counts.items(), key=lambda x: x[1], reverse=True)[:top_k]
    return [{"pair": [a, b], "count": count} for (a, b), count in pairs]


def _collect_meta_usage_from_co(co_occurrences: list[dict]) -> list[dict]:
    """Derive meta-skill usage counts from co-occurrence data."""
    usage: dict[str, int] = {}
    for entry in co_occurrences:
        pair = entry.get("pair")
        count = int(entry.get("count", 0))
        if not isinstance(pair, list):
            continue
        for name in pair:
            text = str(name)
            if text.startswith("meta-"):
                usage[text] = usage.get(text, 0) + count

    ranked = sorted(usage.items(), key=lambda x: x[1], reverse=True)
    return [{"name": name, "count": count} for name, count in ranked]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="OpenClaw session history explorer"
    )
    parser.add_argument(
        "--query", required=True,
        help="Free-text query to search for in turn content"
    )
    parser.add_argument(
        "--window-days", type=int, default=30,
        help="Time window in days for recency filtering"
    )
    parser.add_argument(
        "--include",
        default="turns,tools,meta_runs,co_occurrences",
        help="Comma-separated analytics keys to include"
    )
    parser.add_argument(
        "--top-k", type=int, default=10,
        help="Maximum number of top-N entries to return"
    )
    args = parser.parse_args(argv)

    include = {
        part.strip()
        for part in str(args.include).split(",")
        if part.strip()
    }

    # Read session JSON from stdin
    stdin_data = sys.stdin.read()
    session = _parse_session(stdin_data)

    result: dict[str, Any] = {
        "query": args.query,
        "window_days": args.window_days,
    }

    if session is None:
        result["placeholder"] = (
            "no session data received; "
            "downstream should rely on user intent only"
        )
        print(json.dumps(result, ensure_ascii=False))
        return 0

    history: list[dict] = session.get("history") or []
    meta_run_history: list[dict] = session.get("metaRunHistory") or []

    if not history and not meta_run_history:
        result["placeholder"] = (
            "session has no turn history or meta-run records; "
            "downstream should rely on user intent only"
        )
        print(json.dumps(result, ensure_ascii=False))
        return 0

    if "turns" in include:
        result["turns"] = _collect_turns(
            history, args.query, args.window_days, args.top_k
        )

    if "tools" in include:
        result["tools"] = _collect_tools(history, args.top_k)

    if "meta_runs" in include:
        result["meta_runs"] = _collect_meta_runs(meta_run_history, args.top_k)

    if "co_occurrences" in include:
        co = _collect_co_occurrences(history, args.top_k)
        result["co_occurrences"] = co
        # Also derive meta-skill usage from co-occurrence data
        meta_usage = _collect_meta_usage_from_co(co)
        if meta_usage:
            result["meta_usage"] = meta_usage

    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

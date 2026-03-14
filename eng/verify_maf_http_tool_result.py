#!/usr/bin/env python3
import argparse
import collections
import json
import pathlib
from datetime import datetime


def extract_text(value):
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        parts = []
        for item in value:
            if isinstance(item, str):
                parts.append(item)
            elif isinstance(item, dict):
                if "text" in item:
                    parts.append(str(item.get("text", "")))
                elif "content" in item:
                    parts.append(extract_text(item.get("content")))
        return "".join(parts)
    if isinstance(value, dict):
        if "text" in value:
            return str(value.get("text", ""))
        if "content" in value:
            return extract_text(value.get("content"))
    return str(value)


def fail(message):
    raise SystemExit(message)


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def verify_response(response_path, expected_result_fragment):
    response = load_json(response_path)
    choices = response.get("choices") or []
    if not choices:
        fail("Gateway response did not include any choices.")

    message = choices[0].get("message") or {}
    content = message.get("content", "")
    if expected_result_fragment not in content:
        fail(
            f"Gateway response did not include the expected tool result fragment "
            f"{expected_result_fragment!r}. Content: {content!r}"
        )
    return {
        "content": content,
        "usage": response.get("usage") or {},
    }


def verify_provider_log(provider_log_path, expected_result_fragment, expected_provider, expected_model):
    entries = [
        json.loads(line)
        for line in provider_log_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(entries) < 2:
        fail(f"Expected at least two upstream provider requests, saw {len(entries)}.")

    tool_messages = []
    for entry in entries:
        body = entry.get("body") or {}
        if body.get("model") != expected_model:
            fail(f"Provider request used unexpected model: {body.get('model')!r}")

        for message in body.get("messages", []) or []:
            if isinstance(message, dict) and message.get("role") == "tool":
                tool_messages.append(extract_text(message.get("content")))

    if not any(expected_result_fragment in message for message in tool_messages):
        fail(f"Upstream provider log did not capture the expected tool result: {tool_messages!r}")

    if expected_provider != "openai-compatible":
        fail(f"Unexpected provider assertion input: {expected_provider!r}")

    return {
        "requestCount": len(entries),
        "paths": sorted({entry.get("path", "") for entry in entries if entry.get("path")}),
        "toolResultMessages": tool_messages,
    }


def verify_artifact(notes_dir, artifact_path, expected_artifact_content):
    if artifact_path is not None:
        if not artifact_path.exists():
            fail(f"Expected artifact file was not written: {artifact_path}")

        text = artifact_path.read_text(encoding="utf-8")
        if expected_artifact_content not in text:
            fail(
                f"Expected artifact content was not present in {artifact_path}: "
                f"{expected_artifact_content!r}"
            )

        return {
            "type": "file",
            "path": str(artifact_path),
            "fileCount": 1,
        }

    if notes_dir is None:
        fail("Either --notes-dir or --artifact-path is required.")

    note_files = sorted(notes_dir.glob("*.md"))
    if not note_files:
        fail("No note files were written by the memory tool.")

    if not any(expected_artifact_content in note.read_text(encoding="utf-8") for note in note_files):
        fail("Memory note content was not persisted on disk.")

    return {
        "type": "notes",
        "path": str(notes_dir),
        "fileCount": len(note_files),
    }


def verify_summary(summary_path, expected_mode, expected_orchestrator, expected_provider, expected_model):
    summary = load_json(summary_path)
    runtime = summary.get("runtime") or {}
    usage = summary.get("usage") or {}

    if runtime.get("orchestrator") != expected_orchestrator:
        fail(f"Admin summary reported unexpected orchestrator: {runtime.get('orchestrator')!r}")

    if runtime.get("effectiveMode") != expected_mode:
        fail(f"Admin summary reported unexpected effective mode: {runtime.get('effectiveMode')!r}")

    provider_snapshot = next(
        (
            item for item in usage.get("providers", [])
            if item.get("providerId") == expected_provider and item.get("modelId") == expected_model
        ),
        None,
    )
    if provider_snapshot is None:
        fail("Admin summary did not include the expected provider usage snapshot.")

    if int(provider_snapshot.get("requests", 0)) < 2:
        fail(f"Expected at least two provider requests, saw {provider_snapshot.get('requests')!r}.")

    if int(provider_snapshot.get("outputTokens", 0)) <= 0:
        fail(f"Expected output tokens to be recorded, saw {provider_snapshot.get('outputTokens')!r}.")

    recent_turn = next(
        (
            item for item in usage.get("recentTurns", [])
            if item.get("providerId") == expected_provider
            and item.get("modelId") == expected_model
            and item.get("channelId") == "openai-http"
        ),
        None,
    )
    if recent_turn is None:
        fail("Admin summary did not include a recent turn for the HTTP MAF request.")

    if not recent_turn.get("sessionId"):
        fail("Recent turn usage entry did not include a session id.")

    return {
        "sessionId": recent_turn["sessionId"],
        "providerSnapshot": provider_snapshot,
        "recentTurn": recent_turn,
        "plugins": summary.get("plugins") or {},
    }


def verify_plugin_summary(summary_plugins, expected_plugin_id, expected_plugin_tool_count):
    if expected_plugin_id is None:
        return None

    reports = summary_plugins.get("reports") or []
    matching = next((item for item in reports if item.get("pluginId") == expected_plugin_id), None)
    if matching is None:
        fail(f"Admin summary did not include plugin report for {expected_plugin_id!r}.")
    if matching.get("loaded") is not True:
        fail(f"Plugin {expected_plugin_id!r} was not loaded successfully: {matching!r}")
    if expected_plugin_tool_count is not None and int(matching.get("toolCount", -1)) != expected_plugin_tool_count:
        fail(
            f"Plugin {expected_plugin_id!r} reported unexpected tool count: "
            f"{matching.get('toolCount')!r} != {expected_plugin_tool_count}."
        )

    loaded_count = int(summary_plugins.get("loaded", 0))
    if loaded_count < 1:
        fail(f"Expected at least one loaded plugin in admin summary, saw {loaded_count}.")

    return {
        "loadedCount": loaded_count,
        "blockedByMode": int(summary_plugins.get("blockedByMode", 0)),
        "report": matching,
    }


def parse_timestamp(value):
    normalized = value.replace("Z", "+00:00")
    return datetime.fromisoformat(normalized)


def verify_events(events_path, expected_provider, expected_model, expected_session_id):
    events = load_json(events_path).get("items") or []
    if not events:
        fail("Admin events endpoint returned no LLM events.")

    relevant_events = [item for item in events if item.get("sessionId") == expected_session_id]
    groups = {}
    action_counts = collections.Counter()
    severity_counts = collections.Counter()

    for item in relevant_events:
        action = item.get("action")
        severity = item.get("severity")
        if action:
            action_counts[action] += 1
        if severity:
            severity_counts[severity] += 1

    coherent_groups = []
    request_durations_ms = []

    for item in events:
        correlation_id = item.get("correlationId")
        if not correlation_id or item.get("sessionId") != expected_session_id:
            continue
        groups.setdefault(correlation_id, []).append(item)

    if not groups:
        fail("Runtime events did not include correlation ids for the MAF request.")

    for correlation_id, items in groups.items():
        ordered_items = sorted(items, key=lambda item: parse_timestamp(item["timestampUtc"]))
        actions = {item.get("action") for item in items}
        if not {"route_selected", "request_started", "request_completed"}.issubset(actions):
            continue

        provider_ids = {
            (item.get("metadata") or {}).get("providerId")
            for item in items
            if isinstance(item.get("metadata"), dict)
        }
        model_ids = {
            (item.get("metadata") or {}).get("modelId")
            for item in items
            if isinstance(item.get("metadata"), dict)
        }

        if expected_provider not in provider_ids:
            continue
        if expected_model not in model_ids:
            continue

        starts = [item for item in ordered_items if item.get("action") == "request_started"]
        completions = [item for item in ordered_items if item.get("action") == "request_completed"]
        pair_count = min(len(starts), len(completions))
        for index in range(pair_count):
            started_at = parse_timestamp(starts[index]["timestampUtc"])
            completed_at = parse_timestamp(completions[index]["timestampUtc"])
            request_durations_ms.append(max(0, int((completed_at - started_at).total_seconds() * 1000)))

        coherent_groups.append(correlation_id)

    if not coherent_groups:
        fail("Runtime events did not show a coherent LLM event group for the MAF request.")

    return {
        "eventCount": len(relevant_events),
        "correlationGroupCount": len(groups),
        "coherentCorrelationIds": coherent_groups,
        "actionCounts": dict(sorted(action_counts.items())),
        "severityCounts": dict(sorted(severity_counts.items())),
        "requestDurationsMs": request_durations_ms,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--response", required=True)
    parser.add_argument("--provider-log", required=True)
    parser.add_argument("--notes-dir")
    parser.add_argument("--artifact-path")
    parser.add_argument("--summary", required=True)
    parser.add_argument("--events", required=True)
    parser.add_argument("--expected-mode", required=True)
    parser.add_argument("--expected-orchestrator", default="maf")
    parser.add_argument("--expected-provider", default="openai-compatible")
    parser.add_argument("--expected-model", default="fake-maf-model")
    parser.add_argument("--expected-note-key", required=True)
    parser.add_argument("--expected-note-content", required=True)
    parser.add_argument("--expected-result-fragment")
    parser.add_argument("--expected-plugin-id")
    parser.add_argument("--expected-plugin-tool-count", type=int)
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--gateway-request-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    response_path = pathlib.Path(args.response)
    provider_log_path = pathlib.Path(args.provider_log)
    notes_dir = pathlib.Path(args.notes_dir) if args.notes_dir else None
    artifact_path = pathlib.Path(args.artifact_path) if args.artifact_path else None
    summary_path = pathlib.Path(args.summary)
    events_path = pathlib.Path(args.events)
    expected_result_fragment = args.expected_result_fragment or f"Saved note: {args.expected_note_key}"

    gateway = verify_response(response_path, expected_result_fragment)
    provider = verify_provider_log(
        provider_log_path,
        expected_result_fragment,
        args.expected_provider,
        args.expected_model)
    artifact = verify_artifact(notes_dir, artifact_path, args.expected_note_content)
    summary = verify_summary(
        summary_path,
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model)
    plugin = verify_plugin_summary(
        summary["plugins"],
        args.expected_plugin_id,
        args.expected_plugin_tool_count)
    events = verify_events(events_path, args.expected_provider, args.expected_model, summary["sessionId"])

    report = {
        "runtimeMode": args.expected_mode,
        "orchestrator": args.expected_orchestrator,
        "providerId": args.expected_provider,
        "modelId": args.expected_model,
        "sessionId": summary["sessionId"],
        "timings": {
            "gatewayStartupMs": args.gateway_startup_ms,
            "gatewayRequestMs": args.gateway_request_ms,
        },
        "gateway": gateway,
        "provider": provider,
        "artifact": artifact,
        "usage": {
            "providerSnapshot": summary["providerSnapshot"],
            "recentTurn": summary["recentTurn"],
        },
        "plugin": plugin,
        "events": events,
    }

    if args.report:
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

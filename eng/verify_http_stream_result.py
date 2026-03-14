#!/usr/bin/env python3
import argparse
import collections
import json
import pathlib
from datetime import datetime


def fail(message):
    raise SystemExit(message)


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def parse_timestamp(value):
    normalized = value.replace("Z", "+00:00")
    return datetime.fromisoformat(normalized)


def parse_stream(stream_path):
    role_seen = False
    done_seen = False
    text_chunks = []
    finish_reasons = []
    raw_events = []

    for line in stream_path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("data: "):
            continue

        payload = line[len("data: "):]
        if payload == "[DONE]":
            done_seen = True
            raw_events.append({"done": True})
            continue

        item = json.loads(payload)
        raw_events.append(item)
        choices = item.get("choices") or []
        if not choices:
            continue

        choice = choices[0]
        delta = choice.get("delta") or {}
        if delta.get("role") == "assistant":
            role_seen = True

        content = delta.get("content")
        if content:
            text_chunks.append(content)

        finish_reason = choice.get("finish_reason")
        if finish_reason:
            finish_reasons.append(finish_reason)

    if not role_seen:
        fail("Gateway SSE response did not include an assistant role chunk.")
    if not done_seen:
        fail("Gateway SSE response did not terminate with [DONE].")
    if not finish_reasons:
        fail("Gateway SSE response did not include a final finish_reason chunk.")

    return {
        "chunkCount": len(text_chunks),
        "textChunks": text_chunks,
        "fullText": "".join(text_chunks),
        "finishReasons": finish_reasons,
        "roleChunkSeen": role_seen,
        "doneSentinelSeen": done_seen,
        "eventCount": len(raw_events),
    }


def verify_stream(stream, expected_content, expected_chunks):
    if stream["fullText"] != expected_content:
        fail(
            f"Gateway SSE response assembled unexpected text: "
            f"{stream['fullText']!r} != {expected_content!r}"
        )

    if expected_chunks is not None and stream["textChunks"] != expected_chunks:
        fail(
            f"Gateway SSE response emitted unexpected text chunks: "
            f"{stream['textChunks']!r} != {expected_chunks!r}"
        )

    if "stop" not in stream["finishReasons"]:
        fail(f"Gateway SSE response did not include finish_reason=stop: {stream['finishReasons']!r}")


def verify_provider_log(provider_log_path, expected_provider, expected_model):
    entries = [
        json.loads(line)
        for line in provider_log_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(entries) != 1:
        fail(f"Expected exactly one upstream streaming request, saw {len(entries)}.")

    entry = entries[0]
    body = entry.get("body") or {}
    if body.get("model") != expected_model:
        fail(f"Provider request used unexpected model: {body.get('model')!r}")
    if body.get("stream") is not True:
        fail(f"Provider request did not enable streaming: {body.get('stream')!r}")
    if expected_provider != "openai-compatible":
        fail(f"Unexpected provider assertion input: {expected_provider!r}")

    return {
        "requestCount": len(entries),
        "path": entry.get("path", ""),
        "stream": body.get("stream"),
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
            item
            for item in usage.get("providers", [])
            if item.get("providerId") == expected_provider and item.get("modelId") == expected_model
        ),
        None,
    )
    if provider_snapshot is None:
        fail("Admin summary did not include the expected provider usage snapshot.")
    if int(provider_snapshot.get("requests", 0)) < 1:
        fail(f"Expected at least one provider request, saw {provider_snapshot.get('requests')!r}.")
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
        fail("Admin summary did not include a recent turn for the HTTP streaming request.")
    if not recent_turn.get("sessionId"):
        fail("Recent turn usage entry did not include a session id.")

    return {
        "sessionId": recent_turn["sessionId"],
        "providerSnapshot": provider_snapshot,
        "recentTurn": recent_turn,
    }


def verify_events(events_path, expected_provider, expected_model, expected_session_id):
    events = load_json(events_path).get("items") or []
    if not events:
        fail("Admin events endpoint returned no LLM events.")

    relevant_events = [item for item in events if item.get("sessionId") == expected_session_id]
    if not relevant_events:
        fail("Admin events endpoint returned no LLM events for the streaming session.")

    action_counts = collections.Counter()
    severity_counts = collections.Counter()
    groups = {}

    for item in relevant_events:
        action = item.get("action")
        severity = item.get("severity")
        if action:
            action_counts[action] += 1
        if severity:
            severity_counts[severity] += 1

        correlation_id = item.get("correlationId")
        if correlation_id:
            groups.setdefault(correlation_id, []).append(item)

    coherent_groups = []
    stream_durations_ms = []

    for correlation_id, items in groups.items():
        actions = {item.get("action") for item in items}
        if not {"route_selected", "stream_started", "stream_completed"}.issubset(actions):
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
        if expected_provider not in provider_ids or expected_model not in model_ids:
            continue

        ordered = sorted(items, key=lambda item: parse_timestamp(item["timestampUtc"]))
        starts = [item for item in ordered if item.get("action") == "stream_started"]
        completions = [item for item in ordered if item.get("action") == "stream_completed"]
        pair_count = min(len(starts), len(completions))
        for index in range(pair_count):
            started_at = parse_timestamp(starts[index]["timestampUtc"])
            completed_at = parse_timestamp(completions[index]["timestampUtc"])
            stream_durations_ms.append(max(0, int((completed_at - started_at).total_seconds() * 1000)))

        coherent_groups.append(correlation_id)

    if not coherent_groups:
        fail("Runtime events did not show a coherent streaming LLM event group for the request.")

    return {
        "eventCount": len(relevant_events),
        "correlationGroupCount": len(groups),
        "coherentCorrelationIds": coherent_groups,
        "actionCounts": dict(sorted(action_counts.items())),
        "severityCounts": dict(sorted(severity_counts.items())),
        "streamDurationsMs": stream_durations_ms,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stream", required=True)
    parser.add_argument("--provider-log", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--events", required=True)
    parser.add_argument("--expected-mode", required=True)
    parser.add_argument("--expected-orchestrator", required=True)
    parser.add_argument("--expected-provider", default="openai-compatible")
    parser.add_argument("--expected-model", default="fake-maf-model")
    parser.add_argument("--expected-content", required=True)
    parser.add_argument("--expected-chunks-json")
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--gateway-request-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    stream_path = pathlib.Path(args.stream)
    provider_log_path = pathlib.Path(args.provider_log)
    summary_path = pathlib.Path(args.summary)
    events_path = pathlib.Path(args.events)
    expected_chunks = json.loads(args.expected_chunks_json) if args.expected_chunks_json else None

    stream = parse_stream(stream_path)
    verify_stream(stream, args.expected_content, expected_chunks)
    provider = verify_provider_log(provider_log_path, args.expected_provider, args.expected_model)
    summary = verify_summary(
        summary_path,
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model,
    )
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
        "stream": stream,
        "provider": provider,
        "usage": {
            "providerSnapshot": summary["providerSnapshot"],
            "recentTurn": summary["recentTurn"],
        },
        "events": events,
    }

    if args.report:
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

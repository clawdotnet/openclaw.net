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


def verify_transcript(path, expected_note_key):
    transcript = load_json(path)
    approval_id = transcript.get("approvalId")
    if not approval_id:
        fail("WebSocket transcript did not capture an approval id.")

    approval_prompt = transcript.get("approvalPromptText") or ""
    if "Tool approval required." not in approval_prompt or "- tool: memory" not in approval_prompt:
        fail(f"Unexpected approval prompt: {approval_prompt!r}")

    approval_ack = transcript.get("approvalAckText") or ""
    if f"Tool approval recorded: {approval_id} = approved" not in approval_ack:
        fail(f"Approval ack was missing or unexpected: {approval_ack!r}")

    final_text = transcript.get("finalText") or ""
    if "Saved note:" not in final_text or expected_note_key not in final_text:
        fail(f"Final websocket response did not include the expected tool result: {final_text!r}")

    return {
        "approvalId": approval_id,
        "approvalPromptText": approval_prompt,
        "approvalAckText": approval_ack,
        "finalText": final_text,
        "receivedMessageCount": len(transcript.get("receivedMessages") or []),
    }


def verify_provider_log(provider_log_path, expected_note_key, expected_model):
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

    if not any("Saved note:" in message and expected_note_key in message for message in tool_messages):
        fail(f"Upstream provider log did not capture the expected tool result: {tool_messages!r}")

    return {
        "requestCount": len(entries),
        "toolResultMessages": tool_messages,
    }


def verify_notes(notes_dir, expected_note_content):
    note_files = sorted(notes_dir.glob("*.md"))
    if not note_files:
        fail("No note files were written by the memory tool.")

    if not any(expected_note_content in note.read_text(encoding="utf-8") for note in note_files):
        fail("Memory note content was not persisted on disk.")

    return {
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
    if int(runtime.get("pendingApprovals", -1)) != 0:
        fail(f"Expected pending approvals to be drained, saw {runtime.get('pendingApprovals')!r}.")

    provider_snapshot = next(
        (
            item for item in usage.get("providers", [])
            if item.get("providerId") == expected_provider and item.get("modelId") == expected_model
        ),
        None,
    )
    if provider_snapshot is None:
        fail("Admin summary did not include the expected provider usage snapshot.")

    recent_turn = next(
        (
            item for item in usage.get("recentTurns", [])
            if item.get("providerId") == expected_provider
            and item.get("modelId") == expected_model
            and item.get("channelId") == "websocket"
        ),
        None,
    )
    if recent_turn is None:
        fail("Admin summary did not include a websocket recent turn for the approval run.")

    return {
        "providerSnapshot": provider_snapshot,
        "recentTurn": recent_turn,
    }


def verify_approval_history(history_path, approval_id):
    items = load_json(history_path).get("items") or []
    relevant = [item for item in items if item.get("approvalId") == approval_id]
    if len(relevant) < 2:
        fail(f"Approval history did not contain both create and decision entries for {approval_id}.")

    actions = {item.get("eventType") for item in relevant}
    if actions != {"created", "decision"}:
        fail(f"Unexpected approval history actions for {approval_id}: {actions!r}")

    decision = next(item for item in relevant if item.get("eventType") == "decision")
    if decision.get("approved") is not True:
        fail(f"Approval decision was not approved: {decision!r}")

    return {
        "items": relevant,
        "eventTypes": sorted(actions),
        "decisionSource": decision.get("decisionSource"),
    }


def verify_approval_events(events_path, approval_id, expected_session_id):
    items = load_json(events_path).get("items") or []
    relevant = [item for item in items if item.get("sessionId") == expected_session_id]
    if not relevant:
        fail("Approval runtime events were not recorded for the websocket session.")

    requested = [
        item for item in relevant
        if item.get("action") == "requested"
        and (item.get("metadata") or {}).get("approvalId") == approval_id
    ]
    if not requested:
        fail("Approval runtime events did not contain the expected requested entry.")

    action_counts = collections.Counter(item.get("action") for item in relevant if item.get("action"))
    return {
        "eventCount": len(relevant),
        "actionCounts": dict(sorted(action_counts.items())),
    }


def verify_llm_events(events_path, expected_provider, expected_model, expected_session_id):
    items = load_json(events_path).get("items") or []
    relevant = [item for item in items if item.get("sessionId") == expected_session_id]
    if not relevant:
        fail("LLM runtime events were not recorded for the websocket session.")

    groups = {}
    action_counts = collections.Counter()
    for item in relevant:
        action = item.get("action")
        if action:
            action_counts[action] += 1
        correlation_id = item.get("correlationId")
        if correlation_id:
            groups.setdefault(correlation_id, []).append(item)

    coherent = []
    durations = []
    for correlation_id, group in groups.items():
        actions = {item.get("action") for item in group}
        if not {"route_selected", "request_started", "request_completed"}.issubset(actions):
            continue

        provider_ids = {
            (item.get("metadata") or {}).get("providerId")
            for item in group
            if isinstance(item.get("metadata"), dict)
        }
        model_ids = {
            (item.get("metadata") or {}).get("modelId")
            for item in group
            if isinstance(item.get("metadata"), dict)
        }
        if expected_provider not in provider_ids or expected_model not in model_ids:
            continue

        ordered = sorted(group, key=lambda item: parse_timestamp(item["timestampUtc"]))
        starts = [item for item in ordered if item.get("action") == "request_started"]
        completions = [item for item in ordered if item.get("action") == "request_completed"]
        pair_count = min(len(starts), len(completions))
        for index in range(pair_count):
            started_at = parse_timestamp(starts[index]["timestampUtc"])
            completed_at = parse_timestamp(completions[index]["timestampUtc"])
            durations.append(max(0, int((completed_at - started_at).total_seconds() * 1000)))
        coherent.append(correlation_id)

    if not coherent:
        fail("LLM runtime events did not show a coherent correlation group for the approval run.")

    return {
        "eventCount": len(relevant),
        "actionCounts": dict(sorted(action_counts.items())),
        "coherentCorrelationIds": coherent,
        "requestDurationsMs": durations,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--transcript", required=True)
    parser.add_argument("--provider-log", required=True)
    parser.add_argument("--notes-dir", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--approval-history", required=True)
    parser.add_argument("--approval-events", required=True)
    parser.add_argument("--llm-events", required=True)
    parser.add_argument("--pending-approvals", required=True)
    parser.add_argument("--expected-mode", required=True)
    parser.add_argument("--expected-orchestrator", required=True)
    parser.add_argument("--expected-provider", default="openai-compatible")
    parser.add_argument("--expected-model", default="fake-maf-model")
    parser.add_argument("--expected-note-key", required=True)
    parser.add_argument("--expected-note-content", required=True)
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--approval-flow-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    transcript = verify_transcript(pathlib.Path(args.transcript), args.expected_note_key)
    provider = verify_provider_log(pathlib.Path(args.provider_log), args.expected_note_key, args.expected_model)
    notes = verify_notes(pathlib.Path(args.notes_dir), args.expected_note_content)
    summary = verify_summary(
        pathlib.Path(args.summary),
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model)

    pending = load_json(pathlib.Path(args.pending_approvals)).get("items") or []
    if pending:
        fail(f"Expected no pending approvals after decision, saw {len(pending)} item(s).")

    approval_history = verify_approval_history(pathlib.Path(args.approval_history), transcript["approvalId"])
    approval_events = verify_approval_events(
        pathlib.Path(args.approval_events),
        transcript["approvalId"],
        summary["recentTurn"]["sessionId"])
    llm_events = verify_llm_events(
        pathlib.Path(args.llm_events),
        args.expected_provider,
        args.expected_model,
        summary["recentTurn"]["sessionId"])

    report = {
        "runtimeMode": args.expected_mode,
        "orchestrator": args.expected_orchestrator,
        "providerId": args.expected_provider,
        "modelId": args.expected_model,
        "sessionId": summary["recentTurn"]["sessionId"],
        "timings": {
            "gatewayStartupMs": args.gateway_startup_ms,
            "approvalFlowMs": args.approval_flow_ms,
        },
        "transcript": transcript,
        "provider": provider,
        "notes": notes,
        "usage": summary,
        "approvalHistory": approval_history,
        "approvalEvents": approval_events,
        "llmEvents": llm_events,
    }

    if args.report:
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

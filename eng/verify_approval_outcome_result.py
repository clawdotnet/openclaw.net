#!/usr/bin/env python3
import argparse
import collections
import json
import pathlib


def fail(message):
    raise SystemExit(message)


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def load_jsonl(path):
    return [
        json.loads(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def verify_transcript(path, *, expected_decision, expect_ack, expected_final_fragment):
    transcript = load_json(path)
    approval_id = transcript.get("approvalId")
    if not approval_id:
        fail(f"{path.name}: approval id was missing from transcript.")

    prompt = transcript.get("approvalPromptText") or ""
    if "Tool approval required." not in prompt or "- tool: memory" not in prompt:
        fail(f"{path.name}: approval prompt was missing or malformed.")

    ack = transcript.get("approvalAckText") or ""
    if expect_ack:
        expected_ack = f"Tool approval recorded: {approval_id} = {expected_decision}"
        if expected_ack not in ack:
            fail(f"{path.name}: approval ack did not match {expected_ack!r}.")
    elif ack:
        fail(f"{path.name}: unexpected approval ack was captured.")

    final_text = transcript.get("finalText") or ""
    if expected_final_fragment not in final_text:
        fail(f"{path.name}: final text did not contain {expected_final_fragment!r}. Saw {final_text!r}.")

    return {
        "approvalId": approval_id,
        "finalText": final_text,
        "messageCount": len(transcript.get("receivedMessages") or []),
    }


def verify_http_response(path, expected_status, expected_fragment):
    payload = load_json(path)
    status = int(payload.get("statusCode") or 0)
    if status != expected_status:
        fail(f"{path.name}: expected status {expected_status}, saw {status}.")

    body = payload.get("body") or ""
    if expected_fragment not in body:
        fail(f"{path.name}: expected response body fragment {expected_fragment!r}.")

    return {
        "statusCode": status,
        "body": body,
    }


def verify_ws_mismatch_ack(path):
    transcript = load_json(path)
    reply = transcript.get("reply") or ""
    if "Approval id is not valid for this sender/channel" not in reply:
        fail(f"{path.name}: unexpected websocket mismatch reply {reply!r}.")
    return {
        "reply": reply,
        "messageCount": len(transcript.get("receivedMessages") or []),
    }


def verify_summary(path, expected_mode, expected_orchestrator, expected_provider, expected_model):
    summary = load_json(path)
    runtime = summary.get("runtime") or {}
    usage = summary.get("usage") or {}

    if runtime.get("effectiveMode") != expected_mode:
        fail(f"Admin summary reported unexpected effective mode: {runtime.get('effectiveMode')!r}")
    if runtime.get("orchestrator") != expected_orchestrator:
        fail(f"Admin summary reported unexpected orchestrator: {runtime.get('orchestrator')!r}")
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
        fail("Admin summary did not include the expected provider snapshot.")

    return {
        "providerSnapshot": provider_snapshot,
        "runtime": runtime,
    }


def verify_history(path, scenario_ids):
    items = load_json(path).get("items") or []
    by_id = collections.defaultdict(list)
    for item in items:
        approval_id = item.get("approvalId")
        if approval_id:
            by_id[approval_id].append(item)

    results = {}
    for label, expected in scenario_ids.items():
        approval_id = expected["approvalId"]
        relevant = by_id.get(approval_id) or []
        if len(relevant) < 2:
            fail(f"Approval history was missing entries for {label} ({approval_id}).")

        event_types = {item.get("eventType") for item in relevant}
        if event_types != {"created", "decision"}:
            fail(f"Approval history for {label} had unexpected event types: {event_types!r}.")

        decision = next(item for item in relevant if item.get("eventType") == "decision")
        approved = decision.get("approved")
        if approved is not expected["approved"]:
            fail(f"Approval history for {label} had unexpected approved={approved!r}.")

        decision_source = decision.get("decisionSource")
        if decision_source != expected["decisionSource"]:
            fail(
                f"Approval history for {label} had unexpected decisionSource {decision_source!r}; "
                f"expected {expected['decisionSource']!r}."
            )

        results[label] = {
            "approvalId": approval_id,
            "decisionSource": decision_source,
            "approved": approved,
        }

    return results


def verify_events(path, scenario_ids):
    items = load_json(path).get("items") or []
    requested_ids = {
        (item.get("metadata") or {}).get("approvalId")
        for item in items
        if item.get("action") == "requested"
    }
    if None in requested_ids:
        requested_ids.remove(None)

    for label, expected in scenario_ids.items():
        approval_id = expected["approvalId"]
        if approval_id not in requested_ids:
            fail(f"Approval runtime events did not include the request for {label} ({approval_id}).")

    action_counts = collections.Counter(item.get("action") for item in items if item.get("action"))
    for label, expected in scenario_ids.items():
        approval_id = expected["approvalId"]
        matched = [
            item for item in items
            if (item.get("metadata") or {}).get("approvalId") == approval_id
        ]
        if expected["eventAction"] not in {item.get("action") for item in matched}:
            fail(
                f"Approval runtime events for {label} did not include action "
                f"{expected['eventAction']!r}."
            )

    return {
        "eventCount": len(items),
        "actionCounts": dict(sorted(action_counts.items())),
    }


def verify_pending(path):
    items = load_json(path).get("items") or []
    if items:
        fail(f"Expected no pending approvals after outcome matrix, saw {len(items)}.")
    return {"count": 0}


def verify_provider_log(path, expected_model, minimum_requests):
    entries = load_jsonl(path)
    if len(entries) < minimum_requests:
        fail(f"Expected at least {minimum_requests} upstream provider requests, saw {len(entries)}.")

    for entry in entries:
        body = entry.get("body") or {}
        if body.get("model") != expected_model:
            fail(f"Provider request used unexpected model: {body.get('model')!r}")

    return {
        "requestCount": len(entries),
    }


def verify_notes_empty(path):
    note_dir = pathlib.Path(path)
    note_files = sorted(note_dir.glob("*.md"))
    if note_files:
        fail(f"Expected no note files for deny/timeout outcome matrix, saw {len(note_files)}.")
    return {"fileCount": 0}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ws-deny", required=True)
    parser.add_argument("--http-admin-deny", required=True)
    parser.add_argument("--http-requester-mismatch", required=True)
    parser.add_argument("--ws-timeout", required=True)
    parser.add_argument("--ws-mismatch-ack", required=True)
    parser.add_argument("--admin-deny-response", required=True)
    parser.add_argument("--mismatch-finalize-response", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--approval-history", required=True)
    parser.add_argument("--approval-events", required=True)
    parser.add_argument("--pending-approvals", required=True)
    parser.add_argument("--provider-log", required=True)
    parser.add_argument("--notes-dir", required=True)
    parser.add_argument("--expected-mode", required=True)
    parser.add_argument("--expected-orchestrator", required=True)
    parser.add_argument("--expected-provider", default="openai-compatible")
    parser.add_argument("--expected-model", default="fake-maf-model")
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--ws-deny-ms", type=int)
    parser.add_argument("--http-admin-deny-ms", type=int)
    parser.add_argument("--http-requester-mismatch-ms", type=int)
    parser.add_argument("--ws-timeout-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    transcripts = {
        "wsDeny": verify_transcript(
            pathlib.Path(args.ws_deny),
            expected_decision="denied",
            expect_ack=True,
            expected_final_fragment="Tool execution denied by user.",
        ),
        "httpAdminDeny": verify_transcript(
            pathlib.Path(args.http_admin_deny),
            expected_decision="denied",
            expect_ack=False,
            expected_final_fragment="Tool execution denied by user.",
        ),
        "httpRequesterMismatch": verify_transcript(
            pathlib.Path(args.http_requester_mismatch),
            expected_decision="denied",
            expect_ack=False,
            expected_final_fragment="Tool execution denied by user.",
        ),
        "wsTimeout": verify_transcript(
            pathlib.Path(args.ws_timeout),
            expected_decision="denied",
            expect_ack=False,
            expected_final_fragment="Tool execution denied by user.",
        ),
    }

    mismatch_ack = verify_ws_mismatch_ack(pathlib.Path(args.ws_mismatch_ack))
    admin_deny_response = verify_http_response(
        pathlib.Path(args.admin_deny_response),
        expected_status=200,
        expected_fragment='"success":true',
    )
    mismatch_finalize_response = verify_http_response(
        pathlib.Path(args.mismatch_finalize_response),
        expected_status=200,
        expected_fragment='"success":true',
    )

    scenario_ids = {
        "wsDeny": {
            "approvalId": transcripts["wsDeny"]["approvalId"],
            "approved": False,
            "decisionSource": "chat",
            "eventAction": "decision_recorded",
        },
        "httpAdminDeny": {
            "approvalId": transcripts["httpAdminDeny"]["approvalId"],
            "approved": False,
            "decisionSource": "http_admin",
            "eventAction": "decision_recorded",
        },
        "httpRequesterMismatch": {
            "approvalId": transcripts["httpRequesterMismatch"]["approvalId"],
            "approved": False,
            "decisionSource": "http_admin",
            "eventAction": "decision_rejected",
        },
        "wsTimeout": {
            "approvalId": transcripts["wsTimeout"]["approvalId"],
            "approved": False,
            "decisionSource": "timeout",
            "eventAction": "timed_out",
        },
    }

    summary = verify_summary(
        pathlib.Path(args.summary),
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model,
    )
    history = verify_history(pathlib.Path(args.approval_history), scenario_ids)
    events = verify_events(pathlib.Path(args.approval_events), scenario_ids)
    pending = verify_pending(pathlib.Path(args.pending_approvals))
    provider = verify_provider_log(pathlib.Path(args.provider_log), args.expected_model, minimum_requests=8)
    notes = verify_notes_empty(args.notes_dir)

    report = {
        "runtimeMode": args.expected_mode,
        "orchestrator": args.expected_orchestrator,
        "timings": {
            "gatewayStartupMs": args.gateway_startup_ms,
            "wsDenyMs": args.ws_deny_ms,
            "httpAdminDenyMs": args.http_admin_deny_ms,
            "httpRequesterMismatchMs": args.http_requester_mismatch_ms,
            "wsTimeoutMs": args.ws_timeout_ms,
        },
        "transcripts": transcripts,
        "httpResponses": {
            "mismatchAck": mismatch_ack,
            "adminDeny": admin_deny_response,
            "mismatchFinalize": mismatch_finalize_response,
        },
        "summary": summary,
        "history": history,
        "events": events,
        "pending": pending,
        "provider": provider,
        "notes": notes,
    }

    if args.report:
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

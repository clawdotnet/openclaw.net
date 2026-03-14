#!/usr/bin/env python3
import argparse
import json
import pathlib


def fail(message):
    raise SystemExit(message)


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def verify_transcript(path, expected_text, label):
    transcript = load_json(path)
    final_text = transcript.get("finalText") or ""
    if expected_text not in final_text:
        fail(f"{label} transcript did not contain expected text {expected_text!r}: {final_text!r}")
    return {
        "sessionId": transcript.get("sessionId"),
        "finalText": final_text,
        "envelopeCount": len(transcript.get("envelopes") or []),
    }


def verify_summary(summary_path, expected_mode, expected_orchestrator, expected_provider, expected_model):
    summary = load_json(summary_path)
    runtime = summary.get("runtime") or {}
    usage = summary.get("usage") or {}

    if runtime.get("effectiveMode") != expected_mode:
        fail(f"Admin summary reported unexpected effective mode: {runtime.get('effectiveMode')!r}")
    if runtime.get("orchestrator") != expected_orchestrator:
        fail(f"Admin summary reported unexpected orchestrator: {runtime.get('orchestrator')!r}")

    provider_snapshot = next(
        (
            item
            for item in usage.get("providers", [])
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


def verify_session_file(session_path, session_id, expected_fragments):
    session = load_json(session_path)
    if session.get("id") != session_id:
        fail(f"Persisted session file returned unexpected session id: {session.get('id')!r}")

    history = session.get("history") or []
    parts = []
    for item in history:
        if not isinstance(item, dict):
            continue
        parts.append(str(item.get("content") or ""))
        for call in item.get("toolCalls") or []:
            if isinstance(call, dict):
                parts.append(str(call.get("result") or ""))
    history_text = "\n".join(parts)
    for fragment in expected_fragments:
        if fragment not in history_text:
            fail(f"Session history did not contain expected fragment {fragment!r}.")

    return {
        "historyCount": len(history),
        "historyText": history_text,
    }


def verify_provider_log(path, expected_note_key, expected_followup_text):
    entries = [
        json.loads(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(entries) < 4:
        fail(f"Expected at least four upstream requests across restart flow, saw {len(entries)}.")

    bodies = [entry.get("body") or {} for entry in entries]
    serialized = json.dumps(bodies)
    if expected_note_key not in serialized:
        fail(f"Provider log did not mention expected note key {expected_note_key!r}.")
    if expected_followup_text not in serialized:
        fail("Provider log did not capture the expected follow-up answer.")

    return {
        "requestCount": len(entries),
    }


def verify_sidecar(log_second_path, log_third_path, sidecar_path, orchestrator):
    if orchestrator != "maf":
        return {
            "sidecarPath": None,
            "restored": "not_applicable",
            "fallback": "not_applicable",
        }

    if sidecar_path is None:
        fail("MAF restart report was missing the sidecar path.")

    sidecar = pathlib.Path(sidecar_path)
    if not sidecar.exists():
        fail(f"Expected MAF sidecar to exist at {sidecar}.")

    second_log = pathlib.Path(log_second_path).read_text(encoding="utf-8")
    if "Restored MAF session sidecar" not in second_log:
        fail("Second gateway run did not log a MAF sidecar restore.")

    third_log = pathlib.Path(log_third_path).read_text(encoding="utf-8")
    if "Failed to load MAF session sidecar" not in third_log and "Discarding MAF session sidecar" not in third_log:
        fail("Third gateway run did not log a MAF sidecar fallback after corruption.")

    return {
        "sidecarPath": sidecar_path,
        "restored": "logged",
        "fallback": "logged",
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--turn1", required=True)
    parser.add_argument("--turn2", required=True)
    parser.add_argument("--turn3", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--session-file", required=True)
    parser.add_argument("--provider-log", required=True)
    parser.add_argument("--expected-mode", required=True)
    parser.add_argument("--expected-orchestrator", required=True)
    parser.add_argument("--expected-provider", default="openai-compatible")
    parser.add_argument("--expected-model", default="fake-maf-model")
    parser.add_argument("--expected-note-key", required=True)
    parser.add_argument("--expected-note-content", required=True)
    parser.add_argument("--expected-session-id", required=True)
    parser.add_argument("--log-second", required=True)
    parser.add_argument("--log-third", required=True)
    parser.add_argument("--sidecar-path")
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--turn1-ms", type=int)
    parser.add_argument("--turn2-ms", type=int)
    parser.add_argument("--turn3-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    followup_text = f"Earlier note was {args.expected_note_key}."
    turn1 = verify_transcript(pathlib.Path(args.turn1), f"Saved note: {args.expected_note_key}", "First")
    turn2 = verify_transcript(pathlib.Path(args.turn2), followup_text, "Second")
    turn3 = verify_transcript(pathlib.Path(args.turn3), followup_text, "Third")
    summary = verify_summary(
        pathlib.Path(args.summary),
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model,
    )
    session = verify_session_file(
        pathlib.Path(args.session_file),
        args.expected_session_id,
        [
            "Use the memory tool to save a note.",
            f"Saved note: {args.expected_note_key}",
            "What note did I save earlier?",
            followup_text,
            "Repeat the saved note key.",
        ],
    )
    provider = verify_provider_log(pathlib.Path(args.provider_log), args.expected_note_key, followup_text)
    sidecar = verify_sidecar(args.log_second, args.log_third, args.sidecar_path, args.expected_orchestrator)

    report = {
        "runtimeMode": args.expected_mode,
        "orchestrator": args.expected_orchestrator,
        "providerId": args.expected_provider,
        "modelId": args.expected_model,
        "sessionId": args.expected_session_id,
        "timings": {
            "gatewayStartupMs": args.gateway_startup_ms,
            "turn1Ms": args.turn1_ms,
            "turn2Ms": args.turn2_ms,
            "turn3Ms": args.turn3_ms,
        },
        "turns": {
            "first": turn1,
            "second": turn2,
            "third": turn3,
        },
        "summary": summary,
        "session": session,
        "provider": provider,
        "sidecar": sidecar,
    }

    if args.report:
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

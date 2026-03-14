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
    created_seen = False
    in_progress_seen = False
    failed_seen = False
    completed_seen = False
    sequence_numbers = []
    tool_calls = {}
    tool_output_items = {}
    event_counts = collections.Counter()
    current_event = None
    current_data = []
    failed_response = None

    def process_sse_message(event_type, payload):
        nonlocal created_seen, in_progress_seen, failed_seen, completed_seen, failed_response

        if not event_type or payload is None:
            return

        item = json.loads(payload)
        event_type = item.get("type", event_type)
        event_counts[event_type] += 1
        if "sequence_number" in item:
            sequence_numbers.append(item["sequence_number"])

        if event_type == "response.created":
            created_seen = True
        elif event_type == "response.in_progress":
            in_progress_seen = True
        elif event_type == "response.failed":
            failed_seen = True
            failed_response = item.get("response")
        elif event_type == "response.completed":
            completed_seen = True
        elif event_type == "response.output_item.added":
            stream_item = item.get("item") or {}
            if stream_item.get("type") == "function_call":
                tool_calls[stream_item.get("id")] = {
                    "itemId": stream_item.get("id"),
                    "callId": stream_item.get("call_id"),
                    "name": stream_item.get("name"),
                    "arguments": stream_item.get("arguments", ""),
                    "outputIndex": item.get("output_index"),
                }
            elif stream_item.get("type") == "function_call_output":
                tool_output_items[stream_item.get("id")] = {
                    "itemId": stream_item.get("id"),
                    "callId": stream_item.get("call_id"),
                    "output": stream_item.get("output", ""),
                    "status": stream_item.get("status"),
                    "outputIndex": item.get("output_index"),
                }
        elif event_type == "response.output_item.done":
            stream_item = item.get("item") or {}
            if stream_item.get("type") == "function_call_output":
                tool_output_items[stream_item.get("id")] = {
                    "itemId": stream_item.get("id"),
                    "callId": stream_item.get("call_id"),
                    "output": stream_item.get("output", ""),
                    "status": stream_item.get("status"),
                    "outputIndex": item.get("output_index"),
                }

    for line in stream_path.read_text(encoding="utf-8").splitlines():
        if line.startswith("event: "):
            current_event = line[len("event: "):]
            continue

        if line.startswith("data: "):
            current_data.append(line[len("data: "):])
            continue

        if line == "":
            if current_data:
                process_sse_message(current_event, "\n".join(current_data))
            current_event = None
            current_data = []

    if current_data:
        process_sse_message(current_event, "\n".join(current_data))

    normalized_tool_calls = sorted(
        tool_calls.values(),
        key=lambda item: (item.get("outputIndex", 0), item.get("itemId") or ""),
    )
    normalized_tool_outputs = sorted(
        tool_output_items.values(),
        key=lambda item: (item.get("outputIndex", 0), item.get("itemId") or ""),
    )

    return {
        "createdSeen": created_seen,
        "inProgressSeen": in_progress_seen,
        "failedSeen": failed_seen,
        "completedSeen": completed_seen,
        "sequenceNumbers": sequence_numbers,
        "toolCalls": normalized_tool_calls,
        "toolOutputItems": normalized_tool_outputs,
        "eventCounts": dict(sorted(event_counts.items())),
        "failedResponse": failed_response,
    }


def verify_stream(stream, expected_tool_name, expected_tool_arguments, expected_tool_result, expected_error_code, expected_error_message_substring):
    if not stream["createdSeen"]:
        fail("Responses SSE did not emit response.created.")
    if not stream["inProgressSeen"]:
        fail("Responses SSE did not emit response.in_progress.")
    if not stream["failedSeen"]:
        fail("Responses SSE did not emit response.failed.")
    if stream["completedSeen"]:
        fail("Responses SSE unexpectedly emitted response.completed during the failure scenario.")

    total_events = sum(stream["eventCounts"].values())
    if len(stream["sequenceNumbers"]) != total_events:
        fail(
            "Responses SSE omitted sequence_number on one or more events: "
            f"{len(stream['sequenceNumbers'])} numbered vs {total_events} total"
        )
    expected_sequence = list(range(1, len(stream["sequenceNumbers"]) + 1))
    if stream["sequenceNumbers"] != expected_sequence:
        fail(
            "Responses SSE sequence_number values were not contiguous from 1: "
            f"{stream['sequenceNumbers']!r}"
        )

    tool_call = next((item for item in stream["toolCalls"] if item.get("name") == expected_tool_name), None)
    if tool_call is None:
        fail(f"Responses SSE did not include a function_call item for {expected_tool_name!r}.")
    if tool_call.get("arguments") != expected_tool_arguments:
        fail(
            f"Responses SSE function_call arguments differed: "
            f"{tool_call.get('arguments')!r} != {expected_tool_arguments!r}"
        )

    tool_output = next(
        (
            item for item in stream["toolOutputItems"]
            if item.get("callId") == tool_call.get("callId") and item.get("output") == expected_tool_result
        ),
        None,
    )
    if tool_output is None:
        fail(
            "Responses SSE did not include a standard function_call_output item "
            f"for {expected_tool_name!r}: {stream['toolOutputItems']!r}"
        )

    failed_response = stream.get("failedResponse") or {}
    if failed_response.get("status") != "failed":
        fail(f"Failed response reported unexpected status: {failed_response.get('status')!r}")
    error = failed_response.get("error") or {}
    if error.get("code") != expected_error_code:
        fail(f"Failed response reported unexpected error code: {error.get('code')!r}")
    if expected_error_message_substring not in str(error.get("message", "")):
        fail(f"Failed response reported unexpected error message: {error.get('message')!r}")

    output_items = failed_response.get("output") or []
    function_call_output_item = next(
        (
            item for item in output_items
            if item.get("type") == "function_call_output" and item.get("call_id") == tool_call.get("callId")
        ),
        None,
    )
    if function_call_output_item is None:
        fail("Failed response did not include the function_call_output item.")
    if function_call_output_item.get("output") != expected_tool_result:
        fail(
            "Failed response function_call_output differed: "
            f"{function_call_output_item.get('output')!r} != {expected_tool_result!r}"
        )

    if any(item.get("type") == "message" for item in output_items):
        fail("Failed response unexpectedly included a final assistant message item.")


def verify_provider_log(provider_log_path, expected_model, expected_tool_result):
    entries = [
        json.loads(line)
        for line in provider_log_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(entries) < 2:
        fail(f"Expected at least two upstream streaming requests, saw {len(entries)}.")

    initial_requests = []
    followup_requests = []

    for index, entry in enumerate(entries, start=1):
        body = entry.get("body", {}) or {}
        if body.get("stream") is not True:
            fail(f"Upstream request {index} did not enable streaming.")
        if body.get("model") != expected_model:
            fail(f"Upstream request {index} used unexpected model: {body.get('model')!r}")

        tool_result_messages = []
        for message in body.get("messages", []) or []:
            if isinstance(message, dict) and message.get("role") == "tool":
                tool_result_messages.append(message.get("content", ""))

        request_info = {
            "index": index,
            "toolResultMessages": tool_result_messages,
        }
        if any(expected_tool_result in str(message) for message in tool_result_messages):
            followup_requests.append(request_info)
        else:
            initial_requests.append(request_info)

    if not initial_requests:
        fail("Upstream provider log did not include an initial pre-tool request.")
    if not followup_requests:
        fail("Upstream provider log did not include a follow-up request containing the tool result.")

    return {
        "requestCount": len(entries),
        "initialRequestCount": len(initial_requests),
        "followupRequestCount": len(followup_requests),
        "firstFollowupRequestIndex": followup_requests[0]["index"],
        "toolResultMessages": followup_requests[0]["toolResultMessages"],
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
    if int(provider_snapshot.get("requests", 0)) < 2:
        fail(f"Expected at least two provider requests, saw {provider_snapshot.get('requests')!r}.")
    if int(provider_snapshot.get("errors", 0)) < 1:
        fail(f"Expected at least one provider error, saw {provider_snapshot.get('errors')!r}.")

    recent_turn = next(
        (
            item for item in usage.get("recentTurns", [])
            if item.get("providerId") == expected_provider
            and item.get("modelId") == expected_model
            and item.get("channelId") == "openai-responses"
        ),
        None,
    )
    if recent_turn is None:
        fail("Admin summary did not include a recent turn for the failed Responses request.")
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
        fail("Admin events endpoint returned no LLM events for the failed Responses session.")

    action_counts = collections.Counter()
    groups = {}

    for item in relevant_events:
        action = item.get("action")
        if action:
            action_counts[action] += 1

        correlation_id = item.get("correlationId")
        if correlation_id:
            groups.setdefault(correlation_id, []).append(item)

    if action_counts.get("stream_failed", 0) < 1:
        fail(f"Runtime events did not include stream_failed: {dict(action_counts)!r}")
    if action_counts.get("route_selected", 0) < 2 or action_counts.get("stream_started", 0) < 2:
        fail(f"Runtime events did not include the expected two-step stream attempts: {dict(action_counts)!r}")
    if action_counts.get("stream_completed", 0) < 1:
        fail(f"Runtime events did not include the successful first stream: {dict(action_counts)!r}")

    coherent_failure_groups = []
    for correlation_id, items in groups.items():
        actions = {item.get("action") for item in items}
        if not {"route_selected", "stream_started", "stream_failed"}.issubset(actions):
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
        if expected_provider in provider_ids and expected_model in model_ids:
            coherent_failure_groups.append(correlation_id)

    if not coherent_failure_groups:
        fail("Runtime events did not show a coherent failed streaming LLM event group for the request.")

    return {
        "eventCount": len(relevant_events),
        "actionCounts": dict(sorted(action_counts.items())),
        "coherentFailureCorrelationIds": coherent_failure_groups,
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
    parser.add_argument("--expected-tool-name", required=True)
    parser.add_argument("--expected-tool-arguments-json", required=True)
    parser.add_argument("--expected-tool-result", required=True)
    parser.add_argument("--expected-error-code", required=True)
    parser.add_argument("--expected-error-message-substring", required=True)
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--gateway-request-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    stream = parse_stream(pathlib.Path(args.stream))
    verify_stream(
        stream,
        args.expected_tool_name,
        args.expected_tool_arguments_json,
        args.expected_tool_result,
        args.expected_error_code,
        args.expected_error_message_substring,
    )
    provider = verify_provider_log(pathlib.Path(args.provider_log), args.expected_model, args.expected_tool_result)
    summary = verify_summary(
        pathlib.Path(args.summary),
        args.expected_mode,
        args.expected_orchestrator,
        args.expected_provider,
        args.expected_model,
    )
    events = verify_events(pathlib.Path(args.events), args.expected_provider, args.expected_model, summary["sessionId"])

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

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
    completed_seen = False
    content_part_added_seen = False
    content_part_done_seen = False
    output_item_done_count = 0
    sequence_numbers = []
    text_chunks = []
    tool_calls = {}
    tool_output_items = {}
    tool_deltas = []
    tool_results = []
    event_counts = collections.Counter()
    current_event = None
    current_data = []
    completed_response = None

    def process_sse_message(event_type, payload):
        nonlocal created_seen, in_progress_seen, completed_seen, content_part_added_seen, content_part_done_seen, output_item_done_count, completed_response

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
        elif event_type == "response.completed":
            completed_seen = True
            completed_response = item.get("response")
        elif event_type == "response.content_part.added":
            content_part_added_seen = True
        elif event_type == "response.content_part.done":
            content_part_done_seen = True
        elif event_type == "response.output_text.delta":
            text_chunks.append(item.get("delta", ""))
        elif event_type == "response.output_item.added":
            stream_item = item.get("item") or {}
            if stream_item.get("type") == "function_call":
                tool_calls[stream_item.get("id")] = {
                    "itemId": stream_item.get("id"),
                    "callId": stream_item.get("call_id"),
                    "name": stream_item.get("name"),
                    "arguments": stream_item.get("arguments", ""),
                    "argumentDeltas": [],
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
        elif event_type == "response.function_call_arguments.delta":
            state = tool_calls.setdefault(
                item.get("item_id"),
                {
                    "itemId": item.get("item_id"),
                    "callId": None,
                    "name": None,
                    "arguments": "",
                    "argumentDeltas": [],
                    "outputIndex": item.get("output_index"),
                },
            )
            state["argumentDeltas"].append(item.get("delta", ""))
        elif event_type == "response.function_call_arguments.done":
            state = tool_calls.setdefault(
                item.get("item_id"),
                {
                    "itemId": item.get("item_id"),
                    "callId": None,
                    "name": None,
                    "arguments": "",
                    "argumentDeltas": [],
                    "outputIndex": item.get("output_index"),
                },
            )
            state["arguments"] = item.get("arguments", "")
        elif event_type == "response.openclaw_tool_delta":
            tool_deltas.append(item)
        elif event_type == "response.openclaw_tool_result":
            tool_results.append(item)
        elif event_type == "response.output_item.done":
            output_item_done_count += 1
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

    normalized_tool_calls = []
    for item_id, state in sorted(tool_calls.items(), key=lambda entry: (entry[1].get("outputIndex", 0), entry[0])):
        if state.get("argumentDeltas"):
            arguments = "".join(state["argumentDeltas"])
        else:
            arguments = state.get("arguments", "")

        normalized_tool_calls.append(
            {
                "itemId": item_id,
                "callId": state.get("callId"),
                "name": state.get("name"),
                "arguments": arguments,
                "argumentDeltaCount": len(state.get("argumentDeltas", [])),
                "outputIndex": state.get("outputIndex"),
            }
        )

    return {
        "createdSeen": created_seen,
        "inProgressSeen": in_progress_seen,
        "completedSeen": completed_seen,
        "contentPartAddedSeen": content_part_added_seen,
        "contentPartDoneSeen": content_part_done_seen,
        "outputItemDoneCount": output_item_done_count,
        "sequenceNumbers": sequence_numbers,
        "textChunks": text_chunks,
        "fullText": "".join(text_chunks),
        "toolCalls": normalized_tool_calls,
        "toolOutputItems": sorted(
            tool_output_items.values(),
            key=lambda item: (item.get("outputIndex", 0), item.get("itemId") or ""),
        ),
        "toolDeltas": tool_deltas,
        "toolResults": tool_results,
        "eventCounts": dict(sorted(event_counts.items())),
        "completedResponse": completed_response,
    }


def verify_stream(stream, expected_tool_name, expected_tool_arguments, expected_tool_chunks, expected_tool_result, expected_final_text):
    if not stream["createdSeen"]:
        fail("Responses SSE did not emit response.created.")
    if not stream["inProgressSeen"]:
        fail("Responses SSE did not emit response.in_progress.")
    if not stream["completedSeen"]:
        fail("Responses SSE did not emit response.completed.")
    if not stream["contentPartAddedSeen"]:
        fail("Responses SSE did not emit response.content_part.added.")
    if not stream["contentPartDoneSeen"]:
        fail("Responses SSE did not emit response.content_part.done.")
    if stream["fullText"] != expected_final_text:
        fail(f"Responses SSE assembled unexpected final text: {stream['fullText']!r} != {expected_final_text!r}")
    if not stream["sequenceNumbers"]:
        fail("Responses SSE did not include sequence_number metadata.")
    total_events = sum(stream["eventCounts"].values())
    if len(stream["sequenceNumbers"]) != total_events:
        fail(
            "Responses SSE omitted sequence_number on one or more events: "
            f"{len(stream['sequenceNumbers'])} numbered vs {total_events} total"
        )
    if stream["sequenceNumbers"] != sorted(stream["sequenceNumbers"]):
        fail(f"Responses SSE sequence_number values were not monotonic: {stream['sequenceNumbers']!r}")
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

    tool_chunks = [
        item.get("delta", "")
        for item in stream["toolDeltas"]
        if item.get("tool_name") == expected_tool_name
    ]
    if tool_chunks != expected_tool_chunks:
        fail(
            f"Responses SSE did not include the expected tool delta chunks: "
            f"{tool_chunks!r} != {expected_tool_chunks!r}"
        )

    tool_result = next(
        (
            item for item in stream["toolResults"]
            if item.get("tool_name") == expected_tool_name and expected_tool_result in item.get("content", "")
        ),
        None,
    )
    if tool_result is None:
        fail(
            f"Responses SSE did not include the expected tool result extension "
            f"for {expected_tool_name!r}: {stream['toolResults']!r}"
        )

    tool_output_item = next(
        (
            item for item in stream["toolOutputItems"]
            if item.get("callId") == tool_call.get("callId") and item.get("output") == expected_tool_result
        ),
        None,
    )
    if tool_output_item is None:
        fail(
            "Responses SSE did not include a standard function_call_output item "
            f"for {expected_tool_name!r}: {stream['toolOutputItems']!r}"
        )

    completed_response = stream.get("completedResponse") or {}
    if completed_response.get("status") != "completed":
        fail(f"Completed response reported unexpected status: {completed_response.get('status')!r}")

    output_items = completed_response.get("output") or []
    function_call_item = next(
        (item for item in output_items if item.get("type") == "function_call" and item.get("name") == expected_tool_name),
        None,
    )
    if function_call_item is None:
        fail("Completed response did not include the function_call output item.")

    function_call_output_item = next(
        (
            item for item in output_items
            if item.get("type") == "function_call_output"
            and item.get("call_id") == function_call_item.get("call_id")
        ),
        None,
    )
    if function_call_output_item is None:
        fail("Completed response did not include the function_call_output item.")
    if function_call_output_item.get("output") != expected_tool_result:
        fail(
            "Completed response function_call_output differed: "
            f"{function_call_output_item.get('output')!r} != {expected_tool_result!r}"
        )

    message_item = next((item for item in output_items if item.get("type") == "message"), None)
    if message_item is None:
        fail("Completed response did not include the final assistant message item.")

    content = message_item.get("content") or []
    text = "".join(part.get("text", "") for part in content if isinstance(part, dict))
    if text != expected_final_text:
        fail(f"Completed response message text differed: {text!r} != {expected_final_text!r}")


def verify_provider_log(provider_log_path, expected_provider, expected_model, expected_tool_result):
    entries = [
        json.loads(line)
        for line in provider_log_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(entries) < 2:
        fail(f"Expected at least two upstream streaming requests, saw {len(entries)}.")

    tool_result_messages = []
    for entry in entries:
        body = entry.get("body") or {}
        if body.get("model") != expected_model:
            fail(f"Provider request used unexpected model: {body.get('model')!r}")
        if body.get("stream") is not True:
            fail(f"Provider request did not enable streaming: {body.get('stream')!r}")

        for message in body.get("messages", []) or []:
            if isinstance(message, dict) and message.get("role") == "tool":
                tool_result_messages.append(message.get("content", ""))

    if not any(expected_tool_result in str(message) for message in tool_result_messages):
        fail(f"Upstream provider log did not capture the expected tool result: {tool_result_messages!r}")

    if expected_provider != "openai-compatible":
        fail(f"Unexpected provider assertion input: {expected_provider!r}")

    return {
        "requestCount": len(entries),
        "toolResultMessages": tool_result_messages,
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
    if int(provider_snapshot.get("outputTokens", 0)) <= 0:
        fail(f"Expected output tokens to be recorded, saw {provider_snapshot.get('outputTokens')!r}.")

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
        fail("Admin summary did not include a recent turn for the Responses streaming request.")
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
        fail("Admin events endpoint returned no LLM events for the Responses streaming session.")

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
        fail("Runtime events did not show coherent streaming LLM event groups for the request.")

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
    parser.add_argument("--expected-tool-name", required=True)
    parser.add_argument("--expected-tool-arguments-json", required=True)
    parser.add_argument("--expected-tool-chunks-json", required=True)
    parser.add_argument("--expected-tool-result", required=True)
    parser.add_argument("--expected-final-text", required=True)
    parser.add_argument("--gateway-startup-ms", type=int)
    parser.add_argument("--gateway-request-ms", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()

    stream_path = pathlib.Path(args.stream)
    provider_log_path = pathlib.Path(args.provider_log)
    summary_path = pathlib.Path(args.summary)
    events_path = pathlib.Path(args.events)
    expected_tool_chunks = json.loads(args.expected_tool_chunks_json)

    stream = parse_stream(stream_path)
    verify_stream(
        stream,
        args.expected_tool_name,
        args.expected_tool_arguments_json,
        expected_tool_chunks,
        args.expected_tool_result,
        args.expected_final_text,
    )
    provider = verify_provider_log(
        provider_log_path,
        args.expected_provider,
        args.expected_model,
        args.expected_tool_result,
    )
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

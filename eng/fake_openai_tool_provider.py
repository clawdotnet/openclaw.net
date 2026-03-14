#!/usr/bin/env python3
import argparse
import json
import signal
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SERVER = None


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
                item_type = item.get("type")
                if item_type in {"text", "output_text", "input_text"}:
                    parts.append(item.get("text", ""))
                elif "text" in item:
                    parts.append(str(item.get("text", "")))
                elif "content" in item:
                    parts.append(extract_text(item.get("content")))
            else:
                parts.append(str(item))
        return "".join(parts)
    if isinstance(value, dict):
        if "text" in value:
            return str(value.get("text", ""))
        if "content" in value:
            return extract_text(value.get("content"))
    return str(value)


class FakeProviderHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    request_count = 0
    log_path = ""
    tool_name = "memory"
    note_key = "aot-smoke"
    note_content = "from published maf aot"
    model_name = "fake-maf-model"
    tool_arguments_json = None
    stream_chunks = None
    stream_delay_ms = 0
    stream_tool_name = None
    stream_tool_arguments_json = None
    stream_final_chunks = None
    stream_final_template = "Tool streamed: {tool_result}"
    fail_on_stream_final = False
    fail_status_code = 500
    fail_error_message = "simulated upstream failure"
    followup_triggers = None
    followup_required_fragment = None
    followup_response_template = None

    def do_GET(self):
        if self.path == "/health":
            body = b"ok"
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        self.send_response(404)
        self.end_headers()

    def do_POST(self):
        content_length = int(self.headers.get("Content-Length", "0"))
        raw_body = self.rfile.read(content_length).decode("utf-8")
        payload = json.loads(raw_body) if raw_body else {}
        messages = payload.get("messages", []) if isinstance(payload, dict) else []
        all_message_text = "\n".join(
            extract_text(message.get("content"))
            for message in messages
            if isinstance(message, dict)
        )
        last_user_text = next(
            (
                extract_text(message.get("content"))
                for message in reversed(messages)
                if isinstance(message, dict) and message.get("role") == "user"
            ),
            "",
        )

        FakeProviderHandler.request_count += 1
        self._append_log(
            {
                "index": FakeProviderHandler.request_count,
                "path": self.path,
                "body": payload,
            }
        )

        tool_message = next(
            (
                message
                for message in messages
                if isinstance(message, dict) and message.get("role") == "tool"
            ),
            None,
        )

        followup_response = self._build_followup_response(last_user_text, all_message_text)

        if payload.get("stream"):
            if followup_response is not None:
                self._stream_text_response(self._split_text_chunks(followup_response))
                return
            if tool_message is None and FakeProviderHandler.stream_tool_name:
                self._stream_tool_call_response()
            elif tool_message is not None and FakeProviderHandler.fail_on_stream_final:
                self._error_response(FakeProviderHandler.fail_status_code, FakeProviderHandler.fail_error_message)
            elif tool_message is not None and FakeProviderHandler.stream_tool_name:
                self._stream_text_response(self._build_stream_final_chunks(extract_text(tool_message.get("content"))))
            else:
                self._stream_text_response(FakeProviderHandler.stream_chunks or ["Hello ", "streaming"])
            return

        if followup_response is not None:
            response = self._assistant_response(followup_response)
        elif tool_message is None:
            response = self._tool_call_response()
        else:
            response = self._final_response(extract_text(tool_message.get("content")))

        encoded = json.dumps(response).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, format, *args):
        return

    def _append_log(self, entry):
        with open(FakeProviderHandler.log_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(entry) + "\n")

    def _tool_call_response(self):
        arguments = (
            FakeProviderHandler.tool_arguments_json
            if FakeProviderHandler.tool_arguments_json is not None
            else json.dumps(
                {
                    "action": "write",
                    "key": FakeProviderHandler.note_key,
                    "content": FakeProviderHandler.note_content,
                }
            )
        )
        return {
            "id": "chatcmpl-fake-tool",
            "object": "chat.completion",
            "created": int(time.time()),
            "model": FakeProviderHandler.model_name,
            "choices": [
                {
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": None,
                        "tool_calls": [
                            {
                                "id": "call_memory_1",
                                "type": "function",
                                "function": {
                                    "name": FakeProviderHandler.tool_name,
                                    "arguments": arguments,
                                },
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
            "usage": {
                "prompt_tokens": 16,
                "completion_tokens": 8,
                "total_tokens": 24,
            },
        }

    def _error_response(self, status_code, message):
        response = {
            "error": {
                "message": message,
                "type": "server_error",
                "code": "simulated_failure",
            }
        }
        encoded = json.dumps(response).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def _final_response(self, tool_result):
        content = f"Tool said: {tool_result}".strip()
        return self._assistant_response(content)

    def _assistant_response(self, content):
        return {
            "id": "chatcmpl-fake-final",
            "object": "chat.completion",
            "created": int(time.time()),
            "model": FakeProviderHandler.model_name,
            "choices": [
                {
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": content,
                    },
                    "finish_reason": "stop",
                }
            ],
            "usage": {
                "prompt_tokens": 24,
                "completion_tokens": 10,
                "total_tokens": 34,
            },
        }

    def _build_followup_response(self, last_user_text, all_message_text):
        triggers = [item.lower() for item in (FakeProviderHandler.followup_triggers or []) if item]
        if not triggers:
            return None

        last_user_normalized = (last_user_text or "").lower()
        if not any(trigger in last_user_normalized for trigger in triggers):
            return None

        required_fragment = FakeProviderHandler.followup_required_fragment
        if required_fragment and required_fragment not in all_message_text:
            return "Missing prior note context."

        template = FakeProviderHandler.followup_response_template or "Earlier note was {note_key}."
        return template.format(
            note_key=FakeProviderHandler.note_key,
            note_content=FakeProviderHandler.note_content,
            required_fragment=required_fragment or "",
        )

    def _stream_text_response(self, chunks):
        created = int(time.time())

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        self._write_sse(self._assistant_role_chunk(created))

        for chunk in chunks:
            self._write_sse(
                {
                    "id": "chatcmpl-fake-stream",
                    "object": "chat.completion.chunk",
                    "created": created,
                    "model": FakeProviderHandler.model_name,
                    "choices": [
                        {
                            "index": 0,
                            "delta": {
                                "content": chunk,
                            },
                        }
                    ],
                }
            )

        self._write_sse(
            {
                "id": "chatcmpl-fake-stream",
                "object": "chat.completion.chunk",
                "created": created,
                "model": FakeProviderHandler.model_name,
                "choices": [
                    {
                        "index": 0,
                        "delta": {},
                        "finish_reason": "stop",
                    }
                ],
            }
        )
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def _stream_tool_call_response(self):
        created = int(time.time())
        arguments = (
            FakeProviderHandler.stream_tool_arguments_json
            if FakeProviderHandler.stream_tool_arguments_json is not None
            else json.dumps({"chunks": ["a", "b", "c"]})
        )
        call_id = "call_stream_1"
        argument_chunks = self._split_argument_chunks(arguments)

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        self._write_sse(
            {
                "id": "chatcmpl-fake-stream-tool",
                "object": "chat.completion.chunk",
                "created": created,
                "model": FakeProviderHandler.model_name,
                "choices": [
                    {
                        "index": 0,
                        "delta": {
                            "role": "assistant",
                            "tool_calls": [
                                {
                                    "index": 0,
                                    "id": call_id,
                                    "type": "function",
                                    "function": {
                                        "name": FakeProviderHandler.stream_tool_name,
                                        "arguments": "",
                                    },
                                }
                            ],
                        },
                    }
                ],
            }
        )

        for chunk in argument_chunks:
            self._write_sse(
                {
                    "id": "chatcmpl-fake-stream-tool",
                    "object": "chat.completion.chunk",
                    "created": created,
                    "model": FakeProviderHandler.model_name,
                    "choices": [
                        {
                            "index": 0,
                            "delta": {
                                "tool_calls": [
                                    {
                                        "index": 0,
                                        "function": {
                                            "arguments": chunk,
                                        },
                                    }
                                ],
                            },
                        }
                    ],
                }
            )

        self._write_sse(
            {
                "id": "chatcmpl-fake-stream-tool",
                "object": "chat.completion.chunk",
                "created": created,
                "model": FakeProviderHandler.model_name,
                "choices": [
                    {
                        "index": 0,
                        "delta": {},
                        "finish_reason": "tool_calls",
                    }
                ],
            }
        )
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def _assistant_role_chunk(self, created):
        return {
            "id": "chatcmpl-fake-stream",
            "object": "chat.completion.chunk",
            "created": created,
            "model": FakeProviderHandler.model_name,
            "choices": [
                {
                    "index": 0,
                    "delta": {
                        "role": "assistant",
                    },
                }
            ],
        }

    def _write_sse(self, payload):
        encoded = json.dumps(payload).encode("utf-8")
        self.wfile.write(b"data: " + encoded + b"\n\n")
        self.wfile.flush()
        if FakeProviderHandler.stream_delay_ms > 0:
            time.sleep(FakeProviderHandler.stream_delay_ms / 1000.0)

    def _build_stream_final_chunks(self, tool_result):
        if FakeProviderHandler.stream_final_chunks is not None:
            return [
                chunk.replace("{tool_result}", tool_result)
                for chunk in FakeProviderHandler.stream_final_chunks
            ]

        rendered = FakeProviderHandler.stream_final_template.replace("{tool_result}", tool_result)
        if len(rendered) < 2:
            return [rendered]

        midpoint = max(1, len(rendered) // 2)
        return [rendered[:midpoint], rendered[midpoint:]]

    def _split_argument_chunks(self, arguments):
        if len(arguments) < 2:
            return [arguments]

        midpoint = max(1, len(arguments) // 2)
        return [arguments[:midpoint], arguments[midpoint:]]

    def _split_text_chunks(self, text):
        if len(text) < 2:
            return [text]

        midpoint = max(1, len(text) // 2)
        return [text[:midpoint], text[midpoint:]]


class ReusableThreadingHTTPServer(ThreadingHTTPServer):
    allow_reuse_address = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--log", required=True)
    parser.add_argument("--tool-name", default="memory")
    parser.add_argument("--note-key", default="aot-smoke")
    parser.add_argument("--note-content", default="from published maf aot")
    parser.add_argument("--model", default="fake-maf-model")
    parser.add_argument("--tool-arguments-json")
    parser.add_argument("--stream-chunks-json")
    parser.add_argument("--stream-delay-ms", type=int, default=0)
    parser.add_argument("--stream-tool-name")
    parser.add_argument("--stream-tool-arguments-json")
    parser.add_argument("--stream-final-chunks-json")
    parser.add_argument("--stream-final-template", default="Tool streamed: {tool_result}")
    parser.add_argument("--fail-on-stream-final", action="store_true")
    parser.add_argument("--fail-status-code", type=int, default=500)
    parser.add_argument("--fail-error-message", default="simulated upstream failure")
    parser.add_argument("--followup-trigger", action="append", default=[])
    parser.add_argument("--followup-required-fragment")
    parser.add_argument("--followup-response-template")
    args = parser.parse_args()

    FakeProviderHandler.log_path = args.log
    FakeProviderHandler.tool_name = args.tool_name
    FakeProviderHandler.note_key = args.note_key
    FakeProviderHandler.note_content = args.note_content
    FakeProviderHandler.model_name = args.model
    FakeProviderHandler.tool_arguments_json = args.tool_arguments_json
    FakeProviderHandler.stream_chunks = json.loads(args.stream_chunks_json) if args.stream_chunks_json else None
    FakeProviderHandler.stream_delay_ms = max(0, args.stream_delay_ms)
    FakeProviderHandler.stream_tool_name = args.stream_tool_name
    FakeProviderHandler.stream_tool_arguments_json = args.stream_tool_arguments_json
    FakeProviderHandler.stream_final_chunks = json.loads(args.stream_final_chunks_json) if args.stream_final_chunks_json else None
    FakeProviderHandler.stream_final_template = args.stream_final_template
    FakeProviderHandler.fail_on_stream_final = args.fail_on_stream_final
    FakeProviderHandler.fail_status_code = args.fail_status_code
    FakeProviderHandler.fail_error_message = args.fail_error_message
    FakeProviderHandler.followup_triggers = args.followup_trigger
    FakeProviderHandler.followup_required_fragment = args.followup_required_fragment
    FakeProviderHandler.followup_response_template = args.followup_response_template

    global SERVER
    SERVER = ReusableThreadingHTTPServer(("127.0.0.1", args.port), FakeProviderHandler)

    def handle_shutdown(signum, frame):
        del signum, frame
        if SERVER is not None:
            threading.Thread(target=SERVER.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, handle_shutdown)
    signal.signal(signal.SIGINT, handle_shutdown)

    try:
        SERVER.serve_forever()
    finally:
        if SERVER is not None:
            SERVER.server_close()


if __name__ == "__main__":
    main()

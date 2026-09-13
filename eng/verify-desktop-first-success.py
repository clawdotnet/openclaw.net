#!/usr/bin/env python3
"""Exercise packaged CLI/setup and gateway through an Ollama-compatible tool round trip."""

import argparse
import base64
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


FINAL_TEXT = "desktop packaged first success complete"
NOTE_KEY = "desktop-release-contract"
NOTE_TEXT = "verified desktop release memory sentinel"


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


class OllamaFixtureHandler(BaseHTTPRequestHandler):
    requests: list[dict] = []

    def do_GET(self) -> None:
        if self.path == "/health":
            self.send_json({"status": "ok"})
        else:
            self.send_error(404)

    def do_POST(self) -> None:
        if self.path != "/api/chat":
            self.send_error(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(length).decode("utf-8"))
        self.requests.append(payload)
        tool_results = [message for message in payload.get("messages", []) if message.get("role") == "tool"]
        if tool_results:
            valid = any(message.get("content") == NOTE_TEXT for message in tool_results)
            message = {"role": "assistant", "content": FINAL_TEXT if valid else "unexpected tool result"}
        else:
            message = {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                    {"function": {"name": "memory_get", "arguments": {"key": NOTE_KEY}}}
                ],
            }
        self.send_json({"message": message, "done": True, "done_reason": "stop", "prompt_eval_count": 12, "eval_count": 4})

    def send_json(self, payload: dict) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: object) -> None:
        del format, args


def read_json(
    url: str,
    token: str | None = None,
    payload: dict | None = None,
    timeout: float = 5,
) -> dict:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = Request(url, data=data, headers=headers, method="POST" if payload is not None else "GET")
    with urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def read_gateway_log(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace") if path.exists() else ""


def wait_for_health(url: str, process: subprocess.Popen[str], log_path: Path) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(
                f"Packaged gateway exited before readiness.\n{read_gateway_log(log_path)}"
            )
        try:
            with urlopen(url, timeout=5) as response:
                response.read()
            return
        except (HTTPError, URLError, TimeoutError):
            time.sleep(0.25)
    raise TimeoutError("Packaged gateway did not become healthy within 30 seconds.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", required=True)
    parser.add_argument("--gateway", required=True)
    args = parser.parse_args()

    cli = str(Path(args.cli).resolve(strict=True))
    gateway = str(Path(args.gateway).resolve(strict=True))
    OllamaFixtureHandler.requests = []
    provider = ThreadingHTTPServer(("127.0.0.1", 0), OllamaFixtureHandler)
    provider_port = provider.server_port
    gateway_port = free_port()
    provider_thread = threading.Thread(target=provider.serve_forever, daemon=True)
    provider_thread.start()

    try:
        with tempfile.TemporaryDirectory(prefix="openclaw-desktop-first-success-") as temporary:
            root = Path(temporary)
            config_path = root / "config" / "openclaw.settings.json"
            workspace = root / "workspace"
            setup = subprocess.run(
                [
                    cli,
                    "setup",
                    "--non-interactive",
                    "--profile", "local",
                    "--workspace", str(workspace),
                    "--provider", "ollama",
                    "--model", "fixture-model",
                    "--model-preset", "ollama-agentic",
                    "--config", str(config_path),
                ],
                capture_output=True,
                text=True,
                timeout=60,
            )
            if setup.returncode:
                raise RuntimeError(f"Packaged CLI setup failed.\n{setup.stdout}\n{setup.stderr}")

            config = json.loads(config_path.read_text(encoding="utf-8-sig"))
            openclaw = config["OpenClaw"]
            openclaw["port"] = gateway_port
            openclaw["llm"]["endpoint"] = f"http://127.0.0.1:{provider_port}"
            profile = openclaw["models"]["profiles"][0]
            profile["baseUrl"] = f"http://127.0.0.1:{provider_port}"
            if profile["presetId"] != "ollama-agentic":
                raise AssertionError("Packaged CLI did not persist the ollama-agentic preset.")
            capabilities = profile["capabilities"]
            if capabilities["supportsTools"] is not True or capabilities["supportsParallelToolCalls"] is not False:
                raise AssertionError("Packaged CLI did not persist sequential tool capabilities.")
            config_path.write_text(json.dumps(config), encoding="utf-8")
            # Seed the file-memory store so errors or missing notes cannot satisfy the gate.
            notes = Path(openclaw["memory"]["storagePath"]) / "notes"
            notes.mkdir(parents=True, exist_ok=True)
            encoded_key = base64.urlsafe_b64encode(NOTE_KEY.encode()).decode().rstrip("=")
            (notes / f"{encoded_key}.md").write_text(NOTE_TEXT, encoding="utf-8")

            gateway_log_path = root / "gateway.log"
            with gateway_log_path.open("w", encoding="utf-8") as gateway_log:
                process = subprocess.Popen(
                    [gateway, "--config", str(config_path)],
                    stdout=gateway_log,
                    stderr=subprocess.STDOUT,
                    text=True,
                    cwd=root,
                )
                try:
                    base_url = f"http://127.0.0.1:{gateway_port}"
                    wait_for_health(f"{base_url}/health", process, gateway_log_path)
                    response = read_json(
                        f"{base_url}/v1/chat/completions",
                        openclaw.get("authToken"),
                        {
                            "model": "local-primary",
                            "messages": [{"role": "user", "content": "Read the desktop release contract memory note."}],
                        },
                        timeout=60,
                    )
                    rendered = json.dumps(response)
                    if FINAL_TEXT not in rendered:
                        raise AssertionError(f"Gateway response did not contain the fixture completion: {rendered}")
                except Exception as error:
                    gateway_log.flush()
                    raise RuntimeError(
                        f"{error}\nPackaged gateway log:\n{read_gateway_log(gateway_log_path)}"
                    ) from error
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

            if len(OllamaFixtureHandler.requests) != 2:
                raise AssertionError(f"Expected two sequential provider requests, got {len(OllamaFixtureHandler.requests)}.")
            first, second = OllamaFixtureHandler.requests
            if any(request.get("model") != "fixture-model" for request in (first, second)):
                raise AssertionError("Gateway did not use the configured model.")
            if len(first.get("tools", [])) < 2:
                raise AssertionError("Gateway did not advertise multiple tools to the sequential-tool model.")
            if not any(message.get("role") == "tool" and message.get("content") == NOTE_TEXT for message in second.get("messages", [])):
                raise AssertionError("Gateway did not return the tool result before requesting the final response.")
    finally:
        provider.shutdown()
        provider.server_close()
        provider_thread.join(timeout=5)

    print("Packaged Ollama Agentic setup, gateway start, and sequential tool round trip passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

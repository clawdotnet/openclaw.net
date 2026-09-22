"""Serve preloaded Laya checkpoints on loopback. No model downloads at runtime."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import socket
import threading
from .protocol import Rejected, canonical, read_json


class DecisionServer(ThreadingHTTPServer):
    daemon_threads = True
    block_on_close = False

    def __init__(self, port, runtime):
        self.runtime = runtime
        self.inference = threading.Lock()
        self.connections = threading.BoundedSemaphore(8)
        super().__init__(("127.0.0.1", port), Handler)

    def process_request(self, request, client_address):
        if not self.connections.acquire(blocking=False):
            request.settimeout(1)
            try:
                request.sendall(b"HTTP/1.0 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n")
            except OSError:
                # The overloaded peer may disconnect before receiving the response.
                pass
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.connections.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.connections.release()

    def handle_error(self, request, client_address):
        # No request bodies, headers, or exceptions in logs.
        pass


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        self.request.settimeout(10)
        super().setup()

    def log_message(self, *args):
        # Request metadata can contain sensitive local prompt information.
        pass

    def send_json(self, status, value):
        body = canonical(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def local_request(self):
        if self.headers.get("Origin") is not None or self.headers.get("Host") != f"127.0.0.1:{self.server.server_port}":
            self.send_json(403, {"error": "local_clients_only"})
            return False
        return True

    def do_GET(self):
        if self.local_request():
            self.send_json(200, self.server.runtime.health()) if self.path == "/health" else self.send_json(404, {"error": "not_found"})

    def do_POST(self):
        if not self.local_request():
            return
        if self.path != "/v1/decisions":
            self.send_json(404, {"error": "not_found"})
            return
        if self.headers.get("Transfer-Encoding") or self.headers.get_content_type() != "application/json":
            self.send_json(415, {"error": "json_content_length_required"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        if not 0 < length <= 65536:
            self.send_json(413, {"error": "request_size"})
            return
        if not self.server.inference.acquire(blocking=False):
            self.send_json(503, {"error": "busy"})
            return
        try:
            try:
                raw = self.rfile.read(length)
                if len(raw) != length:
                    raise Rejected("incomplete_body")
                request = read_json(raw.decode("utf-8"))
            except (Rejected, UnicodeError, ValueError, TypeError, RecursionError) as exc:
                self.send_json(422, {"error": str(exc) if isinstance(exc, Rejected) else "invalid_request"})
                return
            try:
                result = self.server.runtime.predict(request)
                self.send_json(200, result)
            except Rejected as exc:
                self.send_json(422, {"error": str(exc)})
            except (socket.timeout, ConnectionError, BrokenPipeError):
                # The local client controls its own deadline and may close first.
                pass
            except Exception:
                self.send_json(503, {"error": "inference_failed"})
        finally:
            self.server.inference.release()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--calibration")
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--device", choices=("cpu", "mps", "cuda"), default="cpu")
    parser.add_argument("--checkpoint", choices=("auto", "english", "multilingual", "typed-decisions"), default="auto")
    parser.add_argument("--threads", type=int, default=4)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 1 <= args.threads <= 32:
        parser.error("Invalid port or thread count.")
    from .runtime import Runtime
    runtime = Runtime(args.manifest, args.calibration, args.device, args.checkpoint, args.threads)
    with DecisionServer(args.port, runtime) as server:
        print(json.dumps({"listening": f"http://127.0.0.1:{args.port}", **runtime.health()}), flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            # Normal interactive shutdown.
            pass


if __name__ == "__main__":
    main()

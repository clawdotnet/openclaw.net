"""The packaged gate must reject error responses masquerading as a tool round trip."""
import importlib.util
from pathlib import Path
import threading
import unittest
from http.server import ThreadingHTTPServer

spec = importlib.util.spec_from_file_location("desktop_smoke", Path(__file__).with_name("verify-desktop-first-success.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class ToolResultContractTests(unittest.TestCase):
    def test_only_known_memory_result_completes_turn(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), smoke.OllamaFixtureHandler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            for content in ("Error: tool denied", "Note 'desktop-release-contract' not found.", smoke.NOTE_TEXT):
                with self.subTest(content=content):
                    response = smoke.read_json(
                        f"http://127.0.0.1:{server.server_port}/api/chat",
                        payload={"messages": [{"role": "tool", "content": content}]},
                    )
                    self.assertEqual(response["message"]["content"] == smoke.FINAL_TEXT, content == smoke.NOTE_TEXT)
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=5)


if __name__ == "__main__":
    unittest.main()

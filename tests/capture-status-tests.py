#!/usr/bin/env python3
"""Local HTTP contract tests for the status capture CLI; no external services."""
import http.server
import json
import pathlib
import subprocess
import sys
import tempfile
import threading
import unittest

TOOL = pathlib.Path(__file__).resolve().parents[1] / "tools" / "capture-status.py"


class Handler(http.server.BaseHTTPRequestHandler):
    requests = 0

    def do_GET(self):
        Handler.requests += 1
        self.send_response(503 if Handler.requests == 3 else 200)
        if Handler.requests == 1:
            self.send_header("Server-Timing", ", ".join(
                f"{name};dur=0.142" for name in (
                    "registry", "registry_open", "registry_execute", "registry_reader",
                    "registry_projection", "server_headers")))
        elif Handler.requests == 2:
            self.send_header("Server-Timing", "registry;dur=invalid, registry_open;dur=nan")
        self.end_headers()
        self.wfile.write(b'{}')

    def log_message(self, *_args):
        pass


class CaptureTests(unittest.TestCase):
    def test_local_http_capture_reports_percentiles_failures_and_missing_headers(self):
        Handler.requests = 0
        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                with tempfile.TemporaryDirectory() as directory:
                    output = pathlib.Path(directory) / "report.json"
                    result = subprocess.run([
                        sys.executable, str(TOOL), f"http://127.0.0.1:{server.server_port}",
                        "--count", "3", "--interval", "0", "--output", str(output)
                    ], capture_output=True, text=True, timeout=15, check=True)
                    report = json.loads(output.read_text())
                    self.assertEqual(3, report["attempts"])
                    self.assertEqual(1, report["non_200_or_transport"])
                    self.assertEqual(1, report["missing_or_invalid_timings"]["registry"])
                    self.assertEqual({"n": 1, "p50": 0.142, "p95": 0.142, "p99": 0.142},
                                     report["summary"]["registry"])
                    self.assertEqual(2, report["summary"]["http_ms"]["n"])
                    self.assertEqual(3, json.loads(result.stdout)["attempts"])
            finally:
                server.shutdown()
                thread.join()

    def test_transport_failure_is_reported(self):
        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            port = server.server_port
        with tempfile.TemporaryDirectory() as directory:
            output = pathlib.Path(directory) / "report.json"
            subprocess.run([sys.executable, str(TOOL), f"http://127.0.0.1:{port}",
                            "--count", "1", "--interval", "0", "--output", str(output)],
                           capture_output=True, timeout=15, check=True)
            report = json.loads(output.read_text())
            self.assertEqual("transport", report["samples"][0]["error"])
            self.assertEqual(0, report["summary"]["http_ms"]["n"])


if __name__ == "__main__":
    unittest.main()

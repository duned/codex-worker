#!/usr/bin/env python3
"""Local HTTP contract tests for the status capture CLI; no external services."""
import http.server
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

TOOL = pathlib.Path(__file__).resolve().parents[1] / "tools" / "capture-status.py"

spec = importlib.util.spec_from_file_location("capture_status", TOOL)
capture_status = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture_status)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
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
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b'{}')

    def log_message(self, *_args):
        pass


class CaptureTests(unittest.TestCase):
    def test_client_boundaries_are_disjoint_and_preserve_submillisecond_headers(self):
        for established in (True, False):
            with self.subTest(established=established):
                connection = Mock()
                connection.sock = None if established else object()
                response = connection.getresponse.return_value
                response.status = 200
                response.read.side_effect = [b"{}", b""]
                response.getheader.return_value = "registry_execute;dur=0.102, server_headers;dur=0.192"
                with patch.object(capture_status.time, "perf_counter", side_effect=[0, .010, .250, .260]):
                    sample = capture_status.capture(connection)
                self.assertEqual(established, sample["connection_established"])
                self.assertEqual(int(established), connection.connect.call_count)
                self.assertAlmostEqual(10, sample["connect_ms"])
                self.assertAlmostEqual(240, sample["request_headers_ms"])
                self.assertAlmostEqual(10, sample["body_read_ms"])
                self.assertAlmostEqual(sample["http_ms"], sum(sample[key] for key in
                                       ("connect_ms", "request_headers_ms", "body_read_ms")))
                self.assertEqual(.102, sample["registry_execute"])
                self.assertAlmostEqual(259.808, sample["http_after_headers_ms"])
                self.assertNotIn("{}", json.dumps(sample))

    def test_fresh_mode_closes_connection_before_every_capture(self):
        with tempfile.TemporaryDirectory() as directory:
            output = pathlib.Path(directory) / "report.json"
            with patch.object(sys, "argv", [str(TOOL), "http://localhost", "--count", "2",
                                           "--interval", "0", "--connection", "fresh", "--output", str(output)]), \
                    patch.object(capture_status.http.client, "HTTPConnection") as factory, \
                    patch.object(capture_status, "capture", return_value={"utc": "test", "http_status": 200}), \
                    patch("builtins.print"):
                capture_status.main()
            self.assertEqual(3, factory.return_value.close.call_count)  # each sample + final cleanup
            report = json.loads(output.read_text())
            self.assertEqual("fresh", report["connection_mode"])
            self.assertEqual(2, report["successful"])

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
                    self.assertEqual(1, report["http_failures"])
                    self.assertEqual(0, report["transport_failures"])
                    self.assertEqual([True, False, False],
                                     [sample["connection_established"] for sample in report["samples"]])
                    self.assertEqual(2, report["summary"]["body_read_ms"]["n"])
                    self.assertEqual(1, report["missing_or_invalid_timings"]["registry"])
                    self.assertEqual({"n": 1, "p50": 0.142, "p95": 0.142, "p99": 0.142},
                                     report["summary"]["registry"])
                    self.assertEqual(2, report["summary"]["http_ms"]["n"])
                    self.assertEqual(1, report["summary"]["http_after_headers_ms"]["n"])
                    paired = report["samples"][0]
                    self.assertAlmostEqual(paired["http_ms"] - paired["server_headers"],
                                           paired["http_after_headers_ms"])
                    self.assertNotIn("http_after_headers_ms", report["samples"][1])
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
            self.assertEqual(1, report["transport_failures"])
            self.assertEqual(0, report["http_failures"])
            self.assertEqual(0, report["summary"]["http_ms"]["n"])


if __name__ == "__main__":
    unittest.main()

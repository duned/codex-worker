#!/usr/bin/env python3
"""Capture status timings without response bodies, URLs or per-poll logging."""
import argparse
import contextlib
import datetime
import http.client
import json
import math
import time
import urllib.parse


TIMINGS = ("registry", "registry_open", "registry_execute", "registry_reader", "registry_projection", "server_headers")

CLIENT_TIMINGS = ("http_ms", "connect_ms", "request_headers_ms", "body_read_ms", "http_after_headers_ms")


def capture(connection):
    """Measure disjoint client boundaries; never retain response bodies or error text."""
    started_at = datetime.datetime.now(datetime.timezone.utc).isoformat()
    started = time.perf_counter()
    try:
        established = connection.sock is None
        if established:
            connection.connect()  # DNS + TCP + TLS (HTTPS); no request sent yet.
        connected = time.perf_counter()
        connection.request("GET", "/api/status")
        response = connection.getresponse()
        headers = time.perf_counter()
        while response.read(64 * 1024):
            pass  # Include the full body in RTT without retaining it.
        finished = time.perf_counter()
        elapsed = (finished - started) * 1000
        sample = {
            "utc": started_at, "http_status": response.status, "http_ms": elapsed,
            "connection_established": established,
            "connect_ms": (connected - started) * 1000,
            "request_headers_ms": (headers - connected) * 1000,
            "body_read_ms": (finished - headers) * 1000,
        }
        for entry in (response.getheader("Server-Timing") or "").split(","):
            name, _, duration = entry.strip().partition(";dur=")
            if name in TIMINGS:
                try:
                    value = float(duration)
                    if math.isfinite(value) and value >= 0:
                        sample[name] = value
                except ValueError:
                    pass
        # Headers exclude body writing: this paired residual is not network time.
        if "server_headers" in sample:
            sample["http_after_headers_ms"] = elapsed - sample["server_headers"]
        sample["missing_timings"] = [key for key in TIMINGS if key not in sample]
        return sample
    except (OSError, http.client.HTTPException):
        connection.close()
        return {"utc": started_at, "error": "transport", "http_ms": (time.perf_counter() - started) * 1000}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("origin", help="Server origin, e.g. http://127.0.0.1:5000")
    parser.add_argument("--count", type=int, default=300)
    parser.add_argument("--interval", type=float, default=5)
    parser.add_argument("--output", required=True)
    parser.add_argument("--connection", choices=("reuse", "fresh"), default="reuse",
                        help="Reuse HTTP/1.1 connections or establish a new connection per request")
    args = parser.parse_args()
    origin = urllib.parse.urlsplit(args.origin)
    if (origin.scheme not in ("http", "https") or not origin.hostname or origin.username
            or origin.password or origin.path not in ("", "/") or origin.query or origin.fragment
            or args.count < 1 or not math.isfinite(args.interval) or args.interval < 0):
        parser.error("Use an HTTP(S) origin without credentials and positive count/nonnegative interval")
    factory = http.client.HTTPSConnection if origin.scheme == "https" else http.client.HTTPConnection
    samples = []
    with contextlib.closing(factory(origin.hostname, origin.port, timeout=60)) as connection:
        for index in range(args.count):
            started = time.perf_counter()
            if args.connection == "fresh":
                connection.close()
            samples.append(capture(connection))
            if index + 1 < args.count:
                time.sleep(max(0, args.interval - (time.perf_counter() - started)))
    successful = [s for s in samples if s.get("http_status") == 200]
    summary = {}
    for key in (*CLIENT_TIMINGS, *TIMINGS):
        values = sorted(s[key] for s in successful if key in s)
        summary[key] = {"n": len(values), **{
            f"p{p}": values[max(0, math.ceil(len(values) * p / 100) - 1)] if values else None
            for p in (50, 95, 99)}}
    report = {
        "method": "HTTP/1.1; nearest-rank percentiles; milliseconds",
        "connection_mode": args.connection,
        "window_start_utc": samples[0]["utc"],
        "window_end_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "interval_seconds": args.interval, "attempts": len(samples),
        "successful": len(successful),
        "transport_failures": sum("error" in s for s in samples),
        "http_failures": sum("http_status" in s and s["http_status"] != 200 for s in samples),
        "non_200_or_transport": len(samples) - len(successful),
        "missing_or_invalid_timings": {key: sum(key not in s for s in successful) for key in TIMINGS},
        "summary": summary, "samples": samples,
    }
    with open(args.output, "w", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
    print(json.dumps({key: value for key, value in report.items() if key != "samples"}, indent=2))


if __name__ == "__main__":
    main()

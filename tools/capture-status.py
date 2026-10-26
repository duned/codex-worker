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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("origin", help="Server origin, e.g. http://127.0.0.1:5000")
    parser.add_argument("--count", type=int, default=300)
    parser.add_argument("--interval", type=float, default=5)
    parser.add_argument("--output", required=True)
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
            started_at = datetime.datetime.now(datetime.timezone.utc).isoformat()
            started = time.perf_counter()
            try:
                connection.request("GET", "/api/status")
                response = connection.getresponse()
                response.read()  # RTT includes receiving the entire body, never saved.
                elapsed = (time.perf_counter() - started) * 1000
                sample = {"utc": started_at, "http_status": response.status, "http_ms": elapsed}
                for entry in (response.getheader("Server-Timing") or "").split(","):
                    name, _, duration = entry.strip().partition(";dur=")
                    if name in TIMINGS:
                        try:
                            value = float(duration)
                            if math.isfinite(value) and value >= 0:
                                sample[name] = value
                        except ValueError:
                            pass
                sample["missing_timings"] = [key for key in TIMINGS if key not in sample]
                samples.append(sample)
            except (OSError, http.client.HTTPException):
                samples.append({"utc": started_at, "error": "transport", "http_ms": (time.perf_counter() - started) * 1000})
                connection.close()
            if index + 1 < args.count:
                time.sleep(max(0, args.interval - (time.perf_counter() - started)))
    successful = [s for s in samples if s.get("http_status") == 200]
    summary = {}
    for key in ("http_ms", *TIMINGS):
        values = sorted(s[key] for s in successful if key in s)
        summary[key] = {"n": len(values), **{
            f"p{p}": values[max(0, math.ceil(len(values) * p / 100) - 1)] if values else None
            for p in (50, 95, 99)}}
    report = {
        "method": "HTTP/1.1 reused connection; nearest-rank percentiles; milliseconds",
        "interval_seconds": args.interval, "attempts": len(samples),
        "non_200_or_transport": len(samples) - len(successful),
        "missing_or_invalid_timings": {key: sum(key not in s for s in successful) for key in TIMINGS},
        "summary": summary, "samples": samples,
    }
    with open(args.output, "w", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
    print(json.dumps({key: value for key, value in report.items() if key != "samples"}, indent=2))


if __name__ == "__main__":
    main()

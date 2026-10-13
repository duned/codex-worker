#!/usr/bin/env python3
"""Run a local dotnet filter with an exact count; share positive TRX guards with policy."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


def read_results(directory):
    """Read current TRX cases, including failures, for count reporting."""
    results = []
    for artifact in sorted(Path(directory).glob("*.trx")):
        root = ET.parse(artifact).getroot()
        results.extend(root.findall(".//{*}UnitTestResult"))
    return results


def check_results(directory, expected):
    """Require passing cases; None requests a dynamic positive count."""
    results = read_results(directory)
    if not results or (expected is not None and (expected <= 0 or len(results) != expected)):
        raise ValueError(f"Expected {expected if expected is not None else 'positive'} nonzero cases; TRX contains {len(results)}")
    unsuccessful = sum(result.get("outcome") != "Passed" for result in results)
    if unsuccessful:
        raise ValueError(f"{unsuccessful} cases did not pass")
    return len(results)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("filter", help="VSTest filter, passed as one argument")
    parser.add_argument("--expected", type=int, required=True, help="Expected case count, including theory rows")
    parser.add_argument("--project", default="CodexWorker.sln")
    args = parser.parse_args()
    if args.expected <= 0:
        parser.error("--expected must be positive")
    repository = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix="codex-test-filter-") as directory:
        command = ["dotnet", "test", args.project, "--no-build", "--no-restore", "-m:1",
                   "--filter", args.filter, "--logger", "trx", "--results-directory", directory,
                   "--blame-hang-timeout", "60s", "--blame-hang-dump-type", "none"]
        started = time.perf_counter()
        try:
            subprocess.run(command, cwd=repository, check=True, timeout=900)
            count = check_results(directory, args.expected)
        except (subprocess.SubprocessError, OSError, ValueError, ET.ParseError) as error:
            parser.exit(1, f"Filter validation failed: {error}\n")
        print(json.dumps({"filter": args.filter, "passed": count,
                          "wall_seconds": round(time.perf_counter() - started, 3)}))


if __name__ == "__main__":
    main()

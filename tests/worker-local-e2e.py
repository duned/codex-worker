#!/usr/bin/env python3
"""Exercise a built Worker apphost without touching host services or credentials.

Usage: python3 tests/worker-local-e2e.py /absolute/path/to/CodexWorker
Use a self-contained release apphost to also verify startup without a host runtime.
"""
import http.server
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request


def campaign(executable, root):
    projects = root / "projects"
    projects.mkdir()
    identity = root / "state" / "worker-id"
    config = root / "worker.yml"
    empty_bin = root / "bin"
    empty_bin.mkdir()
    # Do not inherit CLI credentials, Git configuration, proxies, or Codex state.
    env = {
        "PATH": str(empty_bin), "HOME": str(root), "CODEX_HOME": str(root / "codex"),
        "XDG_CONFIG_HOME": str(root / "config"), "XDG_CACHE_HOME": str(root / "cache"),
        "DOTNET_CLI_HOME": str(root), "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(root / "bundle"),
        "TMPDIR": str(root), "LANG": "C.UTF-8",
    }
    if "DOTNET_ROOT" in os.environ:
        env["DOTNET_ROOT"] = os.environ["DOTNET_ROOT"]

    def write_config(url="http://127.0.0.1:1", managed=False, api_url="http://127.0.0.1:0"):
        config.write_text(f"""worker:
  pollingSeconds: 1
  preflightTimeoutSeconds: 5
  provisioning:
    enabled: false
projects:
  directory: {projects}
  ownership: {'managed' if managed else 'standalone'}
telegram:
  enabled: false
api:
  enabled: true
  listenUrl: {api_url}
server:
  enabled: {'true' if managed else 'false'}
  url: {url}
  identityFile: {identity}
""")

    def run(*args, code=0, as_json=True, stdin=None):
        command = [str(executable), *args, "--config", str(config)]
        if as_json:
            command.append("--json")
        result = subprocess.run(command, cwd=root, env=env, input=stdin, text=True,
                                capture_output=True, timeout=45)
        if result.returncode != code:
            # Outputs are isolated and contain no real secrets, but avoid dumping tokens.
            raise AssertionError(f"{args}: expected exit {code}, got {result.returncode}")
        print(f"PASS {' '.join(args)} ({'JSON' if as_json else 'human'}, exit {code})")
        return json.loads(result.stdout) if as_json else result.stdout

    write_config()
    for command in ("status", "diagnostics"):
        status = run(command)
        assert status["configuration"]["validity"] == "valid"
        assert status["configuration"]["projectCount"] == 0
        assert status["operation"]["readiness"] == "not-ready"
        assert "codex-cli-unavailable" in status["diagnostics"]
        assert run(command, as_json=False)
    for args in (("config", "show"), ("config", "validate"),
                 ("capabilities", "list"), ("capabilities", "refresh"),
                 ("provision", "status"), ("credential", "status")):
        assert run(*args)["contractVersion"] == 1
        assert run(*args, as_json=False)
    assert not identity.parent.exists(), "Observations must not create node identity state"
    inventory = run("capabilities", "refresh")
    assert {item["id"] for item in inventory["capabilities"]} == {
        "git", "github-cli", "codex-cli", "dotnet-sdk", "dotnet-runtime", "docker"}
    assert all(item["installation"] == "Missing" for item in inventory["capabilities"])

    # An invalid identity must not prevent recovery inventory or get overwritten.
    identity.parent.mkdir()
    identity.write_text("invalid-identity")
    run("provision", "status")
    assert identity.read_text() == "invalid-identity"
    identity.unlink()
    update = run("config", "set", "worker.maxParallelTasks", "2")
    assert update["succeeded"] and update["restartRequired"]
    before = config.read_bytes()
    assert not run("config", "set", "worker.maxParallelTasks", "99", code=2)["succeeded"]
    assert config.read_bytes() == before
    for provider in ("github-cli", "dotnet-sdk", "dotnet-runtime", "codex-cli", "docker"):
        for operation in ("install", "upgrade", "uninstall"):
            result = run("provision", operation, provider, "--allow-elevation", code=2)
            assert result["diagnostic"] == "Denied"
    assert run("credential", "login", "codex-cli", code=2)["report"]["diagnostic"] == "Denied"

    # Real HTTP enrollment against a local deterministic transport fixture.
    class Enrollment(http.server.BaseHTTPRequestHandler):
        reject = False
        requests = 0

        def do_POST(self):
            if self.headers.get("Transfer-Encoding") == "chunked":
                chunks = []
                while True:
                    length = int(self.rfile.readline().strip(), 16)
                    if length == 0:
                        self.rfile.readline()
                        break
                    chunks.append(self.rfile.read(length))
                    self.rfile.read(2)
                payload = b"".join(chunks)
            else:
                payload = self.rfile.read(int(self.headers["Content-Length"]))
            body = json.loads(payload)
            assert self.path == "/api/v1/workers/register"
            assert body["workerId"] == identity.read_text().strip()
            Enrollment.requests += 1
            self.send_response(403 if Enrollment.reject else 200)
            self.end_headers()

        def log_message(self, *_args):
            pass

    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Enrollment) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            write_config(f"http://127.0.0.1:{server.server_port}", managed=True)
            result = run("register", "--token-stdin", stdin="isolated-bootstrap-fixture\n")
            assert result["status"] == "registered" and result["executionReadiness"] == "not-checked"
            retained = {path: path.read_bytes() for path in identity.parent.iterdir()}
            Enrollment.reject = True
            run("register", "--token-stdin", stdin="isolated-bootstrap-fixture\n", code=2)
            assert all(path.read_bytes() == contents for path, contents in retained.items())
            assert Enrollment.requests == 2
            assert run("status")["registration"]["serverAcceptance"] == "unverified"
        finally:
            server.shutdown()
            thread.join(timeout=5)

    # Readiness is observed from the running process; local status is not service inspection.
    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Enrollment) as reservation:
        api_port = reservation.server_port
    write_config(api_url=f"http://127.0.0.1:{api_port}")
    for _ in range(2):
        with (root / "run.log").open("w+") as log:
            process = subprocess.Popen([str(executable), "run", "--config", str(config)],
                                       cwd=root, env=env, stdout=log, stderr=log)
            try:
                deadline = time.monotonic() + 20
                while True:
                    assert process.poll() is None, "Degraded startup must keep the control loop online"
                    try:
                        with urllib.request.urlopen(f"http://127.0.0.1:{api_port}/api/status", timeout=1) as response:
                            status = json.load(response)
                        if status["state"] == "not-ready":
                            break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                    assert time.monotonic() < deadline, "Running readiness observation timed out"
                    time.sleep(0.05)
                assert status["activeExecutionCount"] == 0
                process.send_signal(signal.SIGTERM)
                assert process.wait(timeout=10) == 0
                print("PASS degraded foreground startup / zero execution / graceful stop / restart")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
    print("PASS isolated local Worker campaign; real host package/service/auth mutations not exercised")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    apphost = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="worker-local-e2e-") as directory:
        campaign(apphost, Path(directory))

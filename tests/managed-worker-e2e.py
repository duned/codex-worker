#!/usr/bin/env python3
"""Opt-in live managed lifecycle campaign. See docs/managed-worker-e2e.md.

Runs a real Worker against a test Server and approved disposable GitHub Issues.
Retains all node state on failure; never performs Git or GitHub mutations itself.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def campaign(args):
    endpoint = urllib.parse.urlsplit(args.server)
    check(endpoint.scheme == "https" or (endpoint.scheme == "http" and endpoint.hostname in ("localhost", "127.0.0.1", "::1")),
          "Use HTTPS for remote Server traffic or HTTP on loopback")
    check(endpoint.hostname and not endpoint.username and not endpoint.password and not endpoint.query and not endpoint.fragment,
          "Server URL must not contain credentials, query or fragment")
    token = os.environ["MANAGED_E2E_MANAGEMENT_TOKEN"]
    bootstrap = os.environ["MANAGED_E2E_BOOTSTRAP_TOKEN"]
    root = args.state.resolve()
    root.mkdir(mode=0o700, parents=False, exist_ok=False)
    identity = root / "worker-id"
    projects = root / "projects"
    checkouts = root / "checkouts"
    config = root / "worker.yml"
    # Runtime settings are trusted node-local YAML, never a Server payload.
    runtime = args.runtime.read_text()
    check(not any(line.startswith("checkoutDirectory:") for line in runtime.splitlines()),
          "Runtime fragment must omit checkoutDirectory")
    config.write_text(f"""worker:
  pollingSeconds: 1
  preflightTimeoutSeconds: 60
projects:
  ownership: managed
  directory: {json.dumps(str(projects))}
managedProjects:
  checkoutDirectory: {json.dumps(str(checkouts))}
""" + "".join("  " + line + "\n" for line in runtime.splitlines()) + f"""server:
  enabled: true
  url: {json.dumps(args.server.rstrip('/'))}
  heartbeatIntervalSeconds: 5
  identityFile: {json.dumps(str(identity))}
api:
  enabled: false
telegram:
  enabled: false
""")
    config.chmod(0o600)
    env = dict(os.environ)
    # The Worker/Codex child must never receive campaign management credentials.
    env.pop("MANAGED_E2E_MANAGEMENT_TOKEN", None)
    env.pop("MANAGED_E2E_BOOTSTRAP_TOKEN", None)
    env.pop("CODEX_SERVER_MANAGEMENT_TOKEN", None)
    env["HOME"] = str(root)
    env["XDG_CONFIG_HOME"] = str(root / "config")
    env["XDG_CACHE_HOME"] = str(root / "cache")
    # Explicit service-account authentication homes remain operator-owned.
    check("CODEX_HOME" in env and "GH_CONFIG_DIR" in env,
          "Set CODEX_HOME and GH_CONFIG_DIR to approved service-account authentication stores")

    def api(path, body=None, method=None):
        request = urllib.request.Request(args.server.rstrip("/") + "/api/v1/" + path,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": "Bearer " + token,
                                                  "Content-Type": "application/json"}, method=method)
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                data = response.read()
                return json.loads(data) if data else None
        except urllib.error.HTTPError as error:
            # Never print response bodies or authorization material.
            raise RuntimeError(f"Server request failed: HTTP {error.code}") from None

    def cli(*arguments, stdin=None):
        result = subprocess.run([str(args.worker.resolve()), *arguments, "--config", str(config), "--json"],
                                cwd=root, env=env, input=stdin, text=True, capture_output=True, timeout=90)
        check(result.returncode == 0, "Worker CLI failed; inspect node diagnostics with secrets redacted")
        return json.loads(result.stdout)

    def wait(description, observe, predicate, timeout=None):
        deadline = time.monotonic() + (timeout or args.timeout)
        while True:
            check(process.poll() is None, "Worker exited; state/log retained for investigation")
            value = observe()
            if predicate(value):
                print("PASS " + description, flush=True)
                return value
            check(time.monotonic() < deadline, "Timed out: " + description)
            time.sleep(1)

    def project_path(project):
        return checkouts / hashlib.sha256(project["id"].encode()).hexdigest()

    compatible = api("projects/" + urllib.parse.quote(args.project, safe=""))
    incompatible = api("projects/" + urllib.parse.quote(args.incompatible_project, safe=""))
    check(compatible["id"] != incompatible["id"], "Use distinct compatible and incompatible projects")
    check(compatible["enabled"] and incompatible["enabled"], "Both Server projects must be enabled")
    check(incompatible["requirements"], "Incompatible project must declare an unmet capability requirement")
    check(all(worker["schedulingPolicy"] != "Enabled" for worker in api("workers")),
          "Use a dedicated Server with all other Workers disabled/drained")
    check(not api("executions?state=Queued"), "Dedicated Server must have no queued work")
    check(not projects.exists() and not checkouts.exists(), "Cold start must have no project state")
    registration = cli("register", "--token-stdin", stdin=bootstrap + "\n")
    check(registration["status"] == "registered", "Registration did not complete")
    worker_id = identity.read_text().strip()
    worker_route = "workers/" + worker_id
    log = (root / "worker.log").open("a")

    def start():
        return subprocess.Popen([str(args.worker.resolve()), "run", "--config", str(config)],
                                cwd=root, env=env, stdout=log, stderr=log)

    def stop():
        process.send_signal(signal.SIGTERM)
        check(process.wait(timeout=90) == 0, "Worker did not stop gracefully")

    def observations():
        return api(worker_route + "/diagnostics")

    def observed(value, project, state):
        return any(item["projectId"] == project["id"] and item["workerReportedRevision"] == project["revision"]
                   and item["materializationState"] == state for item in value["projects"])

    def execute(issue):
        execution = api("executions", {"projectId": compatible["id"],
                                       "workReference": {"type": "github-issue", "id": str(issue)}})
        result = wait("bounded Issue reaches terminal state",
                      lambda: api("executions/" + execution["id"]),
                      lambda value: value["state"] in ("Completed", "Failed", "Blocked", "Cancelled", "InfrastructureFailure"))
        check(result["state"] == "Completed", "Issue did not complete; preserve execution/recovery state")
        check(result["assignedWorkerId"] == worker_id, "Issue ran on another Worker")
        print("PASS completed execution " + result["id"], flush=True)

    process = start()
    try:
        first = wait("clean managed Worker alive, heartbeating and synchronized", lambda: api(worker_route),
                     lambda value: value["lifecycleState"] == "running" and value["lastHeartbeatAtUtc"]
                     and value["configurationSynchronization"] == "synchronized" and value["capabilities"])
        wait("heartbeat advances while idle", lambda: api(worker_route),
             lambda value: value["lastHeartbeatAtUtc"] != first["lastHeartbeatAtUtc"])
        diagnostics = wait("Server eligibility before materialization", observations,
                           lambda value: observed(value, compatible, "not-materialized")
                           and observed(value, incompatible, "blocked"))
        eligible = next(item for item in diagnostics["projects"] if item["projectId"] == compatible["id"])
        blocked = next(item for item in diagnostics["projects"] if item["projectId"] == incompatible["id"])
        check(eligible["isEligible"] and not blocked["isEligible"] and blocked["missingRequirements"],
              "Server capability eligibility disagrees with expected projects")
        check(not any(name in reason.lower() for reason in blocked["missingRequirements"]
                      for name in ("github-api", "git-repository", "agent-provider")),
              "Incompatible project must be blocked by declared requirements, with repository authentication available")
        check(not projects.exists() and not checkouts.exists(), "Catalog synchronization created project files/checkouts")
        pending = api("executions", {"projectId": incompatible["id"],
                                     "workReference": {"type": "github-issue", "id": str(args.incompatible_issue)}})
        execute(args.issue)
        wait("first assignment materializes ready checkout", observations,
             lambda value: observed(value, compatible, "ready"))
        checkout = project_path(compatible)
        check((checkout / ".git").is_dir(), "Assigned repository was not materialized")
        marker = checkout / ".git" / "managed-e2e-reuse-marker"
        marker.write_text("preserve across restart\n")
        stop()
        previous_heartbeat = api(worker_route)["lastHeartbeatAtUtc"]
        process = start()
        wait("restart synchronizes retained Worker identity", lambda: api(worker_route),
             lambda value: value["lastHeartbeatAtUtc"] != previous_heartbeat
             and value["configurationSynchronization"] == "synchronized")
        definition = {key: compatible[key] for key in ("name", "repository", "defaultBranch", "description",
                                                      "requirements", "issueReadyLabel", "issueBlockedLabel")}
        definition["description"] += "\nManaged cold-start E2E revision checkpoint."
        compatible = api("projects/" + compatible["id"],
                         {"definition": definition, "expectedRevision": compatible["revision"]}, method="PUT")
        wait("authoritative Server revision consumed", observations,
             lambda value: any(item["projectId"] == compatible["id"]
                               and item["workerReportedRevision"] == compatible["revision"] for item in value["projects"]))
        execute(args.restart_issue)
        check(marker.read_text() == "preserve across restart\n", "Restart replaced materialized checkout")
        pending = api("executions/" + pending["id"])
        check(pending["state"] == "Queued" and pending["assignedWorkerId"] is None,
              "Incompatible work was assigned")
        check(not project_path(incompatible).exists(), "Incompatible repository was materialized")
        check(not projects.exists(), "Managed execution created local project YAML")
        print("PASS incompatible work remains queued; managed state remains derived", flush=True)
        stop()
        # Observe standalone discovery using the same CLI, without executing extra Issues.
        config.write_text(config.read_text().replace("ownership: managed", "ownership: standalone"))
        projects.mkdir()
        check(cli("status")["configuration"]["projectCount"] == 0,
              "Standalone must not discover managed cache as local configuration")
        (projects / "local.yml").write_text("project:\n  name: Local standalone\n  repository: "
                                            + compatible["repository"] + "\n  directory: "
                                            + json.dumps(str(checkout)) + "\n")
        check(cli("status")["configuration"]["projectCount"] == 1,
              "Standalone did not consume local project YAML")
        print("PASS standalone discovers projects only from local YAML", flush=True)
    finally:
        if process.poll() is None:
            process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=90)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        log.close()
        print("Retained campaign state: " + str(root), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--state", type=Path, required=True, help="New, nonexistent durable directory")
    parser.add_argument("--runtime", type=Path, required=True, help="Node-local managedProjects YAML fragment")
    parser.add_argument("--project", required=True)
    parser.add_argument("--incompatible-project", required=True)
    parser.add_argument("--issue", type=int, required=True)
    parser.add_argument("--restart-issue", type=int, required=True)
    parser.add_argument("--incompatible-issue", type=int, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    arguments = parser.parse_args()
    check(arguments.issue != arguments.restart_issue, "Use two distinct bounded Issues")
    check(all(number > 0 for number in (arguments.issue, arguments.restart_issue, arguments.incompatible_issue)),
          "Issue numbers must be positive")
    check(arguments.timeout > 0, "Timeout must be positive")
    campaign(arguments)

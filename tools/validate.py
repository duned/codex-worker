#!/usr/bin/env python3
"""Repository-owned, conservative validation policy, invoked by project YAML."""

import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True

_spec = importlib.util.spec_from_file_location("filter_guard", Path(__file__).with_name("check-test-filter.py"))
guard = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(guard)
ROOT = Path(__file__).resolve().parents[1]
SOLUTION = "CodexWorker.sln"
PROJECTS = ("tests/CodexWorker.Tests/CodexWorker.Tests.csproj",
            "tests/WorkExecutionToolbox.Tests/WorkExecutionToolbox.Tests.csproj")
# Reviewed coverage, not a filename heuristic. All other C# files fall back to full.
# Area filters include every relevant boundary type, including Integration.
COVERAGE = {
    "src/CodexWorker/IssueFormatting.cs": (
        "CodexWorker.Tests.OperabilityTests.",
        "CodexWorker.Tests.ExecutionReportTests.",
        "CodexWorker.Tests.GitBranchTests.",
        "CodexWorker.Tests.WorkerV011Tests.InitialValidationSuccessDoesNotInvokeRepair",
    ),
    "src/CodexWorker/ExecutionFormatting.cs": (
        "CodexWorker.Tests.OperabilityTests.",
        "CodexWorker.Tests.ExecutionReportTests.",
        "CodexWorker.Tests.ManagedCheckoutTests.",
        "CodexWorker.Tests.CodexQuotaTests.",
        "CodexWorker.Tests.WorkerV011Tests.UnrecoverableResumeIsRejectedAndAnotherIssueRunsWithoutStoppingPolling",
        "CodexWorker.Tests.WorkerV011Tests.StructuredTaskFailureStillCleansAndReportsFailedIssue",
    ),
    "src/WorkExecutionToolbox.Cli/LocalRepositoryDiscovery.cs": (
        "WorkExecutionToolbox.Tests.LocalRepositoryDiscoveryTests.",
        "WorkExecutionToolbox.Tests.ToolboxCommandTests.",
    ),
}
DASHBOARD = "src/CodexServer/worker-poc/"
NON_DOTNET_COVERAGE = {
    "tools/dashboard-deploy.py": ("tests/dashboard-deploy-tests.py",),
    "packaging/linux/nginx/reload-codex-proxy.sh": ("tests/remote-https-proxy-tests.py",),
    "packaging/linux/nginx/codex-server.conf": ("tests/remote-https-proxy-tests.py",),
    "packaging/linux/nginx/acme-bootstrap.conf": ("tests/remote-https-proxy-tests.py",),
}


def changes(base, root=ROOT):
    # Includes committed task changes, staged/unstaged changes and new files. A
    # merge-base includes the entire task both before and after Worker rebase.
    ancestor = subprocess.check_output(["git", "merge-base", "HEAD", base], cwd=root, timeout=30).decode().strip()
    tracked = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", ancestor, "--"], cwd=root, timeout=30)
    untracked = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "-z"], cwd=root, timeout=30)
    return sorted(set(p.decode("utf-8") for p in (tracked + untracked).split(b"\0") if p))


def classified(root=ROOT):
    # Unknown/new/unclassified test methods prohibit focused selection. The
    # immutable benchmark inventory and its case counts are never policy input.
    for project in PROJECTS:
        sources = sorted(p for p in (root / project).parent.rglob("*.cs")
                         if not {"obj", "bin"}.intersection(p.relative_to((root / project).parent).parts))
        if not sources:
            return False
        for source in sources:
            text = source.read_text(encoding="utf-8")
            methods = re.findall(r'((?:\s*\[[^\n]+\]\s*)+)public\s+(?:async\s+)?[^\n{]+\(', text)
            discovered = 0
            for attributes in methods:
                if not re.search(r'\[(?:Fact|Theory)(?:\(|\])', attributes):
                    continue
                discovered += 1
                types = re.findall(r'\[Trait\("TestType", "([^"]+)"\)\]', attributes)
                if len(types) != 1 or types[0] not in ("Unit", "Component", "Integration"):
                    return False
            if discovered != len(re.findall(r'\[(?:(?:Xunit|xunit)\.)?(?:Fact|Theory)\w*(?:\s*\(|\s*\])', text)):
                return False
    return True


def documentation(path):
    return path.endswith(".md") or (path.startswith("docs/") and Path(path).suffix in (".svg", ".png", ".jpg", ".jpeg", ".csv", ".json"))


def select(paths, full=False, metadata=True):
    if full:
        return "full", "explicit full/release validation", ()
    if not paths:
        return "full", "empty or unavailable changed scope", ()
    if all(documentation(p) for p in paths):
        return "documentation", "documentation-only scope", ()
    if all(p.startswith(DASHBOARD) or p.startswith("tests/dashboard/") or documentation(p) for p in paths):
        return "dashboard", "dashboard-only scope", ()
    code = [p for p in paths if not documentation(p)]
    if all(p in NON_DOTNET_COVERAGE for p in code):
        return "non-dotnet", "reviewed deployment/proxy coverage", tuple(sorted({test for p in code for test in NON_DOTNET_COVERAGE[p]}))
    if not metadata:
        return "full", "missing or ambiguous test classification", ()
    if not all(p in COVERAGE for p in code):
        return "full", "unmapped scope, contracts, lifecycle or validation infrastructure", ()
    projects = {p.split("/")[1] for p in code}
    if len(projects) != 1:
        return "full", "changes cross production project boundaries", ()
    filters = sorted({name for p in code for name in COVERAGE[p]})
    return "focused", "reviewed area coverage (all boundary types) plus Unit/Component baseline", tuple(filters)


def validate(base, full=False, root=ROOT, run=subprocess.run):
    report = {"mode": "full", "reason": "scope discovery unavailable", "buildScope": "", "runs": []}
    try:
        try:
            paths = changes(base, root)
        except (subprocess.SubprocessError, OSError, UnicodeError):
            paths = []
        mode, reason, areas = select(paths, full)
        if mode == "focused":
            try:
                metadata = classified(root)
            except (OSError, UnicodeError):
                metadata = False
            mode, reason, areas = select(paths, full, metadata)
        report.update(mode=mode, reason=reason)

        def command(kind, argv, project="", test_filter=""):
            entry = {"kind": kind, "project": project, "filter": test_filter, "cases": 0, "durationSeconds": 0, "succeeded": False}
            report["runs"].append(entry)
            started = time.perf_counter()
            try:
                run(argv, cwd=root, check=True, timeout=900)
                entry["succeeded"] = True
            finally:
                entry["durationSeconds"] = round(time.perf_counter() - started, 3)
            return entry

        command("scope-check", ["git", "diff", "--check"])
        if mode == "documentation":
            return report
        if mode == "dashboard":
            command("dashboard", ["npm", "ci", "--prefix", DASHBOARD, "--no-audit", "--no-fund"])
            command("dashboard", ["npm", "run", "check", "--prefix", DASHBOARD])
            command("dashboard", ["node", "--test", *sorted(str(p.relative_to(root)) for p in (root / "tests/dashboard").glob("*.test.cjs"))])
            return report

        if mode == "non-dotnet":
            for source in areas:
                command("non-dotnet", ["python3", source])
            return report

        # Baseline spans both test projects and their dependencies (including
        # Server). One solution build produces all artifacts, once per pass.
        report["buildScope"] = SOLUTION
        command("build", ["dotnet", "build", SOLUTION, "-m:1"], SOLUTION)
        selections = [(p, "") for p in PROJECTS] if mode == "full" else [
            (p, "TestType=Unit|TestType=Component") for p in PROJECTS]
        selections += [(PROJECTS[0] if area.startswith("CodexWorker.") else PROJECTS[1],
                        "FullyQualifiedName~" + area) for area in areas]
        for project, test_filter in selections:
            with tempfile.TemporaryDirectory(prefix="codex-validation-") as directory:
                argv = ["dotnet", "test", project, "--no-build", "--no-restore", "-m:1",
                        "--logger", "trx", "--results-directory", directory,
                        "--blame-hang-timeout", "60s", "--blame-hang-dump-type", "none"]
                if test_filter:
                    argv += ["--filter", test_filter]
                try:
                    entry = command("test", argv, project, test_filter)
                finally:
                    # Count actual cases even when dotnet exits nonzero.
                    report["runs"][-1]["cases"] = len(guard.read_results(directory))
                entry["succeeded"] = False
                # Require positive, passing results for EACH selection; a large
                # baseline cannot conceal an empty area filter or missing project.
                entry["cases"] = guard.check_results(directory, None)
                entry["succeeded"] = True
        if mode == "full":
            for source in sorted((root / "tests").glob("test-*.py")):
                command("policy-tests", ["python3", str(source.relative_to(root))])
            command("dashboard-tests", ["npm", "test", "--prefix", DASHBOARD])
        return report
    finally:
        # A bounded metadata-only protocol; no raw process output or environment.
        print("CODEX_VALIDATION_RESULT=" + json.dumps(report, separators=(",", ":")), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="Configured authoritative base branch/ref")
    parser.add_argument("--full", action="store_true", help="Force full/release validation")
    args = parser.parse_args()
    try:
        validate(args.base, args.full)
    except (subprocess.SubprocessError, OSError, ValueError, guard.ET.ParseError) as error:
        parser.exit(1, f"Validation failed: {type(error).__name__}\n")


if __name__ == "__main__":
    main()

"""Observable policy, process and filter contracts without external services."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("policy", Path(__file__).resolve().parents[1] / "tools/validate.py")
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class RiskValidationTests(unittest.TestCase):
    def test_reviewed_small_area_adds_direct_coverage_to_baseline(self):
        mode, reason, areas = policy.select(["src/CodexWorker/IssueFormatting.cs"])
        self.assertEqual("focused", mode)
        self.assertIn("Unit/Component", reason)
        self.assertIn("CodexWorker.Tests.ExecutionReportTests.", areas)

    def test_real_git_process_boundary_selects_all_mapped_boundary_tests(self):
        mode, _, areas = policy.select(["src/WorkExecutionToolbox.Cli/LocalRepositoryDiscovery.cs"])
        self.assertEqual("focused", mode)
        self.assertIn("WorkExecutionToolbox.Tests.LocalRepositoryDiscoveryTests.", areas)

    def test_uncertain_or_high_risk_scope_is_full(self):
        for paths in ([], ["src/Shared/NodeCapabilities.cs"], ["src/CodexWorker/ExecutionRunner.cs"],
                      ["src/CodexWorker/GitRepository.cs"], ["src/CodexWorker/New.cs"],
                      ["tests/CodexWorker.Tests/IssueCommentContextTests.cs"], ["tools/validate.py"],
                      ["Directory.Build.props"], ["packaging/release.sh"],
                      ["src/CodexWorker/IssueFormatting.cs", "src/WorkExecutionToolbox.Cli/LocalRepositoryDiscovery.cs"]):
            with self.subTest(paths=paths):
                self.assertEqual("full", policy.select(paths)[0])
        self.assertEqual("full", policy.select(["src/CodexWorker/IssueFormatting.cs"], metadata=False)[0])
        self.assertEqual("full", policy.select(["docs/development.md"], full=True)[0])

    def test_non_dotnet_selection(self):
        self.assertEqual("documentation", policy.select(["docs/development.md", "README.md"])[0])
        self.assertEqual("dashboard", policy.select([policy.DASHBOARD + "src/app/App.tsx"])[0])
        self.assertEqual("full", policy.select([policy.DASHBOARD + "src/app/App.tsx", "src/CodexWorker/IssueFormatting.cs"])[0])

    def test_live_metadata_accepts_current_suite_and_rejects_missing_or_ambiguous_traits(self):
        self.assertTrue(policy.classified())
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for project in policy.PROJECTS:
                source = root / project
                source.parent.mkdir(parents=True)
                source.with_suffix(".cs").write_text('[Fact]\npublic void Check() {}')
            self.assertFalse(policy.classified(root))
            for project in policy.PROJECTS:
                source = (root / project).with_suffix(".cs")
                source.write_text('[Trait("TestType", "Unit")]\n[Fact]\npublic void Check() {}')
            self.assertTrue(policy.classified(root))
            source.write_text('[Trait("TestType", "Unit")]\n[Trait("TestType", "Integration")]\n[Fact]\npublic void Check() {}')
            self.assertFalse(policy.classified(root))

    def execute(self, paths, empty_filter=None, fail_build=False, full=False):
        calls = []
        def run(argv, **kwargs):
            calls.append(argv)
            if fail_build and argv[:2] == ["dotnet", "build"]:
                raise subprocess.CalledProcessError(1, argv)
            if argv[:2] == ["dotnet", "test"]:
                directory = argv[argv.index("--results-directory") + 1]
                test_filter = argv[argv.index("--filter") + 1] if "--filter" in argv else ""
                results = '' if empty_filter == test_filter else '<UnitTestResult outcome="Passed" />'
                Path(directory, "result.trx").write_text(f'<TestRun><Results>{results}</Results></TestRun>')
        with patch.object(policy, "changes", return_value=paths), patch.object(policy, "classified", return_value=True), contextlib.redirect_stdout(io.StringIO()) as output:
            report = policy.validate("main", full, run=run)
        self.assertIn("CODEX_VALIDATION_RESULT=", output.getvalue())
        return calls, report

    def test_build_once_tests_reuse_artifacts_and_each_selection_reports_counts(self):
        calls, report = self.execute(["src/CodexWorker/IssueFormatting.cs"])
        self.assertEqual(1, sum(c[:2] == ["dotnet", "build"] for c in calls))
        tests = [c for c in calls if c[:2] == ["dotnet", "test"]]
        self.assertEqual(6, len(tests))
        for argv in tests:
            self.assertIn("--no-build", argv)
            self.assertIn("--no-restore", argv)
            self.assertIn("--filter", argv)
        for run in report["runs"]:
            self.assertGreaterEqual(run["durationSeconds"], 0)
            self.assertTrue(run["succeeded"])
            if run["kind"] == "test":
                self.assertEqual(1, run["cases"])

    def test_empty_direct_filter_cannot_be_hidden_by_passing_baseline(self):
        with self.assertRaises(ValueError):
            self.execute(["src/CodexWorker/IssueFormatting.cs"], empty_filter="FullyQualifiedName~CodexWorker.Tests.OperabilityTests.")

    def test_test_failure_reports_actual_case_count(self):
        def run(argv, **kwargs):
            if argv[:2] == ["dotnet", "test"]:
                directory = argv[argv.index("--results-directory") + 1]
                Path(directory, "result.trx").write_text('<TestRun><Results><UnitTestResult outcome="Failed" /></Results></TestRun>')
                raise subprocess.CalledProcessError(1, argv)
        output = io.StringIO()
        with patch.object(policy, "changes", return_value=[]), contextlib.redirect_stdout(output):
            with self.assertRaises(subprocess.CalledProcessError):
                policy.validate("main", run=run)
        report = json.loads(output.getvalue().split("CODEX_VALIDATION_RESULT=")[1])
        self.assertEqual(1, report["runs"][-1]["cases"])
        self.assertFalse(report["runs"][-1]["succeeded"])

    def test_build_error_fails(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute(["src/CodexWorker/IssueFormatting.cs"], fail_build=True)

    def test_full_path_has_no_filters(self):
        calls, report = self.execute([], full=True)
        self.assertEqual("full", report["mode"])
        tests = [c for c in calls if c[:2] == ["dotnet", "test"]]
        self.assertEqual(2, len(tests))
        self.assertTrue(all("--filter" not in c for c in tests))

    def test_reviewed_non_dotnet_tool_uses_its_contract_tests(self):
        calls, report = self.execute(["tools/dashboard-deploy.py"])
        self.assertEqual("non-dotnet", report["mode"])
        self.assertIn(["python3", "tests/dashboard-deploy-tests.py"], calls)
        self.assertFalse(any(c[0] == "dotnet" for c in calls))

    def test_documentation_never_starts_dotnet(self):
        calls, report = self.execute(["docs/development.md"])
        self.assertFalse(any(c[0] == "dotnet" for c in calls))
        self.assertEqual("", report["buildScope"])

    def test_scope_collects_full_task_diff_and_untracked_paths(self):
        with patch.object(policy.subprocess, "check_output", side_effect=[b"abc\n", b"old.cs\0new.cs\0", b"new.cs\0extra.cs\0"]) as read:
            self.assertEqual(["extra.cs", "new.cs", "old.cs"], policy.changes("main"))
        self.assertEqual(["git", "diff", "--no-renames", "--name-only", "-z", "abc", "--"], read.call_args_list[1].args[0])


if __name__ == "__main__":
    unittest.main()

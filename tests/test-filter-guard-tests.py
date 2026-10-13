"""Deterministic regression checks for the local filter count guard."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location(
    "filter_guard", Path(__file__).resolve().parents[1] / "tools/check-test-filter.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class FilterGuardTests(unittest.TestCase):
    def check(self, outcomes, expected):
        with tempfile.TemporaryDirectory() as directory:
            for index, project_outcomes in enumerate(outcomes):
                results = "".join(f'<UnitTestResult outcome="{outcome}" />' for outcome in project_outcomes)
                Path(directory, f"project-{index}.trx").write_text(
                    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    f"<Results>{results}</Results></TestRun>", encoding="utf-8")
            return guard.check_results(directory, expected)

    def test_aggregates_theory_rows_across_projects(self):
        self.assertEqual(3, self.check([["Passed", "Passed"], ["Passed"]], 3))

    def test_missing_or_empty_results_cannot_pass(self):
        for outcomes in ([], [[]]):
            with self.subTest(outcomes=outcomes), self.assertRaises(ValueError):
                self.check(outcomes, 1)

    def test_zero_expected_cannot_authorize_an_empty_selection(self):
        with self.assertRaises(ValueError):
            self.check([[]], 0)

    def test_partial_or_unexpected_selection_fails(self):
        for expected in (1, 3):
            with self.subTest(expected=expected), self.assertRaises(ValueError):
                self.check([["Passed", "Passed"]], expected)

    def test_failed_or_skipped_cases_cannot_pass(self):
        for outcome in ("Failed", "NotExecuted"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.check([["Passed", outcome]], 2)


if __name__ == "__main__":
    unittest.main()

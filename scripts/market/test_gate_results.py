"""The gate's result reconciliation, through its command line (gate.sh runs these first: python3 -m unittest discover -s scripts/market)."""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import gate_results

KNOWN = "known-a"


def trx(outcomes):
    """A minimal dotnet test TRX file: {test name in class Suite.Tests: outcome}."""
    tests = "".join(
        f'<UnitTest name="{name}" id="id-{name}"><TestMethod className="Suite.Tests" name="{name}" /></UnitTest>' for name in outcomes
    )
    results = "".join(f'<UnitTestResult testId="id-{name}" testName="{name}" outcome="{outcome}" />' for name, outcome in outcomes.items())
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f"<Results>{results}</Results><TestDefinitions>{tests}</TestDefinitions></TestRun>"
    )


class Check(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.known_file = self.directory / "known.txt"
        self.known_file.write_text(f"# comment\nsuite Suite.Tests.{KNOWN}\nother Suite.Tests.passes\n")
        self.known_out = self.directory / "known-out.tsv"

    def run_check(self, content, exit_status, format="trx", file_name="results.trx"):
        results = self.directory / file_name
        if content is not None:
            results.write_text(content)
        out, err = io.StringIO(), io.StringIO()
        arguments = ["check", "--suite", "suite", "--format", format, "--results", str(results), "--exit-status", str(exit_status),
                     "--known", str(self.known_file), "--known-out", str(self.known_out), "--root", "/repo/market-web"]
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            status = gate_results.main(arguments)
        return status, out.getvalue(), err.getvalue()

    def test_exactly_the_known_failures_with_a_failing_exit_pass_and_are_listed(self):
        status, detail, problems = self.run_check(trx({"a": "Passed", "b": "Passed", KNOWN: "Failed"}), 1)
        self.assertEqual((status, problems), (0, ""))
        self.assertEqual(detail, "2 passed, 1 known failures")
        self.assertEqual(self.known_out.read_text(), f"suite\tSuite.Tests.{KNOWN}\n")

    def test_a_new_failure_fails_and_is_named(self):
        status, _, problems = self.run_check(trx({"a": "Failed", KNOWN: "Failed"}), 1)
        self.assertEqual(status, 1)
        self.assertIn("NEW FAILURE: Suite.Tests.a", problems)

    def test_a_known_failure_that_passes_fails_and_asks_for_the_list_to_be_updated(self):
        status, _, problems = self.run_check(trx({"a": "Passed", KNOWN: "Passed"}), 0)
        self.assertEqual(status, 1)
        self.assertIn(f"KNOWN FAILURE now passes: Suite.Tests.{KNOWN}; update {self.known_file}", problems)

    def test_a_known_failure_missing_from_the_results_fails(self):
        status, _, problems = self.run_check(trx({"a": "Passed"}), 0)
        self.assertEqual(status, 1)
        self.assertIn(f"KNOWN FAILURE is not in the results (renamed or removed?): Suite.Tests.{KNOWN}", problems)

    def test_a_skipped_known_failure_fails(self):
        status, _, problems = self.run_check(trx({"a": "Passed", KNOWN: "NotExecuted"}), 0)
        self.assertEqual(status, 1)
        self.assertIn(f"KNOWN FAILURE was skipped: Suite.Tests.{KNOWN}", problems)

    def test_a_failing_exit_with_no_failed_test_fails(self):
        self.known_file.write_text("")
        status, _, problems = self.run_check(trx({"a": "Passed"}), 1)
        self.assertEqual(status, 1)
        self.assertEqual(problems, "GATE: suite: the suite exited 1, but no test failed in its results (a build error or a crash?)\n")

    def test_a_zero_exit_with_failures_in_the_results_fails(self):
        status, _, problems = self.run_check(trx({"a": "Passed", KNOWN: "Failed"}), 0)
        self.assertEqual(status, 1)
        self.assertIn("the suite exited 0, but its results hold failures", problems)

    def test_an_empty_run_fails(self):
        status, _, problems = self.run_check(trx({}), 0)
        self.assertEqual(status, 1)
        self.assertIn("no tests ran", problems)

    def test_a_missing_results_file_fails(self):
        status, detail, problems = self.run_check(None, 1)
        self.assertEqual((status, detail), (1, "no results"))
        self.assertIn("no results file", problems)

    def test_an_unreadable_results_file_fails(self):
        status, detail, _ = self.run_check("<TestRun", 1)
        self.assertEqual((status, detail), (1, "unreadable results"))

    def test_vitest_names_tests_by_file_and_full_name_and_catches_a_file_that_failed_to_load(self):
        report = {"testResults": [
            {"name": "/repo/market-web/tests/a.test.ts", "status": "passed", "message": "",
             "assertionResults": [{"fullName": "group works", "status": "passed"}, {"fullName": "later", "status": "skipped"}]},
            {"name": "/repo/market-web/tests/b.test.tsx", "status": "failed", "message": "SyntaxError: nope\nstack", "assertionResults": []},
        ]}
        status, detail, problems = self.run_check(json.dumps(report), 1, "vitest-json", "vitest.json")
        self.assertEqual(status, 1)
        self.assertEqual(detail, "1 passed, 1 skipped")
        self.assertIn("RUN ERROR: tests/b.test.tsx failed outside its tests: SyntaxError: nope", problems)

    def test_vitest_failure_is_named_with_its_file(self):
        report = {"testResults": [{"name": "/repo/market-web/bff-tests/bff.test.ts", "status": "failed", "message": "",
                                   "assertionResults": [{"fullName": "1. the allowlist refuses", "status": "failed"}]}]}
        status, _, problems = self.run_check(json.dumps(report), 1, "vitest-json", "vitest.json")
        self.assertEqual(status, 1)
        self.assertIn("NEW FAILURE: bff-tests/bff.test.ts > 1. the allowlist refuses", problems)

    def test_playwright_names_tests_by_project_and_counts_flaky_ones_as_passed(self):
        report = {"errors": [], "suites": [{"title": "journeys.spec.ts", "specs": [
            {"title": "signs in", "tests": [{"projectName": "desktop", "status": "expected"}, {"projectName": "phone", "status": "flaky"}]},
        ], "suites": [{"title": "purchase", "specs": [{"title": "buys", "tests": [{"projectName": "desktop", "status": "unexpected"}]}]}]}]}
        status, detail, problems = self.run_check(json.dumps(report), 1, "playwright-json", "playwright.json")
        self.assertEqual(status, 1)
        self.assertEqual(detail, "2 passed, 1 failed, 1 flaky (passed on retry)")
        self.assertIn("NEW FAILURE: [desktop] journeys.spec.ts > purchase > buys", problems)

    def test_playwright_error_outside_any_test_fails(self):
        report = {"errors": [{"message": "Error: global setup failed\nat x"}], "suites": []}
        status, _, problems = self.run_check(json.dumps(report), 1, "playwright-json", "playwright.json")
        self.assertEqual(status, 1)
        self.assertIn("RUN ERROR: error outside any test: Error: global setup failed", problems)
        self.assertNotIn("no tests ran", problems)


class KnownFailuresFile(unittest.TestCase):
    def test_the_database_list_is_the_seven_linux_config_failures(self):
        known = gate_results.read_known_failures(gate_results.KNOWN_FAILURES_FILE, "database")
        self.assertEqual(len(known), 7)
        self.assertEqual(sum(name.startswith("ACE.Database.Tests.AccountTests.") for name in known), 5)
        self.assertEqual(sum(name.startswith("ACE.Database.Tests.WeenieSearchTests.") for name in known), 2)


if __name__ == "__main__":
    unittest.main()

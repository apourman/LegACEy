#!/usr/bin/env python3
"""Reads one test suite's per-test results and reconciles them with its exit status and the gate's known failures.

Used by gate.sh; tested by test_gate_results.py. A suite passes only when:
- its results file exists and lists at least one test;
- every failed test is on the known-failures list for that suite;
- every test on that list is in the results and still fails (a known failure that passes, or is gone, means the list needs updating);
- no test passed only on a retry (Playwright's "flaky");
- its exit status agrees with the results: 0 with no failures, non-zero only with failures.
Skipped tests don't fail the suite; they are named for the summary.

Formats: trx (dotnet test --logger trx), vitest-json (Vitest's json reporter), playwright-json (Playwright's json reporter).

Also redacts secret values (from env files and Config.js files) out of text, for the summary gate.sh prints.
"""

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

KNOWN_FAILURES_FILE = Path(__file__).with_name("gate-known-failures.txt")
TRX_NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
TRX_SKIPPED_OUTCOMES = {"NotExecuted", "Inconclusive"}


@dataclass
class SuiteResults:
    passed: set = field(default_factory=set)
    failed: set = field(default_factory=set)
    skipped: set = field(default_factory=set)
    # failed at first, then passed on a retry (Playwright); also in passed
    flaky: set = field(default_factory=set)
    # failures that belong to no single test: a file that didn't load, an error outside any test
    run_errors: list = field(default_factory=list)

    @property
    def total(self):
        return len(self.passed) + len(self.failed) + len(self.skipped)


@dataclass
class Verdict:
    ok: bool
    detail: str
    problems: list
    known_failures: list
    skipped: list


def first_line(message):
    """The first non-blank line of a report's message, or 'no message'."""
    lines = [line.strip() for line in (message or "").splitlines() if line.strip()]
    return lines[0] if lines else "no message"


def read_trx(path):
    """dotnet test results. Names are the fully qualified ones: <class>.<test name>, data rows included."""
    document = ET.parse(path).getroot()
    classes = {}
    for test in document.iterfind(".//t:UnitTest", TRX_NAMESPACE):
        method = test.find("t:TestMethod", TRX_NAMESPACE)
        classes[test.get("id")] = method.get("className") if method is not None else ""

    results = SuiteResults()
    for result in document.iterfind(".//t:UnitTestResult", TRX_NAMESPACE):
        name = f"{classes.get(result.get('testId'), '')}.{result.get('testName')}".lstrip(".")
        outcome = result.get("outcome")
        if outcome == "Passed":
            results.passed.add(name)
        elif outcome in TRX_SKIPPED_OUTCOMES:
            results.skipped.add(name)
        else:
            results.failed.add(name)
    return results


def read_vitest_json(path, root):
    """Vitest results. Names are <file relative to root> > <describe blocks and title>."""
    document = json.loads(Path(path).read_text())
    results = SuiteResults()
    for test_file in document.get("testResults", []):
        file_name = os.path.relpath(test_file["name"], root) if root else test_file["name"]
        failed_before = len(results.failed)
        for test in test_file.get("assertionResults", []):
            name = f"{file_name} > {test['fullName']}"
            status = test.get("status")
            if status == "passed":
                results.passed.add(name)
            elif status == "failed":
                results.failed.add(name)
            else:
                results.skipped.add(name)
        if test_file.get("status") == "failed" and len(results.failed) == failed_before:
            results.run_errors.append(f"{file_name} failed outside its tests: {first_line(test_file.get('message'))}")
    return results


def read_playwright_json(path):
    """Playwright results. Names are [<project>] <file> > <describe blocks> > <title>; no project prefix when the config has none."""
    document = json.loads(Path(path).read_text())
    results = SuiteResults()

    def visit(suite, titles):
        titles = titles + [suite["title"]] if suite.get("title") else titles
        for spec in suite.get("specs", []):
            for test in spec.get("tests", []):
                project = test.get("projectName")
                name = f"{f'[{project}] ' if project else ''}{' > '.join(titles + [spec['title']])}"
                status = test.get("status")
                if status in ("expected", "flaky"):
                    results.passed.add(name)
                    if status == "flaky":
                        results.flaky.add(name)
                elif status == "skipped":
                    results.skipped.add(name)
                else:
                    results.failed.add(name)
        for child in suite.get("suites", []):
            visit(child, titles)

    for suite in document.get("suites", []):
        visit(suite, [])
    for error in document.get("errors", []):
        results.run_errors.append(f"error outside any test: {first_line(error.get('message'))}")
    return results


def read_results(format, path, root=None):
    if format == "trx":
        return read_trx(path)
    if format == "vitest-json":
        return read_vitest_json(path, root)
    if format == "playwright-json":
        return read_playwright_json(path)
    raise ValueError(f"unknown format {format}")


FORMATS = ("playwright-json", "trx", "vitest-json")


def read_known_failures(path, suite):
    """The known failures for one suite: lines '<suite> <test name>'; # starts a comment."""
    known = set()
    for line in Path(path).read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        line_suite, _, name = line.partition(" ")
        if line_suite == suite:
            known.add(name.strip())
    return known


def reconcile(results, exit_status, known, known_failures_file=KNOWN_FAILURES_FILE):
    problems = []
    if results.total == 0 and not results.run_errors:
        problems.append("no tests ran (an empty run counts as a failure)")

    for name in sorted(results.failed - known):
        problems.append(f"NEW FAILURE: {name}")
    for name in sorted(results.flaky):
        problems.append(f"FLAKY (failed, then passed on a retry): {name}")
    problems.extend(f"RUN ERROR: {error}" for error in results.run_errors)
    for name in sorted(known - results.failed):
        if name in results.passed:
            state = "now passes"
        elif name in results.skipped:
            state = "was skipped"
        else:
            state = "is not in the results (renamed or removed?)"
        problems.append(f"KNOWN FAILURE {state}: {name}; update {known_failures_file}")

    any_failure = bool(results.failed or results.run_errors)
    if exit_status == 0 and any_failure:
        problems.append("the suite exited 0, but its results hold failures")
    if exit_status != 0 and not any_failure and not results.flaky:
        problems.append(f"the suite exited {exit_status}, but no test failed in its results (a build error or a crash?)")

    parts = [f"{len(results.passed - results.flaky)} passed"]
    known_seen = sorted(results.failed & known)
    if known_seen:
        parts.append(f"{len(known_seen)} known failures")
    other_failed = len(results.failed - known)
    if other_failed:
        parts.append(f"{other_failed} failed")
    if results.flaky:
        parts.append(f"{len(results.flaky)} flaky (passed only on a retry)")
    if results.skipped:
        parts.append(f"{len(results.skipped)} skipped")
    return Verdict(ok=not problems, detail=", ".join(parts), problems=problems, known_failures=known_seen, skipped=sorted(results.skipped))


def append_listing(path, suite, names):
    if path:
        with open(path, "a") as out:
            out.writelines(f"{suite}\t{name}\n" for name in names)


def check(arguments):
    known = read_known_failures(arguments.known, arguments.suite) if arguments.known else set()
    if not Path(arguments.results).is_file():
        print("no results", end="")
        print(f"GATE: {arguments.suite}: no results file at {arguments.results}; the suite exited {arguments.exit_status}.", file=sys.stderr)
        return 1
    try:
        results = read_results(arguments.format, arguments.results, arguments.root)
    except (ET.ParseError, json.JSONDecodeError, KeyError) as error:
        print("unreadable results", end="")
        print(f"GATE: {arguments.suite}: can't read {arguments.results}: {error}", file=sys.stderr)
        return 1

    verdict = reconcile(results, arguments.exit_status, known, arguments.known or KNOWN_FAILURES_FILE)
    print(verdict.detail, end="")
    append_listing(arguments.known_out, arguments.suite, verdict.known_failures)
    append_listing(arguments.skipped_out, arguments.suite, verdict.skipped)
    for problem in verdict.problems:
        print(f"GATE: {arguments.suite}: {problem}", file=sys.stderr)
    return 0 if verdict.ok else 1


SECRET_NAME = re.compile(r"(PASSWORD|SECRET|KEY|TOKEN)", re.IGNORECASE)
CONFIG_PASSWORD = re.compile(r'"Password"\s*:\s*"([^"]+)"')


def secret_values(paths):
    """The secret values in env files (NAME=value where the name holds PASSWORD, SECRET, KEY or TOKEN) and Config.js files ("Password")."""
    secrets = set()
    for path in paths:
        try:
            text = Path(path).read_text()
        except OSError:
            continue
        secrets.update(CONFIG_PASSWORD.findall(text))
        for line in text.splitlines():
            name, separator, value = line.strip().partition("=")
            value = value.strip().strip('"').strip("'")
            if separator and SECRET_NAME.search(name) and value:
                secrets.add(value)
    return sorted(secrets, key=len, reverse=True)


def redact(text, paths):
    for value in secret_values(paths):
        text = text.replace(value, "[redacted]")
    return text


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    check_parser = commands.add_parser("check", help="reconcile one suite; prints a one-line detail, problems on stderr, exits 1 on any")
    check_parser.add_argument("--suite", required=True, help="the suite's name in the known-failures file")
    check_parser.add_argument("--format", required=True, choices=FORMATS)
    check_parser.add_argument("--results", required=True, help="the suite's results file")
    check_parser.add_argument("--exit-status", required=True, type=int, help="the suite's own exit status")
    check_parser.add_argument("--known", help="the known-failures file (none: no failure is known)")
    check_parser.add_argument("--root", help="directory test file paths are shown relative to (Vitest)")
    check_parser.add_argument("--known-out", help="append '<suite>\\t<test>' for each known failure seen")
    check_parser.add_argument("--skipped-out", help="append '<suite>\\t<test>' for each skipped test")
    redact_parser = commands.add_parser("redact", help="copy stdin to stdout with the secret values of the given files replaced")
    redact_parser.add_argument("--secrets", action="append", default=[], help="an env or Config.js file holding secrets (repeatable; missing files are skipped)")
    arguments = parser.parse_args(argv)
    if arguments.command == "redact":
        sys.stdout.write(redact(sys.stdin.read(), arguments.secrets))
        return 0
    return check(arguments)


if __name__ == "__main__":
    sys.exit(main())

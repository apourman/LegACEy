#!/usr/bin/env bash
# The local gate: one command that proves a change end to end and prints a Markdown summary to paste into the pull request.
#
#   scripts/market/gate.sh [--play-test] [--results-dir <dir>]
#
# Runs, stopping at the first failure:
#   - its own result-reconciliation tests (test_gate_results.py);
#   - everything the pull-request workflow runs (.github/workflows/pull-request.yml): the .NET solution build; the website's npm ci, type
#     check and production build; the BFF request, unit and component tests (Vitest); the OpenAPI drift check (openapi-drift.sh);
#   - the Market API, database and game server test suites (the game server's with x64 and without StartupTests);
#   - the browser checks against the fake API (npm run test:browser, unfiltered, on MARKET_WEB_TEST_PORT, default 5187);
#   - the end-to-end pair: e2e.sh up (fresh data, then the production stack), the Playwright suite (npm run test:e2e), the ledger audit,
#     and e2e.sh down, which also runs when anything after up fails or the gate is interrupted;
#   - the automated play-test (play-test.sh), only with --play-test or MARKET_GATE_PLAY_TEST=1, as it needs the game running.
#
# Every test suite writes per-test results, which gate_results.py reconciles with the suite's exit status and the exact list in
# gate-known-failures.txt: any other failure stops the gate and names the test, and so does a known failure that passes or disappears.
#
# Needs what the end-to-end scripts need (docker.env or MARKET_DOCKER_ENV_FILE, docker-ace-db-1 healthy, the DATs, jq), Node 22 for the
# website, and a Source/ACE.Server/Config.js the .NET tests can use. Results, logs and summary.md go to the results directory (default
# ~/.local/state/legacey/market-gate/<UTC time>).
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

SCRIPTS="$ROOT/scripts/market"
WEB="$ROOT/market-web"
KNOWN_FAILURES="$SCRIPTS/gate-known-failures.txt"
play_test="${MARKET_GATE_PLAY_TEST:-0}"
run_dir="${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-gate/$(date -u +%Y%m%dT%H%M%SZ)"
arguments="$*"

while (( $# )); do
    case "$1" in
        --play-test) play_test=1; shift ;;
        --results-dir) run_dir="${2:?--results-dir needs a directory}"; shift 2 ;;
        *) echo "Usage: scripts/market/gate.sh [--play-test] [--results-dir <dir>]" >&2; exit 2 ;;
    esac
done
[[ "$play_test" == 0 || "$play_test" == 1 ]] || { echo "MARKET_GATE_PLAY_TEST must be 0 or 1." >&2; exit 2; }

export MARKET_WEB_TEST_PORT="${MARKET_WEB_TEST_PORT:-5187}"
export PYTHONDONTWRITEBYTECODE=1
mkdir -p "$run_dir/logs"
summary="$run_dir/summary.md"
known_seen="$run_dir/known-failures.tsv"
: > "$known_seen"

# by step name; the summary lists them in the planned order
declare -A step_results=() step_details=()
stopped_at=
stop_problems=
e2e_up=0

# the step's log file name: lowercase, dashes
log_for() {
    local slug
    slug="$(tr '[:upper:]' '[:lower:]' <<<"$1" | tr -cs 'a-z0-9' '-')"
    echo "$run_dir/logs/${slug%-}.log"
}

record() {
    step_results["$1"]="$2"
    step_details["$1"]="${3:-}"
}

banner() {
    printf '\n==== gate: %s ====\n' "$1"
}

# run <step> <command...>: a step judged by its exit status alone. Returns non-zero (and records why) when it fails.
run() {
    local name="$1" status log
    shift
    log="$(log_for "$name")"
    banner "$name"
    "$@" 2>&1 | tee "$log"
    status=${PIPESTATUS[0]}
    if (( status == 0 )); then
        record "$name" pass
        return 0
    fi
    record "$name" FAIL "exit $status; log: $log"
    stopped_at="$name"
    stop_problems="$(printf '%s exited %s. The end of its log (%s):\n%s' "$name" "$status" "$log" "$(tail -n 15 "$log")")"
    return 1
}

# suite <step> <suite name in gate-known-failures.txt> <format> <results file> <command...>: a test suite. Its exit status is captured,
# never ignored, and reconciled with its per-test results.
suite() {
    local name="$1" suite_name="$2" format="$3" results="$4" status detail problems log
    shift 4
    log="$(log_for "$name")"
    rm -f "$results"
    banner "$name"
    "$@" 2>&1 | tee "$log"
    status=${PIPESTATUS[0]}
    problems="$run_dir/logs/$suite_name.problems"
    if detail="$(python3 "$SCRIPTS/gate_results.py" check --suite "$suite_name" --format "$format" --results "$results" \
        --exit-status "$status" --known "$KNOWN_FAILURES" --root "$WEB" --known-out "$known_seen" 2>"$problems")"; then
        record "$name" pass "$detail"
        return 0
    fi
    cat "$problems" >&2
    record "$name" FAIL "$detail (exit $status)"
    stopped_at="$name"
    stop_problems="$(cat "$problems")"
    return 1
}

in_web() {
    (cd "$WEB" && "$@")
}

dotnet_suite() {
    local name="$1" suite_name="$2"
    shift 2
    suite "$name" "$suite_name" trx "$run_dir/$suite_name.trx" \
        dotnet test "$@" --results-directory "$run_dir" --logger "trx;LogFileName=$suite_name.trx"
}

e2e_down() {
    e2e_up=0
    run "E2E stack down" "$SCRIPTS/e2e.sh" down
}

print_summary() {
    local verdict=PASS commit dirty name detail
    [[ -n "$stopped_at" ]] && verdict=FAIL
    commit="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    dirty=
    [[ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]] && dirty=" plus uncommitted changes"
    {
        echo "## Local gate: $verdict"
        echo
        echo "\`$commit\`$dirty, $(date -u '+%Y-%m-%d %H:%M UTC'), \`scripts/market/gate.sh${arguments:+ $arguments}\`"
        echo
        echo "| Step | Result | Details |"
        echo "| --- | --- | --- |"
        for name in "${planned[@]}"; do
            detail="${step_details[$name]:-}"
            echo "| $name | ${step_results[$name]:-not run} | ${detail//|/\\|} |"
        done
        if [[ -s "$known_seen" ]]; then
            echo
            echo "**Known failures** (accepted; listed in \`scripts/market/gate-known-failures.txt\`):"
            echo
            while IFS=$'\t' read -r suite_name name; do
                echo "- $suite_name: \`$name\`"
            done < "$known_seen"
        fi
        if [[ -f "$run_dir/play-test.md" ]]; then
            echo
            echo "**Play-test record:**"
            echo
            cat "$run_dir/play-test.md"
        fi
        if [[ -n "$stopped_at" ]]; then
            echo
            echo "**Stopped at: $stopped_at**"
            echo
            echo '```'
            echo "$stop_problems"
            echo '```'
        fi
    } > "$summary"
    printf '\n'
    cat "$summary"
    printf '\n(Summary saved to %s.)\n' "$summary"
}

finish() {
    local status=$?
    trap - EXIT INT TERM
    if (( e2e_up )); then
        echo "Tearing down the end-to-end stack after: ${stopped_at:-an interruption}." >&2
        e2e_down || true
    fi
    [[ -z "$stopped_at" && $status -ne 0 ]] && { stopped_at="(interrupted)"; stop_problems="The gate stopped with status $status."; }
    print_summary
    [[ -z "$stopped_at" ]] || exit 1
    exit 0
}

planned=(
    "Gate self-test" ".NET build" "Website install" "Website typecheck" "Website build" "BFF, unit and component tests" "OpenAPI drift check"
    "Market API tests" "Database tests" "Game server tests (x64)" "Browser checks (fake API)" "E2E fresh data and stack up" "E2E Playwright"
    "E2E ledger audit" "E2E stack down" "Play-test"
)
trap finish EXIT
trap 'exit 130' INT TERM

echo "Gate results: $run_dir"

run "Gate self-test" python3 -m unittest discover -s "$SCRIPTS" -p 'test_gate_*.py' || exit 1
run ".NET build" dotnet build "$ROOT/Source/ACE.sln" -c Debug -p:Platform=x64 || exit 1
run "Website install" in_web npm ci || exit 1
run "Website typecheck" in_web npm run typecheck || exit 1
run "Website build" in_web npm run build || exit 1
suite "BFF, unit and component tests" bff vitest-json "$run_dir/bff.json" \
    in_web npm run test:bff -- --reporter=default --reporter=json --outputFile.json="$run_dir/bff.json" || exit 1
run "OpenAPI drift check" "$SCRIPTS/openapi-drift.sh" || exit 1

dotnet_suite "Market API tests" market-api "$ROOT/Source/ACE.MarketApi.Tests" || exit 1
dotnet_suite "Database tests" database "$ROOT/Source/ACE.Database.Tests" || exit 1
dotnet_suite "Game server tests (x64)" server "$ROOT/Source/ACE.Server.Tests" -p:Platform=x64 --filter 'FullyQualifiedName!~StartupTests' || exit 1

# unfiltered on purpose: a config that matches no checks gives an empty run, which fails
suite "Browser checks (fake API)" browser playwright-json "$run_dir/browser.json" \
    in_web env PLAYWRIGHT_JSON_OUTPUT_FILE="$run_dir/browser.json" npm run test:browser -- --reporter=list,json || exit 1

e2e_up=1
run "E2E fresh data and stack up" "$SCRIPTS/e2e.sh" up || exit 1
suite "E2E Playwright" e2e playwright-json "$run_dir/e2e.json" \
    in_web env PLAYWRIGHT_JSON_OUTPUT_FILE="$run_dir/e2e.json" npm run test:e2e -- --reporter=list,json || exit 1
run "E2E ledger audit" "$SCRIPTS/e2e.sh" audit || exit 1
e2e_down || exit 1

if (( play_test )); then
    run "Play-test" "$SCRIPTS/play-test.sh" --record "$run_dir/play-test.md" || exit 1
else
    record "Play-test" off "enable with --play-test or MARKET_GATE_PLAY_TEST=1 (needs the game server running)"
fi

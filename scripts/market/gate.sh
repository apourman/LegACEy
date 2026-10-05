#!/usr/bin/env bash
# The local gate: one command that proves a change end to end and prints a Markdown summary to paste into the pull request.
#
#   scripts/market/gate.sh [--play-test] [--results-dir <dir>]
#
# Runs, stopping at the first failure:
#   - a check of the end-to-end settings (common.sh: docker.env and its secrets), so a missing one stops the gate before the long steps;
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
# gate-known-failures.txt: any other failure stops the gate and names the test, and so does a known failure that passes or disappears, and
# a Playwright test that passed only on a retry. Skipped tests are listed by name in the summary.
#
# Needs what the end-to-end scripts need (a docker.env in this checkout's root, where docker/docker-compose.local.yml reads it;
# docker-ace-db-1 healthy; the DATs; jq), Node 22 for the website, and a Source/ACE.Server/Config.js the .NET tests can use. Results,
# logs and summary.md go to the results directory (default ~/.local/state/legacey/market-gate/<UTC time>).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPTS="$ROOT/scripts/market"
WEB="$ROOT/market-web"
KNOWN_FAILURES="$SCRIPTS/gate-known-failures.txt"
# where the secrets the summary must not show live: the defaults of common.sh (docker.env) and e2e.sh (the E2E API login, its Config.js)
E2E_RUN_DIR="${MARKET_E2E_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-e2e}"
SECRET_FILES=(
    "${MARKET_DOCKER_ENV_FILE:-$ROOT/docker.env}"
    "${MARKET_E2E_API_CREDENTIALS_FILE:-$E2E_RUN_DIR/api-db.env}"
    "$E2E_RUN_DIR/Config.js"
    "$ROOT/Source/ACE.Server/Config.js"
)
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
mkdir -p "$run_dir/logs" || exit 1
summary="$run_dir/summary.md"
known_seen="$run_dir/known-failures.tsv"
skipped_seen="$run_dir/skipped.tsv"
: > "$known_seen"
: > "$skipped_seen"

# The steps, in order: "<name>|<function>". The single list of step names: the loop below runs them, the summary lists them, and a step
# function records its result under the name the loop gives it (current_step).
STEPS=(
    "E2E settings (docker.env)|step_settings"
    "Gate self-test|step_self_test"
    ".NET build|step_dotnet_build"
    "Website install|step_web_install"
    "Website typecheck|step_web_typecheck"
    "Website build|step_web_build"
    "BFF, unit and component tests|step_bff_tests"
    "OpenAPI drift check|step_openapi_drift"
    "Market API tests|step_market_api_tests"
    "Database tests|step_database_tests"
    "Game server tests (x64)|step_server_tests"
    "Browser checks (fake API)|step_browser_checks"
    "E2E fresh data and stack up|step_e2e_up"
    "E2E Playwright|step_e2e_playwright"
    "E2E ledger audit|step_e2e_audit"
    "E2E stack down|step_e2e_down"
    "Play-test|step_play_test"
)

# by step name
declare -A step_results=() step_details=()
current_step=
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
    step_results["$current_step"]="$1"
    step_details["$current_step"]="${2:-}"
}

# fail <detail> <problems>: records the current step as failed and stops the gate there. A failure while tearing down after an earlier one
# keeps the earlier one as the reason.
fail() {
    record FAIL "$1"
    [[ -n "$stopped_at" ]] && return
    stopped_at="$current_step"
    stop_problems="$2"
}

# run_logged <command...>: runs the command under a banner, its output also in the current step's log; returns its exit status
run_logged() {
    printf '\n==== gate: %s ====\n' "$current_step"
    "$@" 2>&1 | tee "$(log_for "$current_step")"
    return "${PIPESTATUS[0]}"
}

# check_exit <command...>: a step judged by its exit status alone
check_exit() {
    local status log
    log="$(log_for "$current_step")"
    run_logged "$@"
    status=$?
    if (( status == 0 )); then
        record pass
        return 0
    fi
    fail "exit $status; log: $log" "$(printf '%s exited %s. The end of its log (%s):\n%s' "$current_step" "$status" "$log" "$(tail -n 15 "$log")")"
    return 1
}

# check_suite <suite name in gate-known-failures.txt> <format> <results file> <command...>: a test suite. Its exit status is captured,
# never ignored, and reconciled with its per-test results.
check_suite() {
    local suite_name="$1" format="$2" results="$3" status detail problems
    shift 3
    rm -f "$results"
    run_logged "$@"
    status=$?
    problems="$run_dir/logs/$suite_name.problems"
    if detail="$(python3 "$SCRIPTS/gate_results.py" check --suite "$suite_name" --format "$format" --results "$results" \
        --exit-status "$status" --known "$KNOWN_FAILURES" --root "$WEB" --known-out "$known_seen" --skipped-out "$skipped_seen" \
        2>"$problems")"; then
        record pass "$detail"
        return 0
    fi
    cat "$problems" >&2
    fail "$detail (exit $status)" "$(cat "$problems")"
    return 1
}

# the step name STEPS gives a step function
step_named_for() {
    local entry
    for entry in "${STEPS[@]}"; do
        [[ "${entry#*|}" == "$1" ]] && { echo "${entry%%|*}"; return; }
    done
    echo "$1"
}

in_web() {
    (cd "$WEB" && "$@")
}

dotnet_suite() {
    local suite_name="$1"
    shift
    check_suite "$suite_name" trx "$run_dir/$suite_name.trx" \
        dotnet test "$@" --results-directory "$run_dir" --logger "trx;LogFileName=$suite_name.trx"
}

step_settings() {
    # common.sh exits with a message when docker.env or a secret is missing; a subshell keeps that exit inside the step
    # shellcheck disable=SC2016 # expanded by the inner bash
    check_exit bash -c 'source "$1/common.sh" && require_service_key && require_cookie_secret && echo "docker.env: $DOCKER_ENV_FILE"' _ "$SCRIPTS"
}
step_self_test() { check_exit python3 -m unittest discover -s "$SCRIPTS" -p 'test_gate_*.py'; }
step_dotnet_build() { check_exit dotnet build "$ROOT/Source/ACE.sln" -c Debug -p:Platform=x64; }
step_web_install() { check_exit in_web npm ci; }
step_web_typecheck() { check_exit in_web npm run typecheck; }
step_web_build() { check_exit in_web npm run build; }
step_bff_tests() {
    check_suite bff vitest-json "$run_dir/bff.json" \
        in_web npm run test:bff -- --reporter=default --reporter=json --outputFile.json="$run_dir/bff.json"
}
step_openapi_drift() { check_exit "$SCRIPTS/openapi-drift.sh"; }
step_market_api_tests() { dotnet_suite market-api "$ROOT/Source/ACE.MarketApi.Tests"; }
step_database_tests() { dotnet_suite database "$ROOT/Source/ACE.Database.Tests"; }
step_server_tests() { dotnet_suite server "$ROOT/Source/ACE.Server.Tests" -p:Platform=x64 --filter 'FullyQualifiedName!~StartupTests'; }
# unfiltered on purpose: a config that matches no checks gives an empty run, which fails
step_browser_checks() {
    check_suite browser playwright-json "$run_dir/browser.json" \
        in_web env PLAYWRIGHT_JSON_OUTPUT_FILE="$run_dir/browser.json" npm run test:browser -- --reporter=list,json
}
step_e2e_up() {
    e2e_up=1
    check_exit "$SCRIPTS/e2e.sh" up
}
step_e2e_playwright() {
    check_suite e2e playwright-json "$run_dir/e2e.json" \
        in_web env PLAYWRIGHT_JSON_OUTPUT_FILE="$run_dir/e2e.json" npm run test:e2e -- --reporter=list,json
}
step_e2e_audit() { check_exit "$SCRIPTS/e2e.sh" audit; }
step_e2e_down() {
    e2e_up=0
    check_exit "$SCRIPTS/e2e.sh" down
}
step_play_test() {
    if (( play_test )); then
        check_exit "$SCRIPTS/play-test.sh" --record "$run_dir/play-test.md"
    else
        record off "enable with --play-test or MARKET_GATE_PLAY_TEST=1 (needs the game server running)"
    fi
}

print_summary() {
    local verdict=PASS commit dirty entry name detail suite_name test_name skipped_count
    [[ -n "$stopped_at" ]] && verdict=FAIL
    commit="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    dirty=
    [[ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]] && dirty=" plus uncommitted changes"
    skipped_count="$(wc -l < "$skipped_seen")"
    {
        echo "## Local gate: $verdict"
        echo
        echo "\`$commit\`$dirty, $(date -u '+%Y-%m-%d %H:%M UTC'), \`scripts/market/gate.sh${arguments:+ $arguments}\`$( (( play_test )) && echo ", play-test on")"
        echo
        echo "| Step | Result | Details |"
        echo "| --- | --- | --- |"
        for entry in "${STEPS[@]}"; do
            name="${entry%%|*}"
            detail="${step_details[$name]:-}"
            echo "| $name | ${step_results[$name]:-not run} | ${detail//|/\\|} |"
        done
        if [[ -s "$known_seen" ]]; then
            echo
            echo "**Known failures** (accepted; listed in \`scripts/market/gate-known-failures.txt\`):"
            echo
            while IFS=$'\t' read -r suite_name test_name; do
                echo "- $suite_name: \`$test_name\`"
            done < "$known_seen"
        fi
        echo
        if (( skipped_count )); then
            echo "**Skipped tests** ($skipped_count; not failures, but check each is meant to be skipped):"
            echo
            while IFS=$'\t' read -r suite_name test_name; do
                echo "- $suite_name: \`$test_name\`"
            done < "$skipped_seen"
        else
            echo "**Skipped tests:** none in the suites that ran."
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
    } | python3 "$SCRIPTS/gate_results.py" redact "${SECRET_FILES[@]/#/--secrets=}" > "$summary"
    printf '\n'
    cat "$summary"
    printf '\n(Summary saved to %s.)\n' "$summary"
    printf 'Review it before pasting it into a pull request: it can hold the end of a failing step'"'"'s log. Values from docker.env,\n'
    printf 'the E2E login and the Config.js files are redacted; anything else is shown as the step printed it.\n'
}

finish() {
    local status=$?
    trap - EXIT INT TERM
    if (( e2e_up )); then
        echo "Tearing down the end-to-end stack after: ${stopped_at:-an interruption}." >&2
        current_step="$(step_named_for step_e2e_down)"
        step_e2e_down || true
    fi
    if [[ -z "$stopped_at" && $status -ne 0 ]]; then
        stopped_at="${current_step:-(before the first step)}"
        stop_problems="The gate stopped with status $status during this step (interrupted?)."
    fi
    print_summary
    [[ -z "$stopped_at" ]] || exit 1
    exit 0
}

trap finish EXIT
trap 'exit 130' INT TERM

echo "Gate results: $run_dir"
for entry in "${STEPS[@]}"; do
    current_step="${entry%%|*}"
    declare -F "${entry#*|}" >/dev/null || { fail "gate bug: no function ${entry#*|}" "Gate bug: step '$current_step' names no function ${entry#*|}."; exit 1; }
done
for entry in "${STEPS[@]}"; do
    current_step="${entry%%|*}"
    "${entry#*|}" || exit 1
    [[ -n "${step_results[$current_step]:-}" ]] || { fail "gate bug: the step recorded no result" "Gate bug: step '$current_step' ran but recorded no result."; exit 1; }
done

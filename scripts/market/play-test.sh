#!/usr/bin/env bash
# The automated play-test (holtburger + Playwright against the dev stack with the game running). PLACEHOLDER until ticket 07 replaces it.
#
# The hook the local gate calls (scripts/market/gate.sh --play-test):
#   scripts/market/play-test.sh --record <file>
# Runs the whole play-test, writes its per-step record to <file> as Markdown (build SHA, date, then one row per step: result and notes,
# the manual checklist's form), restores the temporary account changes, and exits 0 only when every step passed and the restore was
# verified. The gate puts the record into its summary.
set -euo pipefail

record=
while (( $# )); do
    case "$1" in
        --record) record="${2:?--record needs a file}"; shift 2 ;;
        *) echo "Usage: scripts/market/play-test.sh --record <file>" >&2; exit 2 ;;
    esac
done
[[ -n "$record" ]] || { echo "Usage: scripts/market/play-test.sh --record <file>" >&2; exit 2; }

message="The automated play-test is not implemented yet (ticket 07); nothing was played."
printf '%s\n' "$message" > "$record"
echo "PLAY-TEST NOT IMPLEMENTED: $message" >&2
exit 1

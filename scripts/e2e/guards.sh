#!/usr/bin/env bash
# THE STATIC GUARDS, ALL OF THEM, IN ONE COMMAND.
#
# There were eight of these and nothing called any of them. Two were written on 2026-09-21 and were
# wired nowhere at all. That is the same shape as sections 15, 17 and 18 of class-teacher-e2e.sh,
# which each passed standalone for weeks while the one command that claimed to run everything
# quietly skipped them — a suite nothing calls reports nothing.
#
# These need no server, no database and no browser: they read the source. Seconds, not minutes, so
# scripts/rebuild.sh runs them after a successful build and a structural regression surfaces at
# build time rather than in a browser three days later.
#
#   bash scripts/e2e/guards.sh
#
# A NEW GUARD IS ADDED HERE IN THE SAME COMMIT THAT CREATES IT.
set -u
cd "$(dirname "$0")/../.."

# Pass/fail: each exits non-zero on a finding.
GUARDS="css-token-check component-param-check style-leak-check section-actions-check suite-route-check route-audit list-page-audit purge-manifest-check"

# Advisory: refusal-audit prints a shortlist for a human to read and says so itself — "a page WITH a
# check still needs a human to ask whether it covers the refusal that matters". It has no pass state
# to assert, so failing the run on it would train everyone to ignore the whole script.
ADVISORY="refusal-audit"

failed=0
passed=0
echo "── static guards ──────────────────────────────────────────────"
for g in $GUARDS; do
    out=$(node "scripts/e2e/$g.mjs" 2>&1); code=$?
    if [ $code -eq 0 ]; then
        printf '  \033[32mPASS\033[0m  %-22s %s\n' "$g" "$(echo "$out" | grep -v '^$' | tail -1)"
        passed=$((passed + 1))
    else
        printf '  \033[31mFAIL\033[0m  %-22s\n' "$g"
        echo "$out" | sed 's/^/          /'
        failed=$((failed + 1))
    fi
done

for g in $ADVISORY; do
    out=$(node "scripts/e2e/$g.mjs" 2>&1)
    printf '  \033[33mINFO\033[0m  %-22s advisory — read it when touching a form\n' "$g"
done

echo "───────────────────────────────────────────────────────────────"
if [ $failed -eq 0 ]; then
    echo "  $passed/$passed guards pass."
    exit 0
fi
echo "  $failed of $((passed + failed)) guards FAILED."
exit 1

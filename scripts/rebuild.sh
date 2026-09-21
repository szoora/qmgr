#!/usr/bin/env bash
# Stop the running API/Web, build, and say what happened. MSBuild cannot overwrite a DLL a running
# process holds open (MSB3021/MSB3027), and the failure reads like a compile error when it is not.
#
# On a successful build it also runs the static guards (scripts/e2e/guards.sh). They read the source
# and take seconds, so a structural regression — a var() naming a token nothing defines, a section
# hiding its own actions, a link resolving to no route, an entity missing from the purge manifest —
# surfaces here rather than in a browser three days later. Pass --no-guards to skip them.
set -u
guards=1
for a in "$@"; do [ "$a" = "--no-guards" ] && guards=0; done

for p in Q-Mgr.API Q-Mgr.Web; do
  powershell.exe -NoProfile -Command "Get-Process -Name '$p' -ErrorAction SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1
done
sleep 1
cd "$(dirname "$0")/.."

target="Q-Mgr.slnx"
for a in "$@"; do case "$a" in --no-guards) ;; *) target="$a" ;; esac; done

log=$(mktemp)
dotnet build "$target" -v q --nologo >"$log" 2>&1
code=$?
grep -E "error |warning CS|Build succeeded|Error\(s\)" "$log" | sort -u | head -40
rm -f "$log"

if [ $code -ne 0 ]; then
  echo "  build FAILED — guards not run."
  exit $code
fi

[ $guards -eq 1 ] || exit 0
echo
bash scripts/e2e/guards.sh

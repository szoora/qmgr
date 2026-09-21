#!/usr/bin/env bash
# Stop the running API/Web, build, and say what happened. MSBuild cannot overwrite a DLL a running
# process holds open (MSB3021/MSB3027), and the failure reads like a compile error when it is not.
set -u
for p in Q-Mgr.API Q-Mgr.Web; do
  powershell.exe -NoProfile -Command "Get-Process -Name '$p' -ErrorAction SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1
done
sleep 1
cd "$(dirname "$0")/.."
target="${1:-Q-Mgr.slnx}"
dotnet build "$target" -v q --nologo 2>&1 | grep -E "error |warning CS|Build succeeded|Error\(s\)" | sort -u | head -40

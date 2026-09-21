// EVERY BROWSER SUITE, IN ONE COMMAND.
//
// There were twenty-three of these and nothing called any of them. A suite nothing calls reports
// nothing — the same shape as sections 15, 17 and 18 of class-teacher-e2e.sh, which each passed
// standalone for weeks while the one command that claimed to run everything quietly skipped them,
// so a "full run" under-reported by about ninety assertions.
//
// Writing this runner also turned up the other half of that shape: rooms-ui, select-verify and
// uniform-check counted their failures, printed the tally and then always exited 0. They are fixed;
// A SUITE THAT CANNOT FAIL REPORTS NOTHING EITHER.
//
//   node scripts/e2e/browser/all.mjs            # everything
//   node scripts/e2e/browser/all.mjs hub billing # only suites whose name contains one of these
//
// A NEW SUITE IS ADDED HERE IN THE SAME COMMIT THAT CREATES IT.
import { spawn } from 'node:child_process';
import path from 'node:path';

const WEB = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const CDP = 'http://127.0.0.1:9333';

// Pass/fail. Each prints "N passed, M failed" and exits non-zero on a failure.
const SUITES = [
  'action-location', 'billing-hub', 'daterange-check', 'density-and-staff', 'empty-state-check',
  'hub-nav', 'import-wizard', 'list-sweep', 'minutes-ui', 'payments-ui', 'portal-tabs', 'register-and-duty-sheet',
  'registration-doors', 'roles-and-branch-ui', 'rooms-ui', 'select-verify', 'staff-bulk', 'staff-hub',   'staff-nav-hubs', 'timetable-print', 'timetable-views', 'type-audit', 'type-sweep-all', 'uniform-check',
  'white-label-ui',
];

// Measurement, not assertion: density-check runs an A/B in one page load, furniture-check tallies
// the space a page spends before any data. Neither has a pass state, so failing the run on one
// would train everybody to ignore the whole script — the same call as refusal-audit in guards.sh.
const ADVISORY = ['density-check', 'furniture-check'];

const filter = process.argv.slice(2);
const wanted = (n) => filter.length === 0 || filter.some((f) => n.includes(f));

async function up(url, what, hint) {
  try {
    const res = await fetch(url, { signal: AbortSignal.timeout(4000) });
    // Any answer means something is listening; 401/404/429 are all "reachable".
    if (res) return true;
  } catch { /* fall through */ }
  console.log(`\n  ${what} is not answering on ${url}.`);
  console.log(`  ${hint}\n`);
  return false;
}

const ok = (await up(`${CDP}/json/version`, 'Headless Chrome',
    '"/c/Program Files/Google/Chrome/Application/chrome.exe" --headless=new --remote-debugging-port=9333 \\n' +
    '    --user-data-dir="$(cygpath -w "$TEMP/cdp/profile")" --no-first-run about:blank &'))
  & (await up(`${WEB}/login`, 'Q-Mgr.Web',
    'ApiBaseUrl="http://127.0.0.1:5001" dotnet run --project src/Q-Mgr.Web/Q-Mgr.Web.csproj --urls "http://127.0.0.1:5003" --no-build'))
  & (await up(`${API}/api/v1/health`, 'Q-Mgr.API',
    'dotnet run --project src/Q-Mgr.API/Q-Mgr.API.csproj --urls "http://127.0.0.1:5001" --no-build'));
if (!ok) process.exit(2);

const run = (name) => new Promise((resolve) => {
  const started = Date.now();
  const child = spawn(process.execPath, [path.join(import.meta.dirname, `${name}.mjs`)], {
    env: process.env, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let out = '';
  child.stdout.on('data', (d) => { out += d; });
  child.stderr.on('data', (d) => { out += d; });
  child.on('close', (code) => resolve({ name, code, out, ms: Date.now() - started }));
});

const tally = (out) => {
  // The suites word it differently ("57 passed, 0 failed", "DONE — 20 passed, 0 failed",
  // "  billing-hub: 57 passed, 0 failed"), so take the LAST match rather than the first.
  const m = [...out.matchAll(/(\d+) passed, (\d+) failed/g)].pop();
  return m ? { pass: +m[1], fail: +m[2] } : null;
};

const G = '\x1b[32m', R = '\x1b[31m', Y = '\x1b[33m', D = '\x1b[90m', X = '\x1b[0m';
let totalPass = 0, totalFail = 0, broken = [];

console.log(`\n── browser suites ─────────────────────────────────────────────`);
for (const name of SUITES) {
  if (!wanted(name)) continue;
  const r = await run(name);
  const t = tally(r.out);
  const skips = (r.out.match(/\bSKIP\b/g) ?? []).length;
  const secs = `${(r.ms / 1000).toFixed(0)}s`.padStart(4);
  if (t) {
    totalPass += t.pass; totalFail += t.fail;
    const bad = t.fail > 0 || r.code !== 0;
    if (bad) broken.push({ name, out: r.out });
    console.log(`  ${bad ? R + 'FAIL' : G + 'PASS'}${X}  ${name.padEnd(24)} ${String(t.pass).padStart(3)} passed, ${t.fail} failed` +
                `${skips ? D + `  (${skips} skipped)` + X : ''}${D}  ${secs}${X}`);
  } else {
    // No tally at all means the suite did not get far enough to print one.
    broken.push({ name, out: r.out });
    console.log(`  ${R}BROKE${X} ${name.padEnd(24)} no tally printed${D}  ${secs}${X}`);
  }
}

for (const name of ADVISORY) {
  if (!wanted(name)) continue;
  const r = await run(name);
  console.log(`  ${Y}INFO${X}  ${name.padEnd(24)} measurement — read the output when changing layout`);
}

console.log(`───────────────────────────────────────────────────────────────`);
if (broken.length) {
  for (const b of broken) {
    console.log(`\n── ${b.name} ──`);
    console.log(b.out.split('\n').filter((l) => /FAIL|Error|error|no tally/.test(l)).slice(0, 12).join('\n') || b.out.slice(-800));
  }
  console.log(`\n  ${totalPass} passed, ${totalFail} failed across ${SUITES.filter(wanted).length} suite(s); ${broken.length} suite(s) need a look.`);
  process.exit(1);
}
console.log(`  ${totalPass} passed, 0 failed across ${SUITES.filter(wanted).length} suite(s).`);

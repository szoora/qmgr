// PHASE 3 OF THE LIST-PAGE SWEEP — the audit nobody thinks to do.
// Plan: docs/plans/LIST_PAGE_STANDARDISATION.md
//
// Fault 5 of the register report was the one that LOST WORK rather than merely annoying: two
// refusals in SubmitRegister were reachable only by submitting forty marks and being told. The
// question this answers is "which other pages can do that".
//
// It pairs every API refusal (BadRequestProblem / Problem400 / ConflictProblem, plus the
// ProblemDetails shapes the older controllers use) with the Web page that posts to that controller,
// and reports whether that page validates ANYTHING before it submits — a disabled action, a field
// error list, or a warning it raises itself.
//
// IT CANNOT MATCH REFUSAL TO FIELD, and does not pretend to. A page with no pre-submit check at all
// is the finding; a page that has one still needs a human to ask whether it covers the refusal that
// matters. Read it as a shortlist, not a score.
//
// Run: node scripts/e2e/refusal-audit.mjs
import fs from 'node:fs';
import path from 'node:path';

const API = 'src/Q-Mgr.API/Controllers';
const WEB = 'src/Q-Mgr.Web/Components';

const walk = (dir, ext, out = []) => {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    e.isDirectory() ? walk(p, ext, out) : e.name.endsWith(ext) && out.push(p);
  }
  return out;
};

// ---- every refusal the API can return, by controller ----
const REFUSAL = /(?:BadRequestProblem|Problem400|ConflictProblem)\(\s*"([^"]{3,90})"/g;
const refusals = new Map();
for (const f of walk(API, '.cs')) {
  const s = fs.readFileSync(f, 'utf8');
  const found = [...s.matchAll(REFUSAL)].map(m => m[1]);
  if (found.length) refusals.set(path.basename(f, '.cs'), [...new Set(found)]);
}

// ---- which controller each page talks to ----
// A page names its service (IStaffPerformanceApiService) or its raw route ("api/v1/roles"); both
// are matched loosely against a controller name, which is enough to pair them for a shortlist.
const controllerFor = (pageSource) => {
  const hits = new Set();
  for (const name of refusals.keys()) {
    const stem = name.replace(/Controller$/, '');
    const route = stem.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
    if (new RegExp(`\\b${stem}\\b`, 'i').test(pageSource) || pageSource.includes(`/${route}`)) hits.add(name);
  }
  return [...hits];
};

// A page guards BEFORE it submits when it disables its action, collects field errors, or refuses
// its own submit with a warning. Any of the three is a guard; none is the finding.
const GUARD = [
  /Disabled="@\([^"]*(?:string\.IsNullOrWhiteSpace|== null|\.Count == 0|!\w+\.Any\(\))/,
  /formErrors|fieldErrors|validationErrors|ApiFieldException/,
  /ToastSeverity\.Warning[\s\S]{0,200}return;/,
  /if \([^)]*string\.IsNullOrWhiteSpace[^)]*\)\s*\r?\n?\s*\{[\s\S]{0,300}return;/,
];

const rows = [];
for (const f of walk(WEB, '.razor')) {
  const s = fs.readFileSync(f, 'utf8');
  // Only pages that WRITE: a read-only page cannot lose work to a refusal.
  if (!/(HttpMethod\.(Post|Put|Patch|Delete)|PostAsJsonAsync|PutAsJsonAsync|Async\(.*Request)/.test(s)) continue;
  if (!/@page|\[Parameter\] public bool Embedded/.test(s)) continue;

  const cs = controllerFor(s);
  const total = cs.reduce((n, c) => n + refusals.get(c).length, 0);
  if (total === 0) continue;

  const guarded = GUARD.some(re => re.test(s));
  rows.push({ page: f.replace(/\\/g, '/').replace(WEB + '/', '').replace(/\\/g, '/'), cs, total, guarded });
}

rows.sort((a, b) => (a.guarded === b.guarded ? b.total - a.total : a.guarded ? 1 : -1));

const pad = (s, n) => String(s).padEnd(n);
console.log(`\n${pad('page', 46)}${pad('guards?', 9)}${pad('refusals', 10)}controller(s)`);
console.log('-'.repeat(110));
for (const r of rows) {
  console.log(`${pad(r.page, 46)}${pad(r.guarded ? 'yes' : 'NO', 9)}${pad(r.total, 10)}${r.cs.join(', ')}`);
}

const bare = rows.filter(r => !r.guarded);
console.log(`\n${rows.length} writing page(s); ${bare.length} submit with no pre-submit check of their own.`);
if (bare.length) {
  console.log('\nRead these first — a refusal here is only discovered by submitting:');
  for (const r of bare) console.log(`  ${r.page}`);
}
console.log('\nThis is a shortlist, not a score: a page WITH a check still needs a human to ask whether it covers the refusal that matters.');

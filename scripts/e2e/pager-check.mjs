// EVERY LIST PAGE'S PAGER OFFERS A PAGE SIZE (plan STUDENT_ROSTER_AND_LIST_STANDARD §4, 2026-09-23).
//
// QPager sat on twenty-five pages and every one of them fixed its own size: a reader could step through
// 1,711 students twenty-five at a time and could not ask for a hundred, or for all of them to print. The
// size control is opt-in on the component — so a new page that forgets `PageSizeOptions` is the old pager
// again, and nothing would look broken. This fails it.
//
// ALLOWED is for a pager that DELIBERATELY has a fixed size, with the reason. A small list inside a modal or
// a card is the kind of thing that belongs here — name the file and say why, never add one to get a build
// through.
//
//   node scripts/e2e/pager-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';

/** file (relative to Components/) → why its pager keeps a fixed size. */
const ALLOWED = {
  'Pages/Portal/Portal.razor': "\"My recent actions\" is a CARD on a person's own workspace, ten at a time so the card stays a card; it is not a list page (list-page-audit rules the portal out of scope for the same reason)",
};

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

const findings = [];
let pagers = 0;
for (const f of files) {
  if (path.basename(f) === 'QPager.razor') continue;
  const rel = f.replaceAll('\\', '/').replace(`${ROOT}/`, '');
  // Blank Razor comments line for line, so a note quoting `<QPager` neither counts nor shifts line numbers.
  const src = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n').replace(/@\*[\s\S]*?\*@/g, m => m.replace(/[^\n]/g, ' '));
  for (const m of src.matchAll(/<QPager\b[\s\S]*?\/>/g)) {
    pagers++;
    if (/\bPageSizeOptions\s*=/.test(m[0])) continue;
    if (ALLOWED[rel]) continue;
    const line = src.slice(0, m.index).split('\n').length;
    findings.push(`${ROOT}/${rel}:${line}  ${m[0].replace(/\s+/g, ' ').slice(0, 140)}`);
  }
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} pager(s) with no page-size choice. Pass PageSizeOptions="@QPagerDefaults.Sizes" AllowAll="true" StorageKey="…" PageSizeChanged="…", or name the file in ALLOWED with the reason.`);
  process.exit(1);
}
console.log(`${pagers} pager(s) in ${files.length} components — every one offers a page size.`);

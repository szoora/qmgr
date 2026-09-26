// A VIEW PERMISSION NEVER GATES A WRITE (2026-09-25).
//
// The RBAC sweep of 2026-09-25 found four writes whose only gate was a *.view code: printing a ticket
// (tokens.view, and it opened a TCP connection to an address from the request), generating a feedback
// link (feedback.view, copying a customer's contact details into a new row), minting a visitor badge
// (visitors.view) — and reading the school's messaging secrets was the same family one step over. A
// view code is held by the widest set of roles by design, so a write behind one is open to them all.
//
// So: an [HttpPost]/[HttpPut]/[HttpPatch]/[HttpDelete] whose ONLY [RequirePermission] codes are
// *View constants fails, unless it is on ALLOWED below with the reason it is really a read (a
// precheck, a search sent as a body) or an act the reader genuinely owns.
//
//   node scripts/e2e/view-on-write-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.API/Controllers';

// method name → why a view code is right for it. Keep each reason honest: "it is a read".
const ALLOWED = new Map([
  // The restricted rung has ONE code, and it is held only by the Administrator, the SuperAdmin and the
  // designated safeguarding lead — exactly the people who may write a restricted note. There is no
  // wider audience for it to leak to, so the code that reads the rung is the right one to write it.
  ['StudentsController.UpdateRestrictedNotes', 'welfare.restricted.view is the whole restricted rung; its holders are its writers'],
]);

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const p = path.join(dir, e.name);
    return e.isDirectory() ? walk(p) : p.endsWith('.cs') ? [p] : [];
  });
}

const findings = [];
let writes = 0;
for (const file of walk(ROOT)) {
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  for (let i = 0; i < lines.length; i++) {
    if (!/^\s*\[Http(Post|Put|Patch|Delete)\b/.test(lines[i])) continue;
    // The attribute block: walk up and down over attribute lines and comments to the signature.
    let start = i; while (start > 0 && /^\s*(\[|\/\/)/.test(lines[start - 1])) start--;
    let end = i; while (end + 1 < lines.length && /^\s*(\[|\/\/)/.test(lines[end + 1])) end++;
    const sig = lines[end + 1] ?? '';
    const name = (sig.match(/\s(\w+)\s*\(/) || [])[1] ?? '?';
    writes++;
    const block = lines.slice(start, end + 1).filter(l => !/^\s*\/\//.test(l)).join('\n');
    const codes = [...block.matchAll(/RequirePermission\(([^)]*)\)/g)].flatMap(m => m[1].split(',').map(c => c.trim()));
    if (codes.length === 0) continue;                       // gated in code or by [Authorize] policy — not this guard's shape
    if (!codes.every(c => /View\b/.test(c) && !/Manage|Edit|Create/.test(c))) continue;
    const key = `${path.basename(file, '.cs')}.${name}`;
    if (ALLOWED.has(key)) continue;
    findings.push(`${file.replaceAll('\\', '/')}:${i + 1}  ${key}  gated only on ${codes.join(', ')}`);
  }
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} write(s) gated only on a view permission. Gate on the act (create/edit/manage), or add it to ALLOWED with the reason it is really a read.`);
  process.exit(1);
}
console.log(`${writes} write endpoint(s) scanned — none is gated only on a view permission.`);

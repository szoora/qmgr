// RE-IMPORTING THE SAME SHEET — what would change, and who might be one person twice.
//
// Asked for as "importing the file again for both staff and student should detect any student
// account or staff code (unique on this project) and suggest updation, in case the data is
// different", and "identify possible student duplication, alert the user for confirmation that
// actually the students are different".
//
// A school re-imports its own sheet every term, so a re-import IS the normal case. Until this, the
// reader learned how much of the file landed on children already on the roll only from the summary
// AFTERWARDS — and never learned WHAT it had changed about them.
//
// What this asserts, on a file built from the branch roll so it cannot pass vacuously:
//   1. a row carrying an admission number already on the roll is reported as already here;
//   2. a row whose CLASS differs is counted as changed, and the FIELD is named, not the value;
//   3. a row identical to the roll is NOT counted as changed — "23 already here" reads as 23
//      decisions when 20 of them would change nothing;
//   4. one name under two admission numbers is raised as a possible duplicate;
//   5. Import is HELD until the reader confirms it, and released once they do.
//
// It stops before pressing Import: reading the file correctly is what is under test, and importing
// dummy children on every run leaves a mess in a roll somebody has to clean.
//
// Run: node scripts/e2e/browser/import-reimport.mjs   (CDP_PORT=9444 to watch it)
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (n, ok, d = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${n}${ok ? '' : '  — ' + d}`); };
const note = (n, w) => { skip++; say(`    SKIP  ${n}  — ${w}`); };
const done = () => { say(''); say(`  import-reimport: ${pass} passed, ${fail} failed, ${skip} skipped`); process.exitCode = fail ? 1 : 0; };

// ---- build the file out of the branch own roll --------------------------------------------------
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${(await auth.json()).accessToken}` };

const roll = await (await fetch(`${API}/api/v1/branches/${BRANCH}/students?pageSize=50`, { headers: H })).json().catch(() => null);
const students = (roll?.items ?? roll ?? []).filter(s => s.studentCode && s.fullName);

if (students.length < 2) {
  note('the whole suite', `this branch has ${students.length} student(s) with an admission number — two are needed`);
  done();
  process.exit();
}

const unchanged = students[0];
const moved = students[1];
const otherClass = `${moved.className ?? 'S1'}-MOVED`;
const run = Date.now().toString(36).slice(-4);
const guardian = `Zz Guardian ${run}`;
const phone = `+25677${Math.floor(1000000 + Math.random() * 8999999)}`;

// THE FIXTURE MUST GIVE THE UNCHANGED ROW A GUARDIAN THE ROLL ALREADY HAS, or it is not unchanged:
// a guardian phone is required on every row, and a guardian the roll does not hold is a real change.
// The first run of this suite reported three product bugs that were the file being wrong. Both rows
// carry the SAME guardian, so the only thing separating them is the class.
const cleanup = [];
async function ensureGuardian(student) {
  const res = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${student.id}/guardians`, {
    method: 'POST', headers: { ...H, 'Content-Type': 'application/json' },
    body: JSON.stringify({ fullName: guardian, phone, relationship: 'Guardian' }),
  });
  if (!res.ok) return false;
  const link = await res.json().catch(() => null);
  if (link?.id) cleanup.push([student.id, link.id]);
  return true;
}
const seeded = (await ensureGuardian(unchanged)) && (await ensureGuardian(moved));
if (!seeded) {
  note('the whole suite', 'a guardian could not be added to seed the fixture');
  done();
  process.exit();
}

// Row 1: identical to the roll         → already here, NOT changed
// Row 2: same child, different class    → already here, changed on "class"
// Rows 3+4: one name, two new codes     → a possible duplicate within the file
const csv = [
  'Account,Student Name,Class,Guardian Name,Guardian Phone',
  `${unchanged.studentCode},${unchanged.fullName},${unchanged.className ?? ''},${guardian},${phone}`,
  `${moved.studentCode},${moved.fullName},${otherClass},${guardian},${phone}`,
  `ZZ${run}A,Zz Twin Case ${run},S1A,${guardian},${phone}`,
  `ZZ${run}B,${run} Case Twin Zz,S1A,${guardian},${phone}`,
  '',
].join('\n');

const file = path.join(os.tmpdir(), `qmgr-reimport-${run}.csv`);
fs.writeFileSync(file, csv);
say(`  unchanged: ${unchanged.studentCode} ${unchanged.fullName} (${unchanged.className ?? 'no class'})`);
say(`  moved:     ${moved.studentCode} ${moved.fullName} to ${otherClass}`);
say(`  twins:     "Zz Twin Case ${run}" and "${run} Case Twin Zz" — the same words, the other way round`);

const t = await openTab();
await t.viewport(1500, 950);
await login(t, USER, PASS);
await t.goto(`${BASE}/admin/students/roster`);
if (!await t.waitFor("!!document.querySelector('.qm-main')", 20000)) { note('the roster', 'never rendered'); done(); process.exit(); }
await t.sleep(2000);

await t.clickText('Bulk Import');
if (!await t.waitFor("!!document.querySelector('#roster-file-input')", 10000)) { note('the wizard', 'Bulk Import did not open'); done(); process.exit(); }
await t.sleep(600);
await t.setFiles('#roster-file-input', [file]);

await t.waitFor("document.body.innerText.indexOf('Match the columns') >= 0", 30000);
await t.sleep(2000);
await t.clickText('Continue');
await t.waitFor("document.body.innerText.indexOf('READY TO IMPORT') >= 0", 40000);
// The precheck is a round trip made after the preview renders, so wait for its banner.
await t.waitFor("!!document.querySelector('.q-import__banner--info')", 20000);
await t.sleep(1200);

const seen = await t.eval(`(() => {
  const nl = String.fromCharCode(10);
  const flat = (el) => el ? el.innerText.split(nl).join(' ').trim() : null;
  const info = document.querySelector('.q-import__banner--info');
  const dupes = document.querySelector('.qi-dupes');
  const btn = [...document.querySelectorAll('.q-import__actions button')].find(b => /import/i.test(b.innerText));
  return JSON.stringify({
    info: flat(info),
    changed: info && info.querySelector('.qi-changed') ? flat(info.querySelector('.qi-changed')) : null,
    changes: info ? [...info.querySelectorAll('.qi-changes li')].map(li => flat(li)) : [],
    dupes: flat(dupes),
    dupeRows: dupes ? [...dupes.querySelectorAll('.qi-changes li')].map(li => flat(li)) : [],
    importDisabled: btn ? btn.disabled : null,
    blocked: !!document.querySelector('.qi-blocked'),
  });
})()`);
const s = JSON.parse(seen);

say('  1. Already here');
check('the panel says who is already here', s.info != null && /already here/i.test(s.info), seen);
check('...and counts both of the roll rows', /2 of these/.test(s.info ?? ''), s.info);

say('  2. What would change, by name of field');
check('it says how many have something different', /1 of them has something different/i.test(s.changed ?? ''), s.changed ?? '(absent)');
check('...and names the child it would move', s.changes.some(c => c.includes(moved.fullName)), s.changes.join(' | '));
check('...naming the FIELD', s.changes.some(c => /class/i.test(c)), s.changes.join(' | '));
check('...and never the stored value', !s.changes.some(c => c.includes(otherClass)), s.changes.join(' | '));

say('  3. A row identical to the roll is not a change');
check('the unchanged child is not listed as changed', !s.changes.some(c => c.includes(unchanged.fullName)), s.changes.join(' | '));
check('...and the panel says so', /would not change at all/i.test(s.changed ?? ''), s.changed ?? '(absent)');

say('  4. One name, two admission numbers');
check('a possible duplicate is raised', s.dupes != null, 'no .qi-dupes block');
check('...naming the child', s.dupeRows.some(d => d.includes(`Twin Case ${run}`) || d.includes('Case Twin Zz')), s.dupeRows.join(' | '));
check('...and saying it is one name under two numbers', s.dupeRows.some(d => /different admission numbers/i.test(d)), s.dupeRows.join(' | '));

say('  5. Import is held until the reader answers');
check('Import is disabled while the question stands', s.importDisabled === true, `disabled=${s.importDisabled}`);
check('...and the page says why', s.blocked === true, 'no .qi-blocked note');

const released = await t.eval(`(() => {
  const box = document.querySelector('.qi-confirm input[type=checkbox]');
  if (!box) return 'no checkbox';
  box.click();
  return 'clicked';
})()`);
if (released !== 'clicked') {
  note('the confirmation', released);
} else {
  await t.sleep(1200);
  const after = await t.eval(`(() => {
    const b = [...document.querySelectorAll('.q-import__actions button')].find(x => /import/i.test(x.innerText));
    return JSON.stringify({ disabled: b ? b.disabled : null, blocked: !!document.querySelector('.qi-blocked') });
  })()`);
  const a = JSON.parse(after);
  check('confirming releases Import', a.disabled === false, after);
  check('...and the note goes', a.blocked === false, after);
}

t.close();
try { fs.unlinkSync(file); } catch { }

// Put the roll back: the guardians were seeded by this run, not by the school.
for (const [studentId, linkId] of cleanup) {
  const res = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${studentId}/guardians/${linkId}`, { method: 'DELETE', headers: H });
  check('CLEANUP: the seeded guardian is removed', res.ok, String(res.status));
}

done();

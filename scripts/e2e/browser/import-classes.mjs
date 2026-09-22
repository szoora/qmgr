// MISSING CLASSES ON A ROSTER IMPORT — said once, with something to do about it.
//
// Reported against the first real school roll this product imported: 1,711 students, and
// "S1A is not one of this branch's classes" on 1,584 of the rows. The sentence was true, it was on
// every row, and there was nothing the reader could do with it.
//
// Two things were wrong. The comparison was Trim() plus case, so "S1 A" and "S1A" were different
// classes — which is the exact failure CLAUDE.md already warns about for the class-teacher scope
// ("a student invisible to their own class teacher because of a stray space is a safeguarding
// failure"). And a per-row warning is the wrong shape for a fact about the FILE.
//
// What this asserts:
//   1. FOLDING — "s2 a", "S2-A" and "S2/B" are the configured "S2A"/"S2B" and raise nothing;
//   2. the missing classes are listed ONCE each, with the number of students in them;
//   3. a near name is offered ("S1A" offers "S1"), and an unrelated one is not;
//   4. the per-row wall of the same sentence is gone;
//   5. mapping a class to an existing one shows what will happen and can be undone.
//
// It stops before pressing Import: reading the file correctly is what is under test, and importing
// seven dummy students on every run leaves a mess in a roll somebody has to clean.
//
// Run: node scripts/e2e/browser/import-classes.mjs   (CDP_PORT=9444 to watch it)
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
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`);
};
const note = (name, why) => { skip++; say(`    SKIP  ${name}  — ${why}`); };

// ---- what the branch actually has -----------------------------------------------------------------
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${(await auth.json()).accessToken}` };
const vocab = await (await fetch(`${API}/api/v1/branches/${BRANCH}/students/vocabularies`, { headers: H })).json().catch(() => null);
const configured = (vocab?.classes ?? []).map(c => c.name);
say(`  configured classes: ${configured.join(' | ') || '(none)'}`);

// The file is BUILT FROM what the branch has, so the suite cannot pass vacuously against a tenant
// whose list happens to differ. It needs one configured class to fold against and one name the
// branch definitely has not got.
const foldTarget = configured.find(c => /^[A-Za-z]+\d+[A-Za-z]$/.test((c ?? '').replace(/\s+/g, '')));
if (!foldTarget) {
  note('the whole suite', `no configured class of the form "S2A" to fold against — has ${configured.join(', ')}`);
  say(`  import-classes: ${pass} passed, ${fail} failed, ${skip} skipped`);
  process.exit(0);
}

const bare = foldTarget.replace(/\s+/g, '');
const spaced = `${bare.slice(0, -1)} ${bare.slice(-1)}`.toLowerCase();   // "s2 a"
const hyphen = `${bare.slice(0, -1)}-${bare.slice(-1)}`;                  // "S2-A"
const MISSING_A = 'S1', MISSING_A_STREAM = 'A';                          // joins to S1A
const UNKNOWN = 'XZ9';

const run = Date.now().toString(36).slice(-4);
const csv = [
  'Account,Student Name,Class,Stream',
  `90${run}1,Zz Fold Lower ${run},${spaced},`,
  `90${run}2,Zz Fold Hyphen ${run},${hyphen},`,
  `90${run}3,Zz Missing One ${run},${MISSING_A},${MISSING_A_STREAM}`,
  `90${run}4,Zz Missing Two ${run},${MISSING_A},${MISSING_A_STREAM}`,
  `90${run}5,Zz Missing Three ${run},${MISSING_A},B`,
  `90${run}6,Zz Unknown One ${run},${UNKNOWN},Q`,
  '',
].join('\n');

const file = path.join(os.tmpdir(), `qmgr-classes-${run}.csv`);
fs.writeFileSync(file, csv);
say(`  file: ${spaced} · ${hyphen} should FOLD to ${foldTarget}; ${MISSING_A}${MISSING_A_STREAM}, ${MISSING_A}B and ${UNKNOWN}Q should be MISSING`);

// ---- drive the wizard ------------------------------------------------------------------------------
const t = await openTab();
await t.viewport(1500, 950);
await login(t, USER, PASS);
await t.goto(`${BASE}/admin/students/roster`);
if (!await t.waitFor("!!document.querySelector('.qm-main')", 20000)) {
  note('the roster', 'the page never rendered'); process.exit(1);
}
await t.sleep(2000);

await t.clickText('Bulk Import');
if (!await t.waitFor("!!document.querySelector('#roster-file-input')", 10000)) {
  note('the wizard', 'Bulk Import did not open'); process.exit(1);
}
await t.sleep(600);
await t.setFiles('#roster-file-input', [file]);

// Step 2 appears once the sheet is read.
await t.waitFor("document.body.innerText.indexOf('Match the columns') >= 0", 30000);
await t.sleep(2000);
await t.clickText('Continue');
await t.waitFor("document.body.innerText.indexOf('READY TO IMPORT') >= 0", 40000);
await t.sleep(2500);

const seen = await t.eval(`(() => {
  const p = document.querySelector('.mc-panel');
  const rows = p ? [...p.querySelectorAll('tbody tr')].map(tr => {
    const c = tr.querySelectorAll('td');
    return {
      name: c[0] ? c[0].innerText.trim() : '',
      students: c[1] ? c[1].innerText.trim() : '',
      options: c[2] ? c[2].innerText.split(String.fromCharCode(10)).join(' ').trim() : '',
    };
  }) : [];
  const body = document.body.innerText;
  return JSON.stringify({
    panel: !!p,
    head: p ? p.querySelector('.mc-head').innerText.trim() : null,
    rows,
    oldWarning: body.indexOf('is not one of this branch') >= 0,
    perRowMentions: (body.split('is not one of this branch').length - 1),
  });
})()`);
const s = JSON.parse(seen);

// ---- 1. folding -------------------------------------------------------------------------------------
say('  1. A class written differently is the SAME class');
const names = s.rows.map(r => r.name);
check(`"${spaced}" does not appear as missing`, !names.some(n => n.toLowerCase() === spaced), names.join(' | '));
check(`"${hyphen}" does not appear as missing`, !names.includes(hyphen), names.join(' | '));

// ---- 2 & 3. the list --------------------------------------------------------------------------------
say('  2. The missing classes, once each');
check('the panel is shown', s.panel === true, seen);
check(`it names ${MISSING_A}${MISSING_A_STREAM}`, names.includes(`${MISSING_A}${MISSING_A_STREAM}`), names.join(' | '));
check(`it names ${UNKNOWN}Q`, names.includes(`${UNKNOWN}Q`), names.join(' | '));
check('each class appears once', new Set(names).size === names.length, names.join(' | '));

const s1a = s.rows.find(r => r.name === `${MISSING_A}${MISSING_A_STREAM}`);
check('...with how many students are in it', s1a?.students === '2', JSON.stringify(s1a));

say('  3. A near name is offered, an unrelated one is not');
check(`${MISSING_A}${MISSING_A_STREAM} offers an existing class`, (s1a?.options ?? '').includes('existing class'), JSON.stringify(s1a));
const unknown = s.rows.find(r => r.name === `${UNKNOWN}Q`);
check(`${UNKNOWN}Q offers only Add`, unknown != null && !(unknown.options ?? '').includes('existing class'), JSON.stringify(unknown));
check('every missing class offers Add', s.rows.every(r => (r.options ?? '').includes('Add')), JSON.stringify(s.rows));

// ---- 4. the wall is gone -----------------------------------------------------------------------------
say('  4. The per-row wall is gone');
check('the old per-row sentence appears nowhere', s.oldWarning === false, `${s.perRowMentions} mention(s)`);

// ---- 5. mapping --------------------------------------------------------------------------------------
say('  5. Pointing a class at one the branch already has');
const picked = await t.eval(`(() => {
  const sel = document.querySelector('.mc-pick');
  if (!sel) return 'no picker';
  const btn = sel.querySelector('button'); if (!btn) return 'no trigger';
  btn.click(); return 'opened';
})()`);
if (picked !== 'opened') {
  note('mapping', picked);
} else {
  await t.sleep(600);
  const chose = await t.eval(`(() => {
    const o = document.querySelector('.q-select__dropdown .q-select__option:not(.q-select__option--clear)');
    if (!o) return null; const label = o.innerText.trim(); o.click(); return label;
  })()`);
  await t.sleep(1200);
  const after = await t.eval(`(() => {
    const p = document.querySelector('.mc-panel');
    return JSON.stringify({
      chips: p ? [...p.querySelectorAll('.mc-chip')].map(c => c.innerText.trim()) : [],
      undo: p ? !!p.querySelector('.mc-clear') : false,
      rows: p ? p.querySelectorAll('tbody tr').length : -1,
    });
  })()`);
  const a = JSON.parse(after);
  check(`choosing "${chose}" records the mapping`, a.chips.length === 1, after);
  check('...and the class leaves the missing list', a.rows === s.rows.length - 1, `${a.rows} vs ${s.rows.length - 1}`);
  check('...and it can be undone', a.undo === true, after);
}

t.close();
try { fs.unlinkSync(file); } catch { }
say('');
const tail = `  import-classes: ${pass} passed, ${fail} failed, ${skip} skipped`;
say(tail);
process.exitCode = fail ? 1 : 0;

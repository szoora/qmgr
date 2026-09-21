// Individual timetable extracts: the print route narrowed by ?key=, the picker that writes it back,
// the editor carrying its view key across, the portal's "Print my timetable", and what a narrowed
// sheet is CALLED when it is published to the Library.
//
// Why a browser suite and not curl: every one of these is a client-side decision. The API returns the
// whole timetable and always did — the filtering, the naming, the URL round-trip and the button that
// hands a key over live entirely in the Razor components, so a curl run cannot see any of it. The one
// thing that would have made this pass vacuously is a timetable with a single teacher in it, so the
// suite refuses to run unless the version it picked has at least two.
//
// Run: node scripts/e2e/browser/timetable-print.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
// Section 9 needs somebody who actually HAS lessons: the portal card hides itself otherwise, and a
// skipped section is not a verified one. This account is the seeded Physics teacher on the dev tenant.
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
// Accounts the suites create hold one of two passwords depending on which suite made them
// (CLAUDE.md: the blocklist refuses the older one for NEW accounts), so resolve it against the API
// rather than guessing in the browser — a wrong guess burns a lockout attempt on a real account.
const TEACHER_PASSWORDS = process.env.E2E_TEACHER_PASS
  ? [process.env.E2E_TEACHER_PASS]
  : ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

// ---------------------------------------------------------------- pick a version with >1 teacher
const token = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
}).then(r => r.json()).then(j => j.accessToken);
if (!token) { console.error('could not sign in to the API'); process.exit(1); }

const auth = { Authorization: `Bearer ${token}` };

let teacherPass = null;
for (const pw of TEACHER_PASSWORDS) {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: TEACHER, password: pw }),
  });
  if (r.ok) { teacherPass = pw; break; }
}
if (!teacherPass) { console.error(`could not sign in as ${TEACHER}; set E2E_TEACHER_USER / E2E_TEACHER_PASS`); process.exit(1); }

const teacherToken = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: TEACHER, password: teacherPass }),
}).then(r => r.json()).then(j => j.accessToken);
const claims = (jwt) => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64').toString());
const teacherId = claims(teacherToken).sub;
const ORG = claims(token).org_id;
const versions = await fetch(`${API}/api/v1/branches/${BRANCH}/timetable/timetables`, { headers: auth }).then(r => r.json());

let target = null, lessons = [];
for (const v of versions.filter(v => (v.lessonCount ?? 0) > 1)) {
  const d = await fetch(`${API}/api/v1/branches/${BRANCH}/timetable/timetables/${v.id}`, { headers: auth }).then(r => r.json());
  const teachers = new Set(d.lessons.map(l => l.teacherUserId));
  if (teachers.size > 1) { target = v; lessons = d.lessons; break; }
}
if (!target) {
  console.error('SKIP: no timetable version on this branch has two teachers in it, so a "one teacher, not all" assertion would pass vacuously. Place lessons for two teachers first.');
  process.exit(2);
}

const byTeacher = new Map();
for (const l of lessons) byTeacher.set(l.teacherUserId, l.teacherName);
const teacherIds = [...byTeacher.keys()];
const classNames = [...new Set(lessons.map(l => l.className))];
const busiest = teacherIds.map(id => [id, lessons.filter(l => l.teacherUserId === id).length]).sort((a, b) => b[1] - a[1])[0][0];

console.log(`\n== Timetable print extracts — "${target.name}" (${byTeacher.size} teachers, ${classNames.length} classes) ==`);
post(`\n== Timetable print extracts — "${target.name}" (${byTeacher.size} teachers, ${classNames.length} classes) ==`);

const t = await openTab();
await t.viewport(1440, 900);
await login(t, USER, PASS);

// The signed-in session carries whichever branch it last used; every page reads BranchState, so
// point it at the branch whose timetable we picked or every read answers 404 for the wrong branch.
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);
await t.goto(`${BASE}/admin/timetable`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(1200);

const PRINT = `${BASE}/admin/timetable/${target.id}/print`;
const sheets = async () => t.eval(`[...document.querySelectorAll('.ttp-page')].map(p => p.querySelector('.ttp-head__who strong')?.innerText.trim() || '')`);
const ready = async () => t.waitFor(`!!document.querySelector('.ttp-page, .ttp-muted')`, 20000);

// ---------------------------------------------------------------- 1. no key = every sheet (unchanged)
await t.goto(`${PRINT}?by=teacher`);
await ready();
const all = await sheets();
check('1a: no key prints every teacher', all.length === byTeacher.size, `got ${all.length} of ${byTeacher.size}`);

// ---------------------------------------------------------------- 2. ?key= narrows to one
await t.goto(`${PRINT}?by=teacher&key=${busiest}`);
await ready();
const one = await sheets();
check('2a: ?key= prints exactly one sheet', one.length === 1, `got ${one.length}: ${one.join(' | ')}`);
check('2b: and it is the teacher asked for', one[0] === byTeacher.get(busiest), `got "${one[0]}", wanted "${byTeacher.get(busiest)}"`);

const lessonsOnSheet = await t.eval(`document.querySelectorAll('.ttp-page .ttp-lesson').length`);
const expectedCells = new Set(lessons.filter(l => l.teacherUserId === busiest).map(l => l.groupId ?? l.id)).size;
check('2c: the sheet carries that teacher\'s lessons', lessonsOnSheet === expectedCells, `${lessonsOnSheet} cells, expected ${expectedCells}`);

const title = await t.eval(`document.title + '|' + (document.querySelector('.ttp-doc')?.innerText || '')`);
check('2d: the document is named for the person, not "by teacher"',
  !/by teacher/i.test(await t.eval(`document.querySelector('.q-print__sheet')?.getAttribute('aria-label') || ''`)),
  `aria-label still reads "by teacher" (${title})`);

// ---------------------------------------------------------------- 3. several keys
const two = teacherIds.slice(0, 2);
await t.goto(`${PRINT}?by=teacher&key=${encodeURIComponent(two.join(','))}`);
await ready();
const pair = await sheets();
check('3a: a comma-separated key prints just those sheets', pair.length === 2, `got ${pair.length}`);
check('3b: both are the ones asked for',
  two.every(id => pair.includes(byTeacher.get(id))), `got ${pair.join(' | ')}`);

// ---------------------------------------------------------------- 4. an unknown key prints nothing, and says so
await t.goto(`${PRINT}?by=teacher&key=00000000-0000-0000-0000-000000000000`);
await ready();
const emptyText = await t.eval(`document.querySelector('.ttp-muted')?.innerText.trim() || ''`);
check('4a: an unknown key prints no sheet', (await sheets()).length === 0);
check('4b: and the message names the chosen-sheet case', /chosen teacher/i.test(emptyText), `read "${emptyText}"`);

// ---------------------------------------------------------------- 5. the picker narrows and writes the URL back
await t.goto(`${PRINT}?by=teacher`);
await ready();
const optionLabels = await t.eval(`(() => {
  const trigger = document.querySelector('.ttp-pick button, .ttp-pick [role="combobox"], .ttp-pick .q-select__trigger');
  if (!trigger) return null; trigger.click(); return true;
})()`);
check('5a: the sheet picker is on the toolbar', optionLabels === true, 'no .ttp-pick control rendered');
await t.sleep(400);
const opts = await t.eval(`[...document.querySelectorAll('.ttp-pick [role="option"], .ttp-pick .q-select__option, .ttp-pick li')].map(o => o.innerText.trim()).filter(Boolean)`);
check('5b: it offers one entry per teacher', Array.isArray(opts) && opts.length >= byTeacher.size,
  `offered ${opts?.length ?? 0}, expected at least ${byTeacher.size}`);

const wanted = byTeacher.get(busiest);
const picked = await t.eval(`(() => {
  const o = [...document.querySelectorAll('.ttp-pick [role="option"], .ttp-pick .q-select__option, .ttp-pick li')]
    .find(e => e.innerText.trim().includes(${JSON.stringify(wanted)}));
  if (!o) return false; o.click(); return true;
})()`);
check('5c: a teacher can be chosen in the picker', picked === true, `no option reading "${wanted}"`);
await t.sleep(900);
const urlAfterPick = await t.eval(`location.search`);
check('5d: choosing writes the key into the URL', urlAfterPick.includes('key=') && urlAfterPick.includes(busiest),
  `search is "${urlAfterPick}"`);
check('5e: and the page narrows to that one sheet', (await sheets()).length === 1, `got ${(await sheets()).length}`);

// ---------------------------------------------------------------- 6. switching axis drops a key that means nothing there
const switched = await t.eval(`(() => {
  const tab = [...document.querySelectorAll('button, [role="tab"], a')].find(e => e.innerText.trim() === 'Classes');
  if (!tab) return false; tab.click(); return true;
})()`);
check('6a: the axis tabs are still there', switched === true);
await t.sleep(900);
const afterSwitch = await t.eval(`location.search`);
check('6b: switching axis clears the old axis\'s key', !afterSwitch.includes('key='), `search is "${afterSwitch}"`);
check('6c: and every class prints again', (await sheets()).length === classNames.length,
  `got ${(await sheets()).length} of ${classNames.length}`);

// ---------------------------------------------------------------- 7. the editor carries its view key to the print route
await t.goto(`${BASE}/admin/timetable?t=${target.id}&by=teacher&key=${busiest}`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(1500);
const printHref = await t.eval(`(() => {
  const b = [...document.querySelectorAll('button')].find(e => e.innerText.trim() === 'Print');
  if (!b) return 'NO BUTTON'; b.click(); return 'clicked';
})()`);
check('7a: the editor has a Print button', printHref === 'clicked', printHref);
await t.waitFor(`location.pathname.endsWith('/print')`, 15000);
await t.sleep(600);
const editorPrintSearch = await t.eval(`location.search`);
check('7b: it carries the teacher being viewed', editorPrintSearch.includes(busiest),
  `landed on "${editorPrintSearch}" — the key was dropped`);
await ready();
check('7c: so the editor prints one teacher, not the school', (await sheets()).length === 1,
  `got ${(await sheets()).length} sheets`);

// ---------------------------------------------------------------- 8. no error bar anywhere we have been
const errBar = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui');
  return e ? getComputedStyle(e).display : 'none'; })()`);
check('8a: no Blazor error bar (read with getComputedStyle, not the style attribute)', errBar === 'none', `display: ${errBar}`);

// ---------------------------------------------------------------- 9. a TEACHER prints their own week
// Signed in as the teacher, not the administrator: this is the half of the feature that is supposed to
// need nobody else, and the reader's own permissions are the thing under test. A published version in
// force is required — the card reads .../timetable/current, which is 204 when none is.
await login(t, TEACHER, teacherPass);
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);
await t.goto(`${BASE}/portal`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(3000);
const portalBtn = await t.eval(`(() => {
  const b = [...document.querySelectorAll('button')].find(e => e.innerText.trim() === 'Print my timetable');
  return b ? 'found' : (document.querySelector('.mytt-grid') ? 'card but no button' : 'no card');
})()`);
if (portalBtn === 'no card') {
  check('9a: the portal shows "My timetable" for a teacher with lessons', false,
    'no card — is a timetable PUBLISHED and in force today? .../timetable/current answers 204 otherwise');
} else {
  check('9a: the portal card offers "Print my timetable"', portalBtn === 'found', portalBtn);
  await t.eval(`[...document.querySelectorAll('button')].find(e => e.innerText.trim() === 'Print my timetable')?.click()`);
  await t.waitFor(`location.pathname.endsWith('/print')`, 15000);
  await t.sleep(800);
  const s = await t.eval(`location.search`);
  check('9b: it goes straight to that person\'s own sheet', /by=teacher/.test(s) && /key=/.test(s), `search is "${s}"`);
  await ready();
  const own = await sheets();
  check('9c: and prints exactly one sheet', own.length === 1, `got ${own.length}: ${own.join(' | ')}`);
  const keyInUrl = /key=([^&]+)/.exec(s)?.[1] ?? '';
  check('9d: the sheet is the teacher\'s OWN — the key is their user id and the name is theirs',
    keyInUrl.toLowerCase() === teacherId.toLowerCase() && own[0] === byTeacher.get(teacherId),
    `key ${keyInUrl} vs ${teacherId}; sheet "${own[0]}" vs "${byTeacher.get(teacherId)}"`);
  const errAsTeacher = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui');
    return e ? getComputedStyle(e).display : 'none'; })()`);
  check('9e: a plain teacher reaches the print route with no error', errAsTeacher === 'none', `display: ${errAsTeacher}`);
}

// back to the administrator for the publish check
await login(t, USER, PASS);
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

// ---------------------------------------------------------------- 10. publish names the extract, not the axis
await t.goto(`${PRINT}?by=teacher&key=${busiest}`);
await ready();
const publishable = await t.eval(`!![...document.querySelectorAll('button')].find(e => /Publish to Library/.test(e.innerText))`);
if (!publishable) {
  const line = '    SKIP  10: this user cannot publish to the Library (needs library.publish and the Communication module)';
  console.log(line); post(line);
} else {
  const label = await t.eval(`document.querySelector('.q-print__sheet')?.getAttribute('aria-label') || ''`);
  check('10a: the sheet about to be published is named for the teacher',
    label.includes(byTeacher.get(busiest)) && !/by teacher/i.test(label), `aria-label is "${label}"`);

  // Actually publish it. Asserting the NAME without producing the document would only be testing a
  // string the page already showed us; what matters is the row the Library ends up with.
  await t.eval(`[...document.querySelectorAll('button')].find(e => /Publish to Library/.test(e.innerText))?.click()`);
  const landed = await t.waitFor(`location.href.includes('document=')`, 60000);
  check('10b: publishing produces a document and opens it', landed === true,
    `still on ${await t.eval('location.href')} — ${(await t.eval("document.body.innerText")).slice(0, 200)}`);

  const docs = await fetch(`${API}/api/v1/organizations/${ORG}/media?pageSize=200`, { headers: auth })
    .then(r => r.json()).catch(() => null);
  const list = docs?.items ?? docs ?? [];
  // Narrow on the VERSION name as well as the teacher. A bare name match once reached an unrelated
  // "Staff performance report — <the same teacher>" left by another suite and deleted it; a cleanup
  // that can touch somebody else's row is worse than no cleanup.
  const mine = list.filter(d => {
    const n = d.title ?? d.name ?? '';
    return n.includes(byTeacher.get(busiest)) && n.includes(target.name);
  });
  check('10c: the Library row is named for the teacher, not "by teacher"',
    mine.length > 0 && !mine.some(d => /by teacher/i.test(d.title ?? d.name ?? '')),
    `matched ${mine.length} of ${list.length}: ${list.slice(0, 3).map(d => d.title ?? d.name).join(' | ')}`);

  // Tidy up: these are snapshots produced by a test run, and nothing has been shared from them.
  for (const d of mine) {
    const r = await fetch(`${API}/api/v1/media/${d.id}`, { method: 'DELETE', headers: auth });
    console.log(`    cleanup: removed "${d.title ?? d.name}" (${r.status})`);
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
t.close();
process.exit(fail ? 1 : 0);

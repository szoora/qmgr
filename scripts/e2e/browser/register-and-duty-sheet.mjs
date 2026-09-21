// Two reworks of 2026-09-20, both invisible to a curl suite by construction:
//
//  1. THE REGISTER at the size it is actually taken. The page was built for a five-person duty and
//     reported against a staff meeting: capped at 760px on a 1900px screen, one person per ~150px,
//     no way to mark more than one at a time, and two server refusals (self-marking, a rota's
//     missing reason) reachable only by losing the whole submit to a 400.
//  2. DUTIES ON THE TEACHER PRINT SHEET, so a printed timetable cannot be double-booked against.
//
// Run: node scripts/e2e/browser/register-and-duty-sheet.mjs   (headless Chrome on 9333)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log('\n== ' + s + ' =='); post('\n== ' + s + ' =='); };

const token = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
}).then(r => r.json()).then(j => j.accessToken);
const auth = { Authorization: `Bearer ${token}` };
const meId = JSON.parse(Buffer.from(token.split('.')[1], 'base64').toString()).sub;

// The biggest open register on the branch: the rework is about scale, so a 1-row duty would pass
// every assertion here while proving none of them.
const now = new Date();
const from = new Date(now); from.setDate(from.getDate() - 3);
const to = new Date(now); to.setDate(to.getDate() + 3);
const duties = await fetch(
  `${API}/api/v1/branches/${BRANCH}/staff/duties?from=${from.toISOString()}&to=${to.toISOString()}`, { headers: auth })
  .then(r => r.json());
const open = duties
  .filter(d => d.isActive && !d.registerClosedAt && d.canIRecord && d.kind !== 'Lesson')
  .sort((a, b) => b.expectedCount - a.expectedCount)[0];
if (!open) {
  console.error('SKIP: no open register this side of today that this account records. Seed one first.');
  process.exit(2);
}
console.log(`register under test: "${open.title}" — ${open.expectedCount} expected`);

const t = await openTab();
await t.viewport(1600, 950);
await login(t, USER, PASS);
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

// ---------------------------------------------------------------- 1. the register
hdr(`Register — "${open.title}" (${open.expectedCount} expected)`);
await t.goto(`${BASE}/admin/staff/duties/${open.id}/register`);
await t.waitFor(`document.querySelectorAll('.reg-row').length > 0`, 25000);
await t.sleep(900);

const geom = await t.eval(`(() => {
  const page = document.querySelector('.reg-page');
  const main = document.querySelector('.qm-main');
  const row = document.querySelector('.reg-row');
  return {
    pageW: Math.round(page.getBoundingClientRect().width),
    mainW: Math.round(main.getBoundingClientRect().width),
    rowH: Math.round(row.getBoundingClientRect().height),
    rows: document.querySelectorAll('.reg-row').length,
    docW: document.documentElement.scrollWidth,
    winW: window.innerWidth,
  };
})()`);

check('1a: the page uses the width it has (no reading-width cap)', geom.pageW > geom.mainW - 80,
  `page ${geom.pageW}px inside a ${geom.mainW}px main — still capped`);
check('1b: a row is one line, not three stacked blocks', geom.rowH <= 72, `row is ${geom.rowH}px tall`);
check('1c: the page does not scroll sideways', geom.docW <= geom.winW + 1, `${geom.docW} > ${geom.winW}`);

const virtualised = open.expectedCount > 40;
check('1d: a big register renders a window of rows, not all of them',
  !virtualised || (geom.rows > 0 && geom.rows < open.expectedCount),
  `${geom.rows} rows in the DOM for ${open.expectedCount} people`);

// Filters carry live counts.
const filters = await t.eval(`[...document.querySelectorAll('.q-bulkbar__chip')].map(b => b.innerText.replace(/\\s+/g,' ').trim())`);
check('1e: the filter chips carry counts', filters.some(f => /^All\s+\d+$/.test(f)), filters.join(' | '));

// Search narrows.
const firstName = await t.eval(`document.querySelector('.reg-row__name strong')?.innerText.trim() || ''`);
await t.eval(`(() => { const i = document.querySelector('.q-bulkbar__search input');
  const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  set.call(i, ${JSON.stringify(firstName)}); i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
await t.sleep(900);
const afterSearch = await t.eval(`document.querySelectorAll('.reg-row').length`);
check('1f: search narrows the list', afterSearch >= 1 && afterSearch < Math.max(2, geom.rows), `${afterSearch} rows for "${firstName}"`);

// Clear it again.
await t.eval(`(() => { const i = document.querySelector('.q-bulkbar__search input');
  const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  set.call(i, ''); i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
await t.sleep(900);

// ---- bulk ----
const sweepLabel = await t.eval(`[...document.querySelectorAll('.q-bulkbar__bulk--idle button')].map(b => b.innerText.trim()).join(' | ')`);
check('1g: a sweep for the remainder is offered', /rest /i.test(sweepLabel), `buttons: ${sweepLabel}`);

const pickedCount = await t.eval(`(() => {
  const boxes = [...document.querySelectorAll('.reg-row__pick input')].filter(b => !b.disabled).slice(0, 3);
  boxes.forEach(b => b.click());
  return boxes.length;
})()`);
await t.sleep(700);
const bulkText = await t.eval(`document.querySelector('.q-bulkbar__count')?.innerText.trim() || ''`);
check('1h: ticking rows opens the bulk bar with a count', bulkText.startsWith(String(pickedCount)), `"${bulkText}" for ${pickedCount} ticked`);

const markedBefore = await t.eval(`document.querySelector('.reg-footer__count')?.innerText.trim() || ''`);
await t.eval(`document.querySelector('.reg-bulk__btn')?.click()`);
await t.sleep(900);
const markedAfter = await t.eval(`document.querySelector('.reg-footer__count')?.innerText.trim() || ''`);
const n = (s) => parseInt((s.match(/^(\d+)/) ?? [0, '0'])[1], 10);
check('1i: one bulk press marks every ticked person', n(markedAfter) - n(markedBefore) === pickedCount,
  `"${markedBefore}" → "${markedAfter}" for ${pickedCount} ticked`);
check('1j: the selection is dropped once applied',
  (await t.eval(`document.querySelectorAll('.q-bulkbar__count').length`)) === 0, 'the bulk bar is still showing a selection');

const unsaved = await t.eval(`[...document.querySelectorAll('.reg-footer__status .q-chip, .reg-footer__status [class*=chip]')].map(c => c.innerText.trim()).join(' | ')`);
check('1k: unsaved marks are flagged before saving', /unsaved/i.test(unsaved), `footer chips: "${unsaved}"`);

// ---- self-marking is refused on the row, not by the server ----
// The list virtualises, so the recorder's own row is almost certainly outside the rendered window.
// Search for them by name first, or this asserts nothing and reports SKIP for the wrong reason.
const myName = await fetch(`${API}/api/v1/users/${meId}`, { headers: auth })
  .then(r => r.json()).then(u => u.fullName ?? `${u.firstName ?? ''} ${u.lastName ?? ''}`.trim()).catch(() => '');
if (myName) {
  await t.eval(`(() => { const i = document.querySelector('.q-bulkbar__search input');
    const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    set.call(i, ${JSON.stringify(myName)}); i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
  await t.sleep(900);
}
const selfRow = await t.eval(`(() => {
  const el = document.querySelector('.reg-row--self');
  if (!el) return null;
  return { locked: !!el.querySelector('.reg-row__locked'), seg: el.querySelectorAll('.reg-seg__btn').length,
           text: el.querySelector('.reg-row__locked')?.innerText.trim() || '' };
})()`);
if (selfRow === null) {
  const line = '    SKIP  1l: the recorder is not on this register, so the self-marking rule has no row to show';
  console.log(line); post(line);
} else {
  check('1l: the recorder cannot mark themselves, and the row says why',
    selfRow.locked && selfRow.seg === 0, `locked=${selfRow.locked} buttons=${selfRow.seg} "${selfRow.text}"`);
}

// ---- no error bar ----
const err = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`);
check('1m: no Blazor error bar', err === 'none', `display: ${err}`);

// ---- mobile ----
// Clear the search from the self-row check first: on a register where the only row shown is the
// recorder's own, there are no mark buttons at all — which is the rule above, not a phone failure.
await t.eval(`(() => { const i = document.querySelector('.q-bulkbar__search input'); if (!i) return false;
  const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  set.call(i, ''); i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
await t.sleep(900);
await t.viewport(390, 844, true);
await t.sleep(1200);
const phone = await t.eval(`(() => {
  const row = document.querySelector('.reg-row');
  const seg = document.querySelector('.reg-seg__btn');
  return { docW: document.documentElement.scrollWidth, winW: window.innerWidth,
           segH: seg ? Math.round(seg.getBoundingClientRect().height) : 0,
           rowW: row ? Math.round(row.getBoundingClientRect().width) : 0 };
})()`);
check('2a: no sideways scroll at 390px', phone.docW <= phone.winW + 1, `${phone.docW} > ${phone.winW}`);
check('2b: the marks stay a 44px thumb target on a phone', phone.segH >= 40, `${phone.segH}px`);
await t.viewport(1600, 950);
await t.sleep(600);

// ---------------------------------------------------------------- 3. duties on the teacher sheet
hdr('Duties on the teacher print sheet');
const versions = await fetch(`${API}/api/v1/branches/${BRANCH}/timetable/timetables`, { headers: auth }).then(r => r.json());
let tt = null, lessons = [];
for (const v of versions.filter(v => (v.lessonCount ?? 0) > 1)) {
  const d = await fetch(`${API}/api/v1/branches/${BRANCH}/timetable/timetables/${v.id}`, { headers: auth }).then(r => r.json());
  if (new Set(d.lessons.map(l => l.teacherUserId)).size > 1) { tt = v; lessons = d.lessons; break; }
}
if (!tt) {
  const line = '    SKIP  3: no timetable with two teachers on this branch';
  console.log(line); post(line);
} else {
  const teacher = lessons[0].teacherUserId;
  await t.goto(`${BASE}/admin/timetable/${tt.id}/print?by=teacher&key=${teacher}`);
  await t.waitFor(`!!document.querySelector('.ttp-page')`, 25000);
  await t.sleep(1200);

  const sheet = await t.eval(`(() => {
    const d = document.querySelector('.ttp-duties');
    return { present: !!d, heading: d?.querySelector('h2')?.innerText.trim() || '',
             rows: d ? d.querySelectorAll('.ttp-duties__table tbody tr').length : 0,
             none: d?.querySelector('.ttp-duties__none')?.innerText.trim() || '',
             toggle: !!document.querySelector('.ttp-duties-toggle') };
  })()`);
  check('3a: the teacher sheet carries a Duties section', sheet.present, 'no .ttp-duties on the sheet');
  check('3b: it names the period it covers', /^Duties\s+\d/.test(sheet.heading), `heading "${sheet.heading}"`);
  check('3c: it either lists duties or says there are none', sheet.rows > 0 || sheet.none.length > 0,
    `${sheet.rows} rows, message "${sheet.none}"`);
  check('3d: the toolbar offers the Duties toggle', sheet.toggle, 'no .ttp-duties-toggle');

  // Off means off.
  await t.eval(`document.querySelector('.ttp-duties-toggle input')?.click()`);
  await t.sleep(900);
  check('3e: turning the toggle off removes the section',
    (await t.eval(`document.querySelectorAll('.ttp-duties').length`)) === 0, 'the section is still on the sheet');
  await t.eval(`document.querySelector('.ttp-duties-toggle input')?.click()`);
  await t.sleep(700);

  // The class axis is not a person, so it has no duties.
  await t.goto(`${BASE}/admin/timetable/${tt.id}/print?by=class`);
  await t.waitFor(`!!document.querySelector('.ttp-page')`, 25000);
  await t.sleep(900);
  check('3f: a class sheet carries no duties section and no toggle',
    (await t.eval(`document.querySelectorAll('.ttp-duties, .ttp-duties-toggle').length`)) === 0,
    'duties leaked onto the class axis');

  const err2 = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`);
  check('3g: no Blazor error bar on the print route', err2 === 'none', `display: ${err2}`);
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
t.close();
process.exit(fail ? 1 : 0);

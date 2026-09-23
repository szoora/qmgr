// THE STUDENT ROSTER — filters in the address, a pager with a size, and a bulk bar that only ever
// reaches what the filters show (plan docs/plans/STUDENT_ROSTER_AND_LIST_STANDARD.md §2).
//
// Asked for as: "Add pagination to this and similar list pages. the only available filter feature on
// this page is the search. i need to be able to filter students of a class … page size … including
// all … is this page using the shared bulk action component? … log a record for selected group".
//
// The dev tenant has a handful of students, and a pager over five rows proves nothing. So the suite
// SEEDS sixty into a scratch class of its own ("ZZ Test 7"), with a run id in every code, and
// DEACTIVATES them at the end (a student is never hard-deleted — see StudentsController).
//
// It does NOT submit the Log a record dialog: the welfare ledger is append-only, and the API suite
// covers the submission. It only asserts that the dialog opens with the right number of students.
//
// Dropdowns are driven with a REAL press-hold-release (CDP Input.dispatchMouseEvent), never
// element.click(): a scripted click fires no mousedown and moves no focus, and it passed for weeks
// over a dropdown that did not work (CLAUDE.md, "Dropdowns").
//
// Run: node scripts/e2e/browser/student-roster.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const CLASS = 'ZZ Test 7';
const SEED = 60;

let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

// ---- the API side: sign in, seed, and put everything back --------------------------------------
const token = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
}).then(r => r.json()).then(j => j.accessToken).catch(() => null);
if (!token) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };

const run = Date.now().toString(36).toUpperCase();
const seeded = [];
for (let i = 0; i < SEED; i++) {
  const n = String(i).padStart(2, '0');
  const res = await fetch(`${API}/api/v1/branches/${BRANCH}/students`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ fullName: `Roster Test ${run} ${n}`, studentCode: `ZZ7-${run}-${n}`, className: CLASS }),
  }).catch(() => null);
  if (res?.ok) seeded.push((await res.json()).id);
}

const cleanup = async () => {
  for (const id of seeded) {
    try { await fetch(`${API}/api/v1/branches/${BRANCH}/students/${id}`, { method: 'DELETE', headers: H }); } catch { }
  }
};

if (seeded.length !== SEED) {
  check(`SETUP: ${SEED} scratch students seeded into "${CLASS}"`, false, `${seeded.length} created — does ${USER} hold students.manage?`);
  await cleanup();
  console.log(`  student-roster: ${pass} passed, ${fail} failed`);
  process.exit(1);
}

const roll = await fetch(`${API}/api/v1/branches/${BRANCH}/students?limit=10000`, { headers: H })
  .then(r => r.ok ? r.json() : []).catch(() => []);
const onRoll = roll.filter(s => s.isActive).length;

// ---- the browser --------------------------------------------------------------------------------
const t = await openTab();
await t.viewport(1600, 1000);

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 90) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  return true;
}
const rows = () => t.eval(`document.querySelectorAll('.roster-row:not(.roster-header)').length`);
const caption = () => t.eval(`document.querySelector('.q-pager__caption')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
const pageSummary = () => t.eval(`document.querySelector('.q-pager__summary')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
const selectedText = () => t.eval(`document.querySelector('.q-bulkbar__count')?.innerText ?? ''`);
// The summary is one line of counts now (2026-09-23 compaction): the figure is the <strong> in each item.
const tile = (key) => t.eval(`document.querySelector('[data-tile="${key}"] strong')?.innerText.replace(/[^0-9]/g, '') ?? ''`);
const search = () => t.eval(`decodeURIComponent(location.search.replace(/\\+/g, ' '))`);
const errorBar = () => t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })()`);

/// The pager's "Rows per page" select: open it with a real press and take the option reading `label`.
async function pickSize(label) {
  if (!await press(await center(`document.querySelector('.q-pager__size .q-select')`))) return false;
  if (!await t.waitFor(`!!document.querySelector('.q-pager__size .q-select__dropdown')`, 4000)) return false;
  await t.sleep(200);
  const opt = `[...document.querySelectorAll('.q-pager__size .q-select__dropdown .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(label)})`;
  if (!await press(await center(opt))) return false;
  await t.sleep(900);
  return true;
}

try {
  await login(t, USER, PASS);
  await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);
  await t.goto(`${BASE}/admin/students/roster`);
  const ready = await t.waitFor(`!!document.querySelector('.roster-table')`, 30000);
  await t.sleep(1500);
  check('the roster renders its table', ready, (await t.eval(`document.querySelector('.qm-main')?.innerText.slice(0, 200) ?? ''`)));

  // ---- the summary and the caption ----------------------------------------------------------
  check('the summary shows the whole roll', (await tile('roll')) === String(onRoll), `tile=${await tile('roll')} api=${onRoll}`);
  check('...and "match the filters" equals it with no filter on', (await tile('match')) === String(onRoll), `tile=${await tile('match')}`);
  check('the caption counts every student', (await caption()).includes(`of ${onRoll.toLocaleString('en-US')}`), await caption());
  check('the default page is 25 rows (or the whole roll when smaller)', (await rows()) === Math.min(25, onRoll), `${await rows()} rows`);

  // ---- the class filter, by a real press on the QMultiSelect --------------------------------
  const opened = await press(await center(`document.querySelector('.roster-f--class .q-multiselect')`));
  const listed = opened && await t.waitFor(`!!document.querySelector('.roster-f--class .q-multiselect__dropdown')`, 4000);
  check('the class filter opens on a real press', listed);
  await t.sleep(250);
  const option = `[...document.querySelectorAll('.roster-f--class .q-select__option')].find(o => o.innerText.trim().startsWith(${JSON.stringify(CLASS + ' (')}))`;
  const optionText = await t.eval(`(${option})?.innerText.trim() ?? ''`);
  check(`the scratch class is offered with its count ("${CLASS} (${SEED})")`, optionText === `${CLASS} (${SEED})`, `"${optionText}"`);
  await press(await center(option));
  await t.sleep(700);
  await press(await center(`document.querySelector('.qm-main h1')`));   // move focus away; the list closes on blur
  await t.sleep(1200);

  check('the class filter narrows the roll to the scratch class', (await caption()).includes(`of ${SEED}`), await caption());
  check('...and the address carries it', (await search()).includes(`class=${CLASS}`), await search());
  check('...shown as a removable chip', await t.eval(`[...document.querySelectorAll('.roster-chips .q-chip')].some(c => c.innerText.includes(${JSON.stringify(CLASS)}))`));
  check('..."match the filters" follows it', (await tile('match')) === String(SEED), `tile=${await tile('match')}`);

  await t.eval('location.reload(); true');
  await t.waitFor(`!!document.querySelector('.roster-table')`, 30000);
  await t.sleep(1800);
  check('a reload keeps the filter (read back from the address)', (await caption()).includes(`of ${SEED}`), await caption());

  // ---- page size ----------------------------------------------------------------------------
  check('page size 10 can be chosen', await pickSize('10'));
  check('...giving 10 rows', (await rows()) === 10, `${await rows()} rows`);
  check('...across 6 pages', /Page 1 of 6/.test(await pageSummary()), await pageSummary());

  check('All can be chosen', await pickSize('All'));
  check('...showing every one of the 60', (await rows()) === SEED, `${await rows()} rows`);
  check('...and the caption says so', /all 60/i.test(await caption()), await caption());

  await pickSize('10');
  check('back to 10 rows', (await rows()) === 10, `${await rows()} rows`);

  // ---- selection: this page, then an explicit "all N matching" -------------------------------
  // On a desktop "select this page" is the table header's box (the bar's own row appears once something is selected).
  await press(await center(`document.querySelector('.roster-header .q-checkbox__box')`));
  await t.sleep(800);
  check('"select all shown" selects this page only', /^10 selected/.test((await selectedText()).trim()), await selectedText());
  const offered = await t.eval(`!!document.querySelector('.roster-select-matching')`);
  check('...then offers "Select all 60 matching"', offered && /60/.test(await t.eval(`document.querySelector('.roster-select-matching')?.innerText ?? ''`)));
  await press(await center(`document.querySelector('.roster-select-matching')`));
  await t.sleep(800);
  check('...which selects every matching student', /^60 selected/.test((await selectedText()).trim()), await selectedText());
  check('the pager caption carries the selection', /60 selected/.test(await caption()), await caption());

  // ---- Log a record opens for the group (never submitted: the ledger is append-only) ---------
  const logBtn = await t.eval(`!!document.querySelector('.roster-bulk-log')`);
  check('the bulk bar offers "Log a record"', logBtn);
  if (logBtn) {
    await press(await center(`document.querySelector('.roster-bulk-log')`));
    const dialog = await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible .q-modal')`, 6000);
    const text = await t.eval(`document.querySelector('.q-modal-backdrop--visible .q-modal')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
    check('...and it opens with the 60 students', dialog && /\b60\b/.test(text), text.slice(0, 240));
    await press(await center(`document.querySelector('.q-modal-backdrop--visible .q-modal__close')`));
    await t.waitFor(`!document.querySelector('.q-modal-backdrop--visible')`, 4000);
    await t.sleep(400);
  }

  // ---- a filter change prunes the selection to what is visible --------------------------------
  await t.setValue('.q-bulkbar__search input', `ZZ7-${run}-0`);
  await t.sleep(1400);
  check('the search narrows within the class', (await tile('match')) === '10', `tile=${await tile('match')}`);
  check('...and the selection is pruned to the 10 it shows', /^10 selected/.test((await selectedText()).trim()), await selectedText());

  // ---- clearing -----------------------------------------------------------------------------
  await press(await center(`document.querySelector('.roster-clear-all')`));
  await t.sleep(1400);
  check('Clear all returns the whole roll', (await tile('match')) === String(onRoll), `tile=${await tile('match')}`);
  check('...and empties the address', !/class=|q=/.test(await search()), await search());

  check('no client-side error on the page', !(await errorBar()));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  await cleanup();
  const after = await fetch(`${API}/api/v1/branches/${BRANCH}/students?limit=10000`, { headers: H })
    .then(r => r.ok ? r.json() : []).catch(() => []);
  const left = after.filter(s => s.isActive && seeded.includes(s.id)).length;
  check('CLEANUP: every scratch student is deactivated', left === 0, `${left} still active`);
  t.close();
}

const line = `  student-roster: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;

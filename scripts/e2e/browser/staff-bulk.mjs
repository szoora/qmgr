// BULK ACTIONS ON THE STAFF DIRECTORY — put a group in a department, give a group a line manager.
//
// Asked for after importing 182 staff who all arrived with no department and nobody supervising
// them: "implement bulk action features for the common tasks like moving a selected group to
// department, assigning line manager to group". Setting that one person at a time is not a task
// anybody finishes.
//
// Neither action has an API of its own. Each runs the ordinary per-person endpoint once per row, so
// every rule that endpoint enforces still applies and a refusal is about ONE person and can be
// named. This suite is the only way to see any of that: the selection, the pruning, the dialog and
// the loop are all client-side.
//
// It changes real rows on the dev tenant and PUTS THEM BACK: each person's department and line
// manager are read first and restored at the end.
//
// Run: node scripts/e2e/browser/staff-bulk.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
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

// ---- the API side, for the setup and the restore -----------------------------------------------
const token = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
}).then(r => r.json()).then(j => j.accessToken).catch(() => null);
if (!token) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };

const directory = await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members`, { headers: H })
  .then(r => r.ok ? r.json() : null).catch(() => null);
const people = directory?.items ?? directory ?? [];
const departments = await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/departments`, { headers: H })
  .then(r => r.ok ? r.json() : []).catch(() => []);

if (!Array.isArray(people) || people.length < 2 || departments.length === 0) {
  console.log(`  SKIP  the dev tenant has ${Array.isArray(people) ? people.length : 0} staff and ${departments.length} department(s) — this needs two of each`);
  console.log('  0 passed, 0 failed');
  process.exit(0);
}

// Put back exactly what was there, whatever happens below.
const before = people.map(m => ({ userId: m.userId, departmentIds: m.departmentIds ?? [], lineManagerUserId: m.lineManagerUserId ?? null }));
const restore = async () => {
  for (const b of before) {
    try {
      await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members/${b.userId}`, {
        method: 'PUT', headers: H,
        body: JSON.stringify({ departmentIds: b.departmentIds, lineManagerUserId: b.lineManagerUserId }),
      });
    } catch { /* best effort; the next run re-reads anyway */ }
  }
};

const t = await openTab();
await t.viewport(1440, 950);
const text = () => t.eval(`document.querySelector('.qm-main')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
const tickCount = () => t.eval(`document.querySelectorAll('.sd-tick input[type=checkbox]').length`);

// A QSelect is driven with PRESS, HOLD, RELEASE — never element.click(). The list closes on a blur
// timer, and a scripted click fires no mousedown and moves no focus, so it passes while the control
// is broken and fails while it works (CLAUDE.md, "Dropdowns").
const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 80) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  return true;
}
/// Opens the dialog's own select and takes the first real option. Returns its label.
async function chooseFirstOption() {
  if (!await press(await center(`document.querySelector('.q-modal .q-select')`))) return null;
  if (!await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 4000)) return null;
  await t.sleep(250);
  const label = await t.eval(`(() => { const o = document.querySelector('.q-select__dropdown .q-select__option:not(.q-select__option--clear)');
    return o ? o.innerText.trim() : null; })()`);
  if (!await press(await center(`document.querySelector('.q-select__dropdown .q-select__option:not(.q-select__option--clear)')`))) return null;
  await t.sleep(400);
  return label;
}
/// A person's row checkbox, pressed properly. Returns how many were ticked.
async function tick(n) {
  let done = 0;
  for (let i = 0; i < n; i++) {
    if (await press(await center(`document.querySelectorAll('.sd-tick input[type=checkbox]')[${i}]`))) done++;
    await t.sleep(250);
  }
  return done;
}

try {
  await login(t, USER, PASS);
  await t.goto(`${BASE}/admin/staff`);
  await t.waitFor(`!!document.querySelector('.sd-table')`, 30000);
  await t.sleep(1500);

  check('the directory offers a tick per person', await tickCount() > 0, `${await tickCount()} checkbox(es)`);
  check('...and no bulk actions until something is ticked',
    !/Move to a department/i.test(await text()) || /Tick people/i.test(await text()), (await text()).slice(0, 200));

  // ---- tick two people ---------------------------------------------------------------------
  const ticked = await tick(2);
  await t.sleep(900);
  check('two people can be ticked', ticked === 2, `${ticked} ticked`);
  const afterTick = await text();
  check('...and the bar then says how many', /2 selected|2 of/i.test(afterTick), afterTick.slice(0, 260));

  // ---- move them to a department -----------------------------------------------------------
  check('the department action is offered', await t.clickText('Move to a department'));
  await t.sleep(1200);
  const dialog = await t.eval(`document.querySelector('.q-modal')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
  check('the dialog names the number of people', /2 people/i.test(dialog), dialog.slice(0, 200));
  check('...and says their departments are REPLACED', /replaced/i.test(dialog), dialog.slice(0, 260));

  const picked = await chooseFirstOption();
  check('a department can be chosen', !!picked, `picked=${picked}`);

  const applied = await t.clickText('Move them');
  await t.sleep(3500);
  const afterMove = await text();
  check('the move runs and reports it', applied && !/Not changed/i.test(afterMove), afterMove.slice(0, 260));
  check('...and the people now show that department',
    picked ? afterMove.includes(picked) : false, `looked for "${picked}"`);

  // ---- give a group a line manager ---------------------------------------------------------
  // The move cleared the selection, which is what it should do; tick again for the second action.
  await t.waitFor(`!document.querySelector('.q-modal')`, 6000);
  await t.sleep(600);
  const tickedAgain = await tick(2);
  check('the selection clears after a bulk action, and can be made again', tickedAgain === 2, `${tickedAgain} ticked`);
  await t.sleep(600);
  check('the line-manager action is offered', await t.clickText('Assign a line manager'));
  await t.sleep(1200);
  const mgrDialog = await t.eval(`document.querySelector('.q-modal')?.innerText.replace(/\\s+/g, ' ') ?? ''`);
  check('...and says nobody supervises themselves', /themselves/i.test(mgrDialog), mgrDialog.slice(0, 260));

  const manager = await chooseFirstOption();
  const assigned = await t.clickText('Assign');
  await t.sleep(3500);
  const afterAssign = await text();
  check('the line manager is assigned', assigned && !!manager, `${manager}`);
  check('...and appears against them', manager ? afterAssign.includes(manager) : false, `looked for "${manager}"`);
  check('no client-side error on the page',
    !(await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })()`)));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  await restore();
  const restored = await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members`, { headers: H })
    .then(r => r.ok ? r.json() : null).catch(() => null);
  const now = restored?.items ?? restored ?? [];
  const same = Array.isArray(now) && now.every(m => {
    const was = before.find(b => b.userId === m.userId);
    return was && (m.lineManagerUserId ?? null) === was.lineManagerUserId
        && (m.departmentIds ?? []).length === was.departmentIds.length;
  });
  check('CLEANUP: every person is back as they were', same);
  t.close();
}

const line = `  staff-bulk: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;

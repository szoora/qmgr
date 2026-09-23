// LOGGING ONE RECORD FOR A GROUP OF STAFF — the Staff Directory's "Log a record" (2026-09-23).
//
// The API half is section 33 (bulk-staff-log-e2e.mjs). This half is what only a browser can see: the tick
// boxes, the bulk bar offering the action, the dialog counting the people, and the line that says the reader
// is LEFT OUT when they ticked themselves. Every press is real mouse input (press, hold, release) — never
// element.click(), which fires no mousedown and passes while a control is broken (CLAUDE.md, "Dropdowns").
//
// It NEVER SUBMITS: a staff record cannot be deleted, and the writing path is section 33's to prove.
//
// Run: node scripts/e2e/browser/staff-bulk-log.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const skipped = (name, why) => { skip++; const l = `    SKIP  ${name} — ${why}`; console.log(l); post(l); };

// ---- who is signed in, and are they on the directory ------------------------------------------
const session = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
}).then(r => r.json()).catch(() => null);
if (!session?.accessToken) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const me = session.user?.id;
const H = { Authorization: `Bearer ${session.accessToken}` };
const directory = await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members`, { headers: H })
  .then(r => r.ok ? r.json() : null).catch(() => null);
const people = directory?.items ?? [];
if (people.filter(p => p.userId !== me).length < 3) {
  console.log(`  SKIP  the directory has ${people.length} people — this needs three besides the reader`);
  console.log('  staff-bulk-log: 0 passed, 0 failed');
  process.exit(0);
}
const myRow = people.find(p => p.userId === me);

const t = await openTab();
await t.viewport(1440, 950);
const modalText = () => t.eval(`document.querySelector('.q-modal')?.innerText.replace(/\\s+/g, ' ') ?? ''`);

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
const button = (text) => `[...document.querySelectorAll('button')].find(b => b.offsetParent !== null && b.textContent.trim() === ${JSON.stringify(text)})`;
const pressButton = async (text) => press(await center(button(text)));

/** The row tick for one person, found by the link their name carries. */
const tickFor = (userId) => `(() => { const a = document.querySelector('a.sd-person__name[href$="/${userId}/timeline"]');
  return a ? a.closest('tr')?.querySelector('.sd-tick input[type=checkbox]') : null; })()`;
/** The first N ticks on the page that are NOT the reader's own row. */
const othersOnPage = (n) => t.eval(`(() => [...document.querySelectorAll('.sd-table tbody tr')]
  .filter(tr => !tr.querySelector('a.sd-person__name[href$="/${me}/timeline"]') && tr.querySelector('.sd-tick input[type=checkbox]'))
  .slice(0, ${n}).map(tr => tr.querySelector('a.sd-person__name')?.getAttribute('href')?.split('/')[3]))()`);

async function setSearch(value) {
  await t.eval(`(() => { const i = document.querySelector('.sd-search input'); if (!i) return false;
    const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    i.focus(); set.call(i, ${JSON.stringify(value)}); i.dispatchEvent(new Event('input', { bubbles: true }));
    i.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  await t.sleep(900);
}

try {
  await login(t, USER, PASS);
  await t.goto(`${BASE}/admin/staff`);
  await t.waitFor(`!!document.querySelector('.sd-table')`, 30000);
  await t.sleep(1500);

  // ---- three colleagues, not the reader ---------------------------------------------------------
  const three = await othersOnPage(3);
  let ticked = 0;
  for (const id of three) { if (await press(await center(tickFor(id)))) ticked++; await t.sleep(300); }
  check('three colleagues can be ticked with real presses', ticked === 3, `${ticked} ticked of ${three.length}`);
  await t.sleep(700);
  check('the bulk bar then offers "Log a record"', !!(await t.eval(`!!${button('Log a record')}`)));

  check('pressing it opens the dialog', await pressButton('Log a record') && await t.waitFor(`!!document.querySelector('.q-modal')`, 6000));
  await t.sleep(1500);
  let dialog = await modalText();
  check('the dialog counts the people ("Log a record for 3 people")', /Log a record for 3 people/.test(dialog), dialog.slice(0, 200));
  const firstName = people.find(p => p.userId === three[0])?.fullName ?? '';
  check('...and names them from the server-written full name', !!firstName && dialog.includes(firstName), `looked for "${firstName}"`);
  check('...with no left-out line when the reader is not ticked', !/You are left out/.test(dialog), dialog.slice(0, 260));
  check('...and says what a group log takes (contribution or conduct)', /contribution|Parameter/i.test(dialog), dialog.slice(0, 260));
  await pressButton('Cancel');
  await t.waitFor(`!document.querySelector('.q-modal')`, 6000);

  // ---- tick the reader too -----------------------------------------------------------------------
  if (!myRow) {
    skipped('the left-out line', `${USER} is not on this branch's staff directory, so there is no row of their own to tick`);
  } else {
    // Find the reader's own row by searching; a selection survives a filter only for rows still visible,
    // so the reader is ticked first and the search cleared before anything else is.
    await pressButton('Clear selection');
    await t.sleep(500);
    await setSearch(myRow.fullName);
    const mine = await press(await center(tickFor(me)));
    await t.sleep(400);
    await setSearch('');
    const two = await othersOnPage(2);
    for (const id of two) { await press(await center(tickFor(id))); await t.sleep(300); }
    check('the reader and two colleagues are ticked', mine && two.length === 2);
    await t.sleep(600);
    await pressButton('Log a record');
    await t.waitFor(`!!document.querySelector('.q-modal')`, 6000);
    await t.sleep(1500);
    dialog = await modalText();
    check('ticking yourself shows "You are left out — nobody logs a record about themselves"', /You are left out/.test(dialog) && /about themselves/.test(dialog), dialog.slice(0, 300));
    check('...and the count leaves the reader out ("for 2 people")', /Log a record for 2 people/.test(dialog), dialog.slice(0, 200));
    await pressButton('Cancel');
    await t.waitFor(`!document.querySelector('.q-modal')`, 6000);
  }

  check('no client-side error on the page',
    !(await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })()`)));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  t.close();
}

const line = `  staff-bulk-log: ${pass} passed, ${fail} failed${skip ? ` (${skip} skipped)` : ''}`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;

// THE SCHOOL CALENDAR, IN A REAL BROWSER (2026-09-23).
// Plan: docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md §9.
//
// As a tenant administrator: open /calendar and every tab by deep link, create an event through the
// dialog — the category chosen with a real press, hold and release, never element.click(), which fires no
// mousedown and passes while a dropdown is broken — find it in the month grid and the agenda, open the
// printed programme and find it in the table, see it on My School Day, and make, replace and turn off the
// private calendar link on the profile. As a teacher: no New event button, no Import or Settings tabs.
//
// Every event it creates is deleted through the API afterwards, and the calendar link is left off.
//
// Run: node scripts/e2e/browser/calendar-ui.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
const PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);
const TITLE = `E2E calendar ${RUN}`;

let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };
const note = (m) => { skip++; const l = `SKIP  ${m}`; console.log(l); viewer(l); };

const apiLogin = async (email) => {
  for (const pw of PASSES) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password: pw }) });
    if (r.ok) return { ...(await r.json()), password: pw };
  }
  return null;
};

const admin = await apiLogin(ADMIN);
if (!admin) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }
const H = { Authorization: `Bearer ${admin.accessToken}`, 'Content-Type': 'application/json' };
const iso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const today = new Date();
const TODAY = iso(today);

const t = await openTab();
await t.viewport(1440, 950);

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 80) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(350);
  return true;
}
// #blazor-error-ui is on every Blazor page and hidden by a STYLESHEET, so it is read computed.
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
async function open(path, ready) {
  await t.goto(`${B}${path}`);
  const came = await t.waitFor(ready, 20000);
  await t.sleep(700);
  return came;
}
const tabIds = () => t.eval(`[...document.querySelectorAll('.qm-main [role=tab]')].map(e => e.id)`);

// The branch the page is on: what the Web stored at sign-in, else the account's own.
async function branchOf() {
  const stored = await t.eval(`(() => { for (const k of Object.keys(localStorage)) { const v = localStorage.getItem(k) ?? '';
    const m = /^"?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"?$/i.exec(v); if (m && /branch/i.test(k)) return m[1]; } return null; })()`);
  return stored ?? admin.user?.branchId ?? process.env.BRANCH ?? null;
}

let branch = null;
try {
  // ---- 1. The hub and its tabs, by deep link -------------------------------------------------------------------
  await login(t, ADMIN, admin.password);
  branch = await branchOf();
  const cameCal = await open('/calendar', `!!document.querySelector('#cal-month, .cal-page .q-empty, #cal-agenda')`);
  ok(cameCal, 'the calendar opens for a tenant administrator', await t.eval('location.pathname'));
  ok(await noError(), 'no error bar on /calendar');
  const ids = await tabIds();
  ok(['cal-calendar', 'cal-import', 'cal-settings'].every((i) => ids.includes(i)), 'an administrator sees Calendar, Import and Settings', ids.join(', '));
  ok(await t.eval(`!!document.getElementById('cal-new-event')`), 'New event sits in the title row');
  ok(await t.eval(`(() => { const b = document.getElementById('cal-new-event'); const band = document.querySelector('.page-header'); return !!b && !!band && band.contains(b); })()`),
    '…inside the page header band, not under the tabs');
  ok(await t.eval(`document.querySelectorAll('#cal-month .cal-month__dow').length === 7 && document.querySelector('#cal-month .cal-month__dow').innerText.trim() === 'Mon'`),
    'the month is a seven-column grid, Monday first');

  for (const [key, ready, name] of [
    ['import', `document.getElementById('cal-import')?.classList.contains('active')`, 'Import'],
    ['settings', `!!document.getElementById('cal-categories') || !!document.querySelector('.cals-loading')`, 'Settings'],
    ['calendar', `!!document.querySelector('.cal-page')`, 'Calendar'],
  ]) {
    const came = await open(`/calendar?tab=${key}`, ready);
    ok(came && await t.eval(`document.getElementById('cal-${key}')?.classList.contains('active') === true`), `?tab=${key} opens the ${name} tab`);
    ok(await noError(), `no error bar on the ${name} tab`);
  }
  const cats = await open('/calendar?tab=settings', `!!document.querySelector('#cal-categories input')`);
  ok(cats, 'Settings lists the calendar categories as editable rows');

  // ---- 2. A new event through the dialog --------------------------------------------------------------------------
  await open('/calendar?view=month', `!!document.getElementById('cal-new-event')`);
  await press(await center(`document.getElementById('cal-new-event')`));
  const dialog = await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible #cal-ev-title')`, 8000);
  ok(dialog, 'New event opens the editor');
  if (dialog) {
    await t.setValue('#cal-ev-title', TITLE);
    await t.sleep(300);
    // The category, with real input: press the trigger, then press an option.
    const trigger = `(() => { const l = [...document.querySelectorAll('.q-modal-backdrop--visible .q-select__label')].find(x => x.textContent.trim().startsWith('Category')); return l ? document.getElementById(l.htmlFor) : null; })()`;
    await press(await center(trigger), 150);
    const opened = await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible .q-select__container--open .q-select__option:not(.q-select__option--clear)')`, 4000);
    ok(opened, 'the category dropdown opens on a real press');
    let chosen = null;
    if (opened) {
      chosen = await t.eval(`document.querySelector('.q-modal-backdrop--visible .q-select__container--open .q-select__option:not(.q-select__option--clear)').innerText.trim()`);
      await press(await center(`document.querySelector('.q-modal-backdrop--visible .q-select__container--open .q-select__option:not(.q-select__option--clear)')`), 200);
      const shown = await t.eval(`(() => { const b = ${trigger}; return b ? b.innerText.trim() : ''; })()`);
      ok(shown.includes(chosen), 'pressing an option chooses it', `${chosen} → "${shown}"`);
    }
    // Public as well as Staff, so the signage zone would carry it too.
    const publicBox = `[...document.querySelectorAll('.q-modal-backdrop--visible .cal-audience .q-checkbox')].find(c => c.innerText.includes('Public'))`;
    await press(await center(publicBox));
    ok(await t.eval(`${publicBox}?.querySelector('input').checked === true`), 'Public can be ticked beside Staff');
    await press(await center(`document.getElementById('cal-ev-save')`));
    const saved = await t.waitFor(`!document.querySelector('.q-modal-backdrop--visible #cal-ev-title')`, 10000);
    ok(saved, 'Add event saves and closes the dialog', await t.eval(`document.querySelector('.q-modal-backdrop--visible .form-error')?.innerText ?? ''`));
  }

  // ---- 3. Month and agenda ----------------------------------------------------------------------------------------
  const inMonth = await t.waitFor(`[...document.querySelectorAll('#cal-month .cal-day[data-date="${TODAY}"] .cal-pill')].some(p => p.innerText.includes(${JSON.stringify(TITLE)}))`, 10000);
  ok(inMonth, 'the new event shows on today in the month grid');
  if (inMonth) {
    await press(await center(`[...document.querySelectorAll('#cal-month .cal-pill')].find(p => p.innerText.includes(${JSON.stringify(TITLE)}))`));
    ok(await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible #cal-detail')`, 5000), 'pressing it opens the detail');
    ok(await t.eval(`!!document.getElementById('cal-detail-edit') && !!document.getElementById('cal-detail-delete')`), 'an administrator is offered Edit and Delete');
    await t.clickText('Close', '.q-modal-backdrop--visible button');
    await t.sleep(400);
  }
  await open('/calendar?view=agenda', `!!document.querySelector('#cal-agenda, .cal-page .q-empty')`);
  ok(await t.eval(`[...document.querySelectorAll('#cal-agenda .cal-agenda__day[data-date="${TODAY}"] .cal-row')].some(r => r.innerText.includes(${JSON.stringify(TITLE)}))`),
    'and in the agenda under today');
  ok(await noError(), 'no error bar after switching views');

  // A phone: the grid stacks into the days that have something on them, and nothing scrolls sideways.
  await t.viewport(390, 844, true);
  await open('/calendar?view=month', `!!document.querySelector('#cal-month')`);
  const phone = await t.eval(`(() => { const body = document.querySelector('#cal-month .cal-month__body');
    const cols = getComputedStyle(body).gridTemplateColumns.split(' ').length;
    return { cols, wide: document.documentElement.scrollWidth > window.innerWidth + 1 }; })()`);
  ok(phone.cols === 1 && !phone.wide, 'at 390px the month is one column and the page does not scroll sideways', JSON.stringify(phone));
  await t.viewport(1440, 950);

  // ---- 4. The printed programme -----------------------------------------------------------------------------------
  const came = await open(`/calendar/print?from=${TODAY}&to=${TODAY}`, `!!document.querySelector('#calp-table, #calp-empty')`);
  ok(came, 'the print route renders');
  ok(await t.eval(`[...document.querySelectorAll('#calp-table tbody tr')].some(r => r.innerText.includes(${JSON.stringify(TITLE)}))`), 'the printed programme lists the event');
  ok(await t.eval(`[...document.querySelectorAll('#calp-table thead th')].map(h => h.innerText.trim()).join('|') === 'Date|Time|Activity|Venue|Responsible'`),
    'its columns are Date · Time · Activity · Venue · Responsible');
  ok(await noError(), 'no error bar on the print route');

  // ---- 5. My School Day -------------------------------------------------------------------------------------------
  const myday = await open('/my-day', `!!document.querySelector('.myday-page h1')`);
  if (!myday || !(await t.eval(`location.pathname === '/my-day'`))) note(`My School Day is not open to this tenant (${await t.eval('location.pathname')})`);
  else {
    const shown = await t.waitFor(`[...document.querySelectorAll('#myday-events .myday-event')].some(e => e.innerText.includes(${JSON.stringify(TITLE)}))`, 8000);
    ok(shown, 'My School Day lists the staff event on its day');
    ok(await noError(), 'no error bar on My School Day');
  }

  // ---- 6. The calendar link on the profile ------------------------------------------------------------------------
  const prof = await open('/profile', `!!document.getElementById('cal-feed-create')`);
  ok(prof, 'the profile carries "Calendar on your phone"');
  if (prof) {
    await press(await center(`document.getElementById('cal-feed-create')`));
    // Replace asks first when a link already exists.
    await t.sleep(600);
    if (await t.eval(`[...document.querySelectorAll('.modal, .confirm-dialog, .q-modal-backdrop--visible')].some(m => m.innerText.includes('Replace your calendar link'))`)) {
      await t.clickText('Replace', '.modal button, .confirm-dialog button, .q-modal-backdrop--visible button');
    }
    const first = await t.waitFor(`!!document.querySelector('input#cal-feed-url')?.value`, 8000) ? await t.eval(`document.querySelector('input#cal-feed-url').value`) : null;
    ok(!!first && /^https?:\/\//.test(first), 'Create link shows the address once', first ?? 'nothing shown');
    ok(await t.eval(`!!document.getElementById('cal-feed-copy')`), '…with a Copy button beside it');
    await press(await center(`document.getElementById('cal-feed-create')`));
    await t.waitFor(`[...document.querySelectorAll('.q-modal-backdrop--visible button')].some(b => b.innerText.trim() === 'Replace')`, 5000);
    await t.clickText('Replace', '.q-modal-backdrop--visible button');
    const second = await t.waitFor(`(() => { const v = document.querySelector('input#cal-feed-url')?.value; return !!v && v !== ${JSON.stringify(first)}; })()`, 8000)
      ? await t.eval(`document.querySelector('input#cal-feed-url').value`) : null;
    ok(!!second && second !== first, 'Replace link makes a different address');
    await press(await center(`document.getElementById('cal-feed-off')`));
    await t.waitFor(`[...document.querySelectorAll('button')].some(b => b.offsetParent && b.innerText.trim() === 'Turn off' && b.id !== 'cal-feed-off')`, 5000);
    await t.eval(`[...document.querySelectorAll('button')].filter(b => b.offsetParent && b.innerText.trim() === 'Turn off' && b.id !== 'cal-feed-off')[0]?.click(); true`);
    ok(await t.waitFor(`!document.getElementById('cal-feed-off') && !document.querySelector('input#cal-feed-url')`, 8000), 'Turn off removes the link and hides it');
    const feed = await (await fetch(`${API}/api/v1/calendar/feed`, { headers: H })).json().catch(() => null);
    ok(feed && feed.exists === false, 'the API agrees the feed is off', JSON.stringify(feed));
    ok(await noError(), 'no error bar on the profile');
  }

  // ---- 7. A teacher reads the calendar and cannot change it -------------------------------------------------------
  const teacher = await apiLogin(TEACHER);
  if (!teacher) note(`could not sign in as ${TEACHER}`);
  else {
    await login(t, TEACHER, teacher.password);
    const tcal = await open('/calendar', `!!document.querySelector('.cal-page')`);
    ok(tcal, 'a teacher opens the calendar');
    await t.sleep(800);
    ok(!await t.eval(`!!document.getElementById('cal-new-event')`), 'a teacher has no New event button');
    const tids = await tabIds();
    ok(!tids.includes('cal-import') && !tids.includes('cal-settings'), 'a teacher sees no Import or Settings tab', tids.join(', '));
    await open('/calendar?tab=settings', `!!document.querySelector('.cal-page')`);
    ok(await t.eval(`!document.getElementById('cal-categories')`), '?tab=settings as a teacher opens the calendar, not the settings');
    ok(await noError(), 'no error bar for a teacher');
  }
} finally {
  // ---- Clean up: every event this run made, through the API ---------------------------------------------------------
  if (branch) {
    const from = new Date(today); from.setDate(from.getDate() - 40);
    const to = new Date(today); to.setDate(to.getDate() + 40);
    const r = await fetch(`${API}/api/v1/branches/${branch}/calendar?from=${iso(from)}&to=${iso(to)}`, { headers: H }).catch(() => null);
    const range = r && r.ok ? await r.json() : null;
    let removed = 0;
    for (const e of range?.events ?? []) {
      if (!String(e.title).startsWith(`E2E calendar ${RUN}`)) continue;
      const d = await fetch(`${API}/api/v1/branches/${branch}/calendar/events/${e.id}`, { method: 'DELETE', headers: H }).catch(() => null);
      if (d && d.ok) removed++;
    }
    console.log(`cleanup: ${removed} event(s) removed`);
  } else {
    console.log('cleanup: no branch known — search the calendar for "E2E calendar" and delete by hand');
  }
  await fetch(`${API}/api/v1/calendar/feed`, { method: 'DELETE', headers: H }).catch(() => {});
}

console.log(`\ncalendar-ui: ${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
t.close();
process.exitCode = fail ? 1 : 0;

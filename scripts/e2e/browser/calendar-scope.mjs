// PAST EVENTS, THE TWO SCOPES AND THE AUDIENCE PICKER, IN A REAL BROWSER (2026-09-26).
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, E1–E4, B11.
//
// As a tenant administrator, with three events seeded through the API (one past, one coming, one for another role):
//   * the Agenda starts at today, hides the past event and says how many earlier events it is not showing;
//   * "Show past events" (a real press) brings it back DIMMED, and the choice is remembered across a reload;
//   * "My events" leaves out the event for another role, "Whole school" shows it;
//   * the Month grid dims past days;
//   * the "All" chip counts the rows the view shows (B11);
//   * the editor's audience picker: "Only some people" asks for a group, role, department or person, and counts them.
// Everything seeded is deleted and the person's view choices are put back.
//
// Run: node scripts/e2e/browser/calendar-scope.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);

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
const api = async (method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json };
};

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
  await t.sleep(400);
  return true;
}
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
async function open(path, ready) { await t.goto(`${B}${path}`); const came = await t.waitFor(ready, 20000); await t.sleep(800); return came; }

const created = [];
let branch = null, originalUi = null;
try {
  await login(t, ADMIN, admin.password);
  branch = await t.eval(`(() => { for (const k of Object.keys(localStorage)) { const v = localStorage.getItem(k) ?? '';
    const m = /^"?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"?$/i.exec(v); if (m && /branch/i.test(k)) return m[1]; } return null; })()`)
    ?? process.env.BRANCH;
  originalUi = (await api('GET', '/api/v1/profile/ui-preferences')).json?.preferences ?? null;
  // Start from the defaults, so what is asserted is the default.
  await api('PUT', '/api/v1/profile/ui-preferences', { calendar: { view: 'agenda', scope: 'mine', showPast: false, showDuties: true }, sound: originalUi?.sound ?? { enabled: true } });

  const range = (await api('GET', `/api/v1/branches/${branch}/calendar?from=2026-01-01&to=2026-01-01`)).json;
  const TODAY = range?.today;
  const d = new Date(TODAY + 'T00:00:00Z');
  if (d.getUTCDate() <= 2 || d.getUTCDate() >= 27) note('too close to a month boundary for the agenda to hold a past and a coming day — the past checks are skipped today');
  const iso = (x) => x.toISOString().slice(0, 10);
  const past = iso(new Date(d.getTime() - 2 * 864e5)), soon = iso(new Date(d.getTime() + 2 * 864e5));
  const opts = (await api('GET', `/api/v1/branches/${branch}/calendar/audience-options`)).json;
  const myRole = (opts?.people ?? []).find((p) => p.userId === admin.user.id)?.roleCode;
  const otherRole = (opts?.roles ?? []).find((r) => r.code !== myRole)?.code;
  const mk = async (title, date, extra = {}) => {
    const r = await api('POST', `/api/v1/branches/${branch}/calendar/events`, { title, startsOn: date, endsOn: date, audience: 1, notifyAudience: false, ...extra });
    if (r.json?.id) created.push(r.json.id);
    return r.json?.id;
  };
  const PAST = `E2E past ${RUN}`, SOON = `E2E soon ${RUN}`, OTHER = `E2E other role ${RUN}`;
  await mk(PAST, past);
  await mk(SOON, soon);
  if (otherRole) await mk(OTHER, soon, { staffAudience: { allStaff: false, roleCodes: [otherRole] } });

  // ---- The agenda starts at today ----
  const month = `${TODAY.slice(0, 7)}-15`;
  await open(`/calendar?view=agenda&scope=mine&date=${TODAY}`, `!!document.querySelector('#cal-agenda, .cal-page .q-empty')`);
  ok(await noError(), 'the calendar opens with no error bar');
  const rows = () => t.eval(`[...document.querySelectorAll('#cal-agenda .cal-row')].map(r => r.innerText)`);
  if (d.getUTCDate() > 2 && d.getUTCDate() < 27) {
    ok((await rows()).some((r) => r.includes(SOON)), 'the coming event is listed');
    ok(!(await rows()).some((r) => r.includes(PAST)), 'the past event is NOT listed by default (E1)');
    ok(await t.eval(`!!document.getElementById('cal-earlier') && /Earlier this month: \\d+/.test(document.getElementById('cal-earlier').innerText)`),
      'the page says how many earlier events it is not showing', await t.eval(`document.getElementById('cal-earlier')?.innerText ?? 'no line'`));

    // ---- Show past events, a real press ----
    await press(await center(`document.getElementById('cal-show-past')?.closest('label')`));
    ok(await t.waitFor(`[...document.querySelectorAll('#cal-agenda .cal-row')].some(r => r.innerText.includes(${JSON.stringify(PAST)}))`, 5000), '"Show past events" brings it back');
    ok(await t.eval(`[...document.querySelectorAll('#cal-agenda .cal-row')].find(r => r.innerText.includes(${JSON.stringify(PAST)}))?.closest('.cal-agenda__day')?.classList.contains('cal-agenda__day--past') === true`),
      '…dimmed as past');
    await t.sleep(800);
    await open(`/calendar?view=agenda&scope=mine&date=${TODAY}`, `!!document.querySelector('#cal-agenda')`);
    ok(await t.eval(`document.getElementById('cal-show-past')?.checked === true`), '…and the choice is remembered after a reload');
    const saved = (await api('GET', '/api/v1/profile/ui-preferences')).json?.preferences?.calendar;
    ok(saved?.showPast === true, '…on the server, so it follows the person', JSON.stringify(saved));
  }

  // ---- The two scopes ----
  if (!otherRole) note('only one role on this branch — the scope checks need a second');
  else {
    await open(`/calendar?view=agenda&scope=mine&date=${TODAY}`, `!!document.querySelector('#cal-agenda, .cal-page .q-empty')`);
    ok(!(await rows()).some((r) => r.includes(OTHER)), 'My events leaves out the event for another role');
    await press(await center(`[...document.querySelectorAll('.cal-scope [role=tab]')].find(b => b.innerText.includes('Whole school'))`));
    ok(await t.waitFor(`[...document.querySelectorAll('#cal-agenda .cal-row')].some(r => r.innerText.includes(${JSON.stringify(OTHER)}))`, 6000), 'Whole school shows it');
    ok(await t.eval(`location.search.includes('scope=all')`), '…and the address says so, so a link opens the same view');
  }

  // ---- B11: the chip counts what is shown ----
  const counts = await t.eval(`(() => { const chip = [...document.querySelectorAll('.cal-filter button, .cal-filter [role=button]')].find(b => /^All\\s*\\d+/.test(b.innerText.trim()));
    const n = chip ? parseInt(chip.innerText.replace(/\\D+/g, ' ').trim().split(' ')[0], 10) : -1;
    const shown = new Set([...document.querySelectorAll('#cal-agenda .cal-row[data-event-id]')].map(r => r.dataset.eventId)).size;
    return { n, shown }; })()`);
  ok(counts.n === counts.shown, 'the "All" chip counts the events the view shows (B11)', JSON.stringify(counts));

  // ---- The month grid dims the past ----
  await open(`/calendar?view=month&date=${TODAY}`, `!!document.querySelector('#cal-month')`);
  ok(await t.eval(`!!document.querySelector('#cal-month .cal-day--past') || new Date(${JSON.stringify(TODAY)}).getUTCDate() === 1`), 'the month grid marks past days');
  ok(await t.eval(`!document.getElementById('cal-show-past')`), '…and offers no past switch (a grid is a map of the month)');

  // ---- The audience picker ----
  await press(await center(`document.getElementById('cal-new-event')`));
  const dialog = await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible #cal-ev-title')`, 8000);
  ok(dialog, 'New event opens the editor');
  if (dialog) {
    ok(await t.eval(`!!document.querySelector('.q-modal-backdrop--visible .qap')`), 'the editor carries the audience picker');
    ok(await t.eval(`/Everyone: \\d+ (person|people)/.test(document.querySelector('.q-modal-backdrop--visible .qap__count')?.innerText ?? '')`),
      '…counting everyone by default', await t.eval(`document.querySelector('.q-modal-backdrop--visible .qap__count')?.innerText`));
    await press(await center(`[...document.querySelectorAll('.q-modal-backdrop--visible .qap .q-radio')].find(r => r.innerText.includes('Only some'))`));
    ok(await t.waitFor(`!!document.querySelector('.q-modal-backdrop--visible .qap__grid')`, 4000), '"Only some people" offers groups, roles, departments and people');
    ok(await t.eval(`document.querySelector('.q-modal-backdrop--visible .qap__count')?.classList.contains('qap__count--none')`), '…and says nobody is chosen yet');
    await t.setValue('.q-modal-backdrop--visible #cal-ev-title', `E2E picker ${RUN}`);
    await press(await center(`document.getElementById('cal-ev-save')`));
    ok(await t.waitFor(`/which staff/i.test(document.querySelector('.q-modal-backdrop--visible .qap .form-error')?.innerText ?? '')`, 4000),
      'saving an audience of nobody is refused ON the picker, before the server');
    await t.clickText('Cancel', '.q-modal-backdrop--visible button');
  }
  ok(await noError(), 'no error bar at the end');
} catch (e) {
  ok(false, 'the suite ran to the end', e?.stack ?? String(e));
} finally {
  for (const id of created) await api('DELETE', `/api/v1/branches/${branch}/calendar/events/${id}`);
  if (originalUi) await api('PUT', '/api/v1/profile/ui-preferences', originalUi);
  console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
  process.exitCode = fail ? 1 : 0;
  await t.close?.();
}

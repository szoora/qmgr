// SHARED TICK BOXES AND SWITCHES, AND A SCHOOL'S NAME ORDER, IN A REAL BROWSER (2026-09-23).
//
// "The checkboxes look like plain bootstrap." Sixty-five raw checkboxes became QCheckbox and a new
// QSwitch; this opens the pages that carried them and PRESSES them — press, hold, release, never
// element.click(), which fires no mousedown and passes while a control is broken — and checks each
// is ours: themed, sized, and actually changing state. Then it sets the school's name order through
// the API and reads a list back in the page.
//
// It saves nothing a switch controls: every switch it presses is pressed back.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const USER = 'e2e.admin.ct@qmgr.local', PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };
const note = (m) => { skip++; const l = `SKIP  ${m}`; console.log(l); viewer(l); };

const t = await openTab();
await t.viewport(1440, 950);
await login(t, USER, PASSES[0]);

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
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
// A raw checkbox is any input[type=checkbox] that is not the one inside our own components.
const rawCount = () => t.eval(`[...document.querySelectorAll('.qm-main input[type=checkbox]')]
  .filter(i => !i.classList.contains('q-checkbox__input') && !i.classList.contains('q-switch__input') && !i.closest('.q-multiselect')).length`);
async function open(path, ready) {
  await t.goto(`${B}${path}`);
  const came = await t.waitFor(ready, 20000);
  await t.sleep(600);
  const where = await t.eval('location.pathname');
  return came && !where.startsWith('/billing') && !where.startsWith('/unauthorized') ? true : where;
}

// ---- 1. Settings switches -----------------------------------------------------------------------------------------
const settings = await open('/admin/settings?tab=notifications', `!!document.querySelector('.q-switch')`);
if (settings !== true) note(`notification settings did not open (${settings})`);
else {
  ok(await t.eval(`!document.querySelector('.form-switch, .form-check-input[role=switch]')`), 'notification settings carry no Bootstrap switch');
  ok(await rawCount() === 0, 'and no raw checkbox');
  const sw = `document.querySelector('.q-switch:not(.q-switch--disabled) .q-switch__track')`;
  const state = () => t.eval(`document.querySelector('.q-switch:not(.q-switch--disabled) input').checked`);
  const before = await state();
  await press(await center(sw));
  const after = await state();
  ok(after !== before, 'pressing a switch turns it', `${before} → ${after}`);
  const colours = await t.eval(`(() => { const on = document.querySelector('.q-switch--on .q-switch__track');
    if (!on) return null; const probe = document.createElement('span'); probe.style.color = 'var(--qm-primary)'; document.body.appendChild(probe);
    const want = getComputedStyle(probe).color; probe.remove(); return { track: getComputedStyle(on).backgroundColor, want }; })()`);
  ok(!!colours && colours.track === colours.want, 'an ON switch wears the tenant\'s primary colour', JSON.stringify(colours));
  ok(await t.eval(`document.querySelector('.q-switch input').getAttribute('role') === 'switch' && !!document.querySelector('.q-switch input').getAttribute('aria-label')`),
    'the switch is announced as a switch, with a name');
  await press(await center(sw));
  ok(await state() === before, 'pressing it again puts it back (nothing is saved)');
  ok(await noError(), 'no error bar on notification settings');
}

// ---- 2. Row tick boxes on list pages -----------------------------------------------------------------------------
const lists = [
  ['/admin/users', `!!document.querySelector('.us-table tbody tr')`, 'Users & Roles'],
  ['/admin/students/roster', `!!document.querySelector('.roster-row:not(.roster-header)') || !!document.querySelector('.q-empty')`, 'Student roster'],
  ['/admin/welfare-reports?tab=search', `(() => { const b = [...document.querySelectorAll('.qm-main button')].find(x => x.innerText.trim() === 'Search'); if (b && !window.__searched) { b.click(); window.__searched = true; } return !!document.querySelector('.results-row .q-checkbox'); })()`, 'Welfare reports'],
  ['/admin/visitors', `!!document.querySelector('.qm-main h1')`, 'Visitors'],
  ['/admin/appointments', `!!document.querySelector('.qm-main h1')`, 'Appointments'],
];
for (const [path, ready, name] of lists) {
  const opened = await open(path, ready);
  if (opened !== true) { note(`${name}: not open to this tenant (${opened})`); continue; }
  ok(await rawCount() === 0, `${name}: no raw checkbox on the page`);
  const box = await t.eval(`!!document.querySelector('.qm-main tbody .q-checkbox, .qm-main .roster-row:not(.roster-header) .q-checkbox, .qm-main .results-row .q-checkbox')`);
  if (!box) { note(`${name}: no row tick box to press (empty list or not permitted)`); continue; }
  const sel = `document.querySelector('.qm-main tbody .q-checkbox, .qm-main .roster-row:not(.roster-header) .q-checkbox, .qm-main .results-row .q-checkbox')`;
  const size = await t.eval(`(() => { const r = ${sel}.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height)]; })()`);
  ok(size[0] >= 24 && size[1] >= 24, `${name}: a row tick box is a 24px target at least`, size.join('×'));
  await press(await center(sel));
  ok(await t.eval(`${sel}.querySelector('input').checked`), `${name}: pressing a row tick box ticks it`);
  await press(await center(sel));
  ok(!await t.eval(`${sel}.querySelector('input').checked`), `${name}: pressing again clears it`);
  ok(await noError(), `${name}: no error bar`);
}

// ---- 3. The school's name order ------------------------------------------------------------------------------------
const apiLogin = async () => {
  for (const pw of PASSES) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: USER, password: pw }) });
    if (r.ok) return r.json();
  }
  return null;
};
const auth = await apiLogin();
const H = { Authorization: `Bearer ${auth.accessToken}`, 'Content-Type': 'application/json' };
const NAMES = `${API}/api/v1/organizations/${auth.user.organizationId}/people-names`;
const original = await (await fetch(NAMES, { headers: H })).json();
try {
  const general = await open('/admin/settings', `!!document.getElementById('people-names')`);
  if (general !== true) note(`the Settings page did not show People's names (${general})`);
  else {
    ok(await t.eval(`document.querySelectorAll('#people-preview span').length === 2`), 'Settings shows a two-name preview of the order');
    ok(await t.eval(`!!document.getElementById('people-display-order') || !!document.querySelector('#people-names .q-select')`), '…beside the "Show names as" and "Sort lists by" choices');
  }

  const users = await (await fetch(`${API}/api/v1/users?includeInactive=true`, { headers: H })).json();
  const me = (Array.isArray(users) ? users : users.items).find((u) => u.id === auth.user.id);
  await fetch(NAMES, { method: 'PUT', headers: H, body: JSON.stringify({ displayOrder: 'FamilyFirst', sortOrder: null }) });
  const opened = await open('/admin/users', `!!document.querySelector('.us-table tbody tr')`);
  if (opened !== true) note('the users list did not open');
  else {
    await t.setValue('.q-bulkbar__search input', me.lastName);
    await t.sleep(700);
    const shown = await t.eval(`[...document.querySelectorAll('.us-table .user-name')].map(e => e.innerText.trim())`);
    ok(shown.includes(`${me.lastName} ${me.firstName}`), 'with surname first chosen, the users list writes the administrator surname first', shown.slice(0, 4).join(' | '));
  }
  // The staff import preview builds each row's name in the BROWSER, from two columns — the one place
  // the Web writes a name itself. It must follow the school's order like everything the API writes.
  const fs = await import('node:fs'); const os = await import('node:os'); const path = await import('node:path');
  const csv = path.join(os.tmpdir(), `names-${Date.now()}.csv`);
  fs.writeFileSync(csv, `First name,Last name,Staff number,Role
Agatha,Ayebare,NO-${Date.now().toString(36)},teacher
`);
  const imp = await open('/admin/staff?tab=import', `!!document.querySelector('#ss-import-file')`);
  if (imp !== true) note(`the staff import did not open (${imp})`);
  else {
    await t.setFiles('#ss-import-file', [csv]);
    if (!await t.waitFor(`!!document.querySelector('.q-import__map-rows')`, 30000)) ok(false, 'the generated staff sheet is read');
    else {
      await t.sleep(1000);
      await t.clickText('Continue');
      await t.waitFor(`!!document.querySelector('.ss-preview-name')`, 20000);
      const preview = await t.eval(`[...document.querySelectorAll('.ss-preview-name')].map(e => e.innerText.trim())`);
      ok(preview.includes('Ayebare Agatha'), 'the staff import preview writes the row surname first too — nothing is imported', preview.join(' | '));
    }
  }
  try { fs.unlinkSync(csv); } catch { }

  // The header name comes from the session, refreshed by /auth/me; a fresh sign-in shows it.
  await login(t, USER, PASSES[0]);
  const header = await t.eval(`document.querySelector('.user-dropdown .user-name, .qm-header .user-name')?.innerText.trim() ?? ''`);
  if (header) ok(header === `${me.lastName} ${me.firstName}`, 'the header names the signed-in person surname first too', header);
  else note('the header does not print the name at this width');
} finally {
  await fetch(NAMES, { method: 'PUT', headers: H, body: JSON.stringify({ displayOrder: original.displayOrder, sortOrder: original.sortOrder ?? null }) });
}

console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
t.close();
process.exitCode = fail ? 1 : 0;

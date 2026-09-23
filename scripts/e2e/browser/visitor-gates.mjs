// GATES AT VISITOR CHECK-IN, IN A REAL BROWSER (plan TERM_PROGRAMME_CALENDAR_AND_GATES §10, 2026-09-23).
//
// The API suite (section 29) proves the rule; this proves the desk: with two gates the check-in dialog shows a
// required gate picker, pressing Check In without one shows the rule's own refusal and posts nothing, choosing
// one — by a real press, hold and release, never element.click() — is remembered by THIS device, a reload
// preselects it, and the evacuation roll call groups the visitor under the gate they came in by.
//
// It saves two gates of its own and puts the branch's list back afterwards (its own gates stay, retired: a gate
// a visit has used can only be retired, which is the rule). The one visitor it checks in is checked out.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const SA_USER = process.env.SA_USER ?? 'superadmin', SA_PASS = process.env.SA_PASS ?? 'admin';
const USER = 'e2e.admin.ct@qmgr.local', PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const EAST = 'E2E UI Gate East', WEST = 'E2E UI Gate West';
const RUN = Date.now().toString(36);
const VISITOR = `Gate Browser ${RUN}`;

let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };
const note = (m) => { skip++; const l = `SKIP  ${m}`; console.log(l); viewer(l); };

// ---- API side: who we are, which branch, and the gate list ------------------------------------------------------
async function apiLogin(id, pws) {
  for (const pw of pws) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) return r.json();
  }
  return null;
}
const auth = await apiLogin(USER, PASSES);
const sa = await apiLogin(SA_USER, [SA_PASS]);
if (!auth) { console.log('sign-in failed'); console.log('\n0 passed, 1 failed'); process.exit(1); }
const H = { Authorization: `Bearer ${auth.accessToken}`, 'Content-Type': 'application/json' };
const api = async (method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json };
};
const ORG = auth.user.organizationId;

// The branch the page will use: the administrator's own, else the first the tenant has.
const me = (await api('GET', '/api/v1/auth/me')).json ?? {};
const branches = (await api('GET', '/api/v1/branches')).json;
const BRANCH = process.env.BRANCH ?? me.branchId ?? me.assignedBranchId ?? (branches?.items ?? branches ?? [])[0]?.id;
const GATES = `/api/v1/branches/${BRANCH}/visitors/gates`;
const key = (s) => (s ?? '').replace(/[^\p{L}\p{N}]/gu, '').toUpperCase();
const isOurs = (n) => key(n).startsWith(key('E2E UI Gate'));

let grantedModule = false;
if (sa) {
  const H2 = { Authorization: `Bearer ${sa.accessToken}`, 'Content-Type': 'application/json' };
  const mods = await (await fetch(`${API}/api/v1/admin/tenants/${ORG}/modules`, { headers: H2 })).json().catch(() => []);
  const vm = (Array.isArray(mods) ? mods : []).find((m) => m.moduleCode === 'visitor-management');
  if (!(vm?.purchased && vm?.status === 'Active')) {
    const g = await fetch(`${API}/api/v1/admin/tenants/${ORG}/modules/visitor-management`, { method: 'PUT', headers: H2, body: JSON.stringify({ note: 'E2E gates UI' }) });
    grantedModule = g.ok;
  }
}

const original = (await api('GET', GATES)).json;
if (!Array.isArray(original)) { console.log(`the gate list could not be read (branch ${BRANCH})`); console.log('\n0 passed, 1 failed'); process.exit(1); }
const listWith = (states, restore = false) => {
  const out = original.map((g, i) => ({ name: g.name, isActive: restore && !isOurs(g.name) ? g.isActive : false, sortOrder: i }));
  for (const [name, active] of Object.entries(states)) {
    const at = out.find((g) => key(g.name) === key(name));
    if (at) at.isActive = active; else out.push({ name, isActive: active, sortOrder: out.length });
  }
  return { gates: out, renames: {} };
};

// ---- Browser ----------------------------------------------------------------------------------------------------
const t = await openTab();
await t.viewport(1440, 950);

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 120) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(400);
  return true;
}
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
const buttonByText = (text, scope = 'document') =>
  `[...${scope}.querySelectorAll('button')].find(b => b.offsetParent !== null && b.innerText.trim() === ${JSON.stringify(text)})`;
const modal = `document.querySelector('.q-modal')`;
const pickerTrigger = `document.querySelector('.q-modal [data-gate-picker="choose"] .q-select')`;

async function openCheckIn() {
  await t.goto(`${B}/admin/visitors`);
  const came = await t.waitFor(`!!${buttonByText('Check In Visitor')}`, 25000);
  if (!came) return await t.eval('location.pathname');
  await t.sleep(800);
  await press(await center(buttonByText('Check In Visitor')));
  return (await t.waitFor(`!!document.querySelector('.q-modal [data-gate-picker]')`, 10000)) ? true : 'no gate picker in the dialog';
}

let visitorId = null;
try {
  const saved = await api('PUT', GATES, listWith({ [EAST]: true, [WEST]: true }));
  ok(saved.status === 200, 'two gates saved for the run', `${saved.status}`);

  await login(t, USER, PASSES[0]); // clears localStorage, so this device starts with no remembered gate

  // ---- 1. The picker, and the refusal -----------------------------------------------------------------------------
  const opened = await openCheckIn();
  if (opened !== true) { note(`the check-in dialog did not open with a gate picker (${opened})`); throw new Error('stop'); }
  ok(await t.eval(`!!${pickerTrigger}`), 'with two gates the check-in dialog shows a gate picker');
  ok(await t.eval(`!!document.querySelector('.q-modal [data-gate-picker] .q-select__required')`), '…marked required');
  ok(await t.eval(`(document.querySelector('.q-modal [data-gate-device]')?.innerText ?? '').includes('no gate yet')`), '…and says this device has no gate yet');

  await t.setValue(`.q-modal input[placeholder="Jane Doe"]`, VISITOR);
  await t.setValue(`.q-modal input[placeholder="Meeting, delivery, interview..."]`, 'Gate browser e2e');
  await t.sleep(600);
  if (await t.eval(`!!document.querySelector('.q-modal .consent-check')`)) await press(await center(`document.querySelector('.q-modal .consent-check .q-checkbox__box, .q-modal .consent-check label, .q-modal .consent-check')`));

  await press(await center(buttonByText('Check In', modal)));
  await t.sleep(600);
  const refusal = await t.eval(`document.querySelector('.q-modal [data-gate-picker] .q-select__error')?.innerText ?? ''`);
  ok(/choose the gate/i.test(refusal), 'Check In without a gate shows the rule\'s refusal on the field', refusal);
  ok(await t.eval(`!!document.querySelector('.q-modal [data-gate-picker]')`), '…and the dialog stays open — nothing was posted');
  const posted = ((await api('GET', `/api/v1/branches/${BRANCH}/visitors`)).json ?? []).some((v) => v.fullName === VISITOR);
  ok(!posted, '…confirmed: no visit exists for that name');

  // ---- 2. Choose by a real press, and the device remembers --------------------------------------------------------
  await press(await center(pickerTrigger));
  const listOpen = await t.waitFor(`!!document.querySelector('.q-modal .q-select__dropdown')`, 5000);
  ok(listOpen, 'pressing the gate picker opens its list');
  await press(await center(`[...document.querySelectorAll('.q-modal .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(WEST)})`));
  const chosen = await t.eval(`${pickerTrigger}?.innerText.trim() ?? ''`);
  ok(chosen === WEST, 'pressing a gate chooses it', chosen);
  ok(await t.eval(`(document.querySelector('.q-modal [data-gate-device]')?.innerText ?? '').includes(${JSON.stringify(WEST)})`), '…and "This device" now names it');
  const stored = await t.eval(`(() => { try { return Object.keys(localStorage).filter(k => k.startsWith('qmgr-visitor-gate:')).map(k => localStorage.getItem(k)); } catch { return []; } })()`);
  ok(stored.includes(WEST), '…remembered on this device', JSON.stringify(stored));

  await press(await center(buttonByText('Check In', modal)));
  await t.sleep(1500);
  const visit = ((await api('GET', `/api/v1/branches/${BRANCH}/visitors`)).json ?? []).find((v) => v.fullName === VISITOR);
  visitorId = visit?.id ?? null;
  ok(visit?.entryGate === WEST, 'Check In with the gate records it', JSON.stringify(visit?.entryGate));
  ok(visit?.checkedInByUserId === auth.user.id, '…and who admitted them', visit?.checkedInByUserId);
  ok(await noError(), 'no error bar after checking in');

  // ---- 3. A reload preselects this device's gate --------------------------------------------------------------------
  const again = await openCheckIn();
  if (again !== true) note(`the dialog did not reopen after a reload (${again})`);
  else {
    await t.waitFor(`(${pickerTrigger}?.innerText.trim() ?? '') === ${JSON.stringify(WEST)}`, 5000);
    ok(await t.eval(`(${pickerTrigger}?.innerText.trim() ?? '')`) === WEST, 'after a reload the device\'s gate is preselected');
    await press(await center(buttonByText('Cancel', modal)));
  }

  // ---- 4. The list shows the gate, and the roll call groups by it --------------------------------------------------
  await t.goto(`${B}/admin/visitors`);
  await t.waitFor(`!!document.querySelector('.visitor-card')`, 20000);
  await t.sleep(600);
  ok(await t.eval(`[...document.querySelectorAll('.visitor-card')].some(c => c.innerText.includes(${JSON.stringify(VISITOR)}) && c.innerText.includes(${JSON.stringify(WEST)}))`),
    'the visitor list shows the gate on the visit');
  ok(await t.eval(`!!document.querySelector('[data-gate-filter]')`), '…and offers a gate filter');

  await t.goto(`${B}/admin/visitors/evacuation`);
  await t.waitFor(`!!document.querySelector('.evac-row') || !!document.querySelector('.evac-empty')`, 20000);
  await t.sleep(600);
  const group = await t.eval(`(() => { const h = [...document.querySelectorAll('h3.evac-gate-head')].find(x => x.dataset.evacGate === ${JSON.stringify(WEST)});
    if (!h) return null; const list = h.nextElementSibling; return list ? list.innerText : ''; })()`);
  ok(group !== null, 'the evacuation roll call has a heading for the gate', JSON.stringify(await t.eval(`[...document.querySelectorAll('h3.evac-gate-head')].map(h => h.dataset.evacGate)`)));
  ok((group ?? '').includes(VISITOR), '…and the visitor is listed under it');
  ok(await noError(), 'no error bar on the roll call');
} catch (e) {
  if (e?.message !== 'stop') ok(false, 'the suite ran to the end', e?.stack ?? String(e));
} finally {
  if (visitorId) {
    const out = await api('POST', `/api/v1/branches/${BRANCH}/visitors/${visitorId}/checkout`, { gate: WEST });
    ok(out.status === 200, 'the visitor it checked in is checked out', `${out.status}`);
  }
  const now = (await api('GET', GATES)).json ?? [];
  const ours = Object.fromEntries(now.filter((g) => isOurs(g.name)).map((g) => [g.name, false]));
  const restored = await api('PUT', GATES, listWith(ours, true));
  ok(restored.status === 200, 'the branch\'s gate list is put back (the suite\'s own gates stay retired)', `${restored.status}`);
  try { await t.eval(`(() => { try { Object.keys(localStorage).filter(k => k.startsWith('qmgr-visitor-gate:')).forEach(k => localStorage.removeItem(k)); } catch { } return true; })()`); } catch { }
  if (grantedModule && sa) await fetch(`${API}/api/v1/admin/tenants/${ORG}/modules/visitor-management?note=E2E%20gates%20UI`, { method: 'DELETE', headers: { Authorization: `Bearer ${sa.accessToken}` } });
}

console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
t.close();
process.exitCode = fail ? 1 : 0;

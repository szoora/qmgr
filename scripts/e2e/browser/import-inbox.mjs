// THE IMPORT INBOX, IN A REAL BROWSER (2026-09-26).
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, E11.
//
// A document is staged through the API (as the calendar keeper), then, in the browser:
//   * the sidebar offers "Import inbox" to the administrator and not to a teacher;
//   * the inbox lists the document with its part waiting, and "Review and approve" (a real press) opens the programme
//     import in APPROVAL MODE — checked with the server, "Approve and import";
//   * approving writes the events once and the part reads approved;
//   * with the school's "second approver" switch on, the page says why the uploader cannot approve.
// The import is undone and the switch put back.
//
// Run: node scripts/e2e/browser/import-inbox.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.teacher.s4@qmgr.local';
const PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);

let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };

const apiLogin = async (email) => {
  for (const pw of PASSES) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password: pw }) });
    if (r.ok) return { ...(await r.json()), password: pw };
  }
  return null;
};
const admin = await apiLogin(ADMIN);
const teacher = await apiLogin(TEACHER);
if (!admin || !teacher) { console.error('could not sign in (administrator or teacher)'); process.exit(1); }
const api = async (method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: { Authorization: `Bearer ${admin.accessToken}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
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

let branch = null, jobId = null, importJob = null, originalSettings = null;
const day = new Date(Date.now() + (340 + Math.floor(Math.random() * 20)) * 864e5).toISOString().slice(0, 10);
const TITLE = `E2E inbox ui ${RUN}`;
try {
  await login(t, ADMIN, admin.password);
  await t.goto(`${B}/`);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1200);
  branch = await t.eval(`(() => { for (const k of Object.keys(localStorage)) { const v = localStorage.getItem(k) ?? '';
    const m = /^"?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"?$/i.exec(v); if (m && /branch/i.test(k)) return m[1]; } return null; })()`)
    ?? process.env.BRANCH;
  ok(await t.eval(`!!document.getElementById('nav-import-inbox')`), 'the sidebar offers the Import inbox to the administrator');

  originalSettings = (await api('GET', '/api/v1/imports/settings')).json;
  await api('PUT', '/api/v1/imports/settings', { requireSecondApprover: false });
  const sent = await api('POST', `/api/v1/branches/${branch}/imports`, { sourceFiles: [`e2e-inbox-ui-${RUN}.docx`], sections: [{
    kind: 'events', title: 'Calendar events', rowCount: 1, programme: { preview: false, sourceFiles: [`e2e-inbox-ui-${RUN}.docx`], notifyPeople: false, meetings: [], rotaSlots: [],
      events: [{ sourceKey: `e2e:${RUN}:ui`, title: TITLE, startsOn: day, endsOn: day, audience: 'Staff', classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [] }] } }] });
  jobId = sent.json?.id;
  ok(sent.status === 201 && !!jobId, 'a document is sent for approval (staged, nothing written)', String(sent.status));

  await t.goto(`${B}/imports`);
  const listed = await t.waitFor(`!!document.querySelector('[data-job="${jobId}"]')`, 15000);
  ok(listed, 'the inbox lists it');
  ok(await t.eval(`document.querySelector('[data-job="${jobId}"] [data-section="s1"]')?.dataset.state === 'awaiting'`), '…with its part waiting');
  ok(await noError(), 'no error bar on the inbox');

  await press(await center(`[...document.querySelectorAll('[data-job="${jobId}"] [data-section="s1"] button')].find(b => b.innerText.includes('Review and approve'))`));
  const approving = await t.waitFor(`!!document.getElementById('pi-commit') && /Approve an imported document/.test(document.querySelector('h1')?.innerText ?? '')`, 20000);
  ok(approving, '"Review and approve" opens the import in approval mode, checked with the server');
  if (approving) {
    ok(await t.eval(`/Approve and import/.test(document.getElementById('pi-commit').innerText)`), '…offering "Approve and import"');
    ok(await t.eval(`!document.getElementById('pi-send-approval')`), '…and never "Send for approval" again');
    await press(await center(`document.getElementById('pi-commit')`));
    ok(await t.waitFor(`!!document.getElementById('pi-done')`, 20000), 'approving imports it');
  }
  const cal = await api('GET', `/api/v1/branches/${branch}/calendar?from=${day}&to=${day}&scope=all`);
  const ours = (cal.json?.events ?? []).filter((e) => e.title === TITLE);
  ok(ours.length === 1, 'the event is on the calendar once', `${ours.length}`);
  const job = ((await api('GET', `/api/v1/branches/${branch}/imports`)).json ?? []).find((j) => j.id === jobId);
  importJob = job?.sections?.[0]?.resultJobId ?? null;
  ok(job?.sections?.[0]?.state === 'approved', 'the part reads approved');

  // The second-approver rule, as the uploader sees it.
  await api('PUT', '/api/v1/imports/settings', { requireSecondApprover: true });
  const again = await api('POST', `/api/v1/branches/${branch}/imports`, { sourceFiles: [`e2e-inbox-ui-${RUN}-2.docx`], sections: [{
    kind: 'events', title: 'Calendar events', rowCount: 1, programme: { preview: false, sourceFiles: ['x'], notifyPeople: false, meetings: [], rotaSlots: [],
      events: [{ sourceKey: `e2e:${RUN}:ui2`, title: `${TITLE} 2`, startsOn: day, endsOn: day, audience: 'Staff', classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [] }] } }] });
  await t.goto(`${B}/imports`);
  await t.waitFor(`!!document.querySelector('[data-job="${again.json?.id}"]')`, 15000);
  ok(await t.eval(`/second person/i.test(document.querySelector('[data-job="${again.json?.id}"] [data-section="s1"]')?.innerText ?? '')`),
    'with a second approver required, the uploader is told why they cannot approve');
  ok(await t.eval(`[...document.querySelectorAll('[data-job="${again.json?.id}"] [data-section="s1"] button')].some(b => b.innerText.includes('Withdraw'))`),
    '…and may withdraw it');
  await api('POST', `/api/v1/branches/${branch}/imports/${again.json?.id}/sections/s1/reject`, { note: 'test done' });

  // A teacher has no inbox.
  await login(t, TEACHER, teacher.password);
  await t.goto(`${B}/`);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1200);
  ok(await t.eval(`!document.getElementById('nav-import-inbox')`), 'a teacher, who approves nothing, is not offered the inbox');
} catch (e) {
  ok(false, 'the suite ran to the end', e?.stack ?? String(e));
} finally {
  if (importJob) await api('POST', `/api/v1/branches/${branch}/calendar/import/jobs/${importJob}/undo`);
  if (originalSettings) await api('PUT', '/api/v1/imports/settings', originalSettings);
  console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
  process.exitCode = fail ? 1 : 0;
  t.close?.();
}

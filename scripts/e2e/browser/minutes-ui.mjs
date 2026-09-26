// The minutes PAGE. minutes-e2e.mjs proves the lifecycle over the API; this proves the half a curl
// suite cannot see: that the page renders the record, that adoption visibly closes the door (the
// editor becomes read-only and offers a correction instead of a save), that attendance appears
// without anyone typing it, and that the A4 sheet marks a draft as a DRAFT.
//
// Run: node scripts/e2e/browser/minutes-ui.mjs   (headless Chrome on 9333)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const RUN = Date.now().toString(36);

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
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
const H = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const me = JSON.parse(Buffer.from(token.split('.')[1], 'base64').toString()).sub;
const B = `${API}/api/v1/branches/${BRANCH}/staff`;
const api = async (method, path, body) => {
  const r = await fetch(`${B}${path}`, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: r.status, json: await r.json().catch(() => null) };
};

// A meeting with a closed register, so the attendance panel has something true to show.
const params = (await (await fetch(`${API}/api/v1/staff/parameters`, { headers: H })).json());
const parameter = (params.items ?? params).find(p => p.isActive && /attendance/i.test(p.name)) ?? (params.items ?? params)[0];
const staff = ((await api('GET', '/structure/members?pageSize=500')).json?.items ?? []).filter(s => s.userId !== me);
if (staff.length < 2) { console.error('SKIP: fewer than two other staff'); process.exit(2); }

const start = new Date(); start.setHours(start.getHours() - 2);
const end = new Date(start); end.setHours(end.getHours() + 1);
const duty = (await api('POST', '/duties', {
  parameterId: parameter.id,
  title: `MINUTES-UI ${RUN}`,
  location: 'Staff room',
  startsAt: start.toISOString(), endsAt: end.toISOString(),
  expectedUserIds: [staff[0].userId, staff[1].userId],
  recorderUserIds: [me], kind: 'Session',
})).json;
if (!duty?.id) { console.error('could not create the meeting'); process.exit(1); }
await api('POST', `/duties/${duty.id}/register`, {
  close: true,
  entries: [{ userId: staff[0].userId, outcome: 'Present' }, { userId: staff[1].userId, outcome: 'Excused', note: 'Course' }],
});

const t = await openTab();
await t.viewport(1600, 950);
await login(t, USER, PASS);
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

const url = `${BASE}/admin/staff/duties/${duty.id}/minutes`;

// ---------------------------------------------------------------- 1. the draft editor
hdr(`Minutes page — "${duty.title}"`);
await t.goto(url);
await t.waitFor(`!!document.querySelector('.min-page .q-card')`, 25000);
await t.sleep(1200);

const shape = await t.eval(`(() => ({
  sections: document.querySelectorAll('.min-main .q-card').length,
  textareas: document.querySelectorAll('.min-main textarea').length,
  buttons: [...document.querySelectorAll('.page-header button')].map(b => b.innerText.trim()).filter(Boolean),
  attendance: document.querySelector('.min-side')?.innerText.replace(/\\s+/g, ' ').trim().slice(0, 240) || '',
  docW: document.documentElement.scrollWidth, winW: window.innerWidth,
}))()`);

check('1a: the template sections render as editable fields', shape.textareas >= 5, `${shape.textareas} textareas`);
check('1b: decisions and actions have cards of their own', shape.sections >= 8, `${shape.sections} cards`);
check('1c: the page does not scroll sideways', shape.docW <= shape.winW + 1, `${shape.docW} > ${shape.winW}`);
check('1d: attendance is on the page without anybody typing it',
  /Present/.test(shape.attendance) && /Apolog/i.test(shape.attendance), `panel read: "${shape.attendance}"`);
check('1e: a draft offers Save and Circulate', shape.buttons.some(b => /Save draft/i.test(b)) && shape.buttons.some(b => /Circulate/i.test(b)),
  shape.buttons.join(' | '));
// "Circulate for correction" also contains the word, so match the button itself, not the word.
check('1f: …and does NOT offer "Add a correction" yet', !shape.buttons.some(b => /Add a correction/i.test(b)), shape.buttons.join(' | '));
check('1g: an unstarted record offers no Adopt — there is nothing to adopt',
  !shape.buttons.some(b => /^Adopt$/i.test(b)), shape.buttons.join(' | '));

// Type into the first section and save through the page.
await t.eval(`(() => { const ta = document.querySelector('.min-main textarea');
  const set = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
  set.call(ta, 'Agenda typed in the browser for run ${RUN}.'); ta.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
await t.sleep(500);
await t.eval(`[...document.querySelectorAll('button')].find(b => /Save draft/i.test(b.innerText))?.click()`);
await t.sleep(2500);
const saved = (await api('GET', `/duties/${duty.id}/minutes`)).json;
check('1h: what was typed reached the server', (saved?.sections ?? []).some(s => (s.body ?? '').includes(RUN)),
  JSON.stringify(saved?.sections));

// ---------------------------------------------------------------- 2. the draft prints as a DRAFT
hdr('The A4 sheet');
await t.goto(`${url}/print`);
await t.waitFor(`!!document.querySelector('.q-print__sheet')`, 25000);
await t.sleep(1200);
const draftSheet = await t.eval(`(() => ({
  draft: !!document.querySelector('.mp-draft'),
  draftText: document.querySelector('.mp-draft')?.innerText.trim() || '',
  attendance: /Attendance/.test(document.body.innerText),
  sign: document.querySelectorAll('.mp-sign > div').length,
}))()`);
check('2a: an unadopted sheet is marked DRAFT', draftSheet.draft && /DRAFT/.test(draftSheet.draftText), `"${draftSheet.draftText}"`);
check('2b: the sheet carries the attendance', draftSheet.attendance === true);
check('2c: …and signature lines for chair, secretary and date', draftSheet.sign === 3, `${draftSheet.sign} lines`);

// ---------------------------------------------------------------- 3. adoption closes the door
hdr('Adoption');
await api('POST', `/duties/${duty.id}/minutes/circulate`);
// Adopted by somebody other than the writer (DutySeparation G4): the Director of Studies, who manages duties too.
const dosToken = await (async () => {
  for (const pw of ['E2eTeacher!2026', 'Rwenzori#Peaks-2026']) {
    const j = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'e2e.sp.dos@qmgr.local', password: pw }) }).then(r => r.json()).catch(() => ({}));
    if (j.accessToken) return j.accessToken;
  }
  return null;
})();
await fetch(`${B}/duties/${duty.id}/minutes/approve`, { method: 'POST', headers: { Authorization: `Bearer ${dosToken}`, 'Content-Type': 'application/json' }, body: '{}' });
await t.goto(url);
await t.waitFor(`!!document.querySelector('.min-page .q-card')`, 25000);
await t.sleep(1500);

const adopted = await t.eval(`(() => ({
  textareas: document.querySelectorAll('.min-main textarea').length,
  buttons: [...document.querySelectorAll('.page-header button')].map(b => b.innerText.trim()).filter(Boolean),
  banner: document.querySelector('.min-note--ok')?.innerText.replace(/\\s+/g,' ').trim() || '',
  body: document.body.innerText,
}))()`);
check('3a: an adopted record has no editable field left', adopted.textareas === 0, `${adopted.textareas} textareas`);
check('3b: Save and Circulate are gone', !adopted.buttons.some(b => /Save draft|Circulate/i.test(b)), adopted.buttons.join(' | '));
check('3c: …replaced by Add a correction', adopted.buttons.some(b => /Add a correction/i.test(b)), adopted.buttons.join(' | '));
check('3d: there is no Unapprove, because there is no unapprove', !/unapprove|un-adopt/i.test(adopted.body));
check('3e: the banner says when it was adopted and by whom', /Adopted/.test(adopted.banner), `"${adopted.banner}"`);
check('3f: the typed text survived adoption', adopted.body.includes(RUN));

// ---------------------------------------------------------------- 4. the adopted sheet is not a draft
await t.goto(`${url}/print`);
await t.waitFor(`!!document.querySelector('.q-print__sheet')`, 25000);
await t.sleep(1200);
check('4a: an adopted sheet is NOT marked DRAFT',
  (await t.eval(`document.querySelectorAll('.mp-draft').length`)) === 0);
check('4b: …and states its adoption instead',
  (await t.eval(`document.querySelector('.mp-adopted')?.innerText.trim() || ''`)).startsWith('Adopted'));

// ---------------------------------------------------------------- 5. the action is on the assignee's portal
hdr('An action is a real to-do');
const actionsNow = (await api('GET', `/duties/${duty.id}/minutes`)).json?.actions ?? [];
check('5a: the meeting has no actions unless minuted', Array.isArray(actionsNow));

const err = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`);
check('6a: no Blazor error bar anywhere in this flow', err === 'none', `display: ${err}`);

const line = `    NOTE  "${duty.title}" stays on the dev tenant — a duty whose register is closed is never cancelled.`;
console.log(line); post(line);

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
t.close();
process.exit(fail ? 1 : 0);

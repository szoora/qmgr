// The Access tab and the Houses tab, in the browser (2026-09-24).
//
// Section 37 proves the API. This opens the two new hub tabs — "a hub's tab is not verified until something OPENS
// it" — and does what a head does there: reads who holds the safeguarding and acting-head posts, reads the access
// review, and records a review by pressing through the dialog. Then Staff Directory's Houses tab, and a teacher, who
// must not be offered either.
//
// It puts the safeguarding leads back as it found them. Run: node scripts/e2e/browser/access-ui.mjs  (CDP_PORT=9334 to watch)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.BASE ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const PASSWORDS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const signIn = async (email) => {
  for (const pw of PASSWORDS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password: pw }) });
    if (r.ok) return { token: (await r.json()).accessToken, password: pw };
  }
  return null;
};
const api = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: { Authorization: `Bearer ${token}`, ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch { }
  return { status: r.status, json, text };
};

const AD = (await signIn(USER))?.token;
const TE = await signIn('e2e.sp.math1@qmgr.local');
if (!AD || !TE) { console.error('could not sign in as the administrator and e2e.sp.math1'); process.exit(1); }
const users = (await api(AD, 'GET', '/api/v1/users?pageSize=500')).json;
const list = Array.isArray(users) ? users : users?.items ?? [];
const dos = list.find(u => u.username === 'e2e.sp.dos');
if (!dos) { console.error('section 14\'s e2e.sp.dos is missing — run section 14 first'); process.exit(1); }
const leadershipBefore = (await api(AD, 'GET', '/api/v1/leadership')).json;

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
  return true;
}
const button = (text, scope = 'document') => `[...${scope}.querySelectorAll('button')].find(b => b.offsetParent !== null && b.innerText.trim() === ${JSON.stringify(text)})`;
const modal = (title) => `[...document.querySelectorAll('.q-modal')].find(m => m.offsetParent !== null && (m.querySelector('.q-modal__title')?.innerText ?? '').includes(${JSON.stringify(title)}))`;
const setNative = (expr, value) => t.eval(`(() => { const el = ${expr}; if (!el) return false;
  const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(value)});
  el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
const noErrorBar = async () => (await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`)) === 'none';
const pointAtBranch = () => t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);
const text = (sel) => t.eval(`document.querySelector(${JSON.stringify(sel)})?.textContent ?? ''`);

try {
  hdr(`== The Access and Houses tabs (run ${RUN}) ==`);
  // A lead to read back, set through the API so the page has something true to show.
  await api(AD, 'PUT', '/api/v1/leadership/safeguarding', { leadUserId: dos.id, deputyUserIds: [] });

  await login(t, USER, PASS);
  await pointAtBranch();

  hdr('1. Users & Roles → Access, as the Administrator');
  await t.goto(`${BASE}/admin/users?tab=access`);
  await t.waitFor(`!!document.querySelector('#access-table')`, 30000);
  check('1a: the Access tab opens with no error bar', await noErrorBar());
  check('1b: it is on the hub\'s tab strip', await t.eval(`[...document.querySelectorAll('[role=tab]')].some(x => x.textContent.trim() === 'Access')`));
  const lead = await text('#safeguarding-facts');
  check('1c: the safeguarding card names the lead', lead.includes(dos.firstName), lead.slice(0, 160));
  check('1d: the Administrator is offered "Change" on it', await t.eval(`!!${button('Change')}`));
  const rows = await t.eval(`document.querySelectorAll('#access-table tbody tr').length`);
  check('1e: the access review lists people', rows > 3, `${rows} rows`);
  const restrictedChip = `[...document.querySelectorAll('.q-bulk-bar button, .q-bulkbar button, button')].find(b => b.offsetParent !== null && b.textContent.includes('Restricted readers'))`;
  if (await press(await center(restrictedChip))) {
    await t.sleep(600);
    const shown = await t.eval(`[...document.querySelectorAll('#access-table tbody tr')].map(r => r.textContent)`);
    check('1f: "Restricted readers" narrows the list to people who can read restricted records', shown.length > 0 && shown.length < rows && shown.every(r => r.includes('Yes')), `${shown.length} of ${rows}`);
  } else check('1f: the "Restricted readers" filter is offered', false, 'no such chip');

  hdr('2. Recording a review by hand');
  check('2a: "Record this review" sits in the title row', await press(await center(button('Record this review'))));
  await t.waitFor(`!!${modal('Record this review')}`, 8000);
  await setNative(`${modal('Record this review')}?.querySelector('textarea')`, `E2E UI ${RUN}: checked every restricted reader`);
  await press(await center(button('Record', modal('Record this review'))));
  const recorded = await t.waitFor(`!${modal('Record this review')} && (document.querySelector('#last-review')?.textContent ?? '').includes(${JSON.stringify(`E2E UI ${RUN}`)})`, 15000);
  check('2b: the review is recorded and the page says so', !!recorded, await text('#last-review'));

  hdr('3. Staff Directory → Houses');
  await t.goto(`${BASE}/admin/staff?tab=houses`);
  await t.waitFor(`!!document.querySelector('.q-empty-state, .q-empty, #pastoral-posts')`, 30000);
  check('3a: the Houses tab opens with no error bar', await noErrorBar());
  check('3b: it is on the hub\'s tab strip', await t.eval(`[...document.querySelectorAll('[role=tab]')].some(x => x.textContent.trim() === 'Houses')`));

  hdr('4. A teacher is offered neither');
  await login(t, 'e2e.sp.math1@qmgr.local', TE.password);
  await pointAtBranch();
  await t.goto(`${BASE}/admin/users?tab=access`);
  await t.sleep(4000);
  check('4a: the teacher does not reach the access review', !(await t.eval(`!!document.querySelector('#access-table')`)));
  check('4b: …and is not shown an error bar for trying', await noErrorBar());
} catch (e) {
  check('the suite ran to the end', false, e.stack ?? String(e));
} finally {
  await api(AD, 'PUT', '/api/v1/leadership/safeguarding', {
    leadUserId: leadershipBefore?.safeguardingLead?.userId ?? null,
    deputyUserIds: (leadershipBefore?.deputySafeguardingLeads ?? []).map(d => d.userId),
  });
  await t.close?.();
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

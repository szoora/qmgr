// Exam supervision, employment types and QRadioGroup, in the browser (2026-09-23).
//
// Section 35 proves the API. This proves what a person does: an administrator creates a series on the Duties hub's
// Exams tab by pressing through the dialog (a real date picker, real time fields, real multi-selects); the teacher
// appointed to run it finds it on My Workspace — they hold no duty permission, so the sidebar has no Duties entry
// for them and that card is their only door — and is shown the controls a manager has, and not the one they do not
// (appointing). Then the Setup page's employment-types editor, and the radio groups that replaced five hand-made
// styles, pressed with real mouse input.
//
// Everything it creates it cancels. Run: node scripts/e2e/browser/exam-series-ui.mjs   (CDP_PORT=9334 to watch)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.BASE ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const PASSWORDS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);
const NAME = `E2E UI exams ${RUN}`;

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
const math1 = list.find(u => u.username === 'e2e.sp.math1'), math2 = list.find(u => u.username === 'e2e.sp.math2');
if (!math1 || !math2) { console.error('section 14\'s e2e.sp.math1 / math2 are missing — run section 14 first'); process.exit(1); }
const nameOf = (u) => `${u.firstName} ${u.lastName}`;

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
// A field inside a dialog, found by its visible label — every QModal stays in the DOM while hidden.
const field = (modalTitle, label, sel = 'input, textarea') => `(() => { const m = ${modal(modalTitle)}; if (!m) return null;
  const l = [...m.querySelectorAll('label')].find(x => x.textContent.replace('*','').trim().toLowerCase() === ${JSON.stringify(label.toLowerCase())});
  const g = l?.closest('.q-input-wrapper, .q-select-wrapper, .q-datepicker, .form-group') ?? l?.parentElement?.parentElement;
  return g?.querySelector(${JSON.stringify(sel)}) ?? null; })()`;
const setNative = (expr, value) => t.eval(`(() => { const el = ${expr}; if (!el) return false;
  const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(value)});
  el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
/// A multi-select in a dialog: open, filter by the first name, press the person.
async function pickPerson(modalTitle, label, person) {
  const trigger = `(() => { const m = ${modal(modalTitle)}; const l = [...(m?.querySelectorAll('label') ?? [])].find(x => x.textContent.replace('*','').trim().toLowerCase() === ${JSON.stringify(label.toLowerCase())});
    return l?.closest('.q-select-wrapper')?.querySelector('.q-multiselect') ?? null; })()`;
  if (!await press(await center(trigger))) return false;
  await t.waitFor(`!!document.querySelector('.q-multiselect__dropdown')`, 5000);
  await setNative(`document.querySelector('.q-multiselect__dropdown .q-multiselect__filter-input')`, person.firstName);
  await t.sleep(500);
  const ok = await press(await center(`[...document.querySelectorAll('.q-multiselect__dropdown .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(nameOf(person))})`));
  await t.sleep(250);
  await press(await center(`${modal(modalTitle)}?.querySelector('.q-modal__title')`)); // close the list
  await t.sleep(250);
  return ok;
}
/// A date field in a dialog: open its calendar, move one month on, take the 15th.
async function pickDate(modalTitle, label) {
  const trigger = `(() => { const m = ${modal(modalTitle)}; const l = [...(m?.querySelectorAll('label') ?? [])].find(x => x.textContent.replace('*','').trim().toLowerCase() === ${JSON.stringify(label.toLowerCase())});
    return l?.closest('.q-datepicker')?.querySelector('.q-datepicker__trigger') ?? null; })()`;
  if (!await press(await center(trigger))) return false;
  await t.waitFor(`!!document.querySelector('.q-datepicker__popover')`, 5000);
  await press(await center(`document.querySelector('.q-datepicker__popover [aria-label="Next month"]')`));
  await t.sleep(250);
  const ok = await press(await center(`[...document.querySelectorAll('.q-datepicker__popover .q-datepicker__day:not(.is-outside)')].find(b => b.innerText.trim() === '15')`));
  await t.sleep(300);
  return ok;
}
/// The time half of a date+time pair: the n-th time input in the dialog.
const setTime = (modalTitle, n, hhmm) => setNative(`${modal(modalTitle)}?.querySelectorAll('input[type=time]')[${n}]`, hhmm);
const noErrorBar = async () => (await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`)) === 'none';
const pointAtBranch = () => t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

let seriesId = null;
try {
  hdr(`== Exam supervision, employment types and radio groups (run ${RUN}) ==`);
  await login(t, USER, PASS);
  await pointAtBranch();

  // ------------------------------------------------------------------------ 1. an administrator creates a series
  hdr('1. Creating an exam series by hand');
  await t.goto(`${BASE}/admin/staff/duties?tab=exams`);
  await t.waitFor(`!!${button('New exam series')}`, 30000);
  check('1a: the Duties hub has an Exams tab offering "New exam series"', await t.eval(`!!${button('New exam series')}`));
  await press(await center(button('New exam series')));
  await t.waitFor(`!!${modal('New exam series')}`, 8000);
  await setNative(field('New exam series', 'Name'), NAME);
  check('1b: a teacher can be appointed to run it', await pickPerson('New exam series', 'Run by', math1));
  await setNative(field('New exam series', 'Sitting'), 'S.4 Mathematics P1');
  await setNative(field('New exam series', 'Room'), 'Main Hall');
  check('1c: the start date is picked in the calendar', await pickDate('New exam series', 'Starts'));
  await setTime('New exam series', 0, '09:00');
  check('1d: …and the end date', await pickDate('New exam series', 'Ends'));
  await setTime('New exam series', 1, '11:00');
  check('1e: an invigilator is chosen', await pickPerson('New exam series', 'Invigilators', math2));
  await press(await center(button('Create series', modal('New exam series'))));
  const created = await t.waitFor(`!${modal('New exam series')} && !!document.querySelector('.exam-head h2')`, 15000);
  check('1f: "Create series" opens the new series', !!created,
    await t.eval(`${modal('New exam series')}?.querySelector('.form-error')?.innerText ?? '(no error shown)'`));
  // textContent throughout: a chip and a label may be styled uppercase, and innerText returns what is DRAWN.
  const head = await t.eval(`document.querySelector('.exam-head')?.textContent ?? ''`);
  check('1g: …named, and run by the appointed teacher', head.includes(NAME) && head.includes(math1.firstName), head.slice(0, 200));
  const row = await t.eval(`document.querySelector('.exam-table tbody tr')?.textContent ?? ''`);
  check('1h: its sitting is listed with its room and invigilator', row.includes('S.4 Mathematics P1') && row.includes('Main Hall') && row.includes(math2.firstName), row);
  seriesId = ((await api(AD, 'GET', `/api/v1/branches/${BRANCH}/staff/duty-series`)).json ?? []).find(s => s.name === NAME)?.seriesId ?? null;
  check('1i: the server holds it', !!seriesId);
  check('1j: no error bar', await noErrorBar());

  // ------------------------------------------------------------------------ 2. the teacher who runs it
  hdr('2. The teacher who runs it, with no duty permission');
  await login(t, 'e2e.sp.math1@qmgr.local', TE.password);
  await pointAtBranch();
  await t.goto(`${BASE}/portal?tab=teaching`);
  const card = `[...document.querySelectorAll('.q-card')].find(c => c.innerText.includes('Exam series you run'))`;
  await t.waitFor(`!!${card}`, 30000);
  check('2a: My Workspace shows "Exam series you run" — their only door to it', await t.eval(`!!${card} && ${card}.innerText.includes(${JSON.stringify(NAME)})`));
  await press(await center(`[...${card}.querySelectorAll('button')].find(b => b.innerText.trim() === 'Open')`));
  await t.waitFor(`location.search.includes('tab=exams') && !!document.querySelector('.exam-table')`, 20000);
  check('2b: it opens the Exams tab, with the series marked as theirs', await t.eval(`document.querySelector('.exam-table')?.textContent.includes('You run it') ?? false`));
  check('2c: a manager is NOT offered "New exam series" (creating needs the permission)', !(await t.eval(`!!${button('New exam series')}`)));
  const openRow = `[...document.querySelectorAll('.exam-table tbody tr')].find(r => r.innerText.includes(${JSON.stringify(NAME)}))`;
  await press(await center(`${openRow}?.querySelector('button')`));
  await t.waitFor(`!!document.querySelector('.exam-head h2')`, 15000);
  check('2d: inside it, the manager may add sittings', await t.eval(`!!${button('Add sitting')}`));
  await press(await center(button('Rename or appoint')));
  await t.waitFor(`!!${modal('Rename or appoint')}`, 8000);
  const dialogText = await t.eval(`${modal('Rename or appoint')}?.innerText ?? ''`);
  check('2e: …may rename it, but is told only an administrator appoints — no manager picker',
    /Only an administrator appoints/.test(dialogText) && !(await t.eval(`!!${modal('Rename or appoint')}?.querySelector('.q-multiselect')`)), dialogText.slice(0, 200));
  await press(await center(button('Cancel', modal('Rename or appoint'))));
  check('2f: no error bar on the teacher\'s side', await noErrorBar());

  // ------------------------------------------------------------------------ 3. employment types and radio groups
  hdr('3. Employment types, and one radio group for every choice');
  await login(t, USER, PASS);
  await pointAtBranch();
  await t.goto(`${BASE}/admin/staff/parameters?tab=policy`);
  const typesCard = `[...document.querySelectorAll('.q-card')].find(c => c.innerText.includes('Employment types'))`;
  await t.waitFor(`!!${typesCard}`, 30000);
  const typeNames = await t.eval(`[...${typesCard}.querySelectorAll('tbody input')].map(i => i.value)`);
  check('3a: the Setup page lists the school\'s employment types, seeded with the six the enum had',
    ['Permanent', 'Contract', 'Probation', 'Part time', 'Volunteer', 'Seconded'].every(n => typeNames.includes(n)), JSON.stringify(typeNames));

  await t.goto(`${BASE}/admin/appearance?tab=branding`);
  await t.waitFor(`!!document.querySelector('.q-radio-group')`, 30000);
  const groups = await t.eval(`[...document.querySelectorAll('.q-radio-group')].map(g => ({ legend: g.querySelector('legend')?.innerText.trim(), options: g.querySelectorAll('.q-radio').length, native: g.querySelectorAll('input[type=radio]').length }))`);
  check('3b: the banner\'s Position and Scroll direction are QRadioGroups of native radios',
    groups.length >= 2 && groups.every(g => g.options === g.native && g.options >= 2), JSON.stringify(groups));
  const top = `[...document.querySelectorAll('.q-radio-group')].find(g => g.innerText.includes('Position'))?.querySelectorAll('.q-radio')[1]`;
  await press(await center(top));
  await t.sleep(400);
  check('3c: pressing "Top" chooses it (a real press)', await t.eval(`(${top})?.classList.contains('q-radio--checked') && (${top})?.querySelector('input').checked`));
  const sizes = await t.eval(`[...document.querySelectorAll('.q-radio')].map(r => Math.round(r.getBoundingClientRect().height))`);
  check('3d: every option is at least 24px tall (WCAG 2.5.8)', sizes.length > 0 && sizes.every(h => h >= 24), JSON.stringify(sizes));
  check('3e: no error bar', await noErrorBar());
} catch (e) {
  check('the suite ran to the end', false, e.stack?.slice(0, 300) ?? String(e));
} finally {
  if (seriesId) {
    const d = (await api(AD, 'GET', `/api/v1/branches/${BRANCH}/staff/duty-series/${seriesId}`)).json;
    for (const s of d?.slots ?? []) await api(AD, 'DELETE', `/api/v1/branches/${BRANCH}/staff/duty-series/${seriesId}/slots/${s.id}`);
    console.log(`    cleanup: cancelled ${d?.slots?.length ?? 0} sitting(s) of the series`);
  }
  t.close();
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

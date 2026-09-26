// THE SCHOOL'S OWN PERIOD TYPES, ON THE PAGE (2026-09-26).
//
// "where is the ui to add a kind? we only have Assembly, Lesson, Break" → "can this be a dynamic feature?". Section 46 proves
// the rules; this proves what the reader does: add a type, name it, give a period that type with a real press on the
// dropdown (never element.click() — CLAUDE.md), save, and read it back after a reload.
//
//   1  the School Day editor shows a Period types card with Lesson, Break and Assembly
//   2  "Add a period type" adds a row; it is named on the page
//   3  a non-teaching period is given the new type through its dropdown
//   4  Save; after a reload the type and the period's type are still there
//   5  the type's delete button is disabled while a period uses it
//   6  the school day is put back exactly as it was (through the API)
//
// Run: API and Web up, Chrome on CDP_PORT (default 9333), then node scripts/e2e/browser/period-types-ui.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };

// The school day as it is now, to put back afterwards.
const signIn = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: 'e2e.admin.ct@qmgr.local', password: 'E2eTeacher!2026' }) });
const token = signIn.ok ? (await signIn.json()).accessToken : null;
const SETTINGS = `${API}/api/v1/branches/${BRANCH}/timetable/settings`;
const original = token ? await (await fetch(SETTINGS, { headers: { Authorization: `Bearer ${token}` } })).json() : null;
if (!original?.dayTypes) { console.error('could not read the school day through the API'); process.exit(1); }

const t = await openTab();
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
const setNative = (expr, value) => t.eval(`(() => { const el = ${expr}; if (!el) return false;
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(value)});
  el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
const TYPES = `document.querySelector('table.tts-types-table tbody')`;
const typeNames = () => t.eval(`[...${TYPES}.querySelectorAll('tr')].map(r => r.querySelector('input')?.value)`);
const name = `E2E Games ${Date.now().toString(36).slice(-4)}`;
let saved = false;

try {
  await t.viewport(1440, 950);
  await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
  await t.goto(`${BASE}/admin/timetable?tab=schoolday`);
  const came = await t.waitFor(`!!document.querySelector('table.tts-types-table tbody tr')`, 25000);
  const names = came ? await typeNames() : [];
  check('1: the School Day editor has a Period types card with Lesson, Break and Assembly', ['Lesson', 'Break', 'Assembly'].every((n) => names.includes(n)), JSON.stringify(names));

  const before = names.length;
  await press(await center(`[...document.querySelectorAll('button')].find(b => b.innerText.trim() === 'Add a period type')`));
  await t.sleep(600);
  check('2: "Add a period type" adds a row', (await typeNames()).length === before + 1);
  await setNative(`[...${TYPES}.querySelectorAll('tr')].at(-1).querySelector('input')`, name);
  await t.sleep(500);
  check('…and it takes the name typed', (await typeNames()).at(-1) === name, JSON.stringify(await typeNames()));

  // The first period whose type is not a teaching one, in the first day type.
  const rowIndex = await t.eval(`[...document.querySelectorAll('table.tts-periods tbody tr')].findIndex(r => /Break|Assembly/.test(r.querySelector('.q-select')?.innerText ?? ''))`);
  const periodLabel = rowIndex >= 0 ? await t.eval(`document.querySelectorAll('table.tts-periods tbody tr')[${rowIndex}].querySelectorAll('input')[1].value`) : null;
  if (rowIndex < 0) { check('3: a non-teaching period to retype', false, 'none found'); throw new Error('no non-teaching period'); }
  await press(await center(`document.querySelectorAll('table.tts-periods tbody tr')[${rowIndex}].querySelector('.q-select')`));
  await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 5000);
  await press(await center(`[...document.querySelectorAll('.q-select__dropdown .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(name)})`));
  await t.sleep(600);
  check(`3: ${periodLabel} is given the type through its dropdown`, (await t.eval(`document.querySelectorAll('table.tts-periods tbody tr')[${rowIndex}].querySelector('.q-select').innerText.trim()`)) === name);

  await press(await center(`[...document.querySelectorAll('button')].find(b => b.innerText.trim() === 'Save')`));
  saved = await t.waitFor(`[...document.querySelectorAll('.q-toast, .toast, [role=status]')].some(e => /School day saved/i.test(e.innerText))`, 15000);
  check('4: the school day saves', saved, await t.eval(`document.querySelector('.tts-error')?.innerText ?? ''`));

  await t.goto(`${BASE}/admin/timetable?tab=schoolday`);
  await t.waitFor(`!!document.querySelector('table.tts-types-table tbody tr')`, 25000);
  await t.sleep(800);
  check('…after a reload the type is on the list', (await typeNames()).includes(name), JSON.stringify(await typeNames()));
  const periodType = await t.eval(`(() => { const r = [...document.querySelectorAll('table.tts-periods tbody tr')].find(r => r.querySelectorAll('input')[1]?.value === ${JSON.stringify(periodLabel)}); return r?.querySelector('.q-select')?.innerText.trim(); })()`);
  check(`…and ${periodLabel} is still of that type`, periodType === name, periodType);
  const deleteDisabled = await t.eval(`(() => { const r = [...${TYPES}.querySelectorAll('tr')].find(r => r.querySelector('input')?.value === ${JSON.stringify(name)}); return !!r?.querySelector('button')?.disabled; })()`);
  check('5: the type cannot be removed while a period uses it', deleteDisabled);
  check('…and no error bar', !(await t.eval(`getComputedStyle(document.querySelector('#blazor-error-ui')).display !== 'none'`)));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  t.close();
  const r = await fetch(SETTINGS, { method: 'PUT', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(original) });
  check('6: the school day is put back', r.status === 200, `${r.status}`);
}
const line = `  period-types-ui: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;

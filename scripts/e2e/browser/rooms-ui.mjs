import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  ROOMS UI: ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };
const t = await openTab();
await t.viewport(1440, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
post('\n== Rooms on the Bell Schedule page ==');
const RUN = Date.now().toString(36);
const name = `UI Room ${RUN}`;

await t.goto(BASE + '/admin/timetable?tab=schoolday');
await t.waitFor(`[...document.querySelectorAll('.q-card')].some(c => /Rooms/.test(c.innerText))`, 20000); await t.sleep(1200);
const card = `[...document.querySelectorAll('.q-card')].find(c => c.querySelector('.q-card__title, h3, h2')?.innerText.trim() === 'Rooms' || /^Rooms/.test(c.innerText.trim()))`;
check('the page has a Rooms card listing the branch rooms', await t.eval(`(${card})?.querySelectorAll('tbody tr').length > 0`), await t.eval(`(${card})?.innerText.slice(0, 200)`));
check('the header no longer sends people to Student Roster for rooms', !(await t.eval(`[...document.querySelectorAll('.header-actions button')].some(b => b.innerText.trim() === 'Rooms')`)));
check('a room with lessons cannot be removed (its button is disabled)', await t.eval(`[...(${card}).querySelectorAll('tbody tr')].some(r => r.cells[4].innerText.trim() !== '—' && r.querySelector('button')?.disabled)`));

const before = await t.eval(`(${card}).querySelectorAll('tbody tr').length`);
await t.eval(`[...(${card}).querySelectorAll('button')].find(b => b.innerText.includes('Add a room')).click()`); await t.sleep(800);
check('Add a room adds an empty row', (await t.eval(`(${card}).querySelectorAll('tbody tr').length`)) === before + 1);
await t.eval(`(() => { const row = [...(${card}).querySelectorAll('tbody tr')].at(-1); window.__r = row; return true; })()`);
for (const [i, v] of [[0, name], [1, 'Lab'], [2, '32']]) {
  await t.eval(`(() => { const el = window.__r.cells[${i}].querySelector('input'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(v)}); el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  await t.sleep(300);
}
await t.clickText('Save', '.header-actions button'); await t.sleep(2500);
await t.goto(BASE + '/admin/timetable?tab=schoolday');
await t.waitFor(`[...document.querySelectorAll('.q-card')].some(c => /Rooms/.test(c.innerText))`, 20000); await t.sleep(1500);
const row = await t.eval(`(() => { const r = [...(${card}).querySelectorAll('tbody tr')].find(r => r.cells[0].querySelector('input')?.value === ${JSON.stringify(name)}); return r ? [...r.cells].slice(0, 3).map(c => c.querySelector('input')?.value) : null; })()`);
check('saved through the page and still there after a reload, with type and seats', JSON.stringify(row) === JSON.stringify([name, 'Lab', '32']), JSON.stringify(row));

// remove it again through the page
await t.eval(`[...(${card}).querySelectorAll('tbody tr')].find(r => r.cells[0].querySelector('input')?.value === ${JSON.stringify(name)}).querySelector('button').click()`); await t.sleep(600);
await t.clickText('Save', '.header-actions button'); await t.sleep(2500);
await t.goto(BASE + '/admin/timetable?tab=schoolday');
await t.waitFor(`[...document.querySelectorAll('.q-card')].some(c => /Rooms/.test(c.innerText))`, 20000); await t.sleep(1500);
check('an unused room removed through the page is gone after a reload', !(await t.eval(`[...(${card}).querySelectorAll('tbody input')].some(i => i.value === ${JSON.stringify(name)})`)));

// phone width
await t.viewport(390, 844, true); await t.sleep(1200);
check('at 390px the page does not scroll sideways', await t.eval(`document.documentElement.scrollWidth <= innerWidth + 1`), await t.eval(`document.documentElement.scrollWidth + ' > ' + innerWidth`));
await t.viewport(1440, 900); await t.sleep(600);

// Student Roster's lists editor
await t.goto(BASE + '/admin/students/roster');
await t.waitFor(`[...document.querySelectorAll('button')].some(b => b.innerText.trim() === 'Lists')`, 20000); await t.sleep(1000);
await t.clickText('Lists'); await t.waitFor(`!!document.querySelector('.stu-tab')`, 8000); await t.sleep(800);
const tabs = await t.eval(`[...document.querySelectorAll('.stu-tab')].map(x => x.innerText.trim())`);
check('Student Roster lists no longer have a Rooms tab', tabs.length > 0 && !tabs.some(x => /Rooms/.test(x)), JSON.stringify(tabs));
check('no console errors', t.consoleErrors.length === 0, t.consoleErrors.slice(0, 3).join(' | '));
const sum = `\n  Rooms UI: ${pass} passed, ${fail} failed`; console.log(sum); post(sum);
t.close();

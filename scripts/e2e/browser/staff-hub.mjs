// The Staff hub: one page, four tabs, one route.
//
// Departments & Structure used to be its own sidebar entry AND its own route, with a Staff tab that
// listed the same people the directory already listed. Two links and two lists for one thing. This
// asserts the merge held: the tabs are there, each section renders, the deep link works, and the
// department / line-manager editor is still reachable now that the duplicate tab is gone.
//
// Run with a HEADED Chrome on 9333 so it can be watched (see CLAUDE.md).
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

const t = await openTab();
await t.viewport(1500, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
post('\n== Staff hub: one page, four tabs, one route ==');

const tabNames = () => t.eval(`JSON.stringify([...document.querySelectorAll('.q-tabs button, .q-tabs [role=tab]')].map(b => b.innerText.trim()))`);
const errBar = () => t.eval(`(() => { const e=document.querySelector('#blazor-error-ui'); return (e?getComputedStyle(e).display!=='none':false) || document.body.innerText.includes('An unhandled error has occurred'); })()`);
const clickTab = (label) => t.eval(`(() => {
  const b = [...document.querySelectorAll('.q-tabs button, .q-tabs [role=tab]')].find(x => x.innerText.trim().toLowerCase() === ${JSON.stringify(label)}.toLowerCase());
  if (!b) return false; b.click(); return true;
})()`);

await t.goto(`${BASE}/admin/staff`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2500);

const tabs = JSON.parse(await tabNames());
check('the hub shows four tabs', tabs.length === 4, JSON.stringify(tabs));
for (const want of ['People', 'Departments', 'Coverage', 'Import']) {
  check(`tab "${want}" is present`, tabs.some(x => x.toLowerCase() === want.toLowerCase()), JSON.stringify(tabs));
}
check('no Staff tab — the duplicate list is gone', !tabs.some(x => x.trim().toLowerCase() === 'staff'), JSON.stringify(tabs));

// Each section renders inside the hub rather than navigating away.
for (const [label, marker] of [['Departments', 'department'], ['Coverage', 'coverage'], ['Import', 'import']]) {
  const clicked = await clickTab(label);
  await t.sleep(1800);
  const path = await t.eval(`location.pathname`);
  const text = (await t.eval(`document.querySelector('.qm-main')?.innerText.slice(0, 400) ?? ''`)).toLowerCase();
  check(`${label} renders inside the hub`, clicked && path === '/admin/staff' && !(await errBar()),
    `clicked=${clicked} path=${path}`);
  check(`${label} shows its own content`, text.includes(marker), text.slice(0, 120));
}

// The deep link the old route's callers now use.
await t.goto(`${BASE}/admin/staff?tab=departments`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2200);
const deepText = (await t.eval(`document.querySelector('.qm-main')?.innerText.slice(0, 400) ?? ''`)).toLowerCase();
check('?tab=departments opens that section directly', deepText.includes('department'), deepText.slice(0, 120));

// The editor that used to live on the removed Staff tab.
await t.goto(`${BASE}/admin/staff`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2500);
const hasStructureAction = await t.eval(`[...document.querySelectorAll('.sd-actions button')].some(b => b.innerText.trim() === 'Structure')`);
check('People rows still offer the department / line-manager editor', hasStructureAction === true,
  'no "Structure" action — that editing became unreachable when the Staff tab was removed');

const hasGaps = await t.eval(`document.body.innerText.includes('missing a department or line manager')`);
check('the gaps filter came across from the removed tab', hasGaps === true, 'filter not found');

console.log(`\n  ${pass} passed, ${fail} failed`);
post(`\n  ${pass} passed, ${fail} failed`);
await t.close();
process.exit(fail ? 1 : 0);

// Every empty state on screen, measured (2026-09-19, "those empty states are unnecessarily big.
// compact them"). One was ~230px tall to say "nothing here yet": 60px of padding, a 64px icon and a
// 20px title. They now share --qm-empty-* (layout.css). This walks the hubs and data pages, clicks
// through every tab, and fails any visible empty state taller than the ceiling or carrying an icon
// larger than the token — which is what a page re-sizing its own would look like.
//
//   node scripts/e2e/browser/empty-state-check.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const MAX_H = 110;     // icon beside title + sentence, plus a button underneath
const MAX_ICON = 30;

const ROUTES = [
  '/admin/branches', '/admin/users', '/admin/appearance', '/admin/settings', '/admin/feedback',
  '/content/library', '/content/signage', '/admin/marketing',
  '/admin/staff', '/admin/staff/records', '/admin/staff/duties', '/admin/timetable', '/admin/staff/parameters',
  '/admin/staff/appraisals', '/admin/students/roster', '/admin/welfare-reports', '/portal', '/my-day',
  '/notifications', '/billing', '/',
];

let pass = 0, fail = 0, seen = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

const MEASURE = `JSON.stringify([...document.querySelectorAll('.qm-main .empty-state, .qm-main .q-empty, .qm-main [class*="-empty"]')]
  .filter(e => e.offsetParent !== null && e.getBoundingClientRect().height > 0)
  .filter(e => !e.parentElement.closest('.empty-state, .q-empty'))
  .map(e => {
    const icon = e.querySelector('i:not(.q-btn__icon), .rzi, .q-empty__icon');
    return { cls: [...e.classList].join('.'), h: Math.round(e.getBoundingClientRect().height),
             icon: icon ? Math.round(parseFloat(getComputedStyle(icon).fontSize)) : 0,
             text: e.innerText.trim().split('\\n')[0].slice(0, 40) };
  }))`;

const report = (route, list) => {
  for (const e of list) {
    seen++;
    check(`${route}  .${e.cls} "${e.text}" ${e.h}px, icon ${e.icon}px`,
      e.h <= MAX_H && e.icon <= MAX_ICON, `${e.h}px tall (max ${MAX_H}), icon ${e.icon}px (max ${MAX_ICON})`);
  }
};

for (const route of ROUTES) {
  await t.goto(BASE + route);
  if (!(await t.waitFor(`!!document.querySelector('.qm-main')`, 20000).then(() => true).catch(() => false))) continue;
  await t.sleep(2200);
  if (!(await t.eval('location.pathname')).startsWith(route)) continue;

  const tabs = Number(await t.eval(`document.querySelectorAll('.qm-main .q-tabs__tab').length`));
  if (!tabs) { report(route, JSON.parse(await t.eval(MEASURE))); continue; }
  for (let i = 0; i < tabs; i++) {
    await t.eval(`document.querySelectorAll('.qm-main .q-tabs__tab')[${i}]?.click()`);
    await t.sleep(1500);
    report(`${route}#${i}`, JSON.parse(await t.eval(MEASURE)));
  }
}

const tally = `\n  empty-state-check: ${seen} empty states measured · ${pass} passed, ${fail} failed`;
console.log(tally); post(tally);
await t.close();
process.exit(fail ? 1 : 0);

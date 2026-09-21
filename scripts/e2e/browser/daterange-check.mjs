// The From and To dates of every range picker sit on ONE line (2026-09-19, "is the date on 2 lines
// intended?" — it was not: the pair shrank beside the presets and the second date wrapped under the
// first). Opens every page and hub tab that carries a QDateRangePicker, at a desktop and a laptop
// width, and fails when the two boxes' tops differ. A phone may stack them, so 390px is not checked.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const ROUTES = ['/', '/admin/staff/duties', '/admin/timetable', '/admin/welfare-reports', '/admin/staff/parameters',
  '/admin/visitors/audit', '/billing?tab=invoices', '/content/signage', '/reports', '/reports/counters', '/reports/queue',
  '/reports/visitors', '/admin/feedback', '/admin/marketing'];

let pass = 0, fail = 0;
const t = await openTab();
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

const MEASURE = `JSON.stringify([...document.querySelectorAll('.qm-main .q-daterange')].filter(e => e.offsetParent !== null).map(r => {
  const f = [...r.querySelectorAll('.q-daterange__field')].map(x => Math.round(x.getBoundingClientRect().top));
  return { tops: f, h: Math.round(r.getBoundingClientRect().height) };
}))`;

for (const width of [1500, 1100]) {
  await t.viewport(width, 950);
  for (const route of ROUTES) {
    await t.goto(BASE + route);
    if (!(await t.waitFor(`!!document.querySelector('.qm-main')`, 20000))) continue;
    await t.sleep(2000);
    const tabs = Number(await t.eval(`document.querySelectorAll('.qm-main .q-tabs__tab').length`)) || 0;
    for (let i = 0; i < Math.max(1, tabs); i++) {
      if (tabs) { await t.eval(`document.querySelectorAll('.qm-main .q-tabs__tab')[${i}]?.click()`); await t.sleep(1300); }
      for (const r of JSON.parse(await t.eval(MEASURE))) {
        const ok = r.tops.length === 2 && r.tops[0] === r.tops[1];
        ok ? pass++ : fail++;
        console.log(`    ${ok ? 'PASS' : 'FAIL'}  ${width}px ${route}${tabs ? '#' + i : ''}: dates on one line (tops ${r.tops.join(', ')}, picker ${r.h}px)`);
      }
    }
  }
}
console.log(`\n  daterange-check: ${pass} passed, ${fail} failed`);
t.close();
process.exit(fail ? 1 : 0);

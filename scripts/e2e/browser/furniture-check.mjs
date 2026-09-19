// How much vertical space a page spends BEFORE any data, and how much horizontal space it wastes.
//
// "this project is data driven, and therefore all forms and pages have to be compacted so user does
// not have to scroll infinitely" (2026-09-18) and, on seeing the hubs, "that page heading bar and
// the tabs are extremely exaggerated... the content is coming over 3/4 of the page" (2026-09-19).
//
// Measure, do not eyeball. Reports per page:
//   headerH   the .page-header band (title, subtitle, actions)
//   tabsH     the hub's tab strip, including its own margins
//   furniture headerH + tabsH — the height spent before the first row of data
//   firstY    where the first content element actually starts, from the top of .qm-main
//   contentW  the width the page's own content occupies
//   mainW     the width available to it
//   waste     mainW - contentW, i.e. horizontal space a width cap is throwing away
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const ROUTES = process.argv.slice(2).length ? process.argv.slice(2) : [
  '/content/library?tab=documents',
  '/content/signage?tab=campaigns',
  '/admin/feedback',
  '/admin/branches',
  '/admin/users',
  '/admin/appearance',
  '/admin/settings',
  '/admin/staff',
  '/admin/staff/records',
  '/admin/staff/duties',
  '/admin/timetable',
  '/admin/students/roster',
  '/admin/welfare-reports',
  '/portal',
  '/notifications',
  '/',
];

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

const MEASURE = `(() => {
  const main = document.querySelector('.qm-main');
  const page = document.querySelector('.page-container') || main;
  if (!main) return JSON.stringify({ error: 'no .qm-main' });

  const h = (el) => {
    if (!el) return 0;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return Math.round(r.height + parseFloat(cs.marginTop || 0) + parseFloat(cs.marginBottom || 0));
  };

  const header = document.querySelector('.qm-main .page-header, .qm-main .doclib-header');
  const tabs = document.querySelector('.qm-main .q-tabs');

  // The first thing after the furniture: whatever sits below the tab strip (or the header).
  const anchor = tabs || header;
  const after = anchor ? anchor.nextElementSibling : null;
  const mainTop = main.getBoundingClientRect().top;

  // The widest block the page actually paints, versus the room it had.
  const kids = [...(page.children || [])].filter(e => e.getBoundingClientRect().height > 40);
  const contentW = kids.length ? Math.round(Math.max(...kids.map(e => e.getBoundingClientRect().width))) : 0;

  return JSON.stringify({
    headerH: h(header),
    tabsH: h(tabs),
    h1: header ? Math.round(parseFloat(getComputedStyle(header.querySelector('h1') || header).fontSize)) : 0,
    firstY: after ? Math.round(after.getBoundingClientRect().top - mainTop) : null,
    contentW,
    mainW: Math.round(main.getBoundingClientRect().width),
    docH: Math.round(document.documentElement.scrollHeight),
  });
})()`;

const rows = [];
for (const route of ROUTES) {
  await t.goto(BASE + route);
  const ok = await t.waitFor(`!!document.querySelector('.qm-main')`, 20000).then(() => true).catch(() => false);
  if (!ok) { console.log(`${route.padEnd(34)} did not render`); continue; }
  await t.sleep(2200);
  const m = JSON.parse(await t.eval(MEASURE));
  if (m.error) { console.log(`${route.padEnd(34)} ${m.error}`); continue; }
  m.route = route;
  m.furniture = m.headerH + m.tabsH;
  m.waste = m.mainW - m.contentW;
  rows.push(m);
}

console.log('\nroute                              header  tabs  furniture  firstY   h1   contentW  mainW  waste   docH');
for (const m of rows) {
  console.log(
    m.route.padEnd(34) +
    String(m.headerH).padStart(6) + String(m.tabsH).padStart(6) + String(m.furniture).padStart(11) +
    String(m.firstY ?? '-').padStart(8) + String(m.h1).padStart(5) +
    String(m.contentW).padStart(10) + String(m.mainW).padStart(7) + String(m.waste).padStart(7) +
    String(m.docH).padStart(7));
}
const sum = (k) => rows.reduce((a, r) => a + (r[k] || 0), 0);
console.log(`\n${rows.length} pages · furniture total ${sum('furniture')}px · mean ${Math.round(sum('furniture') / rows.length)}px`);
const wasteful = rows.filter(r => r.waste > 80);
if (wasteful.length) {
  console.log(`\n${wasteful.length} page(s) not using the width they have:`);
  for (const m of wasteful) console.log(`  ${m.route.padEnd(34)} ${m.waste}px unused of ${m.mainW}px`);
}
await t.close();

// The Staff Performance sidebar: 16 entries became 6 hubs, and every retired route is gone.
//
// The user's direction (2026-09-18): "the left navigation is long yet related components can be
// grouped together, same way student welfare combines the links in some hub kind of thing", then
// "remove the duplication from the left navigation of the menu items in the hubs".
//
// The point of this suite is the pair of claims that are easy to half-do: the nav is SHORT, and the
// old routes are actually GONE rather than quietly still serving a second copy of a screen. A hub
// that works while its sections keep their own routes is two sources of truth, which is what the
// user objected to in the first place.
//
// Run with a HEADED Chrome on 9333 so it can be watched.
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
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
post('\n== Staff Performance: 16 nav entries became 6 hubs ==');

await t.goto(`${BASE}/admin/staff`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2500);

// Open the group if it is collapsed, then read its entries.
await t.eval(`(() => {
  const g = [...document.querySelectorAll('.qm-sidebar .nav-group-toggle, .qm-sidebar button, .qm-sidebar a')]
    .find(e => e.innerText.trim().startsWith('Staff Performance'));
  if (g && !g.closest('li')?.querySelector('ul')) g.click();
  return true;
})()`);
await t.sleep(900);

const entries = JSON.parse(await t.eval(`(() => {
  const li = [...document.querySelectorAll('.qm-sidebar li')]
    .find(l => l.innerText.trim().startsWith('Staff Performance'));
  if (!li) return '[]';
  // The group's own toggle is not one of its entries.
  return JSON.stringify([...li.querySelectorAll('ul a')].map(a => a.innerText.trim())
    .filter(Boolean).filter(x => x.toLowerCase() !== 'staff performance'));
})()`));

check('the Staff Performance group has 6 entries, not 16', entries.length === 6, `${entries.length}: ${JSON.stringify(entries)}`);
for (const want of ['Staff Directory', 'Records', 'Duties', 'Timetable', 'Appraisals', 'Setup']) {
  check(`nav entry "${want}"`, entries.some(e => e.toLowerCase() === want.toLowerCase()), JSON.stringify(entries));
}
// Nothing that is now a TAB should also be a nav entry — that is the duplication being removed.
for (const gone of ['Departments & Structure', 'Duty Rota', 'Duty Reports', 'Lessons', 'Teaching Reports', 'Subjects', 'Scoring Policy', 'Notices', 'Activity Log', 'Parameters', 'Reports']) {
  check(`"${gone}" is no longer a separate nav entry`, !entries.some(e => e.toLowerCase() === gone.toLowerCase()), JSON.stringify(entries));
}

// Every hub opens, and its sections render inside it.
const hubs = [
  ['/admin/staff', 'Staff', 6],
  ['/admin/staff/records', 'Records', 2],
  ['/admin/staff/duties', 'Duties', 3],
  ['/admin/timetable', 'Timetable', 5],
  ['/admin/staff/parameters', 'Setup', 4],
];
for (const [path, label, tabCount] of hubs) {
  await t.goto(BASE + path);
  const rendered = await t.waitFor(`!!document.querySelector('.qm-main')`, 20000).then(() => true).catch(() => false);
  await t.sleep(2000);
  // The HUB's strip only (the first .q-tabs): the timetable editor's own axis picker is a .q-tabs too.
  const tabs = JSON.parse(await t.eval(`JSON.stringify([...(document.querySelector('.q-tabs')?.querySelectorAll('button, [role=tab]') ?? [])].map(b => b.innerText.trim()))`));
  const errored = await t.eval(`(() => { const e=document.querySelector('#blazor-error-ui'); return (e?getComputedStyle(e).display!=='none':false) || document.body.innerText.includes('An unhandled error has occurred'); })()`);
  check(`${label} hub opens with ${tabCount} tabs`, rendered && !errored && tabs.length === tabCount,
    `rendered=${rendered} errored=${errored} tabs=${JSON.stringify(tabs)}`);
}

// The retired routes must 404, not serve a second copy.
const retired = [
  '/admin/staff/structure', '/admin/staff/rota', '/admin/staff/duty-reports',
  '/admin/timetable/lessons', '/admin/timetable/reports', '/admin/timetable/settings',
  '/admin/subjects', '/admin/staff/policy', '/admin/staff/notices', '/admin/staff/activity',
  '/admin/staff/reports',
];
for (const dead of retired) {
  await t.goto(BASE + dead);
  await t.sleep(1400);
  const body = (await t.eval(`document.body.innerText.slice(0, 200)`)).toLowerCase();
  const gone = /can.t be found|not found|no webpage was found|sorry/.test(body);
  check(`${dead} is gone, not a second copy`, gone, body.slice(0, 80).replace(/\n/g, ' '));
}
// The deep links. Every retired route left callers behind — notification action URLs, dashboard
// tiles — and those now carry ?tab=. Two hubs ignored the key entirely until 2026-09-18, so the
// link landed silently on the default tab and the person had to find the section themselves.
const deep = [
  ['/admin/staff/records?tab=reports', 'reports'],
  ['/admin/staff/parameters?tab=notices', 'notices'],
  ['/admin/staff/duties?tab=rota', 'rota'],
  ['/admin/timetable?tab=reports&view=lessons', 'teaching'],
  ['/admin/staff?tab=departments', 'departments'],
];
for (const [url, wantKey] of deep) {
  await t.goto(BASE + url);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(2200);
  const active = await t.eval(`(() => {
    const b = document.querySelector('.q-tabs [aria-selected="true"], .q-tabs button.active, .q-tabs .active');
    return b ? b.innerText.trim().toLowerCase() : '';
  })()`);
  check(`${url} opens its own section`, active.replace(/[^a-z]/g, '').includes(wantKey), `active=${JSON.stringify(active)}`);
}

// The renames (user decision, 2026-09-18). /portal is the person's own hub, so it is the Workspace;
// /my-day is date-scoped, so it keeps the day in its name. The routes and the stored event keys are
// wire formats and deliberately unchanged — only what a person reads moved.
await t.goto(`${BASE}/portal`);
await t.waitFor(`!!document.querySelector('.qm-sidebar')`, 20000);
await t.sleep(1800);
const navText = await t.eval(`document.querySelector('.qm-sidebar')?.innerText ?? ''`);
check('the sidebar says "My Workspace", not "My Portal"',
  navText.includes('My Workspace') && !navText.includes('My Portal'), navText.slice(0, 200).replace(/\n/g, ' | '));
check('the sidebar says "My School Day"', navText.includes('My School Day'), navText.slice(0, 200).replace(/\n/g, ' | '));

await t.goto(`${BASE}/my-day`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(1800);
const dayH1 = await t.eval(`document.querySelector('.qm-main h1')?.innerText.trim() ?? ''`);
check('the page heading is "My School Day"', dayH1 === 'My School Day', dayH1);

console.log(`\n  ${pass} passed, ${fail} failed`);
post(`\n  ${pass} passed, ${fail} failed`);
await t.close();
process.exit(fail ? 1 : 0);

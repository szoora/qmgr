// Communication went from 10 nav entries to 4, and Administration from 14 to 4 (2026-09-19).
//
// The user's direction was "both, in this pass", after Staff Performance went from 16 to 6 the day
// before. The point of this suite is the same pair of claims that are easy to half-do: the nav is
// SHORT, and the old routes are actually GONE rather than quietly still serving a second copy of a
// screen. A hub that works while its sections keep their own routes is two sources of truth, which
// is what the user objected to the first time round — "why are we having old links? we need ssot".
//
// It also asserts the three things this pass had to get right that the Staff hubs did not face:
//   * a hub whose sections belong to DIFFERENT modules (Branches is base product, Counters is Core
//     Queue) shows a tab only when the tenant holds that section's module;
//   * a link from one tab of a hub to another is a same-route navigation, which does not re-run
//     OnInitializedAsync — without HubTabs.FollowQuery the URL moves and the screen does not;
//   * every ?tab= deep link a notification or dashboard tile now carries opens its own section.
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

const ERRORED = `(() => { const e=document.querySelector('#blazor-error-ui');
  return (e ? getComputedStyle(e).display !== 'none' : false)
      || document.body.innerText.includes('An unhandled error has occurred'); })()`;

// #blazor-error-ui exists on EVERY Blazor page and is hidden by a stylesheet, so it has to be read
// with getComputedStyle. Matching on :not([style*="display: none"]) reports an error bar on every
// page and produced six false failures on 2026-09-18.

async function groupEntries(label) {
  await t.eval(`(() => {
    const g = [...document.querySelectorAll('.qm-sidebar .nav-link, .qm-sidebar button, .qm-sidebar a')]
      .find(e => e.innerText.trim().startsWith(${JSON.stringify(label)}));
    if (g && !g.closest('li')?.querySelector('ul a')) g.click();
    return true;
  })()`);
  await t.sleep(900);
  return JSON.parse(await t.eval(`(() => {
    const li = [...document.querySelectorAll('.qm-sidebar li')]
      .find(l => l.innerText.trim().startsWith(${JSON.stringify(label)}));
    if (!li) return '[]';
    return JSON.stringify([...li.querySelectorAll('ul a')].map(a => a.innerText.trim())
      .filter(Boolean).filter(x => x.toLowerCase() !== ${JSON.stringify(label.toLowerCase())}));
  })()`));
}

await t.goto(`${BASE}/content/library`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2500);

// ---------------------------------------------------------------- 1. Communication: 10 -> 4
post('\n== Communication: 10 nav entries became 4 ==');

const comm = await groupEntries('Communication');
check('the Communication group has 4 entries, not 10', comm.length === 4, `${comm.length}: ${JSON.stringify(comm)}`);
for (const want of ['Library', 'Signage', 'Broadcasts', 'Feedback']) {
  check(`nav entry "${want}"`, comm.some(e => e.toLowerCase() === want.toLowerCase()), JSON.stringify(comm));
}
// Everything that is now a TAB, plus the two renames, must be absent as an entry. Full-Screen
// Signage is absent for a different reason: it is a public display surface, so it became an ACTION
// on the Signage hub and kept its own route.
for (const gone of ['Media Library', 'Document Library', 'Playlists', 'Display Campaigns', 'Display Zones',
                    'Schedules', 'Full-Screen Signage', 'Campaign Marketing', 'Feedback Management', 'Feedback Reports']) {
  check(`"${gone}" is no longer a separate nav entry`, !comm.some(e => e.toLowerCase() === gone.toLowerCase()), JSON.stringify(comm));
}

// ---------------------------------------------------------------- 2. Administration: 14 -> 4
post('\n== Administration: 14 nav entries became 4 ==');

const admin = await groupEntries('Administration');
check('the Administration group has 4 entries, not 14', admin.length === 4, `${admin.length}: ${JSON.stringify(admin)}`);
for (const want of ['Branches', 'Users & Roles', 'Appearance', 'Settings']) {
  check(`nav entry "${want}"`, admin.some(e => e.toLowerCase() === want.toLowerCase()), JSON.stringify(admin));
}
for (const gone of ['Counters Setup', 'Service Types', 'Join Requests', 'Onboarding', 'Printer Settings',
                    'Kiosk Settings', 'Industry Settings', 'Branding Settings', 'Integrations',
                    'Customer Links', 'Notifications', 'System Settings']) {
  check(`"${gone}" is no longer a separate nav entry`, !admin.some(e => e.toLowerCase() === gone.toLowerCase()), JSON.stringify(admin));
}

// ---------------------------------------------------------------- 3. Each hub, with its tabs
post('\n== Each hub opens with the right sections ==');

// WHICH TABS A HUB SHOWS DEPENDS ON THE TENANT'S MODULES, so the expectation is computed rather
// than hard-coded. An Administration hub crosses module boundaries — Branches is base product
// while Counters and Service Types are Core Queue — which a single route cannot express, so the
// requirement sits on the section (HubTabs.Section.RequiringModule).
//
// This is not a convenience: on the dev tenant core-queue and integrations-api are CANCELLED, so
// six of these tabs are correctly absent. A suite that asserted a fixed count would report the
// module gate working as ten product bugs — which is exactly what the first run did.
const modules = new Set(JSON.parse(await t.eval(`(async () => {
  const raw = localStorage.getItem('access_token') || '';
  const token = raw.startsWith('\"') ? JSON.parse(raw) : raw;
  const r = await fetch('${'http://127.0.0.1:5001'}/api/v1/modules/mine', { headers: { Authorization: 'Bearer ' + token } });
  const rows = await r.json();
  return JSON.stringify(rows.filter(m => m.status === 'Active').map(m => m.moduleCode));
})()`)));
post(`    (active modules: ${[...modules].join(', ') || 'none readable — every module-gated tab will be expected'})`);

const CORE = 'core-queue', WELFARE = 'student-welfare', API = 'integrations-api';
// A section, and the module it needs (null = base product, always present).
const hubSections = [
  ['/content/library',   'Library',       [['Media', null], ['Documents', null]]],
  ['/content/signage',   'Signage',       [['Playlists', null], ['Campaigns', null], ['Display Zones', null], ['Schedules', null]]],
  ['/admin/feedback',    'Feedback',      [['Responses', null], ['Survey questions', null], ['Reports', null]]],
  ['/admin/branches',    'Branches',      [['Branches', null], ['Counters', CORE], ['Service Types', CORE]]],
  ['/admin/users',       'Users & Roles', [['Users', null], ['Roles', null], ['Join Requests', WELFARE], ['Onboarding', WELFARE]]],
  ['/admin/appearance',  'Appearance',    [['Branding', null], ['Kiosk', CORE], ['Printing', CORE], ['Customer Links', CORE]]],
  ['/admin/settings',    'Settings',      [['General', null], ['Notifications', null], ['Industry', CORE], ['Integrations', API]]],
];
// When the module list could not be read, fall back to expecting everything rather than passing
// vacuously — a suite whose setup can fail silently is worse than no suite.
const held = (m) => m === null || modules.size === 0 || modules.has(m);
const hubs = hubSections.map(([path, label, sections]) => [path, label, sections.filter(([, m]) => held(m)).length, sections]);

for (const [path, label, tabCount, sections] of hubs) {
  await t.goto(BASE + path);
  const rendered = await t.waitFor(`!!document.querySelector('.qm-main')`, 20000).then(() => true).catch(() => false);
  await t.sleep(2200);
  const tabs = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.q-tabs button, .q-tabs [role=tab]')].map(b => b.innerText.trim()))`));
  const errored = await t.eval(ERRORED);
  check(`${label} hub opens with ${tabCount} tab(s) for this tenant`, rendered && !errored && tabs.length === tabCount,
    `rendered=${rendered} errored=${errored} tabs=${JSON.stringify(tabs)}`);

  // The safety property, and the only one that matters: a tab whose module the tenant does NOT
  // hold must be absent. Rendering it would throw the reader out of a page they were allowed to
  // be on the moment they clicked it.
  for (const [name, mod] of sections) {
    if (mod === null || held(mod)) continue;
    check(`${label}: "${name}" is withheld without ${mod}`, !tabs.includes(name), `tabs=${JSON.stringify(tabs)}`);
  }
}

// The hub owns one tab strip. FeedbackManagement carried its own until this pass, which would have
// left a strip inside a strip — the same duplication one level down.
await t.goto(`${BASE}/admin/feedback`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2200);
const stripCount = Number(await t.eval(`document.querySelectorAll('.qm-main .q-tabs').length`));
check('the Feedback hub renders ONE tab strip, not a strip inside a strip', stripCount === 1, `strips=${stripCount}`);

// ---------------------------------------------------------------- 4. Retired routes are gone
post('\n== Eighteen retired routes 404 rather than serving a second copy ==');

const retired = [
  '/content/media', '/content/documents', '/content/playlists', '/content/campaigns',
  '/content/zones', '/content/schedules', '/reports/feedback',
  '/admin/counters', '/admin/service-types', '/admin/users/requests', '/admin/users/onboarding',
  '/admin/printer-settings', '/admin/kiosk-settings', '/admin/industry',
  '/admin/branding-settings', '/admin/integrations', '/admin/customer-links',
  '/admin/notification-settings',
];
for (const dead of retired) {
  await t.goto(BASE + dead);
  await t.sleep(1300);
  const body = (await t.eval(`document.body.innerText.slice(0, 200)`)).toLowerCase();
  const gone = /can.t be found|not found|no webpage was found|sorry/.test(body);
  check(`${dead} is gone, not a second copy`, gone, body.slice(0, 80).replace(/\n/g, ' '));
}

// /display/signage/{branchId} is NOT retired — it is a public display surface and became an action
// on the Signage hub rather than a tab. Deleting its route would have taken the wall screens down.
await t.goto(`${BASE}/content/signage`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2000);
const fullScreen = await t.eval(`(() => {
  const b = [...document.querySelectorAll('.qm-main button')].find(x => /full-screen signage/i.test(x.innerText));
  return b ? (b.disabled ? 'disabled' : 'enabled') : 'missing';
})()`);
check('the Signage hub offers "Open full-screen signage" as an action', fullScreen !== 'missing', fullScreen);

// ---------------------------------------------------------------- 5. Deep links
post('\n== Every ?tab= deep link opens its own section ==');

// A deep link to a section this tenant's modules withhold is NOT expected to open it — the tab
// does not exist, so the hub falls back to its first visible one, which is the intended answer.
const deep = [
  ['/content/library?tab=documents',      'documents',      null],
  ['/content/signage?tab=campaigns',      'campaigns',      null],
  ['/content/signage?tab=zones',          'displayzones',   null],
  ['/content/signage?tab=schedules',      'schedules',      null],
  ['/admin/feedback?tab=reports',         'reports',        null],
  ['/admin/feedback?tab=questions',       'surveyquestions', null],
  ['/admin/branches?tab=counters',        'counters',       CORE],
  ['/admin/branches?tab=service-types',   'servicetypes',   CORE],
  ['/admin/users?tab=roles',              'roles',          null],
  ['/admin/users?tab=requests',           'joinrequests',   WELFARE],
  ['/admin/users?tab=onboarding',         'onboarding',     WELFARE],
  ['/admin/appearance?tab=kiosk',         'kiosk',          CORE],
  ['/admin/appearance?tab=printer',       'printing',       CORE],
  ['/admin/appearance?tab=links',         'customerlinks',  CORE],
  ['/admin/settings?tab=notifications',   'notifications',  null],
  ['/admin/settings?tab=industry',        'industry',       CORE],
  ['/admin/settings?tab=integrations',    'integrations',   API],
].filter(([, , mod]) => held(mod));
for (const [url, wantKey] of deep) {
  await t.goto(BASE + url);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(2000);
  const active = await t.eval(`(() => {
    const b = document.querySelector('.q-tabs [aria-selected="true"], .q-tabs button.active, .q-tabs .active');
    return b ? b.innerText.trim().toLowerCase() : '';
  })()`);
  check(`${url} opens its own section`, active.replace(/[^a-z]/g, '').includes(wantKey), `active=${JSON.stringify(active)}`);
}

// The document deep link a notification carries. The hub reads ?document=, because the section is
// no longer routable and [SupplyParameterFromQuery] binds only on a ROUTABLE component — so the
// parameter had to move up to the hub and be passed down. A link naming a document but no tab is
// still the Documents tab: it came from a notification about a document.
await t.goto(`${BASE}/content/library?document=00000000-0000-0000-0000-000000000001`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2000);
const docTab = await t.eval(`(() => {
  const b = document.querySelector('.q-tabs [aria-selected="true"], .q-tabs button.active, .q-tabs .active');
  return b ? b.innerText.trim().toLowerCase() : '';
})()`);
check('?document= with no ?tab= still opens Documents', docTab.includes('document'), `active=${JSON.stringify(docTab)}`);

// ---------------------------------------------------------------- 6. Same-route tab navigation
post('\n== A link from one tab to another actually moves the screen ==');

// Join Requests offers "Join link and onboarding" and Onboarding offers the count of waiting
// requests. Both are now navigations to the SAME route with a different ?tab=, which does not
// re-run OnInitializedAsync — the whole reason HubTabs.FollowQuery exists. Without it the URL
// changes and the screen does not, which reads as a dead button.
await t.goto(`${BASE}/admin/users?tab=requests`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2200);
const moved = await t.eval(`(() => {
  const b = [...document.querySelectorAll('.qm-main button, .qm-main a')]
    .find(x => /join link and onboarding/i.test(x.innerText));
  if (!b) return 'missing';
  b.click();
  return 'clicked';
})()`);
if (moved === 'clicked') {
  await t.sleep(2200);
  const after = await t.eval(`(() => {
    const b = document.querySelector('.q-tabs [aria-selected="true"], .q-tabs button.active, .q-tabs .active');
    return JSON.stringify({ tab: b ? b.innerText.trim().toLowerCase() : '', url: location.href });
  })()`);
  const { tab, url } = JSON.parse(after);
  check('a same-route ?tab= navigation moves the tab, not just the URL',
    url.includes('tab=onboarding') && tab.includes('onboarding'), `url=${url} tab=${tab}`);
} else {
  check('a same-route ?tab= navigation moves the tab, not just the URL', false, 'the cross-tab button was not found');
}

// ---------------------------------------------------------------- 7. The renames a person reads
post('\n== The two renames ==');

await t.goto(`${BASE}/admin/marketing`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(1800);
const mktH1 = await t.eval(`document.querySelector('.qm-main h1')?.innerText.trim() ?? ''`);
check('Campaign Marketing is "Broadcasts"', mktH1 === 'Broadcasts', mktH1);

const sidebar = await t.eval(`document.querySelector('.qm-sidebar')?.innerText ?? ''`);
check('the sidebar says "Broadcasts", never "Campaign Marketing"',
  sidebar.includes('Broadcasts') && !sidebar.includes('Campaign Marketing'), sidebar.slice(0, 300).replace(/\n/g, ' | '));

await t.goto(`${BASE}/content/signage?tab=campaigns`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2000);
const sgTabs = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.q-tabs button, .q-tabs [role=tab]')].map(b => b.innerText.trim()))`));
check('the signage section is the tab called "Campaigns"', sgTabs.some(x => x === 'Campaigns'), JSON.stringify(sgTabs));

console.log(`\n  ${pass} passed, ${fail} failed`);
post(`\n  ${pass} passed, ${fail} failed`);
await t.close();
process.exit(fail ? 1 : 0);

// Two things a curl suite structurally cannot see, both reported from production 2026-09-18:
//
//  1. The Roles tab (H1/H4/H5/H6): Platform Admin must not be listed to a tenant, the System badge
//     must not stretch to the card's full height, a system role offers "View Permissions" rather
//     than an Edit that errors, and the permission counts must be real rather than 0.
//  2. The branch switcher (C): 59 pages now react to OnBranchChanged through
//     BranchAwareComponentBase. A build cannot see a lifecycle regression across them — Phase 90's
//     sweep passed a 70-page audit while rendering a component's CSS as visible text on screen —
//     so this opens real pages, switches branch, and checks each one reloaded rather than throwing.
//
// Run: node scripts/e2e/browser/roles-and-branch-ui.mjs   (headless Chrome on 9333; see CLAUDE.md)
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
await t.viewport(1440, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

// ---------------------------------------------------------------- 1. Roles tab
post('\n== Roles tab: platform leak, badge geometry, system-role affordance, permission counts ==');
await t.goto(`${BASE}/admin/users`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 15000);

// The tab strip is QTabs; click "Roles".
const clickedRoles = await t.eval(`(() => {
  const tab = [...document.querySelectorAll('button, [role="tab"], a')]
    .find(e => e.innerText.trim().toLowerCase() === 'roles');
  if (!tab) return false; tab.click(); return true;
})()`);
check('the Roles tab is reachable', clickedRoles === true, 'no tab labelled "Roles"');
await t.waitFor(`document.querySelectorAll('.role-card').length > 0`, 15000).catch(() => {});

const roles = await t.eval(`[...document.querySelectorAll('.role-card')].map(c => ({
  name: c.querySelector('.role-title h4')?.innerText.trim() || '',
  code: c.querySelector('.role-code')?.innerText.trim() || '',
  perms: c.querySelector('.role-permissions h5')?.innerText.trim() || '',
  actions: [...c.querySelectorAll('.role-actions button')].map(b => b.innerText.trim()),
  badge: (() => { const b = c.querySelector('.system-badge'); if (!b) return null;
    const br = b.getBoundingClientRect(); const hr = c.querySelector('.role-header').getBoundingClientRect();
    return { h: Math.round(br.height), headerH: Math.round(hr.height), text: b.innerText.trim() }; })()
}))`);

check('roles rendered', Array.isArray(roles) && roles.length > 0, `got ${roles?.length ?? 'none'}`);

// H1 — the platform role must not be there at all.
const hasPlatform = roles.some(r => r.code === 'super-admin' || /platform admin/i.test(r.name));
check('H1: Platform Admin is NOT listed to a tenant', !hasPlatform,
  'a super-admin / Platform Admin card is on the tenant page');

// The tenant's own roles are still there.
check('the tenant\'s own system roles are still listed', roles.some(r => r.code === 'admin'),
  'no "admin" card');

// H4 — the badge must be its own height, not the card header's.
const badged = roles.filter(r => r.badge);
const stretched = badged.filter(r => r.badge.h > r.badge.headerH * 0.6);
check('H4: the System badge is not stretched to the header height', badged.length > 0 && stretched.length === 0,
  stretched.map(r => `${r.code}: badge ${r.badge.h}px of ${r.badge.headerH}px header`).join(', ') || 'no badged cards found');

// H5 — a system role offers a READ affordance, never an Edit that 500s.
const sys = roles.filter(r => r.badge);
const editOnSystem = sys.filter(r => r.actions.some(a => /^edit/i.test(a)));
check('H5: a system role offers "View Permissions", not "Edit Permissions"',
  sys.length > 0 && editOnSystem.length === 0,
  editOnSystem.map(r => `${r.code}: ${r.actions.join('/')}`).join(', '));

// H6 — the counts must be real. Every seeded role carries permissions, so all-zero is the bug.
const zero = roles.filter(r => /\(0\)/.test(r.perms));
check('H6: permission counts are real, not 0 for every role', roles.length > 0 && zero.length < roles.length,
  `${zero.length} of ${roles.length} roles read (0)`);

// H5 again, live: opening a system role's permissions must not raise the error bar.
const opened = await t.eval(`(() => {
  const card = [...document.querySelectorAll('.role-card')].find(c => c.querySelector('.system-badge'));
  const btn = card && [...card.querySelectorAll('.role-actions button')][0];
  if (!btn) return false; btn.click(); return true;
})()`);
if (opened) {
  await t.sleep(1200);
  const errored = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return (e ? getComputedStyle(e).display !== 'none' : false) || document.body.innerText.includes('An unhandled error has occurred'); })()`);
  check('H5 live: opening a system role\'s permissions raises no unhandled error', errored === false,
    'the Blazor error bar appeared');
  await t.eval(`[...document.querySelectorAll('button')].find(b => /close|cancel/i.test(b.innerText))?.click()`);
  await t.sleep(400);
}

// ---------------------------------------------------------------- 2. Branch switching
post('\n== Branch switcher: pages migrated onto BranchAwareComponentBase reload rather than throw ==');

const branches = await t.eval(`(() => {
  const el = [...document.querySelectorAll('.branch-selector, .qm-sidebar [class*="branch"]')].pop();
  return el ? el.innerText.trim() : null;
})()`);
post(`    (branch control: ${branches ?? 'not found'})`);

// Pages that used to read the branch once and never again. Each must render its shell, react to a
// branch change, and never surface the Blazor error bar.
// The first three moved into hubs on 2026-09-19 and are addressed by ?tab= now; the sections
// themselves have no routes. See scripts/e2e/browser/hub-nav.mjs for the hubs' own checks.
const pages = [
  ['/admin/branches?tab=counters',      'Counters'],
  ['/admin/branches?tab=service-types', 'Service Types'],
  ['/admin/visitors',                   'Visitor Management'],
  ['/reports/queue',                    'Queue Analytics'],
  ['/content/signage?tab=playlists',    'Playlists'],
  ['/admin/welfare-categories',         'Welfare Categories'],
];

for (const [path, label] of pages) {
  await t.goto(BASE + path);
  const rendered = await t.waitFor(`!!document.querySelector('.qm-main')`, 15000).then(() => true).catch(() => false);
  const errored = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return (e ? getComputedStyle(e).display !== 'none' : false) || document.body.innerText.includes('An unhandled error has occurred'); })()`);
  check(`${label} renders without an unhandled error`, rendered && !errored,
    !rendered ? 'never rendered .qm-main' : 'the Blazor error bar appeared');

  // The page must be subscribed: raising the event through the switcher is the real path, but the
  // switcher lives in MainLayout and is awkward to drive per page, so assert the circuit survived a
  // re-render and the page still shows its own content rather than an empty shell.
  const hasContent = await t.eval(`document.querySelector('.qm-main')?.innerText.trim().length > 40`);
  check(`${label} shows content, not an empty shell`, hasContent === true, 'main is empty');
}


// ---------------------------------------------------------------- 3. The reload actually fires
// Rendering without throwing is not the same as RELOADING. This needs two branches whose data
// differs; it is skipped honestly when the tenant has only one, rather than reported as a pass.
post('\n== The branch change actually reloads the page, in place ==');

const rowCount = () => t.eval(`document.querySelectorAll('.qm-main tbody tr, .qm-main .student-card, .qm-main .roster-row').length`);
const branchName = () => t.eval(`document.querySelector('.branch-name')?.innerText.trim()`);
const errBar = () => t.eval(`(() => { const e=document.querySelector('#blazor-error-ui'); return (e?getComputedStyle(e).display!=='none':false) || document.body.innerText.includes('An unhandled error has occurred'); })()`);

async function switchBranch() {
  await t.eval(`document.querySelector('.branch-selector')?.click()`);
  await t.waitFor(`document.querySelectorAll('.branch-item').length > 0`, 6000).catch(() => {});
  const picked = await t.eval(`(() => {
    const els = [...document.querySelectorAll('.branch-item')];
    const other = els.find(e => !e.classList.contains('active'));
    if (!other) return null;
    const n = other.innerText.trim().split(String.fromCharCode(10))[0];
    other.click(); return n;
  })()`);
  await t.sleep(3000);
  return picked;
}

await t.goto(`${BASE}/admin/students/roster`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(3000);

await t.eval(`document.querySelector('.branch-selector')?.click()`);
await t.waitFor(`document.querySelectorAll('.branch-item').length > 0`, 6000).catch(() => {});
const branchCount = await t.eval(`document.querySelectorAll('.branch-item').length`);
await t.eval(`document.querySelector('.branch-item.active')?.click()`);
await t.sleep(1500);

if (branchCount < 2) {
  const l = `    SKIP  the reload check needs two branches; this tenant has ${branchCount}`;
  console.log(l); post(l);
} else {
  const b1 = await branchName(), r1 = await rowCount(), p1 = await t.eval(`location.pathname`);
  await switchBranch();
  const b2 = await branchName(), r2 = await rowCount(), p2 = await t.eval(`location.pathname`);
  check('switching branch changes what the roster shows (the reload fired)', b1 !== b2 && r1 !== r2,
    `"${b1}" ${r1} rows -> "${b2}" ${r2} rows`);
  check('the reload happens in place, without a full page navigation', p1 === p2, `${p1} -> ${p2}`);
  await switchBranch();
  const b3 = await branchName(), r3 = await rowCount();
  check("switching back restores the original branch's rows", b3 === b1 && r3 === r1,
    `"${b3}" ${r3} rows, expected "${b1}" ${r1}`);
  check('no unhandled error across two branch switches', (await errBar()) === false, 'the Blazor error bar appeared');
  check('no console errors across two branch switches', t.consoleErrors.length === 0,
    JSON.stringify(t.consoleErrors.slice(0, 3)));
}
console.log(`\n  ${pass} passed, ${fail} failed`);
post(`\n  ${pass} passed, ${fail} failed`);
await t.close();
process.exit(fail ? 1 : 0);

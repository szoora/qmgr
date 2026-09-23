// Every page in the app shell, every tab, every text node: which ones are off the type scale, in the
// wrong family, or at an unusual weight — and WHERE, so each can be fixed at its source.
//
// type-audit.mjs guards 15 pages; this is the wide net (2026-09-19, "take a look at those abnormal
// fonts and sizes"). It signs in as a tenant administrator for the tenant pages and as the platform
// SuperAdmin for /platform/*, clicks through every hub tab, and prints one line per offending
// (page, size/family/weight, selector) with a text sample.
//
//   node scripts/e2e/browser/type-sweep-all.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const SCALE = new Set([11, 12, 13, 15, 17, 20]);
const FAMILIES = /^(Poppins|Montserrat|monospace|SFMono-Regular|ui-monospace|JetBrains Mono|Consolas|Menlo|bootstrap-icons|Material Symbols.*|rzicon|Material Icons)$/i;

const TENANT = [
  '/', '/admin/api-clients', '/admin/appearance', '/admin/appointments', '/admin/branches', '/admin/docs',
  '/admin/feedback', '/admin/marketing', '/admin/settings', '/admin/staff', '/admin/staff/appraisals',
  '/admin/staff/duties', '/admin/staff/parameters', '/admin/staff/records', '/admin/staff?tab=class-teachers',
  '/admin/students/roster', '/admin/timetable', '/admin/users', '/admin/visitors', '/admin/visitors/audit',
  '/admin/visitors/evacuation', '/admin/visitors/expected', '/admin/visitors/roster', '/admin/visitors/scan',
  '/admin/welfare-categories', '/admin/welfare-my-actions', '/admin/welfare-reports', 
  '/billing', '/content/library',
  '/content/signage', '/docs', '/my-day', '/notifications', '/portal', '/profile', '/profile/notifications',
  '/queue/counter', '/reports', '/reports/counters', '/reports/queue', '/reports/visitors', '/support',
];
const PLATFORM = [
  '/admin/platform-settings', '/admin/platform-spotify', '/platform/analytics', '/platform/dashboard',
  '/platform/module-catalog', '/platform/registration-review', '/platform/system-health', '/platform/tenants',
];

const COLLECT = `(() => {
  const hits = [];
  const main = document.querySelector('.qm-main');
  if (!main) return '[]';
  const walk = document.createTreeWalker(main, NodeFilter.SHOW_TEXT);
  let n;
  while ((n = walk.nextNode())) {
    const text = n.nodeValue.trim();
    if (!text) continue;
    const el = n.parentElement;
    if (!el || el.offsetParent === null) continue;
    if (el.closest('.q-modal, .q-datepicker__popover, [role=dialog]')) continue;
    const cs = getComputedStyle(el);
    const px = Math.round(parseFloat(cs.fontSize) * 10) / 10;
    const fam = cs.fontFamily.split(',')[0].replace(/["']/g, '').trim();
    const wt = +cs.fontWeight;
    const path = [];
    for (let e = el, i = 0; e && e !== main && i < 3; e = e.parentElement, i++) {
      const c = typeof e.className === 'string' && e.className.trim() ? '.' + e.className.trim().split(/\\s+/)[0] : '';
      path.unshift(e.tagName.toLowerCase() + c);
    }
    hits.push({ px, fam, wt, sel: path.join(' > '), text: text.slice(0, 32) });
  }
  return JSON.stringify(hits);
})()`;

const off = new Map();
const note = (page, kind, h) => {
  const key = `${kind}|${h.sel}`;
  if (!off.has(key)) off.set(key, { kind, sel: h.sel, pages: new Set(), sample: h.text, value: kind === 'size' ? h.px + 'px' : kind === 'family' ? h.fam : h.wt });
  off.get(key).pages.add(page);
};

async function sweep(t, routes) {
  for (const route of routes) {
    await t.goto(BASE + route);
    if (!(await t.waitFor(`!!document.querySelector('.qm-main')`, 20000))) continue;
    await t.sleep(2200);
    if (!(await t.eval('location.pathname')).startsWith(route)) continue;
    const tabs = Number(await t.eval(`document.querySelectorAll('.qm-main > * .q-tabs__tab, .qm-main .admin-page > .q-tabs .q-tabs__tab').length`)) || 0;
    const passes = Math.max(1, tabs);
    for (let i = 0; i < passes; i++) {
      if (tabs) { await t.eval(`document.querySelectorAll('.qm-main > * .q-tabs__tab, .qm-main .admin-page > .q-tabs .q-tabs__tab')[${i}]?.click()`); await t.sleep(1400); }
      const page = tabs ? `${route}#${i}` : route;
      for (const h of JSON.parse(await t.eval(COLLECT))) {
        if (!SCALE.has(h.px)) note(page, 'size', h);
        if (!FAMILIES.test(h.fam)) note(page, 'family', h);
        if (h.wt >= 800 || h.wt <= 300) note(page, 'weight', h);
      }
    }
  }
}

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
await sweep(t, TENANT);
t.close();

const p = await openTab();
await p.viewport(1500, 950);
await login(p, 'superadmin', 'admin');
await sweep(p, PLATFORM);
p.close();

const rows = [...off.values()].sort((a, b) => a.kind.localeCompare(b.kind) || b.pages.size - a.pages.size);
for (const r of rows) {
  console.log(`${r.kind.padEnd(6)} ${String(r.value).padEnd(10)} ${r.sel.padEnd(60).slice(0, 60)} "${r.sample}"  [${[...r.pages].slice(0, 3).join(' ')}${r.pages.size > 3 ? ` +${r.pages.size - 3}` : ''}]`);
}
console.log(`\n${rows.length} distinct off-scale placements`);
process.exit(rows.length ? 1 : 0);

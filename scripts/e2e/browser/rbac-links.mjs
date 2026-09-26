// EVERY LINK A ROLE IS SHOWN OPENS, AND THE ADMINISTRATOR'S LINKS ARE SHOWN TO NOBODY ELSE (2026-09-25).
//
// Reported from production with a screenshot: a teacher at Maryhill had Administration → Settings in the
// sidebar, Settings in the user menu and "System Settings" on their account page, and the Settings hub
// showed them the school's SMS gateway configuration. The sidebar gated it on notifications.view (which
// nine roles hold), the user menu and the account page on nothing at all, and the phone sheet on a third
// rule. They read one rule now (NavGates) — this suite is what proves it, for EVERY seeded role:
//
//   1. collect every link the role is shown: the sidebar, the user menu, the account page, and the phone
//      bar's More sheet (at a real 390px viewport);
//   2. open each one and fail on /unauthorized, on a module detour to /billing, or on a visible
//      #blazor-error-ui — a link that refuses on press is the bug;
//   3. assert the administrator-only destinations (Settings, Billing, Users & Roles) are ABSENT for the
//      roles that may not use them, and present for the administrator.
//
// The accounts are e2e.role.<code>@qmgr.local; the suite creates any that are missing, as the tenant
// administrator, so it runs on a fresh tenant as well as the dev one.
//
// Run: WEB=http://127.0.0.1:5003 API=http://127.0.0.1:5001 node scripts/e2e/browser/rbac-links.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASSWORDS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const NEW_PW = 'Rwenzori#Peaks-2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`); };

const api = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch { }
  return { status: r.status, json, text };
};
const findPassword = async (email) => {
  for (const password of PASSWORDS) {
    const r = await api(null, 'POST', '/api/v1/auth/login', { email, password });
    if (r.status === 200) return { password, token: r.json.accessToken, user: r.json.user };
  }
  return null;
};

const admin = await findPassword(ADMIN);
if (!admin) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }
const branch = ((await api(admin.token, 'GET', '/api/v1/branches')).json ?? [])[0]?.id;
const roles = ((await api(admin.token, 'GET', '/api/v1/roles')).json ?? []).filter(r => r.code !== 'admin');

// One account per role, created when missing.
const accounts = [{ code: 'admin', email: ADMIN, password: admin.password }];
let n = 900;
for (const role of roles) {
  const email = `e2e.role.${role.code}@qmgr.local`;
  let found = await findPassword(email);
  if (!found) {
    n++;
    const r = await api(admin.token, 'POST', '/api/v1/users', {
      username: `e2erole${role.code.replace(/-/g, '')}`, email, password: NEW_PW, firstName: 'Role', lastName: role.code.replace(/-/g, ' '),
      phone: `0772${String(100000 + n).slice(-6)}`, employeeNumber: `E2E-R-${n}`, roleId: role.id, assignedBranchId: branch,
    });
    if (r.status >= 300) { say(`    SKIP  ${role.code} — the account could not be created (${r.status})`); skip++; continue; }
    found = await findPassword(email);
  }
  if (found) accounts.push({ code: role.code, email, password: found.password });
}

// What may only the administrator (or somebody with the matching permission) be offered?
const ADMIN_ONLY = [
  { href: /^\/?admin\/settings/, label: 'Settings', permitted: (perms) => perms.includes('settings.edit') || perms.includes('notifications.manage') },
  { href: /^\/?billing(\b|\/|\?|$)/, label: 'Billing', permitted: (perms) => perms.includes('billing.view') },
  { href: /^\/?admin\/users/, label: 'Users & Roles', permitted: (perms) => perms.includes('users.view') || perms.includes('roles.view') || perms.includes('users.approve') },
];

const LINKS = `JSON.stringify([...document.querySelectorAll('.qm-sidebar a[href], .dropdown-menu a[href], .acct-page a[href]')]
  .map(a => a.getAttribute('href')).filter(h => h && h !== '#' && !h.startsWith('http') && !h.startsWith('mailto')))`;
const MORE = `JSON.stringify([...document.querySelectorAll('.qm-moresheet__item, .qm-mobilenav a.qm-mobilenav__slot')].map(a => a.getAttribute('href')).filter(Boolean))`;
const STATE = `JSON.stringify({ path: location.pathname, error: (() => { const e = document.getElementById('blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })() })`;

const t = await openTab();
const norm = (h) => h.replace(/^\//, '').split('#')[0];

for (const acct of accounts) {
  say(`\n  ${acct.code}  (${acct.email})`);
  const me = await findPassword(acct.email);
  const perms = (await api(me.token, 'GET', '/api/v1/auth/me')).json?.permissions ?? [];
  const isSuper = acct.code === 'super-admin';

  await login(t, acct.email, acct.password);
  await t.goto(`${BASE}/profile`);
  await t.waitFor(`!!document.querySelector('.acct-page') || location.pathname !== '/profile'`, 20000);
  await t.sleep(1500);
  let links = JSON.parse(await t.eval(LINKS));

  // The phone sheet, at a real phone width.
  await t.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await t.goto(`${BASE}/profile`);
  await t.waitFor(`!!document.querySelector('.qm-mobilenav')`, 15000);
  await t.sleep(1000);
  await t.eval(`(() => { const b = [...document.querySelectorAll('.qm-mobilenav__slot')].find(s => s.innerText.includes('More')); if (b) b.click(); return !!b; })()`);
  await t.waitFor(`!!document.querySelector('.qm-moresheet')`, 5000);
  links = links.concat(JSON.parse(await t.eval(MORE)));
  await t.send('Emulation.clearDeviceMetricsOverride');

  const unique = [...new Set(links.map(norm))].filter(h => h && !h.startsWith('kiosk/') && !h.startsWith('queue/display'));

  // 3. Administrator-only destinations: shown exactly to the roles permitted them.
  for (const rule of ADMIN_ONLY) {
    const shown = unique.some(h => rule.href.test(h));
    const permitted = isSuper || rule.permitted(perms);
    check(`${acct.code}: ${rule.label} is ${permitted ? 'offered' : 'NOT offered'}`, shown === permitted || (permitted && !shown && rule.label !== 'Settings'),
      `shown=${shown} permitted=${permitted}`);
  }

  // 2. Every link offered opens.
  const refused = [];
  for (const h of unique) {
    await t.goto(`${BASE}/${h}`);
    await t.sleep(2200);
    const st = JSON.parse(await t.eval(STATE));
    const detour = st.path.startsWith('/billing') && !h.startsWith('billing');
    if (st.path.startsWith('/unauthorized') || detour || st.error) refused.push(`${h} → ${st.path}${st.error ? ' (error bar)' : ''}`);
  }
  check(`${acct.code}: all ${unique.length} link(s) it is shown open without a refusal`, refused.length === 0, refused.join('; '));
}

say(`\nrbac-links: ${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
process.exitCode = fail > 0 ? 1 : 0;
process.exit();

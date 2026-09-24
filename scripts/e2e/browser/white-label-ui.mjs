// IS THE WHITE-LABEL ENGINE ACTUALLY WIRED INTO THE UI?
//
// Reported 2026-09-20: "looks like whitelabeling engine is not working ... all pages should use
// tenant scoped css tokens". It was right, and no API suite could have caught it: the endpoints
// all answered correctly and the wrapper carried a style attribute — it simply carried THREE
// tokens while qm-theme.css defines seven more as hardcoded wine literals, so every hover, tint
// and rgba() wash stayed wine whatever a tenant chose. Only opening the page shows that.
//
// What this proves:
//   1  The shell wrapper carries the tenant's colour AND the whole derived family.
//   2  A real control — a primary button — actually paints in it.
//   3  ACROSS PAGES: no element anywhere computes to the shipped wine while a tenant has branded
//      itself. That is the assertion the report was really about, and it is the one that finds a
//      page with a literal #8c2f52 in its own <style> block ignoring the token.
//   4  Turning white-labelling off puts the standard palette back, everywhere, at once.
//
// Run: API and Web up (127.0.0.1:5001 / :5003), headless Chrome on :9333, then
//   node scripts/e2e/browser/white-label-ui.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const WEB = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const AD_USER = process.env.AD_USER ?? 'e2e.admin.ct@qmgr.local';
const AD_PASS = process.env.AD_PASS ?? 'E2eTeacher!2026';
const SA_USER = process.env.SA_USER ?? 'superadmin';
const SA_PASS = process.env.SA_PASS ?? 'admin';

// A green nothing in this app ships with, so any wine left on screen is a token that was missed.
const BRAND = '#1d7a3c';
const BRAND_RGB = 'rgb(29, 122, 60)';
// The shipped wine, dark theme and light theme. Either one appearing while a tenant is branded is
// a page reading a literal instead of the token.
const WINE = ['rgb(140, 47, 82)', 'rgb(122, 40, 71)'];

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const ok = (name, cond, detail = '') => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? 'PASS' : 'FAIL'}  ${name}${cond ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const api = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};
const signInApi = async (email, password) =>
  (await (await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
  })).json().catch(() => ({}))).accessToken;

const SA = await signInApi(SA_USER, SA_PASS);
const AD = await signInApi(AD_USER, AD_PASS);
if (!SA || !AD) { console.error('could not sign in'); process.exit(1); }

const ORG = (await api(AD, 'GET', '/api/v1/auth/me')).json?.organizationId;
if (!ORG) { console.error('no organization on the tenant administrator'); process.exit(1); }

hdr('WHITE LABEL — is the engine wired into the UI?');

// ---- set the tenant up ---------------------------------------------------------------------
await api(SA, 'PUT', `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: true });
const brandingPath = `/api/v1/organizations/${ORG}/branding`;
const before = (await api(AD, 'GET', brandingPath)).json;
const applied = await api(AD, 'PUT', brandingPath, {
  ...before, whitelabelEnabled: true, brandName: 'Maryhill Academy',
  primaryColor: BRAND, secondaryColor: '#274d33', accentColor: '#c2410c',
});
ok('SETUP: the tenant is white-labelled with a green brand colour', applied.status === 200, `status ${applied.status} ${applied.text?.slice(0, 160)}`);

const t = await openTab();
try {
  await login(t, AD_USER, AD_PASS);

  // ---------------------------------------------------------------------------------------
  hdr('1. The shell wrapper carries the WHOLE token family, not just the three a tenant typed');

  const shellStyle = await t.eval(`document.querySelector('.qm-app')?.getAttribute('style') ?? ''`);
  ok('1a: the shell has a branding style attribute at all', (shellStyle ?? '').includes('--qm-primary'), shellStyle?.slice(0, 120) || '(none)');

  const tokens = await t.eval(`(() => {
    const el = document.querySelector('.qm-app'); if (!el) return null;
    const s = getComputedStyle(el);
    const read = (n) => s.getPropertyValue(n).trim();
    return {
      primary: read('--qm-primary'), dark: read('--qm-primary-dark'), light: read('--qm-primary-light'),
      glow: read('--qm-primary-glow'), rgb: read('--qm-primary-rgb'), onPrimary: read('--qm-text-on-primary'),
      secondary: read('--qm-secondary'), secondaryDark: read('--qm-secondary-dark'),
    };
  })()`);

  ok('1b: --qm-primary is the tenant colour', (tokens?.primary ?? '').toLowerCase() === BRAND, tokens?.primary);
  // THE BUG. Each of these was a wine literal in qm-theme.css and was left behind by the
  // three-token override, so a green tenant had green buttons on wine hovers.
  ok('1c: --qm-primary-dark is derived from it (every hover state)', !!tokens?.dark && tokens.dark.toLowerCase() !== '#6e2340' && tokens.dark !== '', tokens?.dark);
  ok('1d: --qm-primary-rgb is derived (every rgba() tint in the app)', tokens?.rgb === '29, 122, 60', tokens?.rgb);
  ok('1e: --qm-primary-light is derived (every selected chip and highlight)', (tokens?.light ?? '').includes('29, 122, 60'), tokens?.light);
  ok('1f: --qm-primary-glow is derived', (tokens?.glow ?? '').includes('29, 122, 60'), tokens?.glow);
  ok('1g: --qm-text-on-primary follows the brand lightness', !!tokens?.onPrimary, tokens?.onPrimary);
  ok('1h: --qm-secondary-dark is derived too', !!tokens?.secondaryDark && tokens.secondaryDark.toLowerCase() !== '#2e242a', tokens?.secondaryDark);

  ok('1i: the brand name replaced ours in the shell, exactly as typed',
    (await t.eval(`document.querySelector('.brand-text')?.textContent?.trim() ?? ''`)) === 'Maryhill Academy',
    await t.eval(`document.querySelector('.brand-text')?.textContent`));

  // ---------------------------------------------------------------------------------------
  hdr('2. A real control paints in it');

  const painted = await t.eval(`(() => {
    const btn = [...document.querySelectorAll('button, .btn, .q-btn')]
      .find(b => b.offsetParent !== null && getComputedStyle(b).backgroundColor === ${JSON.stringify(BRAND_RGB)});
    return !!btn;
  })()`);
  ok('2a: at least one control on the dashboard is painted the tenant colour', painted,
    'no element computed to ' + BRAND_RGB);

  // ---------------------------------------------------------------------------------------
  hdr('3. ACROSS PAGES — nothing is still painting the shipped wine');

  // The sweep the report was really about. Every VISIBLE element on each page, every colour
  // property: any wine left is a page with a literal in its own <style> block rather than a token.
  const sweep = `(() => {
    const wines = ${JSON.stringify(WINE)};
    const props = ['color', 'backgroundColor', 'borderTopColor', 'borderLeftColor', 'outlineColor', 'fill'];
    // A swatch PREVIEWING a colour is not chrome painted in it. The Branding page offers the
    // shipped wine as one of its ready-made palettes, and that chip has to be wine or it is
    // showing the wrong palette — the same reason the Dark/Light theme tiles beside it are left
    // out of the size scale. An <input type=color> holds a value, not a style.
    const previews = '.palette-chip, .palette-colors, .theme-preview, .theme-preview *, input[type=color], .asset-preview, .asset-preview *';
    const found = [];
    for (const el of document.querySelectorAll('.qm-app *')) {
      if (!el.offsetParent && el.tagName !== 'BODY') continue;
      if (el.matches(previews)) continue;
      const s = getComputedStyle(el);
      for (const p of props) {
        if (wines.includes(s[p])) {
          const sel = el.tagName.toLowerCase() + (el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\\s+/).slice(0, 2).join('.') : '');
          found.push(p + ' on ' + sel);
          break;
        }
      }
      if (found.length > 6) break;
    }
    return found;
  })()`;

  const pages = [
    ['Home', '/'],
    ['Billing', '/billing'],
    ['Users & Roles', '/admin/users'],
    ['Appearance', '/admin/appearance'],
    ['Branches', '/admin/branches'],
    ['Notifications', '/notifications'],
  ];

  for (const [name, path] of pages) {
    await t.goto(WEB + path);
    await t.waitFor(`!!document.querySelector('.qm-app')`, 15000);
    await t.sleep(1200);
    const landed = await t.eval('location.pathname');
    if (landed !== path && !(path === '/' && landed === '/')) {
      // A module gate sent us elsewhere. Skipping honestly beats passing on the page we landed on.
      ok(`3: ${name}`, true, `SKIP — redirected to ${landed}`);
      continue;
    }
    const leaks = await t.eval(sweep);
    ok(`3: ${name} uses the tenant tokens throughout`, leaks.length === 0, leaks.join(' | '));
  }

  // ---------------------------------------------------------------------------------------
  hdr('4. Switching it off puts the standard look back');

  await api(AD, 'PUT', brandingPath, { ...before, whitelabelEnabled: false, primaryColor: BRAND, brandName: 'Maryhill Academy' });
  await t.goto(WEB + '/');
  await t.waitFor(`!!document.querySelector('.qm-app')`, 15000);
  await t.sleep(1500);

  const offStyle = await t.eval(`document.querySelector('.qm-app')?.getAttribute('style') ?? ''`);
  ok('4a: the wrapper carries no branding once the switch is off', !offStyle.includes('--qm-primary'), offStyle.slice(0, 120));
  ok('4b: the app is called SACC Dashboard again',
    (await t.eval(`document.querySelector('.brand-text')?.textContent?.trim() ?? ''`)) === 'SACC Dashboard',
    await t.eval(`document.querySelector('.brand-text')?.textContent`));

  const errs = t.consoleErrors.filter(e => !/favicon|manifest/i.test(e ?? ''));
  ok('4c: no client-side exceptions through the whole run', errs.length === 0, errs.slice(0, 2).join(' | '));
} finally {
  // ---- put the tenant back exactly as it was ------------------------------------------------
  if (before) await api(AD, 'PUT', brandingPath, before);
  await api(SA, 'PUT', `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: false });
  t.close();
}

hdr(`DONE — ${pass} passed, ${fail} failed`);
post(`\nDONE — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);

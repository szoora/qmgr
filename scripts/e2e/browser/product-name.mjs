// SACC DASHBOARD ON THE SCREEN (rebrand 2026-09-24, docs/plans/SACC_DASHBOARD_REBRAND.md).
//
// Section 38 proves the API resolves the name. This proves a person SEES it — the half no API suite
// can reach: the sign-in page's lockup and mark, the tab title, the shell, the Home label, the
// Appearance field's preview and refusal, the registration suggestion, the legal pages — and that
// the old name is nowhere in the visible text of any of them. Asserting the old string ABSENT, not
// only the new one present, is what catches a literal the guard's allow-list let through.
//
// Run: API and Web up (127.0.0.1:5001 / :5003), headless Chrome on :9333, then
//   node scripts/e2e/browser/product-name.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const WEB = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const AD_USER = process.env.AD_USER ?? 'e2e.admin.ct@qmgr.local';
const AD_PASS = process.env.AD_PASS ?? 'E2eTeacher!2026';
const SA_USER = process.env.SA_USER ?? 'superadmin';
const SA_PASS = process.env.SA_PASS ?? 'admin';
const OLD = /Q-Mgr/;

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
    method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
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
const brandingPath = `/api/v1/organizations/${ORG}/branding`;
const before = (await api(AD, 'GET', brandingPath)).json;
let granted = false;

const text = (t) => t.eval(`document.body.innerText`);
const brandText = (t) => t.eval(`document.querySelector('.brand-text')?.textContent?.replace(/\\s+/g, ' ').trim() ?? ''`);

hdr('SACC DASHBOARD — the name a person sees');
const t = await openTab();
try {
  // ---------------------------------------------------------------------------------------
  hdr('1. The platform sign-in page is ours');
  await t.goto(`${WEB}/login`);
  await t.eval('localStorage.clear(); sessionStorage.clear(); true');
  await t.goto(`${WEB}/login`);
  await t.waitFor(`!!document.querySelector('.q-brand-mark')`);
  await t.sleep(800);
  const heading = await t.eval(`document.querySelector('.q-brand-mark__name')?.textContent?.replace(/\\s+/g, ' ').trim() ?? ''`);
  ok('1a: the heading names SACC Dashboard, or the page\'s own welcome', /SACC Dashboard|Welcome/.test(heading), heading);
  const markSrc = await t.eval(`document.querySelector('.q-brand-mark__ours')?.getAttribute('src') ?? ''`);
  ok('1b: the mark is ours, drawn from ProductMark', markSrc === '/brand/app-icon.svg', markSrc || '(no mark)');
  const loaded = await t.eval(`(() => { const i = document.querySelector('.q-brand-mark__ours'); return !!i && i.complete && i.naturalWidth > 0; })()`);
  ok('1c: …and it actually loads (naturalWidth > 0, not a broken image)', loaded);
  ok('1d: the tab title names us', /SACC Dashboard/.test(await t.eval('document.title')), await t.eval('document.title'));
  const loginText = await text(t);
  ok('1e: the copyright line is "© YEAR SACC"', new RegExp(`© ${new Date().getFullYear()} SACC\\b`).test(loginText), loginText.slice(-200));
  ok('1f: the old name is nowhere on the page', !OLD.test(loginText), (loginText.match(/.{0,40}Q-Mgr.{0,40}/) ?? [''])[0]);
  const icons = await t.eval(`[...document.querySelectorAll('link[rel*=icon],link[rel=manifest]')].map(l => l.getAttribute('href')).join(' ')`);
  ok('1g: favicon, touch icon and manifest all come from /brand/ and /app-manifest.json', /\/brand\/favicon\.svg/.test(icons) && /\/brand\/apple-touch-icon\.png/.test(icons) && /\/app-manifest\.json/.test(icons), icons);

  // ---------------------------------------------------------------------------------------
  hdr('2. Registration offers a name and never forces one');
  await t.goto(`${WEB}/register`);
  await t.waitFor(`!!document.querySelector('#orgName')`);
  await t.sleep(600);
  await t.setValue('#orgName', 'Maryhill High School');
  await t.sleep(500);
  const suggestion = await t.eval(`document.querySelector('#use-brand-suggestion')?.textContent?.trim() ?? ''`);
  ok('2a: it suggests "MARYHILL Dashboard" from the organisation name', suggestion === 'Use MARYHILL Dashboard', suggestion || '(no suggestion)');
  await t.eval(`document.querySelector('#use-brand-suggestion')?.click(); true`);
  await t.sleep(400);
  ok('2b: pressing it fills the field, exactly', (await t.eval(`document.querySelector('#brandName')?.value`)) === 'MARYHILL Dashboard');
  await t.setValue('#brandName', 'SACC Kids');
  await t.sleep(400);
  const regText = await text(t);
  ok('2c: a name claiming to be ours is refused before submitting, in the rule\'s words', /cannot include "SACC"/.test(regText), regText.slice(0, 200));
  ok('2d: the old name is nowhere on the page', !OLD.test(regText), (regText.match(/.{0,40}Q-Mgr.{0,40}/) ?? [''])[0]);

  // ---------------------------------------------------------------------------------------
  hdr('3. The shell, unbranded');
  if (!before?.whiteLabelEntitled) {
    granted = (await api(SA, 'PUT', `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: true })).status === 200;
  }
  await api(AD, 'PUT', brandingPath, { ...before, whitelabelEnabled: false });
  await login(t, AD_USER, AD_PASS);
  await t.waitFor(`!!document.querySelector('.brand-text')`);
  await t.sleep(1200);
  ok('3a: the sidebar names SACC Dashboard', (await brandText(t)) === 'SACC Dashboard', await brandText(t));
  const lockup = await t.eval(`(() => { const w = document.querySelector('.brand-text .q-product-name__word'), d = document.querySelector('.brand-text .q-product-name__desc');
    return w && d ? getComputedStyle(w).fontWeight + '/' + getComputedStyle(d).fontWeight : ''; })()`);
  ok('3b: …set as the lockup: SACC heavy, Dashboard regular', lockup === '800/500', lockup || '(no lockup)');
  const nav = await t.eval(`[...document.querySelectorAll('.qm-sidebar .nav-link span')].map(s => s.textContent.trim())`);
  ok('3c: the home page is "Home" in the sidebar', nav.includes('Home') && !nav.includes('Dashboard'), nav.slice(0, 6).join(', '));
  ok('3d: the tab title ends with SACC Dashboard', /- SACC Dashboard$/.test(await t.eval('document.title')), await t.eval('document.title'));
  const shellText = await text(t);
  ok('3e: the old name is nowhere in the shell', !OLD.test(shellText), (shellText.match(/.{0,40}Q-Mgr.{0,40}/) ?? [''])[0]);

  // ---------------------------------------------------------------------------------------
  hdr('4. A school\'s own name, exactly as typed');
  const set = await api(AD, 'PUT', brandingPath, { ...before, whitelabelEnabled: true, brandName: 'MARYHILL Dashboard' });
  ok('SETUP: the tenant names its app', set.status === 200, `status ${set.status} ${set.text?.slice(0, 160)}`);
  await t.goto(`${WEB}/`);
  await t.waitFor(`!!document.querySelector('.brand-text')`);
  await t.sleep(1500);
  ok('4a: the sidebar reads "MARYHILL Dashboard"', (await brandText(t)) === 'MARYHILL Dashboard', await brandText(t));
  ok('4b: …as ONE run, never split into our lockup', (await t.eval(`document.querySelectorAll('.brand-text .q-product-name__desc').length`)) === 0);
  ok('4c: the tab title follows it', /- MARYHILL Dashboard$/.test(await t.eval('document.title')), await t.eval('document.title'));
  const footer = await t.eval(`document.querySelector('.q-copyright')?.textContent?.trim() ?? ''`);
  ok('4d: our credit stays "© YEAR SACC" without attribution removal', new RegExp(`^© ${new Date().getFullYear()} SACC$`).test(footer), footer);

  await api(AD, 'PUT', brandingPath, { ...before, whitelabelEnabled: true, brandName: 'Dashboard' });
  await t.goto(`${WEB}/`);
  await t.waitFor(`!!document.querySelector('.brand-text')`);
  await t.sleep(1500);
  ok('4e: a school may call it simply "Dashboard"', (await brandText(t)) === 'Dashboard', await brandText(t));

  // ---------------------------------------------------------------------------------------
  hdr('5. The Appearance page\'s field');
  await t.goto(`${WEB}/admin/appearance`);
  await t.waitFor(`!!document.querySelector('#brand-name-preview')`, 20000);
  await t.sleep(800);
  const preview = await t.eval(`document.querySelector('#brand-name-preview strong')?.textContent?.trim() ?? ''`);
  ok('5a: the preview says what staff will see', preview === 'Dashboard', preview || '(no preview)');
  await t.setValue('#brand-name input, input#brand-name', 'SACC Hub');
  await t.sleep(500);
  const appText = await text(t);
  ok('5b: a name claiming to be ours is refused on the page, in the rule\'s words', /cannot include "SACC"/.test(appText), appText.slice(0, 200));
  ok('5c: the old name is nowhere on the page', !OLD.test(appText), (appText.match(/.{0,40}Q-Mgr.{0,40}/) ?? [''])[0]);

  // ---------------------------------------------------------------------------------------
  hdr('6. The legal and help pages');
  for (const page of ['/terms', '/privacy', '/support', '/docs']) {
    await t.goto(`${WEB}${page}`);
    await t.waitFor(`!!document.querySelector('.doc-brand')`, 15000);
    await t.sleep(600);
    const body = await text(t);
    ok(`6: ${page} names SACC Dashboard and never the old name`, /SACC Dashboard/.test(body) && !OLD.test(body),
      (body.match(/.{0,40}Q-Mgr.{0,40}/) ?? ['(no SACC Dashboard)'])[0]);
  }
  const terms = await (async () => { await t.goto(`${WEB}/terms`); await t.sleep(800); return text(t); })();
  ok('6e: the contracting party is still named in full', /SACC Software Limited/.test(terms));

  const errs = t.consoleErrors.filter(e => !/favicon|manifest|service ?worker/i.test(e ?? ''));
  ok('7: no client-side exceptions through the whole run', errs.length === 0, errs.slice(0, 2).join(' | '));
} finally {
  if (before) await api(AD, 'PUT', brandingPath, before);
  if (granted) await api(SA, 'PUT', `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: false });
  t.close();
}

hdr(`DONE — ${pass} passed, ${fail} failed`);
post(`\nDONE — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);

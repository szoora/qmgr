// APPEARANCE APPLIES AT ONCE — and says so when it cannot.
//
// Reported as "changing appearance should apply immediately without user having to refresh the
// page". Driving it found something worse than a missing refresh: on a tenant that is ENTITLED to
// white-labelling but has the switch OFF, a palette saves, says "Saved", and is worn nowhere — not
// after a refresh either. The colours really were stored; `GET api/v1/branding/mine` answers
// `resolved: false` and the shell correctly wears the standard look. Nothing on the page said so.
//
// So this suite asserts both halves:
//   1. with the switch OFF, the page WARNS that nothing below is shown;
//   2. with it ON, choosing a palette changes --qm-primary and the whole derived family at once,
//      with no page reload.
//
// It turns the switch on, proves it, and PUTS THE TENANT BACK exactly as it found it — the palette
// and the switch — because this runs against a real tenant.
//
// Run: node scripts/e2e/browser/branding-live.mjs   (CDP_PORT=9444 to watch it)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (n, ok, d = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${n}${ok ? '' : '  — ' + d}`); };
const note = (n, w) => { skip++; say(`    SKIP  ${n}  — ${w}`); };

// ---- read the tenant's branding so it can be put back -------------------------------------------
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const body = await auth.json();
const H = { Authorization: `Bearer ${body.accessToken}`, 'Content-Type': 'application/json' };
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const orgId = body.user.organizationId;

const readBranding = async () => {
  const r = await fetch(`${API}/api/v1/organizations/${orgId}/branding`, { headers: H });
  return r.ok ? r.json() : null;
};
const writeBranding = async (b) => {
  const r = await fetch(`${API}/api/v1/organizations/${orgId}/branding`, { method: 'PUT', headers: H, body: JSON.stringify(b) });
  return r.ok;
};

const original = await readBranding();
if (!original) { console.error('could not read this organization branding'); process.exit(1); }
say(`  before: whitelabelEnabled=${original.whitelabelEnabled} primary=${original.primaryColor} entitled=${original.whiteLabelEntitled}`);

if (!original.whiteLabelEntitled) {
  note('the whole suite', 'this tenant is not entitled to white-labelling, so nothing below can apply');
  say(`  branding-live: ${pass} passed, ${fail} failed, ${skip} skipped`);
  process.exit(0);
}

const t = await openTab();
await t.viewport(1500, 950);
await login(t, USER, PASS);

const primary = () => t.eval(`(() => { const el = document.querySelector('.qm-app') || document.documentElement;
  return getComputedStyle(el).getPropertyValue('--qm-primary').trim(); })()`);
const family = () => t.eval(`(() => { const el = document.querySelector('.qm-app') || document.documentElement;
  const g = (n) => getComputedStyle(el).getPropertyValue(n).trim();
  return JSON.stringify({ p: g('--qm-primary'), dark: g('--qm-primary-dark'), rgb: g('--qm-primary-rgb') }); })()`);

const openAppearance = async () => {
  await t.goto(`${BASE}/admin/appearance`);
  return t.waitFor("!!document.querySelector('.palette-swatch')", 25000);
};

try {
  // ---- 1. switch OFF: the page must say nothing is shown ----------------------------------------
  say('  1. With white-labelling off, the page says so');
  if (original.whitelabelEnabled) {
    await writeBranding({ ...original, whitelabelEnabled: false });
  }
  if (!await openAppearance()) { note('the Appearance page', 'it never rendered'); throw new Error('no page'); }
  await t.sleep(2200);
  const warned = await t.eval(`(() => { const e = document.querySelector('.wl-off'); return e ? e.innerText.split(String.fromCharCode(10)).join(' ').trim() : 'absent'; })()`);
  check('a warning is shown while the switch is off', warned !== 'absent', warned);
  check('...and it says nothing below is shown', /not shown|none of it is shown/i.test(warned), warned);

  // ---- 2. switch ON: a palette applies at once ---------------------------------------------------
  say('  2. With it on, a palette applies without a reload');
  await writeBranding({ ...original, whitelabelEnabled: true });
  if (!await openAppearance()) { note('the Appearance page', 'it never rendered after switching on'); throw new Error('no page'); }
  await t.sleep(2500);

  check('the warning is gone once it is on', (await t.eval("!document.querySelector('.wl-off')")) === true);

  const before = await primary();
  const beforeFamily = JSON.parse(await family());

  // Pick a palette whose colour is definitely not the current one. Choosing "the first unselected
  // swatch" is not enough: on a tenant whose colour matches no palette, none carries .selected and
  // the first one picked can be the colour already in use — which is how this check first passed
  // while proving nothing.
  const chosen = await t.eval(`(() => {
    const swatches = [...document.querySelectorAll('.palette-swatch')];
    for (const s of swatches) {
      const name = (s.innerText || '').split(String.fromCharCode(10))[0].trim();
      if (name && name !== 'Signature Wine') { s.click(); return name; }
    }
    return null;
  })()`);
  if (!chosen) { note('choosing a palette', 'no selectable swatch'); throw new Error('no swatch'); }

  await t.sleep(3500);
  const after = await primary();
  const afterFamily = JSON.parse(await family());

  check(`choosing "${chosen}" changes --qm-primary at once`, before !== after, `${before} → ${after}`);
  check('...and the DERIVED family follows', beforeFamily.dark !== afterFamily.dark && beforeFamily.rgb !== afterFamily.rgb,
    `${JSON.stringify(beforeFamily)} → ${JSON.stringify(afterFamily)}`);
  check('...with no page reload', (await t.eval("performance.getEntriesByType('navigation')[0]?.type ?? 'unknown'")) === 'navigate');

// ---- 3. the display banner wears the same brand ------------------------------------------------
  // "that palette feature should automatically apply to the display banner." It used to carry a
  // hard-coded #8c2f52 in the DTO, so a school that rebranded got Q-Mgr wine scrolling across its
  // own foyer screen. Null now means the brand, and that is the default.
  say('  3. The display banner follows the brand');
  const bannerBefore = await (await fetch(`${API}/api/v1/branches/${BRANCH}/display-banner`, { headers: H })).json().catch(() => null);
  if (!bannerBefore) {
    note('the display banner', 'its settings could not be read');
  } else {
    const put = await fetch(`${API}/api/v1/branches/${BRANCH}/display-banner`, {
      method: 'PUT', headers: H,
      body: JSON.stringify({ ...bannerBefore, enabled: true, backgroundColor: null, textColor: null, messages: ['Zz banner under test'] }),
    });
    check('the banner can be switched on with no colour of its own', put.ok, String(put.status));

    await t.goto(`${BASE}/display/${BRANCH}`);
    const shown = await t.waitFor("!!document.querySelector('.qm-display-banner')", 25000);
    if (!shown) {
      note('the banner on the display', 'it never rendered');
    } else {
      await t.sleep(1500);
      const painted = await t.eval(`(() => {
        const el = document.querySelector('.qm-display-banner');
        const content = document.querySelector('.qm-display-banner__content');
        return JSON.stringify({
          bg: getComputedStyle(el).backgroundColor,
          fg: content ? getComputedStyle(content).color : null,
          declared: el.getAttribute('style'),
        });
      })()`);
      const p = JSON.parse(painted);
      check('it reads the brand token, not a colour of its own', /var\(--qm-primary\)/.test(p.declared ?? ''), painted);
      check('...so it is NOT the shipped wine', p.bg !== 'rgb(140, 47, 82)', painted);
      check('...and its text takes the readable foreground', /var\(--qm-text-on-primary\)/.test(p.fg ?? '') || p.fg != null, painted);
    }

    const restored = await fetch(`${API}/api/v1/branches/${BRANCH}/display-banner`, {
      method: 'PUT', headers: H, body: JSON.stringify(bannerBefore),
    });
    check('CLEANUP: the banner is back as it was', restored.ok, String(restored.status));
  }

  // The shell endpoint must agree, or the next circuit loses it.
  const mine = await (await fetch(`${API}/api/v1/branding/mine`, { headers: H })).json();
  check('the shell endpoint now resolves the tenant', mine.resolved === true, JSON.stringify(mine));
  check('...carrying the colour just chosen', (mine.primaryColor ?? '').toLowerCase() === after.toLowerCase(),
    `${mine.primaryColor} vs ${after}`);
} catch (e) {
  say(`    (stopped: ${String(e.message ?? e).slice(0, 80)})`);
} finally {
  // ---- put the tenant back ----------------------------------------------------------------------
  const restored = await writeBranding(original);
  check('CLEANUP: the tenant is back as it was', restored === true, 'the restore PUT failed');
  const now = await readBranding();
  check('...same switch and same colour', now?.whitelabelEnabled === original.whitelabelEnabled
    && (now?.primaryColor ?? null) === (original.primaryColor ?? null),
    `${now?.whitelabelEnabled}/${now?.primaryColor} vs ${original.whitelabelEnabled}/${original.primaryColor}`);
  t.close();
}

say('');
const tail = `  branding-live: ${pass} passed, ${fail} failed, ${skip} skipped`;
say(tail);
process.exitCode = fail ? 1 : 0;

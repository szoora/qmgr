// THE CUSTOMER-FACING SCREENS IN A LANGUAGE OTHER THAN ENGLISH.
//
// The whole localisation stack existed and reached nothing: AddLocalization, UseRequestLocalization,
// the /culture/set cookie endpoint, SupportedCultures, LanguageSelector.razor and three .resx files
// carrying 28 translated strings each - and not one component injected IStringLocalizer, so no
// screen was ever in any language but English.
//
// UNDERNEATH THAT WAS A SECOND FAULT THAT ONLY THIS SUITE CAN SEE. AddLocalization was called with
// ResourcesPath = "Resources", which made the factory look for
// "Q-Mgr.Web.Resources.QMgr.Web.Resources.SharedResources" - the marker class already lives in the
// Resources namespace, and the assembly name carries a hyphen so the trim never matched. Every
// lookup missed. IStringLocalizer NEVER THROWS on a missing resource: it returns the key, and the
// keys here ARE the English text, so a completely broken lookup and a correct English render are
// byte-identical. A build, a code read and any English-only assertion all pass either way.
//
// So this suite asserts the only thing that can tell them apart: that a Luganda string actually
// appears on the page.
//
// It SEEDS A SERVICE TYPE, because the kiosk's own strings ("Tap to Get Ticket", "Waiting",
// "Minutes") live on the service cards and a branch with no services renders none of them - the
// first run passed those checks vacuously. It removes it afterwards.
//
// Run: node scripts/e2e/browser/localization.mjs   (CDP_PORT=9444 to watch it)
import { openTab } from './cdp.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (n, ok, d = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${n}${ok ? '' : '  — ' + d}`); };
const note = (n, w) => { skip++; say(`    SKIP  ${n}  — ${w}`); };
const done = () => { say(''); say(`  localization: ${pass} passed, ${fail} failed, ${skip} skipped`); process.exitCode = fail ? 1 : 0; };

// ---- seed a service type, so the kiosk has cards to render its own strings on ----------------
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${(await auth.json()).accessToken}`, 'Content-Type': 'application/json' };

const run = Date.now().toString(36).slice(-4);
let seededId = null;
// The service list is behind Core Queue. A tenant without it gets 403 here and renders no cards at
// all, so the kiosk's own card strings cannot be exercised - which is a SKIP with the reason, not a
// pass and not a failure. Granting a module to read three labels would be the heavier wrong answer.
const listRes = await fetch(`${API}/api/v1/branches/${BRANCH}/service-types`, { headers: H });
const listed = listRes.ok ? await listRes.json().catch(() => null) : null;
const existing = Array.isArray(listed) ? listed : Array.isArray(listed?.items) ? listed.items : null;

if (existing == null) {
  say(`  service types could not be read (${listRes.status}) — the card strings will be skipped`);
} else if (existing.filter(s => s.isActive !== false).length === 0) {
  const made = await fetch(`${API}/api/v1/branches/${BRANCH}/service-types`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ code: `ZZ${run}`, name: `Zz Localisation ${run}`, prefix: 'Z', estimatedServiceTime: 5, isActive: true }),
  });
  seededId = made.ok ? ((await made.json().catch(() => null))?.id ?? null) : null;
  say(`  seeded a service type (${seededId ? 'ok' : 'failed ' + made.status}) — the branch had none`);
} else {
  say(`  the branch already has ${existing.length} service type(s)`);
}
const t = await openTab();
await t.viewport(1500, 950);

const setCulture = async (code, to) => {
  await t.goto(`${BASE}/culture/set?culture=${code}&redirectUri=${encodeURIComponent(to)}`);
  await t.waitFor("document.body.innerText.length > 40", 25000);
  await t.sleep(2600);
};
const read = () => t.eval(`(() => {
  const nl = String.fromCharCode(10);
  const txt = (s) => { const e = document.querySelector(s); return e ? e.innerText.trim() : null; };
  return JSON.stringify({
    body: document.body.innerText.split(nl).map(x => x.trim()).filter(Boolean),
    welcome: txt('.welcome-section h1'),
    instruction: txt('.welcome-section p'),
    action: txt('.service-action'),
    statLabels: [...document.querySelectorAll('.stat-label')].map(e => e.innerText.trim()),
    pickers: document.querySelectorAll('.qm-language-selector').length,
    current: (document.querySelector('.qm-language-option.is-current') || {}).innerText,
    deadButtons: document.querySelectorAll('.lang-btn').length,
  });
})()`);

try {
  // ---- 1. English is the default and is unchanged --------------------------------------------
  say('  1. English, the default');
  await setCulture('en', `/kiosk/branch/${BRANCH}`);
  const en = JSON.parse(await read());
  check('the kiosk renders', en.welcome != null, JSON.stringify(en.body.slice(0, 6)));
  check('...in English', en.welcome === 'Welcome!', en.welcome ?? '(none)');
  check('the picker is there, and only one of it', en.pickers === 1, `${en.pickers} picker(s)`);
  check('...marking English as current', (en.current ?? '').trim() === 'English', en.current ?? '(none)');
  check('the inert EN/AR buttons are gone', en.deadButtons === 0, `${en.deadButtons} left`);

  const hasCards = en.action != null;
  if (!hasCards) note('the service-card strings', 'this branch renders no service cards');

  // ---- 2. Luganda ------------------------------------------------------------------------------
  say('  2. Luganda');
  await setCulture('lg', `/kiosk/branch/${BRANCH}`);
  const lg = JSON.parse(await read());
  check('the welcome is translated', lg.welcome === 'Tukusanyukidde!', lg.welcome ?? '(none)');
  check('...and so is the instruction under it',
    (lg.instruction ?? '').startsWith('Londa empeereza'), lg.instruction ?? '(none)');
  check('...the picker marks Luganda as current', (lg.current ?? '').trim() === 'Luganda', lg.current ?? '(none)');
  if (hasCards) {
    check('the service card action is translated', lg.action === 'Nyiga Ofune Tikiti', lg.action ?? '(none)');
    check('...and the two stat labels with it',
      lg.statLabels.includes('Kulinda') && lg.statLabels.includes('Eddakiika'), JSON.stringify(lg.statLabels));
  }

  // ---- 3. Swahili ------------------------------------------------------------------------------
  say('  3. Swahili');
  await setCulture('sw', `/kiosk/branch/${BRANCH}`);
  const sw = JSON.parse(await read());
  check('the welcome is translated', sw.welcome === 'Karibu!', sw.welcome ?? '(none)');
  check('...and it is NOT the Luganda one', sw.welcome !== lg.welcome, `${sw.welcome} vs ${lg.welcome}`);

  // ---- 4. The other public screens follow the same cookie --------------------------------------
  say('  4. Join the queue, and ticket status');
  await setCulture('lg', `/join/${BRANCH}`);
  const join = JSON.parse(await read());
  check('join-the-queue is translated', join.body.some(l => l === 'Yingira mu lunyiriri'),
    JSON.stringify(join.body.slice(0, 8)));
  check('...and carries the picker', join.pickers === 1, `${join.pickers} picker(s)`);

  await setCulture('lg', `/ticket/${BRANCH}`);
  const ticket = JSON.parse(await read());
  check('the ticket page is translated', ticket.body.some(l => l.includes('Yingira mu lunyiriri')),
    JSON.stringify(ticket.body.slice(0, 8)));
  check('...and carries the picker', ticket.pickers === 1, `${ticket.pickers} picker(s)`);

  // ---- 5. An untranslated string degrades to readable English, never to a key ------------------
  say('  5. What is not translated reads as English, not as a key');
  check('a screen with no translation for a line still reads it in English',
    ticket.body.some(l => /Track your ticket|Ticket number/i.test(l)),
    JSON.stringify(ticket.body.slice(0, 10)));
} catch (e) {
  say(`    (stopped: ${String(e.message ?? e).slice(0, 90)})`);
  fail++;
} finally {
  // ---- put it back ------------------------------------------------------------------------------
  await setCulture('en', '/login').catch(() => {});
  t.close();
  if (seededId) {
    const del = await fetch(`${API}/api/v1/branches/${BRANCH}/service-types/${seededId}`, { method: 'DELETE', headers: H });
    check('CLEANUP: the seeded service type is removed', del.ok, String(del.status));
  }
  done();
}

// MY ACCOUNT AND MY FILE — one home each, on one screen (2026-09-25).
//
// Reported with two screenshots: the account page stacked nine 95px tiles down a whole screen, printed
// "Choose File / No file chosen" over the avatar, offered every teacher "System Settings", and edited the
// phone number — which My file edited too, through a second writer with a different rule. The split:
//   My account  how you get in — photo, username, email, password, devices, notifications, calendar feed
//   My file     how the school knows you — contact detail (one writer) and the employment record
// A tenant without Welfare & Performance has no My file, so the SAME contact card renders on the account page.
//
// What this asserts, as the administrator of a school and of a tenant with no My Workspace:
//   1. the account page fits one 1080p screen, uses the standard band, and draws no error bar;
//   2. the native file picker is invisible (the :deep() bug) and still covers the photo to be pressed;
//   3. no "System Settings", no "Logout", and no phone field on the account page of a school;
//   4. email, password and "sign out everywhere" each open their own dialog built from shared controls;
//   5. My file carries the contact card with the phone, its confirmation state, and the employment half;
//   6. a tenant without the module gets the contact card on the account page instead.
//
// Run: WEB=http://127.0.0.1:5003 API=http://127.0.0.1:5001 node scripts/e2e/browser/account-and-file.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const SCHOOL_ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const QUEUE_ADMIN = process.env.E2E_QUEUE_USER ?? 'e2e.queue.admin@qmgr.local';
const PASSWORDS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`); };
const note = (name, why) => { skip++; say(`    SKIP  ${name}  — ${why}`); };

const passwordFor = async (email) => {
  for (const password of PASSWORDS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (r.ok) return password;
  }
  return null;
};
const ERROR_BAR = `(() => { const e = document.getElementById('blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })()`;

const t = await openTab();
await t.send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });

// ---------------------------------------------------------------------------------------------------
say('\n  A school (with My Workspace)');
const schoolPw = await passwordFor(SCHOOL_ADMIN);
if (!schoolPw) { console.error(`could not sign in as ${SCHOOL_ADMIN}`); process.exit(1); }
await login(t, SCHOOL_ADMIN, schoolPw);
await t.goto(`${BASE}/profile`);
check('the account page renders', await t.waitFor(`!!document.querySelector('.acct-page .acct-grid')`, 20000), await t.eval('location.pathname'));
await t.sleep(1500);

const page = JSON.parse(await t.eval(`JSON.stringify({
  h1: document.querySelector('.page-header h1')?.innerText.trim(),
  band: !!document.querySelector('.page-header .header-actions'),
  height: document.documentElement.scrollHeight,
  text: document.querySelector('.acct-page')?.innerText ?? '',
  phoneInputs: document.querySelectorAll('.acct-page input[type=tel], .acct-page .q-input input[placeholder*="0770"]').length,
})`));
check('1a: titled "My account" in the standard band with its action top right', page.h1 === 'My account' && page.band, JSON.stringify({ h1: page.h1, band: page.band }));
check('1b: the whole page fits one 1080p screen', page.height <= 1080, `${page.height}px`);
check('1c: no error bar', !await t.eval(ERROR_BAR));

const picker = JSON.parse(await t.eval(`(() => {
  const input = document.querySelector('.acct-photo input[type=file]');
  const label = document.querySelector('.acct-photo');
  if (!input || !label) return JSON.stringify({ present: false });
  const cs = getComputedStyle(input), ir = input.getBoundingClientRect(), lr = label.getBoundingClientRect();
  return JSON.stringify({ present: true, opacity: cs.opacity, covers: ir.width >= lr.width - 1 && ir.height >= lr.height - 1 });
})()`));
check('2a: the native file picker is invisible', picker.present && picker.opacity === '0', JSON.stringify(picker));
check('2b: …and covers the photo, so pressing the photo opens it', picker.covers === true, JSON.stringify(picker));
check('2c: no "No file chosen" text anywhere on the page', !/No file chosen|Choose File/.test(page.text), page.text.slice(0, 120));

check('3a: no "System Settings"', !/System Settings/.test(page.text));
check('3b: no "Logout" on the page (it is in the user menu)', !/\bLogout\b|\bLog out\b/.test(page.text));
check('3c: no phone field on a school\'s account page — contact is on My file', page.phoneInputs === 0 && /On My file/.test(page.text), `${page.phoneInputs} phone input(s)`);

// 4. Each dialog opens, built from shared controls.
const openDialog = async (buttonText) => {
  await t.eval(`(() => { const b = [...document.querySelectorAll('.acct-page button')].find(x => x.innerText.trim() === ${JSON.stringify(buttonText)}); if (b) b.click(); return !!b; })()`);
  await t.waitFor(`!!document.querySelector('.q-modal, .modal.show, [role=dialog]')`, 5000);
  await t.sleep(400);
  const d = JSON.parse(await t.eval(`JSON.stringify({ title: document.querySelector('.q-modal__title, .modal-title')?.innerText.trim() ?? '',
    inputs: [...document.querySelectorAll('.q-modal input, [role=dialog] input')].map(i => i.type),
    raw: document.querySelectorAll('.q-modal input.form-control:not(.q-input__field), [role=dialog] input.form-control:not(.q-input__field)').length })`));
  await t.eval(`(() => { const b = [...document.querySelectorAll('.q-modal button, [role=dialog] button')].find(x => x.innerText.trim() === 'Cancel'); if (b) b.click(); return true; })()`);
  await t.sleep(400);
  return d;
};
const email = await openDialog('Change');
check('4a: "Change" email opens a dialog with one email field', /email/i.test(email.title) && email.inputs.includes('email'), JSON.stringify(email));
const pw = await openDialog('Change password');
check('4b: "Change password" opens a dialog with three password fields', pw.inputs.filter(x => x === 'password').length === 3, JSON.stringify(pw));
const everywhere = await openDialog('Sign out everywhere');
check('4c: "Sign out everywhere" asks first', /Sign out everywhere/.test(everywhere.title), JSON.stringify(everywhere));
check('4d: the devices section is on the page', /Phones signed in/.test(page.text));

// 5. My file.
await t.goto(`${BASE}/portal?tab=file`);
const hasFile = await t.waitFor(`!!document.querySelector('#my-details')`, 20000);
check('5a: My file carries the contact card', hasFile, await t.eval('location.pathname + location.search'));
if (hasFile) {
  await t.sleep(1200);
  const file = await t.eval(`document.querySelector('#my-details')?.innerText ?? ''`);
  check('5b: …with the phone and its confirmation state', /Phone/i.test(file) && /(Confirmed|Not confirmed|—)/.test(file), file.slice(0, 200));
  check('5c: …and the school\'s employment half, read-only', /Held by the school/i.test(file) && /Staff number/i.test(file), file.slice(0, 300));
  await t.eval(`(() => { const b = [...document.querySelectorAll('#my-details button')].find(x => x.innerText.trim() === 'Edit'); if (b) b.click(); return !!b; })()`);
  await t.sleep(700);
  const editing = await t.eval(`[...document.querySelectorAll('#my-details label')].map(l => l.innerText.trim()).join('|')`);
  check('5d: Edit opens the five contact fields, and only those', /Phone/i.test(editing) && /Emergency number/i.test(editing) && !/National|Staff number/i.test(editing), editing);
}
check('5e: no error bar', !await t.eval(ERROR_BAR));

// ---------------------------------------------------------------------------------------------------
say('\n  A tenant with no My Workspace');
const queuePw = await passwordFor(QUEUE_ADMIN);
if (!queuePw) {
  note('6', `${QUEUE_ADMIN} does not exist on this API — register a tenant with only Core Queue to run it`);
} else {
  await login(t, QUEUE_ADMIN, queuePw);
  await t.goto(`${BASE}/profile`);
  await t.waitFor(`!!document.querySelector('.acct-page .acct-grid')`, 20000);
  const contact = await t.waitFor(`!!document.querySelector('#my-details')`, 10000);
  check('6a: the contact card is on the account page', contact);
  const text = await t.eval(`document.querySelector('.acct-page')?.innerText ?? ''`);
  check('6b: …titled "Contact details", with no employment half', /Contact details/i.test(text) && !/Held by the school/i.test(text), text.slice(0, 200));
  check('6c: …and no link to a My file that does not exist', !/On My file/.test(text));
  check('6d: no error bar', !await t.eval(ERROR_BAR));
}

await t.send('Emulation.clearDeviceMetricsOverride');
say(`\naccount-and-file: ${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
process.exitCode = fail > 0 ? 1 : 0;
process.exit();

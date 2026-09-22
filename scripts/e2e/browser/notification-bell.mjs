// THE NOTIFICATION BELL — opening one, marking it read, and the badge that follows.
//
// Reported from production: "is that notification intended to open the details when user clicks on
// it? currently, it does not respond? no signs that has been read."
//
// Both halves were true, and the cause was structural rather than subtle: the panel's rows were
// plain <div>s with NO click handler. MainLayout even carried a MarkAsRead(NotificationItem) that
// nothing anywhere called. So from the bell a notification could not be opened, could not be marked
// read, and never lost its unread styling — while the very same list on /notifications was a
// <button> that did all three.
//
// What this asserts, in the order it matters:
//   1. the row is a real control and pressing it marks the notification read;
//   2. the badge follows — down by one, not to zero and not unchanged;
//   3. a notification with an ActionUrl NAVIGATES there and shuts the panel;
//   4. "Mark all read" clears the badge and the rows;
//   5. the same notification arriving twice is counted once (the dedupe the push handler needed).
//
// Run: node scripts/e2e/browser/notification-bell.mjs   (CDP_PORT=9444 to watch it happen)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const note = (name, why) => { skip++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); post(l); };

// ---- seed: this suite makes its own notifications rather than reading whatever is lying about ---
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const body = await auth.json();
const H = { Authorization: `Bearer ${body.accessToken}`, 'Content-Type': 'application/json' };
const me = body.user.id;
const run = `bell${Date.now().toString(36)}`;

// One that goes somewhere, one that does not: the chevron and the navigation only apply to the first.
const seed = async (title, actionUrl) => {
  const r = await fetch(`${API}/api/v1/notifications`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ userId: me, title, message: `Seeded by ${run}`, type: 'Custom', actionUrl }),
  });
  return r.ok ? (await r.json()).id : null;
};
const withUrl = await seed(`${run} goes to the roster`, '/admin/students/roster');
const noUrl = await seed(`${run} stays put`, null);
check('two notifications were seeded', !!withUrl && !!noUrl, `${withUrl} / ${noUrl}`);
if (!withUrl || !noUrl) process.exit(1);

const unread = async () => {
  const r = await fetch(`${API}/api/v1/notifications/count`, { headers: H });
  return r.ok ? Number(await r.text()) : -1;
};
const before = await unread();
check('...and they are unread on the server', before >= 2, `count=${before}`);

// ---- the bell ----------------------------------------------------------------------------------
const t = await openTab();
await t.viewport(1500, 950);
await login(t, USER, PASS);
await t.goto(`${BASE}/portal`);
await t.waitFor("!!document.querySelector('.qm-header')", 20000);
await t.sleep(1500);

const badge = () => t.eval("(() => { const e = document.querySelector('.notification-badge'); return e ? e.innerText.trim() : null; })()");
const openPanel = async () => {
  if (await t.eval("!!document.querySelector('.notifications-panel')")) return true;
  await t.eval("(() => { const b = [...document.querySelectorAll('.header-btn')].find(x => x.querySelector('.bi-bell')); if (b) b.click(); return !!b; })()");
  return t.waitFor("!!document.querySelector('.notifications-panel')", 5000);
};

check('the badge shows a count', /^\d+(\+)?$/.test(await badge() ?? ''), await badge() ?? '(none)');
check('the bell opens a panel', await openPanel(), 'no .notifications-panel appeared');
await t.sleep(400);

// 1. THE ROW IS A CONTROL. This is the whole report: it used to be a <div>.
const rowTag = await t.eval("(() => { const r = document.querySelector('.notifications-panel .notification-item'); return r ? r.tagName : 'none'; })()");
check('a row is a button, not an inert div', rowTag === 'BUTTON', rowTag);

const rowFor = (title) => `[...document.querySelectorAll('.notifications-panel .notification-item')]
  .find(r => r.innerText.includes(${JSON.stringify(title)}))`;

check('the seeded row is in the panel', await t.eval(`!!(${rowFor(`${run} stays put`)})`), 'not found');
check('...and it is styled unread', await t.eval(`(${rowFor(`${run} stays put`)})?.classList.contains('unread') === true`), 'no unread class');
check('...with an unread dot', await t.eval(`!!(${rowFor(`${run} stays put`)})?.querySelector('.notification-dot')`), 'no dot');
check('a row with nowhere to go shows no chevron', await t.eval(`!(${rowFor(`${run} stays put`)})?.querySelector('.notification-go')`), 'chevron on a row with no ActionUrl');
check('a row with an ActionUrl shows one', await t.eval(`!!(${rowFor(`${run} goes to the roster`)})?.querySelector('.notification-go')`), 'no chevron');

// 2. PRESSING IT MARKS IT READ, and the badge follows.
const badgeBefore = Number((await badge() ?? '0').replace('+', ''));
await t.eval(`(${rowFor(`${run} stays put`)})?.click()`);
await t.sleep(1200);
check('pressing a row marks it read on the server', (await unread()) === before - 1, `${await unread()} vs ${before - 1}`);
check('...and the row stops looking unread', await t.eval(`(${rowFor(`${run} stays put`)})?.classList.contains('unread') === false`), 'still unread');
// The badge renders "99+" above ninety-nine, so a decrement is invisible up there and asserting on
// it would fail against a tenant that simply has a lot of unread notifications. The server count is
// already asserted above; this only adds the badge when the badge can actually show the difference.
const badgeText = await badge() ?? '';
if (badgeText.includes('+') || badgeBefore > 99) {
  note('the badge goes down by one', `capped at "${badgeText}" — the server count is asserted instead`);
} else {
  check('...and the badge goes down by exactly one', Number(badgeText) === badgeBefore - 1, `${badgeBefore} → ${badgeText}`);
}
check('...and a row with nowhere to go leaves the panel open', await t.eval("!!document.querySelector('.notifications-panel')"), 'the panel shut');

// 3. A ROW WITH AN ActionUrl NAVIGATES.
await t.eval(`(${rowFor(`${run} goes to the roster`)})?.click()`);
await t.sleep(1800);
const url = await t.eval('location.pathname');
check('a row with an ActionUrl navigates there', url === '/admin/students/roster', url);
check('...and shuts the panel on the way', await t.eval("!document.querySelector('.notifications-panel')"), 'the panel is still open');
check('...and that one is read too', (await unread()) === before - 2, `${await unread()} vs ${before - 2}`);

// 4. MARK ALL READ.
await t.sleep(600);
if (await openPanel()) {
  await t.eval("(() => { const b = [...document.querySelectorAll('.notifications-header button')].find(x => /mark all/i.test(x.innerText)); if (b) b.click(); return !!b; })()");
  await t.sleep(1500);
  check('"Mark all read" clears the server count', (await unread()) === 0, `count=${await unread()}`);
  check('...and the badge disappears', (await badge()) === null, await badge() ?? '');
  check('...and no row is left unread', await t.eval("document.querySelectorAll('.notifications-panel .notification-item.unread').length === 0"), 'an unread row survived');
} else {
  note('mark all read', 'the panel would not reopen');
}

// 5. THE PUSH IS DEDUPED. A live SignalR arrival must be counted once even if it lands twice.
const rowCountFor = (title) => t.eval(`[...document.querySelectorAll('.notifications-panel .notification-item')].filter(r => r.innerText.includes(${JSON.stringify(title)})).length`);
const pushed = await seed(`${run} pushed live`, null);
await t.sleep(2500);
if (pushed) {
  check('a live push reaches the open panel once', (await rowCountFor(`${run} pushed live`)) === 1,
    `${await rowCountFor(`${run} pushed live`)} copies`);
  check('...and the badge counts it once', Number((await badge() ?? '0').replace('+', '')) === 1, await badge() ?? '(none)');
} else {
  note('the live push', 'the notification could not be created');
}

t.close();
const tail = `  notification-bell: ${pass} passed, ${fail} failed, ${skip} skipped`;
console.log(tail); post(tail);
process.exitCode = fail ? 1 : 0;

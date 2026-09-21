// THE TWO DOORS, in a browser. registration-doors-e2e.mjs proves the API; this proves the half that
// was actually the bug — the WORDS on the sign-in page and where they lead.
//
// Before 2026-09-20 the sign-in page said "Don't have an account? Create one" and that was the only
// door. A teacher at a school already using Q-Mgr read it, told the truth, and got a duplicate
// school on a trial. The join link that served them existed and was unreachable from here.
//
// Run: node scripts/e2e/browser/registration-doors.mjs   (headless Chrome on 9333)
import { openTab } from './cdp.mjs';

const BASE = 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const AD_USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const AD_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const RUN = Date.now().toString(36);
const DOMAIN = `doors-ui-${RUN}.sch.ug`;

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log('\n== ' + s + ' =='); post('\n== ' + s + ' =='); };

// ---- setup: make this tenant one that invites people at a domain, and put it back afterwards ----
const tok = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: AD_USER, password: AD_PASS }),
}).then(r => r.json()).then(j => j.accessToken);
const H = { Authorization: `Bearer ${tok}`, 'Content-Type': 'application/json' };
const api = async (method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: r.status, json: await r.json().catch(() => null) };
};

const before = (await api('GET', '/api/v1/staff-onboarding/settings')).json;
const restore = {
  joinEnabled: before?.joinEnabled ?? false,
  allowedEmailDomains: before?.allowedEmailDomains ?? [],
  requestExpiryDays: before?.requestExpiryDays,
  purgeAfterDays: before?.purgeAfterDays,
  defaultBranchId: before?.defaultBranchId,
};
await api('PUT', '/api/v1/staff-onboarding/settings', { ...restore, joinEnabled: true, allowedEmailDomains: [DOMAIN] });
const issued = await api('POST', '/api/v1/staff-onboarding/join-link/rotate', { validDays: 7 });
const code = issued.json?.code ?? issued.json?.joinCode;

const t = await openTab();
await t.viewport(1280, 900);

try {
  // ---------------------------------------------------------------- 1. the sign-in page
  // SIGNED OUT FIRST. The headless profile carries a session from whatever suite ran before, and a
  // signed-in visitor is sent straight to the dashboard — every assertion below would then be made
  // against the wrong page and pass or fail by accident.
  hdr('The sign-in page offers BOTH doors');
  await t.goto(`${BASE}/login`);
  await t.sleep(500);
  await t.eval('localStorage.clear(); sessionStorage.clear(); true');
  await t.goto(`${BASE}/login`);
  await t.waitFor(`!!document.querySelector('.login-footer')`, 20000);
  await t.sleep(800);

  const footer = await t.eval(`(() => {
    const f = document.querySelector('.login-footer');
    return {
      text: (f?.innerText || '').replace(/\\s+/g, ' ').trim(),
      links: [...(f?.querySelectorAll('a') || [])].map(a => a.getAttribute('href') + ' :: ' + a.innerText.trim()),
    };
  })()`);

  check('1a: the join door is offered', footer.links.some(l => l.startsWith('/join ')), footer.links.join(' | '));
  // The door is a BUTTON with a label, not a sentence. The prose that used to say "setting up a
  // new one?" was the fix for "Create one"; once the two doors are separate labelled buttons the
  // labels carry it, and the sentence was removed on the user's instruction the same day
  // ("buttons with descriptive icons is sufficient"). The assertion follows the design.
  check('1b: the register door is offered, and its label says ORGANISATION',
    footer.links.some(l => /^\/register .*organisation/i.test(l)), footer.links.join(' | '));
  check('1c: the old wording is gone — "Create one" told a teacher to make a second school',
    !/create one/i.test(footer.text), footer.text);

  // ---------------------------------------------------------------- 2. the join page
  hdr('/join takes a code, or a whole link');
  await t.goto(`${BASE}/join`);
  await t.waitFor(`!!document.querySelector('#code')`, 20000);
  await t.sleep(600);
  check('2a: /join renders a code box', (await t.eval(`!!document.querySelector('#code')`)) === true);
  check('2b: Continue is disabled until something is typed',
    (await t.eval(`document.querySelector('button[type=submit]')?.disabled`)) === true);

  // A whole URL is what people are sent; it must be accepted as-is.
  await t.eval(`(() => { const i = document.querySelector('#code');
    const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    set.call(i, 'https://qmgr.example.com/join/${code}?utm=email'); i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
  await t.sleep(700);
  check('2c: a pasted URL enables Continue', (await t.eval(`document.querySelector('button[type=submit]')?.disabled`)) === false);
  await t.eval(`document.querySelector('button[type=submit]')?.click()`);
  await t.waitFor(`location.pathname.startsWith('/join/')`, 15000);
  await t.sleep(1800);
  const landed = await t.eval(`JSON.stringify({ path: location.pathname, h1: document.querySelector('h1')?.innerText.trim() })`);
  check('2d: …and lands on the real join page for that school', landed.includes(code), landed);
  check('2e: …naming the school rather than a code', /Join /i.test(JSON.parse(landed).h1 ?? ''), landed);

  // ---------------------------------------------------------------- 3. the registration page
  hdr('The registration page says what it creates, and warns');
  await t.goto(`${BASE}/register`);
  await t.waitFor(`!!document.querySelector('#email')`, 20000);
  await t.sleep(900);

  const heading = await t.eval(`JSON.stringify({
    h1: document.querySelector('h1')?.innerText.trim(),
    note: document.querySelector('.auth-subnote')?.innerText.replace(/\\s+/g,' ').trim() || '',
  })`);
  const head = JSON.parse(heading);
  check('3a: the heading says this registers an ORGANISATION', /organisation|organization/i.test(head.h1 ?? ''), heading);
  // Likewise: the page opens with one line (the trial) and offers the join door as a BUTTON in the
  // footer rather than a paragraph — "14-day free trial is just sufficient", same day. What has to
  // be true is that the other door is reachable from here, not that a sentence names it.
  check('3b: …and the join door is reachable from here',
    await t.eval(`!!document.querySelector('.login-footer a[href="/join"]')`), 'no /join door in the footer');

  // A domain nobody published: no warning.
  await t.eval(`(() => { const i = document.querySelector('#email');
    const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    set.call(i, 'someone@nobody-${RUN}.example'); i.dispatchEvent(new Event('input', { bubbles: true }));
    i.dispatchEvent(new Event('change', { bubbles: true })); i.blur(); return true; })()`);
  await t.sleep(1800);
  check('3c: an unknown domain shows no warning',
    (await t.eval(`document.querySelectorAll('.auth-alert-warning').length`)) === 0);

  // The published domain: the school is named.
  await t.eval(`(() => { const i = document.querySelector('#email');
    const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    set.call(i, 'a.teacher@${DOMAIN}'); i.dispatchEvent(new Event('input', { bubbles: true }));
    i.dispatchEvent(new Event('change', { bubbles: true })); i.blur(); return true; })()`);
  await t.sleep(2200);

  const warn = await t.eval(`(() => {
    const w = document.querySelector('.auth-alert-warning');
    return JSON.stringify({
      shown: !!w,
      text: (w?.innerText || '').replace(/\\s+/g, ' ').trim(),
      hasJoinLink: !!w?.querySelector('a[href="/join"]'),
      hasContinue: [...(w?.querySelectorAll('button') || [])].some(b => /continue/i.test(b.innerText)),
      inputsStillEnabled: !document.querySelector('#orgName')?.disabled,
    });
  })()`);
  const w = JSON.parse(warn);
  check('3d: the published domain names the school', w.shown === true, warn);
  check('3e: …says it already uses Q-Mgr', /already uses Q-Mgr/i.test(w.text), w.text);
  check('3f: …offers the join link', w.hasJoinLink === true, warn);
  check('3g: …and a way to continue anyway — it WARNS, never blocks', w.hasContinue === true, warn);
  check('3h: …with the form still usable', w.inputsStillEnabled === true, warn);
  check('3i: the warning never carries the join code, which is the school\'s secret',
    !w.text.toLowerCase().includes(String(code ?? '\u0000').toLowerCase()), w.text);

  const err = await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`);
  check('4a: no Blazor error bar through any of it', err === 'none', `display: ${err}`);
} finally {
  await api('PUT', '/api/v1/staff-onboarding/settings', restore);
  const line = '    NOTE  the tenant\'s onboarding settings were put back as they were.';
  console.log(line); post(line);
  t.close();
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

// A PROFILE PHOTOGRAPH, ON EVERY SURFACE THAT SHOWS A PERSON.
//
// Reported from production: "i added a photo, but can not view it anywhere, not even in profile on
// top right and Good morning savior. i expect to find my uploaded photo. even on the staff list."
// All of that was true. The upload worked, the file was stored, it was classified correctly by
// UploadAuthorizer and it was gated correctly — and then NO DTO that any avatar reads carried it.
// UserInfo (the header), StaffMemberDto (the greeting, the directory, the staff file), UserDto (the
// users list), StaffProfileDto and RegisterRowDto were all missing the column. QAvatar had taken a
// PhotoUrl parameter since the day it was written and not one of its sixteen call sites passed one.
//
// So this suite asserts the thing that was actually broken: that the bytes reach the <img>. An API
// suite could assert the DTO field and still miss a page that does not pass it; only opening the
// page shows that.
//
// Two halves, because they fail differently:
//   1. every surface renders an <img> inside the avatar rather than initials;
//   2. that <img> actually LOADS (naturalWidth > 0) — a signed link whose token is wrong, expired
//      or missing renders as an <img> all the same, and QAvatar then removes it on error. A suite
//      that only counted <img> elements would pass against a 404.
//
// Run: node scripts/e2e/browser/profile-photo.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const ADMIN_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const note = (name, why) => { skip++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); post(l); };

// ---- sign in to the API too: the photo is uploaded through it -----------------------------------
const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: ADMIN, password: ADMIN_PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }
const authBody = await auth.json();
const token = authBody.accessToken ?? authBody.token;
const user = authBody.user;
const me = user.id;
const MY_NAME = user.fullName ?? user.username;
const bearer = { Authorization: `Bearer ${token}` };

// A real PNG, built here rather than committed: 8x8 solid, ~100 bytes. The storage layer refuses a
// type that is on no allow-list and checks the bytes, so this has to be a genuine PNG.
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAgAAAAIAQMAAAD+wSzIAAAABlBMVEX///+/v7+jQ3Y5AAAADklEQVQI12P4AIX8EAgALgAD/aNpbtEAAAAASUVORK5CYII=',
  'base64');

const form = new FormData();
form.append('file', new Blob([PNG], { type: 'image/png' }), 'e2e-avatar.png');
const up = await fetch(`${API}/api/v1/profile/photo`, { method: 'POST', headers: bearer, body: form });
check('the photo uploads', up.ok, `${up.status} ${await up.clone().text().catch(() => '')}`);
if (!up.ok) process.exit(1);
const { photoUrl } = await up.json();
check('the upload answers with a SIGNED link', !!photoUrl && photoUrl.includes('?t='), photoUrl ?? '(none)');

// ---- the gate itself ----------------------------------------------------------------------------
// A staff photo is a gated upload. Anonymous with no token must be refused, and the refusal is 401
// for an anonymous caller — never 403, which would confirm the file exists.
const bare = photoUrl.split('?')[0];
const anon = await fetch(bare);
check('anonymous and unsigned is refused', anon.status === 401, `got ${anon.status}`);
const signed = await fetch(photoUrl);
check('the signed link serves the bytes', signed.ok && signed.headers.get('content-type')?.startsWith('image/'),
  `${signed.status} ${signed.headers.get('content-type')}`);

// ---- every DTO that an avatar reads now carries it ----------------------------------------------
const get = async (path) => { const r = await fetch(`${API}${path}`, { headers: bearer }); return r.ok ? r.json() : null; };

const meInfo = await get('/api/v1/auth/me');
check('auth/me carries a signed photo', !!meInfo?.photoUrl?.includes('?t='), meInfo?.photoUrl ?? '(none)');

const profile = await get('/api/v1/profile');
check('profile carries a signed photo', !!profile?.photoUrl?.includes('?t='), profile?.photoUrl ?? '(none)');

const users = await get('/api/v1/users?pageSize=200');
const rows = Array.isArray(users) ? users : users?.items ?? [];
const mine = rows.find(u => u.id === me);
check('the users list carries a signed photo', !!mine?.photoUrl?.includes('?t='), mine?.photoUrl ?? '(none)');

// ---- and the pages actually render it ------------------------------------------------------------
const t = await openTab();
await t.viewport(1600, 1000);
await login(t, ADMIN, ADMIN_PASS);

// An avatar's photo is an <img> inside .q-avatar. "Loaded" is naturalWidth, not presence: QAvatar
// removes the element on error, so a 404 leaves initials and no <img> at all — but a slow one is
// still in the DOM with naturalWidth 0, and counting elements would call that a pass.
const avatarProbe = (scope) => `(() => {
  const root = document.querySelector(${JSON.stringify(scope)});
  if (!root) return { found: false };
  const imgs = [...root.querySelectorAll('.q-avatar img.q-avatar__img')];
  return {
    found: true,
    imgs: imgs.length,
    loaded: imgs.filter(i => i.naturalWidth > 0).length,
    src: imgs[0]?.getAttribute('src') ?? null,
  };
})()`;

const surface = async (name, url, scope, ready) => {
  await t.goto(`${BASE}${url}`);
  const ok = await t.waitFor(ready, 20000);
  if (!ok) { note(name, `the page never showed ${ready}`); return; }
  // The browser needs a moment to fetch the images themselves.
  await t.waitFor(`(${avatarProbe(scope)}).loaded > 0`, 8000);
  const r = await t.eval(avatarProbe(scope));
  if (!r.found) { note(name, `no ${scope} on the page`); return; }
  check(`${name}: renders a photo, not initials`, r.imgs > 0, 'the avatar fell back to initials');
  check(`${name}: the photo actually loads`, r.loaded > 0, `src=${r.src}`);
};

// The header is on every page, so it is checked once on the portal.
await surface('header', '/portal', '.qm-header', `!!document.querySelector('.qm-header .q-avatar')`);
await surface('my workspace greeting', '/portal', '.portal-greeting, .qm-main',
  `!!document.querySelector('.qm-main .q-avatar')`);
await surface('profile', '/profile', '.acct-id',
  `!!document.querySelector('.acct-id')`);

// THE TWO LISTS HAVE TO BE FILTERED TO THIS PERSON FIRST. The dev tenant carries 143 people and the
// lists show 25 and 10 of them; without narrowing, the probe reads twenty-five colleagues who have
// no photograph and reports a bug that is not there. Measuring the wrong rows is the trap here, not
// the feature.
const narrow = async (name, url, setFilter) => {
  await t.goto(`${BASE}${url}`);
  if (!await t.waitFor(`!!document.querySelector('.qm-main .q-avatar')`, 20000)) {
    note(name, 'the list never rendered'); return;
  }
  if (!await t.eval(setFilter)) { note(name, 'no filter control on the page'); return; }
  const found = await t.waitFor(
    `[...document.querySelectorAll('.qm-main .q-avatar')].length > 0 && document.querySelector('.qm-main').innerText.includes(${JSON.stringify(MY_NAME)})`, 10000);
  if (!found) { note(name, `could not narrow the list to ${MY_NAME}`); return; }
  await t.waitFor(`document.querySelectorAll('.qm-main .q-avatar img.q-avatar__img').length > 0`, 8000);
  const r = await t.eval(avatarProbe('.qm-main'));
  check(`${name}: renders a photo, not initials`, r.imgs > 0, 'the avatar fell back to initials');
  check(`${name}: the photo actually loads`, r.loaded > 0, `src=${r.src}`);
};

// The directory's own search box, set the way Blazor needs (the native value setter plus an input
// event — a plain assignment does not reach @bind).
await narrow('staff directory', '/admin/staff', `(() => {
  const el = document.querySelector('.sd-search input');
  if (!el) return false;
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(MY_NAME)});
  el.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
})()`);

// Users & Roles is a Radzen grid showing ten of 143 at a time. Its own filter box is a Radzen
// component whose binding a synthetic input event does not reach, so this pages to the row instead
// — which is what a person would do, and is not tied to Radzen's internals.
await t.goto(`${BASE}/admin/users`);
if (!await t.waitFor(`!!document.querySelector('.qm-main .q-avatar')`, 20000)) {
  note('users & roles', 'the list never rendered');
} else {
  let onPage = false;
  for (let page = 0; page < 20 && !onPage; page++) {
    onPage = await t.eval(`[...document.querySelectorAll('.user-name')].some(e => e.textContent.trim() === ${JSON.stringify(MY_NAME)})`);
    if (onPage) break;
    const moved = await t.eval(`(() => {
      const next = document.querySelector('.rz-pager .rz-paginator-next, .rz-pager .rz-pager-next');
      if (!next || next.classList.contains('rz-state-disabled')) return false;
      next.click(); return true;
    })()`);
    if (!moved) break;
    await t.sleep(700);
  }
  if (!onPage) { note('users & roles', `${MY_NAME} was on no page of the grid`); }
  else {
    await t.waitFor(`document.querySelectorAll('.qm-main .q-avatar img.q-avatar__img').length > 0`, 8000);
    const r = await t.eval(avatarProbe('.qm-main'));
    check('users & roles: renders a photo, not initials', r.imgs > 0, 'the avatar fell back to initials');
    check('users & roles: the photo actually loads', r.loaded > 0, `src=${r.src}`);
  }
}

// The profile page is also the only place a photo can be CHANGED once onboarding is finished.
await t.goto(`${BASE}/profile`);
await t.waitFor(`!!document.querySelector('.acct-id')`, 20000);
// The account page was rebuilt 2026-09-25: the photo is the picker (.acct-photo), and its native input is invisible.
const changer = await t.eval(`!!document.querySelector('.acct-photo input[type="file"]')`);
check('profile offers a way to change the photo', changer, 'no file input beside the avatar');

// An expired or wrong token must not leave a broken-image glyph on the page.
const broken = await t.eval(`(async () => {
  const el = document.querySelector('.qm-header .q-avatar');
  if (!el) return 'no avatar';
  const img = el.querySelector('img');
  if (!img) return 'no img';
  img.dispatchEvent(new Event('error'));
  await new Promise(r => setTimeout(r, 100));
  return el.querySelector('img') ? 'still there' : (el.textContent.trim() ? 'initials' : 'empty');
})()`);
check('a failed photo falls back to initials', broken === 'initials', broken);

t.close();

const tail = `  profile-photo: ${pass} passed, ${fail} failed, ${skip} skipped`;
console.log(tail); post(tail);
process.exitCode = fail ? 1 : 0;

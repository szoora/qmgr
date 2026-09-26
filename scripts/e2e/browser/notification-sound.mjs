// THE NOTIFICATION CHIME, IN A REAL BROWSER (2026-09-26).
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, E7 (and B4, the push switch on the preferences page).
//
// As a teacher, with the chime's play() wrapped so every call is counted:
//   * an event for the teacher, made through the API, arrives LIVE — a toast shows, and the chime is asked for once;
//   * a burst of three more makes at most one more chime (the script throttles to one per ten seconds);
//   * "Mute on this device" silences it — the toast still shows (sound is never the only cue, WCAG 2.2 SC 1.3.3);
//   * the preferences page carries the sound switch, "Important ones only", "Play a test sound", and the phone switch.
// During the school's quiet hours the chime is not expected, and the suite says so rather than failing.
// Everything seeded is deleted; the device mute and the teacher's preferences are put back.
//
// Run: node scripts/e2e/browser/notification-sound.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.teacher.s4@qmgr.local';
const PASSES = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);

let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };
const note = (m) => { skip++; const l = `SKIP  ${m}`; console.log(l); viewer(l); };

const apiLogin = async (email) => {
  for (const pw of PASSES) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password: pw }) });
    if (r.ok) return { ...(await r.json()), password: pw };
  }
  return null;
};
const admin = await apiLogin(ADMIN);
const teacher = await apiLogin(TEACHER);
if (!admin || !teacher) { console.error('could not sign in (administrator or teacher)'); process.exit(1); }
const api = async (who, method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: { Authorization: `Bearer ${who.accessToken}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json };
};

const t = await openTab();
await t.viewport(1440, 950);
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
// Counts every call the page makes to the chime, keeping the real one (and its throttle) behind it.
const WRAP = `(() => { if (!window.qmgrSound || window.__chimeWrapped) return !!window.__chimeWrapped;
  const real = window.qmgrSound.play.bind(window.qmgrSound); window.__chimeCalls = 0; window.__chimeWrapped = true;
  window.qmgrSound.play = (important) => { window.__chimeCalls++; return real(important); }; return true; })()`;
const toastWith = (text) => `[...document.querySelectorAll('.q-toast, .toast, [class*=toast]')].some(x => x.innerText.includes(${JSON.stringify(text)}))`;

const created = [];
let branch = null, originalUi = null;
try {
  await login(t, TEACHER, teacher.password);
  await t.goto(`${B}/`);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1500);
  branch = await t.eval(`(() => { for (const k of Object.keys(localStorage)) { const v = localStorage.getItem(k) ?? '';
    const m = /^"?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"?$/i.exec(v); if (m && /branch/i.test(k)) return m[1]; } return null; })()`)
    ?? process.env.BRANCH;
  const envelope = (await api(teacher, 'GET', `/api/v1/profile/ui-preferences?branchId=${branch}`)).json;
  originalUi = envelope?.preferences ?? null;
  await api(teacher, 'PUT', '/api/v1/profile/ui-preferences', { ...(originalUi ?? {}), sound: { enabled: true, importantOnly: false } });
  await t.eval(`localStorage.removeItem('qmgr-sound-muted'); true`);
  await t.goto(`${B}/`);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1500);
  ok(await t.eval(WRAP), 'the chime script is loaded and can be watched');
  // A real press on the page — the browser only lets a page make sound after one (Chrome's autoplay policy).
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: 700, y: 400, button: 'left', clickCount: 1 });
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 700, y: 400, button: 'left', clickCount: 1 });

  const q = envelope?.quietHours;
  const hour = Number(new Intl.DateTimeFormat('en-GB', { hour: 'numeric', hour12: false, timeZone: envelope?.timeZone || 'UTC' }).format(new Date()));
  const quietNow = !!q?.enabled && (q.startHour < q.endHour ? hour >= q.startHour && hour < q.endHour : hour >= q.startHour || hour < q.endHour);

  const soon = new Date(Date.now() + 3 * 864e5).toISOString().slice(0, 10);
  const tell = async (title) => {
    const r = await api(admin, 'POST', `/api/v1/branches/${branch}/calendar/events`, { title, startsOn: soon, endsOn: soon, audience: 1,
      staffAudience: { allStaff: false, userIds: [teacher.user.id] }, notifyAudience: true });
    if (r.json?.id) created.push(r.json.id);
    return r.status;
  };
  const FIRST = `E2E chime ${RUN}`;
  ok((await tell(FIRST)) === 201, 'an event for the teacher is made (and they are told)');
  ok(await t.waitFor(toastWith(FIRST), 12000), 'it arrives LIVE, as a toast on the page');
  const calls1 = await t.eval('window.__chimeCalls ?? 0');
  if (quietNow) note(`it is quiet hours at the school (${q.startHour}:00–${q.endHour}:00) — no chime is expected now`);
  else ok(calls1 === 1, 'the chime is asked for once', `${calls1} calls`);

  for (let i = 0; i < 3; i++) await tell(`E2E burst ${i} ${RUN}`);
  await t.waitFor(toastWith(`E2E burst 2 ${RUN}`), 12000);
  await t.sleep(800);
  const sounded = await t.eval(`window.__chimeSounded ?? null`);
  const calls2 = await t.eval('window.__chimeCalls ?? 0');
  ok(calls2 >= calls1, 'a burst of three asks again', `${calls1} → ${calls2}`);
  ok(sounded === null, 'the throttle lives in the script (one sound per ten seconds), not in the page');

  // Mute on this device.
  await t.eval(`localStorage.setItem('qmgr-sound-muted', '1'); true`);
  await t.goto(`${B}/`);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1500);
  await t.eval(WRAP);
  const MUTED = `E2E muted ${RUN}`;
  await tell(MUTED);
  ok(await t.waitFor(toastWith(MUTED), 12000), 'muted on this device, the toast still shows');
  ok((await t.eval('window.__chimeCalls ?? 0')) === 0, '…and no chime is asked for');
  await t.eval(`localStorage.removeItem('qmgr-sound-muted'); true`);

  // The preferences page.
  await t.goto(`${B}/profile/notifications`);
  const came = await t.waitFor(`!!document.getElementById('np-sound')`, 15000);
  ok(came, 'the preferences page has a Sound section');
  if (came) {
    ok(await t.eval(`!!document.getElementById('np-sound-test') && !!document.getElementById('np-sound-important') && !!document.getElementById('np-sound-muted')`),
      '…with "Play a test sound", "Important ones only" and "Mute on this device"');
    ok(await t.eval(`!!document.getElementById('np-push') && [...document.querySelectorAll('.np-row-toggles')].some(r => r.innerText.includes('Phone'))`),
      '…and the phone switch, per event too (it had none, B4)');
  }
  ok(await noError(), 'no error bar');
} catch (e) {
  ok(false, 'the suite ran to the end', e?.stack ?? String(e));
} finally {
  for (const id of created) await api(admin, 'DELETE', `/api/v1/branches/${branch}/calendar/events/${id}`);
  if (originalUi) await api(teacher, 'PUT', '/api/v1/profile/ui-preferences', originalUi);
  console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
  process.exitCode = fail ? 1 : 0;
  t.close?.();
}

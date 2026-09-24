// SECTION 39 — MOVING A PERIOD ON THE SCHOOL DAY (2026-09-24).
//
// Asked as "how to edit school day. i need to move p4 to before break. ui has no way to do this". The server already
// sorted periods by start time; what was missing was (1) the page following the times as they change, and (2) lessons
// already on staff calendars following the new time — a lesson's time is copied into its duty when it is generated,
// and saving the school day did not regenerate, so a teacher's day went on saying the old time until the 01:00 run.
//
//   39.1  a period moved before the one ahead of it is accepted, sent in its OLD list position, and comes back sorted
//   39.2  a lesson already generated in that period moves to the new time, without anybody pressing Generate
//   39.3  an overlap is still refused
//   39.4  everything is put back: the school day, the lessons, the seeded week
//
// It makes its own live week (browser/seed-timetable.mjs) and never leaves the school day changed.
//
// Run: node scripts/e2e/school-day-order-e2e.mjs   (or through class-teacher-e2e.sh)
import { seedLiveTimetable } from './browser/seed-timetable.mjs';

const API = process.env.API || 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH || 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const PASSWORDS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

let pass = 0, fail = 0;
const failures = [];
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=api', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (name) => { pass++; const l = `  \x1b[32mPASS\x1b[0m  ${name}`; console.log(l); viewer(l); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  const l = `  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 400)}`;
  console.log(l); viewer(l);
};
const hdr = (t) => { console.log(`\n\x1b[1m${t}\x1b[0m`); viewer(t); };
const eq = (name, actual, expected) => (actual === expected ? ok(name) : bad(name, expected, actual));
const truthy = (name, cond, detail = '') => (cond ? ok(name) : bad(name, 'true', `false ${detail}`));
const skip = (what, why) => { const l = `  \x1b[33mSKIP\x1b[0m  ${what} — ${why}`; console.log(l); viewer(l); };

async function call(token, method, path, body) {
  const h = { Authorization: `Bearer ${token}` };
  if (body !== undefined) h['Content-Type'] = 'application/json';
  const res = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text };
}
async function signIn(email) {
  for (const password of PASSWORDS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, id: j.user?.id ?? j.userId }; }
  }
  return null;
}
const toMin = (hhmm) => { const [h, m] = hhmm.split(':').map(Number); return h * 60 + m; };
const toHm = (min) => `${String(Math.floor(min / 60)).padStart(2, '0')}:${String(min % 60).padStart(2, '0')}`;
const waitFor = async (fn, ms = 60000) => { const end = Date.now() + ms; while (Date.now() < end) { const v = await fn(); if (v) return v; await new Promise((r) => setTimeout(r, 1500)); } return null; };

const admin = await signIn('e2e.admin.ct@qmgr.local');
const teacher = await signIn('e2e.sp.math1@qmgr.local');
if (!admin || !teacher) { console.error('the administrator or e2e.sp.math1 could not sign in — run section 14 first'); process.exit(1); }

const B = `/api/v1/branches/${BRANCH}`;
const SETTINGS = `${B}/timetable/settings`;
const LS = `${B}/staff/lessons`;
const branchInfo = (await call(admin.token, 'GET', B)).json ?? {};
const TZ = branchInfo.timezone ?? branchInfo.timeZone ?? 'Africa/Kampala';
const local = (d) => Object.fromEntries(new Intl.DateTimeFormat('en-GB', { timeZone: TZ, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', weekday: 'long' })
  .formatToParts(new Date(d)).map((p) => [p.type, p.value]));
const hmOf = (d) => { const p = local(d); return `${p.hour}:${p.minute}`; };
const dateOf = (d) => { const p = local(d); return `${p.year}-${p.month}-${p.day}`; };
const DOW = { Sunday: 0, Monday: 1, Tuesday: 2, Wednesday: 3, Thursday: 4, Friday: 5, Saturday: 6 };
const DOW_NAME = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

const original = (await call(admin.token, 'GET', SETTINGS)).json;
if (!original?.dayTypes) { console.error('could not read the school day'); process.exit(1); }
const lessonsOf = async () => ((await call(admin.token, 'GET',
  `${LS}?teacherUserId=${teacher.id}&from=${encodeURIComponent(new Date().toISOString())}&to=${encodeURIComponent(new Date(Date.now() + 8 * 86400000).toISOString())}`)).json?.items ?? []);

let seeded = null, changed = false;
try {
  hdr('39. Moving a period on the school day');
  seeded = await seedLiveTimetable({ API, BRANCH, token: admin.token, teacherId: teacher.id, label: 'school-day' });
  if (!seeded.target) { skip('section 39', `no version this teacher teaches in could be put in force (${seeded.reason})`); throw 'skip'; }
  eq('setup: lessons are generated for the live week', (await call(admin.token, 'POST', `${LS}/generate`)).status, 200);

  // A lesson a day or more ahead, so "now" can never overtake it during the run.
  const ahead = (await lessonsOf()).filter((l) => l.status === 'Scheduled' && new Date(l.startsAt).getTime() > Date.now() + 26 * 3600_000 && l.periodLabel)
    .sort((a, b) => a.startsAt.localeCompare(b.startsAt));
  const target = ahead[0];
  if (!target) { skip('39.1–39.2', 'the teacher has no lesson a day or more ahead in the seeded week'); throw 'skip'; }

  // The period it is in, on the day type that covers its weekday, and the period right before it in time.
  const weekday = DOW[local(target.startsAt).weekday];
  const dayType = original.dayTypes.find((t) => (t.days ?? []).some((d) => (typeof d === 'number' ? d : DOW[d]) === weekday));
  const sorted = [...(dayType?.periods ?? [])].sort((a, b) => toMin(a.start) - toMin(b.start));
  const k = sorted.findIndex((p) => p.label === target.periodLabel);
  if (!dayType || k <= 0) { skip('39.1–39.2', `"${target.periodLabel}" is the first period of its day or has no day type`); throw 'skip'; }
  const K = sorted[k], J = sorted[k - 1];
  const lenK = toMin(K.end) - toMin(K.start), lenJ = toMin(J.end) - toMin(J.start);
  const newK = { start: toHm(toMin(J.start)), end: toHm(toMin(J.start) + lenK) };
  const newJ = { start: newK.end, end: toHm(toMin(newK.end) + lenJ) };
  console.log(`  moving ${K.label} (${K.start}–${K.end}) before ${J.label} (${J.start}–${J.end}): ${K.label} ${newK.start}–${newK.end}, ${J.label} ${newJ.start}–${newJ.end}`);

  // ---------------------------------------------------------------------------------------------
  hdr('39.1 The move is accepted in its old position and comes back in time order');
  const moved = structuredClone(original);
  const mt = moved.dayTypes.find((t) => t.key === dayType.key);
  for (const p of mt.periods) {
    if (p.key === K.key) Object.assign(p, newK);
    if (p.key === J.key) Object.assign(p, newJ);
  }
  // Sent exactly as a reader would leave it: K still BELOW J in the list, earlier in time.
  truthy(`${K.label} is still listed after ${J.label} in what is sent`, mt.periods.findIndex((p) => p.key === K.key) > mt.periods.findIndex((p) => p.key === J.key));
  const saved = await call(admin.token, 'PUT', SETTINGS, moved);
  eq('the school day is saved', saved.status, 200);
  changed = saved.status === 200;
  const back = saved.json?.dayTypes?.find((t) => t.key === dayType.key)?.periods ?? [];
  truthy(`…and ${K.label} now comes before ${J.label}`, back.findIndex((p) => p.key === K.key) < back.findIndex((p) => p.key === J.key), back.map((p) => p.label).join(', '));
  eq(`…keeping its key, so its lessons move with it`, back.find((p) => p.label === K.label)?.key, K.key);

  // ---------------------------------------------------------------------------------------------
  hdr('39.2 The lesson already on the calendar moves, with nobody pressing Generate');
  const day = dateOf(target.startsAt);
  const movedLesson = await waitFor(async () => (await lessonsOf()).find((l) => dateOf(l.startsAt) === day && l.periodLabel === K.label
    && l.status === 'Scheduled' && hmOf(l.startsAt) === newK.start));
  truthy(`the ${K.label} lesson on ${DOW_NAME[weekday]} is now at ${newK.start} (it was ${hmOf(target.startsAt)})`, !!movedLesson,
    JSON.stringify((await lessonsOf()).filter((l) => dateOf(l.startsAt) === day).map((l) => [l.periodLabel, hmOf(l.startsAt), l.status])));
  const stillOld = (await lessonsOf()).filter((l) => dateOf(l.startsAt) === day && l.periodLabel === K.label && l.status === 'Scheduled' && hmOf(l.startsAt) === hmOf(target.startsAt));
  eq('…and nothing is left at the old time', stillOld.length, 0);

  // ---------------------------------------------------------------------------------------------
  hdr('39.3 An overlap is still refused');
  const clash = structuredClone(original);
  const ct = clash.dayTypes.find((t) => t.key === dayType.key);
  const ck = ct.periods.find((p) => p.key === K.key);
  ck.start = toHm(toMin(J.start) + 1); ck.end = toHm(toMin(J.start) + 1 + lenK);
  const refused = await call(admin.token, 'PUT', SETTINGS, clash);
  truthy('two periods that overlap are refused, naming the period', refused.status === 400 && /overlaps/i.test(refused.text), `${refused.status} ${refused.text.slice(0, 160)}`);
} catch (e) {
  if (e !== 'skip') bad('the suite ran to the end', 'no exception', e?.stack ?? String(e));
} finally {
  hdr('39.4 Put things back');
  if (changed) {
    const r = await call(admin.token, 'PUT', SETTINGS, original);
    console.log(`  school day restored (${r.status})`);
    console.log(`  lessons regenerated (${(await call(admin.token, 'POST', `${LS}/generate`)).status})`);
  }
  if (seeded?.cleanup) await seeded.cleanup();
}

console.log(`\n  section 39: ${pass} passed, ${fail} failed`);
if (failures.length) console.log('  failed: ' + failures.join(' | '));
process.exitCode = fail ? 1 : 0;

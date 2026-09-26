// SECTION 46 — THE SCHOOL'S OWN PERIOD TYPES (2026-09-26).
//
// Asked as "where is the ui to add a kind? we only have Assembly, Lesson, Break", then "can this be a dynamic feature?".
// A period's kind was a fixed list of three because each one is a behaviour. It is now the school's own list of types, and
// each type carries the two switches the code acts on: lessons can be placed in it, and it shows on a teacher's own
// timetable. TimetableCycle.ApplyPeriodTypes turns a type into the kind every older reader asks for, in one place.
//
//   46.1  the school day carries Lesson / Break / Assembly types, and every period names one
//   46.2  a document with no types (as stored before today) is understood: the types are filled in from the kinds
//   46.3  a type the school adds (Games) is saved, and a period given it becomes non-teaching
//   46.4  the kind is DERIVED — a client claiming "Lesson" on a Break period is overruled
//   46.5  a type that allows lessons makes its period a teaching period
//   46.6  refusals: a type in use removed, two types with one name, no type that allows lessons, a period with no type
//   46.7  a period with PUBLISHED lessons cannot stop taking lessons — by changing its type, or by switching the type off
//   46.8  everything is put back
//
// Run: node scripts/e2e/period-types-e2e.mjs   (or through class-teacher-e2e.sh)
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

const admin = await signIn('e2e.admin.ct@qmgr.local');
const teacher = await signIn('e2e.sp.math1@qmgr.local');
if (!admin || !teacher) { console.error('the administrator or e2e.sp.math1 could not sign in — run section 14 first'); process.exit(1); }

const SETTINGS = `/api/v1/branches/${BRANCH}/timetable/settings`;
const original = (await call(admin.token, 'GET', SETTINGS)).json;
if (!original?.dayTypes) { console.error('could not read the school day'); process.exit(1); }
// The server's own answer to a save of what it handed out — the baseline every later change starts from. Saving it first
// also means "put back" returns the school day to exactly what it was, types and all.
const kindOf = (k) => (typeof k === 'number' ? ['Lesson', 'Break', 'Assembly'][k] : k);
const periods = (s) => s.dayTypes.flatMap((d) => d.periods);
const firstNonTeaching = (s) => periods(s).find((p) => kindOf(p.kind) !== 'Lesson');

let changed = false, seeded = null;
const save = async (doc) => { const r = await call(admin.token, 'PUT', SETTINGS, doc); if (r.status === 200) changed = true; return r; };

try {
  hdr('46.1 The school day carries its period types');
  const names = (original.periodTypes ?? []).map((t) => t.name);
  truthy('there is a list of period types', names.length >= 3, JSON.stringify(names));
  truthy('…at least one of which allows lessons', (original.periodTypes ?? []).some((t) => t.teaching), JSON.stringify(original.periodTypes));
  const known = new Set((original.periodTypes ?? []).map((t) => t.key));
  truthy('every period names a type on that list', periods(original).every((p) => known.has(p.type)),
    JSON.stringify(periods(original).filter((p) => !known.has(p.type)).map((p) => [p.key, p.type])));

  hdr('46.2 A document with no types — as stored before today — is understood');
  const legacy = structuredClone(original);
  delete legacy.periodTypes;
  for (const p of periods(legacy)) delete p.type;
  const legacySaved = await save(legacy);
  eq('it saves', legacySaved.status, 200);
  const ls = legacySaved.json ?? { dayTypes: [], periodTypes: [] };
  truthy('…with the three starting types filled in', ['Lesson', 'Break', 'Assembly'].every((n) => (ls.periodTypes ?? []).some((t) => t.name === n)), JSON.stringify(ls.periodTypes));
  truthy('…and every period given the type that behaves as its kind did',
    periods(ls).every((p) => { const t = ls.periodTypes.find((x) => x.key === p.type); return t && kindOf(p.kind) === (t.teaching ? 'Lesson' : t.onPersonalTimetable ? 'Break' : 'Assembly'); }),
    JSON.stringify(periods(ls).map((p) => [p.key, p.type, kindOf(p.kind)])));
  truthy('…and no period changed kind on the way', periods(ls).every((p) => kindOf(p.kind) === kindOf(periods(original).find((o) => o.key === p.key && o.start === p.start)?.kind ?? p.kind)));

  hdr('46.3 A type the school adds');
  const base = ls;
  const target = firstNonTeaching(base);
  if (!target) { skip('46.3–46.5', 'the school day has no non-teaching period to retype'); throw 'skip'; }
  const withGames = structuredClone(base);
  withGames.periodTypes.push({ key: 'e2e-games', name: 'Games', teaching: false, onPersonalTimetable: true, isRetired: false });
  for (const p of periods(withGames)) if (p.key === target.key) p.type = 'e2e-games';
  const gamesSaved = await save(withGames);
  eq('a new type, Games, is saved', gamesSaved.status, 200);
  const g = gamesSaved.json ?? { dayTypes: [] };
  truthy('…and is on the list', (g.periodTypes ?? []).some((t) => t.key === 'e2e-games' && t.name === 'Games'));
  const gp = periods(g).find((p) => p.key === target.key);
  eq(`${target.label} is of type Games`, gp?.type, 'e2e-games');
  eq('…and does not take lessons (its kind is Break)', kindOf(gp?.kind), 'Break');

  hdr('46.4 The kind is derived from the type, never taken from the client');
  const lying = structuredClone(g);
  for (const p of periods(lying)) if (p.key === target.key) p.kind = 'Lesson';
  const lyingSaved = await save(lying);
  eq('it saves', lyingSaved.status, 200);
  eq(`…and ${target.label} is still not a teaching period`, kindOf(periods(lyingSaved.json ?? { dayTypes: [] }).find((p) => p.key === target.key)?.kind), 'Break');

  hdr('46.5 A type that allows lessons makes a teaching period');
  const prep = structuredClone(g);
  prep.periodTypes.find((t) => t.key === 'e2e-games').teaching = true;
  const prepSaved = await save(prep);
  eq('switching Games to "lessons can be placed" saves', prepSaved.status, 200);
  const pp = periods(prepSaved.json ?? { dayTypes: [] }).find((p) => p.key === target.key);
  eq(`…and ${target.label} is now a teaching period`, kindOf(pp?.kind), 'Lesson');
  eq('…and a teaching type is always on a teacher\'s own timetable', prepSaved.json?.periodTypes?.find((t) => t.key === 'e2e-games')?.onPersonalTimetable, true);

  hdr('46.6 Refusals');
  const removed = structuredClone(g);
  removed.periodTypes = removed.periodTypes.filter((t) => t.key !== 'e2e-games');
  const r1 = await save(removed);
  truthy('a type a period uses cannot be removed, and the period is named', r1.status === 400 && r1.text.includes(target.label) && /retire/i.test(r1.text), `${r1.status} ${r1.text.slice(0, 200)}`);
  const twin = structuredClone(g);
  twin.periodTypes.push({ key: 'e2e-twin', name: 'games', teaching: false, onPersonalTimetable: true });
  const r2 = await save(twin);
  truthy('two types with one name are refused', r2.status === 400 && /called/i.test(r2.text), `${r2.status} ${r2.text.slice(0, 200)}`);
  const noTeaching = structuredClone(g);
  for (const t of noTeaching.periodTypes) t.teaching = false;
  const r3 = await save(noTeaching);
  truthy('a list with no type that allows lessons is refused', r3.status === 400 && /allow lessons/i.test(r3.text), `${r3.status} ${r3.text.slice(0, 200)}`);
  const blank = structuredClone(g);
  blank.periodTypes.push({ key: 'e2e-blank', name: '   ', teaching: false });
  const r4 = await save(blank);
  truthy('a type with no name is refused', r4.status === 400 && /needs a name/i.test(r4.text), `${r4.status} ${r4.text.slice(0, 200)}`);

  hdr('46.7 A period with published lessons cannot stop taking lessons');
  // Back to the saved baseline first, so the refusal below is about the lessons and nothing else.
  eq('setup: the school day is back to its baseline', (await save(base)).status, 200);
  seeded = await seedLiveTimetable({ API, BRANCH, token: admin.token, teacherId: teacher.id, label: 'period-types' });
  if (!seeded.target) { skip('46.7', `no published version could be put in force (${seeded.reason})`); throw 'skip'; }
  const lesson = (seeded.lessons ?? [])[0];
  const lp = periods(base).find((p) => p.key.toLowerCase() === String(lesson?.periodKey ?? '').toLowerCase());
  if (!lesson || !lp) { skip('46.7', 'the published version has no lesson in a period of this school day'); throw 'skip'; }
  const breakType = base.periodTypes.find((t) => !t.teaching && !t.isRetired);
  const retyped = structuredClone(base);
  for (const p of periods(retyped)) if (p.key === lp.key) p.type = breakType.key;
  const r5 = await save(retyped);
  truthy(`giving ${lp.label} a type that takes no lessons is refused, naming a published lesson`,
    r5.status === 400 && /published lesson/i.test(r5.text) && r5.text.includes(lp.label), `${r5.status} ${r5.text.slice(0, 240)}`);
  const switchedOff = structuredClone(base);
  const lessonType = switchedOff.periodTypes.find((t) => t.key === lp.type);
  lessonType.teaching = false;
  // Keep ONE other period teaching, so the save is not refused for having no teaching periods at all — the refusal under
  // test is the published lessons one.
  switchedOff.periodTypes.push({ key: 'e2e-spare', name: 'Spare teaching', teaching: true, onPersonalTimetable: true });
  const spare = firstNonTeaching(switchedOff);
  if (spare) spare.type = 'e2e-spare';
  const r6 = await save(switchedOff);
  truthy(`switching "${lessonType.name}" off is refused the same way`, r6.status === 400 && /published lesson/i.test(r6.text), `${r6.status} ${r6.text.slice(0, 240)}`);
} catch (e) {
  if (e !== 'skip') bad('the suite ran to the end', 'no exception', e?.stack ?? String(e));
} finally {
  hdr('46.8 Put things back');
  if (seeded?.cleanup) await seeded.cleanup();
  if (changed) console.log(`  school day restored (${(await call(admin.token, 'PUT', SETTINGS, original)).status})`);
}

console.log(`\n  section 46: ${pass} passed, ${fail} failed`);
if (failures.length) console.log('  failed: ' + failures.join(' | '));
process.exitCode = fail ? 1 : 0;

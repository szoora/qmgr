// The timetable editor's FOUR VIEW TABS — Class · Teacher · Room · School.
//
// Reported from production as "that class tab not working. previously default tab was class", and it
// was exactly that: ChooseDefaultKey carried the opening default ("a teacher opening the page sees
// their own week") and OnViewChanged called it on every press. Pressing Class cleared the key, the
// next line saw an empty key and flipped the axis straight back to Teacher, so the Class tab was
// unpressable for anybody who taught a lesson — including the administrator building the timetable.
//
// Why a browser suite: the axis, the key and the flip are client-side state. The API returns the same
// whole timetable whichever tab is showing, so a curl run cannot see any of it.
//
// The vacuous-pass traps this refuses to run into, both of which cost nothing to hit by accident:
//   - a teacher with NO lessons in the version on screen never arms the flip, so every assertion
//     below would pass while proving nothing;
//   - an administrator who teaches nothing is the same again for the manager half, so this seeds a
//     draft carrying a lesson of their own and removes it afterwards.
//
// Run: node scripts/e2e/browser/timetable-views.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const ADMIN_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
const TEACHER_PASSWORDS = process.env.E2E_TEACHER_PASS
  ? [process.env.E2E_TEACHER_PASS]
  : ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

const signIn = async (email, passwords) => {
  for (const password of [].concat(passwords)) {
    const r = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
    });
    if (r.ok) { const j = await r.json(); return { password, token: j.accessToken, id: j.user?.id ?? j.userId }; }
  }
  return null;
};

const admin = await signIn(ADMIN, ADMIN_PASS);
if (!admin?.token) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }
const teacher = await signIn(TEACHER, TEACHER_PASSWORDS);
if (!teacher?.token) { console.error(`could not sign in as ${TEACHER}; set E2E_TEACHER_USER / E2E_TEACHER_PASS`); process.exit(1); }

const auth = { Authorization: `Bearer ${admin.token}`, 'Content-Type': 'application/json' };
const tt = `${API}/api/v1/branches/${BRANCH}/timetable`;
const get = async (url) => {
  const r = await fetch(url, { headers: { Authorization: `Bearer ${admin.token}` } });
  return r.ok ? r.json() : null;
};

// ---- the version the page will open on, resolved the way LoadAsync resolves it ------------------
const today = new Date().toISOString().slice(0, 10);
const versions = await get(`${tt}/timetables`) ?? [];
const opening = versions.find(v => v.status === 'Published' && v.effectiveFrom <= today && v.effectiveTo >= today)
  ?? versions.find(v => v.status === 'Draft') ?? versions[0];
if (!opening) { console.error('the dev tenant has no timetable version at all'); process.exit(1); }
const detail = await get(`${tt}/timetables/${opening.id}`);
const teacherLessons = detail.lessons.filter(l => l.teacherUserId === teacher.id).length;
if (teacherLessons === 0) {
  console.error(`${TEACHER} teaches nothing in "${opening.name}", so the opening flip could never fire`);
  console.error('and every assertion would pass while proving nothing. Point E2E_TEACHER_USER at a teacher in it.');
  process.exit(1);
}
console.log(`  version "${opening.name}" [${opening.status}] · ${detail.lessons.length} lessons · ${TEACHER} teaches ${teacherLessons}`);

// ---- the browser --------------------------------------------------------------------------------
const t = await openTab();
const views = '[aria-label="Timetable view"]';
const activeView = () => t.eval(`(() => { const s = document.querySelector('${views}');
  const b = s && s.querySelector('button[aria-selected="true"]'); return b ? b.innerText.trim() : null; })()`);
const pressView = async (label) => {
  const hit = await t.eval(`(() => { const s = document.querySelector('${views}'); if (!s) return false;
    const b = [...s.querySelectorAll('button')].find(x => x.innerText.trim() === ${JSON.stringify(label)});
    if (!b) return false; b.click(); return true; })()`);
  await t.sleep(1400);
  return hit;
};
const keyText = () => t.eval(`(() => { const k = document.querySelector('.tt-key');
  return k ? k.innerText.replace(/\\s+/g, ' ').trim() : null; })()`);
// #blazor-error-ui is on every page and hidden by a STYLESHEET, so it is read computed (CLAUDE.md).
const errorBar = () => t.eval(`(() => { const e = document.querySelector('#blazor-error-ui');
  return !!e && getComputedStyle(e).display !== 'none'; })()`);
const openEditor = async (query = '') => {
  await t.goto(`${BASE}/admin/timetable${query}`);
  await t.waitFor(`!!document.querySelector('${views}')`, 30000);
  await t.sleep(1400);
};

let draftId = null;
try {
  // ---- 1. a teacher: their own week on arrival, and Class when they ask for it ------------------
  await login(t, TEACHER, teacher.password);
  await openEditor();

  const teacherOpening = await activeView();
  check('a teacher opens on their own week', teacherOpening === 'Teacher', `opened on ${teacherOpening}`);
  const ownKey = await keyText();
  check('...with a teacher in the picker', (ownKey ?? '').startsWith('Teacher'), `picker read "${ownKey}"`);

  check('pressing Class is registered', await pressView('Class'));
  const afterClass = await activeView();
  check('Class STAYS selected for a teacher who teaches', afterClass === 'Class', `snapped back to ${afterClass}`);
  const classKey = await keyText();
  check('...and the picker moved to a class', (classKey ?? '').startsWith('Class'), `picker read "${classKey}"`);
  check('...and the grid rendered', await t.eval(`!!document.querySelector('.tt-grid')`));

  await pressView('Teacher');
  check('Teacher can be pressed back', await activeView() === 'Teacher');
  check('no client-side error on the page', !(await errorBar()));

  // ---- 2. a timetable master who also teaches: the week, not their own two lessons --------------
  // Seeded rather than assumed: the dev tenant's administrator teaches nothing, and an administrator
  // with no lessons cannot tell the manager rule from the plain default.
  const draft = await (await fetch(`${tt}/timetables`, {
    method: 'POST', headers: auth,
    body: JSON.stringify({ name: `VIEW-TABS ${Date.now().toString(36)}`, copyFromTimetableId: opening.id }),
  })).json();
  draftId = draft?.timetable?.id;
  if (!draftId) throw new Error('could not create the draft: ' + JSON.stringify(draft).slice(0, 300));

  const sample = detail.lessons[0];
  const used = new Set(detail.lessons.filter(l => l.className === sample.className).map(l => `${l.cycleDay}/${l.periodKey}`));
  const periods = [...new Set(detail.lessons.map(l => l.periodKey))];
  const free = [];
  for (const day of [...new Set(detail.lessons.map(l => l.cycleDay))]) {
    for (const p of periods) if (!used.has(`${day}/${p}`)) free.push({ day, p });
  }
  if (!free.length) throw new Error(`no free period for ${sample.className} to give the administrator`);
  const placed = await fetch(`${tt}/timetables/${draftId}/lessons`, {
    method: 'POST', headers: auth,
    body: JSON.stringify({
      cycleDay: free[0].day, periodKey: free[0].p, className: sample.className,
      subjectId: sample.subjectId, teacherUserIds: [admin.id],
    }),
  });
  check('the administrator is given a lesson of their own', placed.ok, `${placed.status} ${(await placed.text()).slice(0, 200)}`);

  await login(t, ADMIN, admin.password);
  await openEditor(`?t=${draftId}`);
  const masterOpening = await activeView();
  check('a timetable master opens on Class, not their own week', masterOpening === 'Class', `opened on ${masterOpening}`);

  const pressedTeacher = await pressView('Teacher');
  check('the master can press Teacher', pressedTeacher && await activeView() === 'Teacher');
  await pressView('Class');
  const backToClass = await activeView();
  check('...and Class again', backToClass === 'Class', `snapped back to ${backToClass}`);
  check('no client-side error on the master view', !(await errorBar()));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  if (draftId) {
    const gone = await fetch(`${tt}/timetables/${draftId}`, { method: 'DELETE', headers: auth });
    check('the seeded draft is removed', gone.ok, `${gone.status}`);
  }
  t.close();
}

const line = `  timetable-views: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;

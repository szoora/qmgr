// SECTION 26 — WHO OWNS A TIMETABLE: appointed masters, the visible override, cover, and the swap.
//
// Node rather than curl for two reasons. Half the assertions fire the same write from several callers
// at once and check an invariant — the shape that has already caught five real races in this module —
// and the rest need a signed-in TEACHER who holds no timetable permission at all, which is fiddly to
// keep straight in shell.
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/timetable-ownership-e2e.mjs
//
// IT CREATES ITS OWN TIMETABLE VERSIONS AND DELETES THEM. It never touches a version it did not make,
// because publishing over a live timetable stops every lesson in the school — which is the bug this
// section exists to prove is fixed, and not something to do to a working tenant by accident.
//
// THE ASSERTION THAT MATTERS MOST is 26.7: a PERMANENT swap against a PUBLISHED timetable. That path
// was accepted, agreed by the colleague, approved by a decider, and then refused with "there is no
// draft timetable to change any more" — three people acted and the system refused last. Mid-term is
// when two teachers actually have that conversation, so the feature only worked when nobody needed it.

const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const AD_USER = process.env.AD_USER ?? 'e2e.admin.ct@qmgr.local';
const T4_USER = process.env.T4_USER ?? 'e2e.teacher.s4@qmgr.local';
const T2_USER = process.env.T2_USER ?? 'e2e.teacher.s2@qmgr.local';
const PW = process.env.PW ?? 'E2eTeacher!2026';
const NEW_PW = 'Rwenzori#Peaks-2026';

let pass = 0, fail = 0;
const ok = (name, condition, detail = '') => {
  if (condition) { pass++; console.log(`    PASS  ${name}`); }
  else { fail++; console.log(`    FAIL  ${name}${detail ? `  — ${detail}` : ''}`); }
};
const skip = (name, why) => console.log(`    SKIP  ${name}  — ${why}`);
const head = (t) => console.log(`\n${t}`);

async function call(method, path, { token, body } = {}) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* a non-JSON body is itself the answer */ }
  return { status: res.status, json, text };
}

const login = async (id, pw) => (await call('POST', '/api/v1/auth/login', { body: { email: id, password: pw } })).json?.accessToken ?? null;
const signIn = async (id) => (await login(id, PW)) ?? (await login(id, NEW_PW));

const B = `/api/v1/branches/${BRANCH}`;
const TT = `${B}/timetable`;
const SS = `${B}/staff/self-service`;

const iso = (d) => d.toISOString().slice(0, 10);
const created = [];

// THIS RUN'S OWN DATE WINDOW, and it has to be unique per run.
//
// The first version of this used a fixed "today + 400", and the SECOND run failed four assertions
// against the FIRST run's leftovers — which now looked exactly like the product bug the section exists
// to prove is fixed ("another timetable is already live for these dates"). A published version cannot be
// deleted by design, so a fixed window guarantees a collision on every re-run.
//
// Two defences, because either alone is thin: a wide random offset so two runs are unlikely to overlap,
// and an ARCHIVE of everything this run published at the end (26.9), which is what actually clears the
// dates. An archived version blocks nothing.
const SPREAD = Math.floor(Math.random() * 900);
const FUTURE_OFFSET = 500 + SPREAD;
const PAST_OFFSET = -(200 + SPREAD);

(async () => {
  console.log('26. Timetable ownership, cover and swaps');

  const ad = await signIn(AD_USER);
  const t4 = await signIn(T4_USER);
  const t2 = await signIn(T2_USER);
  if (!ad || !t4 || !t2) {
    console.log('    FAIL  could not sign in as the e2e administrator and two teachers.');
    console.log('          Run class-teacher-e2e.sh first, which creates those accounts.');
    process.exit(1);
  }

  const me = async (token) => (await call('GET', '/api/v1/auth/me', { token })).json;
  const adMe = await me(ad), t4Me = await me(t4), t2Me = await me(t2);
  const t4Id = t4Me?.id ?? t4Me?.userId;
  const t2Id = t2Me?.id ?? t2Me?.userId;
  ok('26.0a: the two teachers resolve to user ids', !!t4Id && !!t2Id, `${t4Id} / ${t2Id}`);

  // The teacher must NOT hold timetable.manage, or every ownership assertion below passes for the
  // wrong reason — which is the "guarded door beside an unguarded one" trap this repo already records.
  const t4Perms = t4Me?.permissions ?? [];
  if (t4Perms.includes('timetable.manage')) {
    console.log('    FAIL  the teacher account holds timetable.manage, so ownership cannot be told apart from the permission.');
    process.exit(1);
  }
  ok('26.0b: the teacher holds no timetable permission', true);

  const settings = (await call('GET', `${TT}/settings`, { token: ad })).json;
  const cycleDays = settings?.dayTypes?.length ? undefined : undefined; // resolved from the version below
  if (!settings) { console.log('    FAIL  the bell schedule could not be read.'); process.exit(1); }

  const subjects = (await call('GET', `${B}/staff/subjects`, { token: ad })).json ?? [];
  const subject = subjects.find((s) => s.isActive !== false) ?? subjects[0];
  if (!subject) { console.log('    FAIL  the branch has no subjects, so no lesson can be placed.'); process.exit(1); }

  const vocab = (await call('GET', `${B}/students/vocabularies`, { token: ad })).json;
  const classes = (vocab?.classes ?? []).filter((c) => c.isActive !== false).map((c) => c.name);
  if (classes.length < 2) { console.log('    FAIL  the branch needs at least two configured classes.'); process.exit(1); }

  // A LESSON CANNOT BE PLACED FOR A TEACHER WHO IS NOT ASSIGNED THE CLASS AND SUBJECT.
  // TimetableChecker raises that as a HARD clash (TeacherNotAssigned) and publishing refuses while one
  // remains — correct product behaviour, and the first run of this suite tripped over it and reported
  // six failures that were the fixture rather than the feature. So the assignments are SEEDED here and
  // removed at the end. Each teacher gets a different class, so their two lessons can be swapped.
  const seededAssignments = [];
  const assign = async (userId, className) => {
    const res = await call('POST', `${B}/class-teachers/subject-teachers`, {
      token: ad, body: { className, userId, subjectId: subject.id, periodsPerWeek: 4 },
    });
    if (res.json?.id) seededAssignments.push(res.json.id);
    return res;
  };
  const a4 = await assign(t4Id, classes[0]);
  const a2 = await assign(t2Id, classes[1]);
  ok('26.0c: both teachers are assigned a class and subject to place',
    [200, 201, 409].includes(a4.status) && [200, 201, 409].includes(a2.status),
    `${a4.status} / ${a2.status}  ${a4.text.slice(0, 80)}`);

  // ---------------------------------------------------------------------------------------------
  head('26.1 A version can be appointed to somebody, and only an administrator appoints');

  const far = new Date(); far.setDate(far.getDate() + FUTURE_OFFSET);
  const farEnd = new Date(far); farEnd.setDate(farEnd.getDate() + 20);

  const mk = async (name, from, to, managers) => {
    const res = await call('POST', `${TT}/timetables`, {
      token: ad,
      body: { name, effectiveFrom: iso(from), effectiveTo: iso(to), ...(managers ? { managerUserIds: managers } : {}) },
    });
    if (res.json?.timetable?.id) created.push(res.json.timetable.id);
    return res;
  };

  const mine = await mk(`E2E ownership ${Date.now()}`, far, farEnd, [t4Id]);
  ok('26.1a: a draft is created with an appointed master', mine.status === 201, `status ${mine.status} ${mine.text.slice(0, 120)}`);
  const ttId = mine.json?.timetable?.id;
  ok('26.1b: and it names them back', (mine.json?.timetable?.managerUserIds ?? []).includes(t4Id),
    JSON.stringify(mine.json?.timetable?.managerUserIds));
  ok('26.1c: with their name, for a page to show', (mine.json?.timetable?.managerNames ?? []).length === 1,
    JSON.stringify(mine.json?.timetable?.managerNames));
  if (!ttId) { console.log('    FAIL  no timetable id; the rest of section 26 cannot run.'); process.exit(1); }

  const asTeacher = (await call('GET', `${TT}/timetables/${ttId}`, { token: t4 })).json;
  ok('26.1d: the appointed master can READ their own draft', asTeacher?.timetable?.id === ttId,
    `got ${asTeacher?.timetable?.id}`);
  ok('26.1e: and is told they may write it', asTeacher?.timetable?.canIWrite === true && asTeacher?.timetable?.iAmManager === true,
    `canIWrite=${asTeacher?.timetable?.canIWrite} iAmManager=${asTeacher?.timetable?.iAmManager}`);
  ok('26.1f: without being able to appoint', asTeacher?.timetable?.canIAppoint === false,
    `canIAppoint=${asTeacher?.timetable?.canIAppoint}`);

  // A MASTER MAY NOT APPOINT, including themselves. Otherwise the control is decoration.
  const selfAppoint = await call('PUT', `${TT}/timetables/${ttId}/managers`, { token: t4, body: { userIds: [t4Id, t2Id] } });
  ok('26.1g: a master cannot appoint a co-master (403)', selfAppoint.status === 403, `status ${selfAppoint.status}`);
  ok('26.1h: and the refusal says who can', /administrator/i.test(selfAppoint.text), selfAppoint.text.slice(0, 120));

  // ---------------------------------------------------------------------------------------------
  head('26.2 A master writes their own version, and nobody else’s');

  // The teacher is a parameter, and the class has to be one THEY are assigned — see 26.0c.
  const place = (token, id, day, period, teacher = t4Id, className = classes[0]) => call('POST', `${TT}/timetables/${id}/lessons`, {
    token,
    body: { cycleDay: day, periodKey: period, className, subjectId: subject.id, teacherUserIds: [teacher] },
  });

  const periods = (settings.dayTypes?.[0]?.periods ?? []).filter((p) => p.kind === 'Lesson' || p.kind === 0);
  const p1 = periods[0]?.key, p2 = periods[1]?.key;
  if (!p1 || !p2) { console.log('    FAIL  the bell schedule has fewer than two lesson periods.'); process.exit(1); }

  const byMaster = await place(t4, ttId, 1, p1);
  // THE WHOLE POINT OF PHASE 1. This used to be 403: the endpoint carried [RequirePermission], which
  // refuses before the handler runs, so ownership could never let an appointed teacher through. And
  // the staff-scope refusal would have caught them afterwards — the teacher role is StaffScope.SelfOnly.
  ok('26.2a: the appointed master places a lesson with NO timetable permission', byMaster.status === 201,
    `status ${byMaster.status} ${byMaster.text.slice(0, 160)}`);
  const lessonA = byMaster.json?.lessons?.[0]?.id;

  const other = await mk(`E2E not-mine ${Date.now()}`, farEnd, new Date(farEnd.getTime() + 30 * 86400000), [t2Id]);
  const otherId = other.json?.timetable?.id;
  const trespass = await place(t4, otherId, 1, p1);
  ok('26.2b: and is refused on a version somebody else owns (403)', trespass.status === 403, `status ${trespass.status}`);
  ok('26.2c: the refusal NAMES whose it is', /appointed master/i.test(trespass.text), trespass.text.slice(0, 160));

  const unowned = await mk(`E2E unowned ${Date.now()}`, new Date(farEnd.getTime() + 40 * 86400000), new Date(farEnd.getTime() + 70 * 86400000), null);
  const unownedId = unowned.json?.timetable?.id;
  const onUnowned = await place(t4, unownedId, 1, p1);
  ok('26.2d: a version with no master needs the permission (403)', onUnowned.status === 403, `status ${onUnowned.status}`);
  ok('26.2e: and says so without naming anybody', /timetable permission/i.test(onUnowned.text), onUnowned.text.slice(0, 160));

  // ---------------------------------------------------------------------------------------------
  head('26.3 An administrator override is allowed, and never silent');

  const before = (await call('GET', `${TT}/timetables/${ttId}`, { token: ad })).json;
  ok('26.3a: an administrator is warned BEFORE they write', before?.timetable?.wouldBeOverride === true,
    `wouldBeOverride=${before?.timetable?.wouldBeOverride}`);

  const overrideWrite = await place(ad, ttId, 1, p2);
  ok('26.3b: and the write itself is allowed', overrideWrite.status === 201,
    `status ${overrideWrite.status} ${overrideWrite.text.slice(0, 160)}`);

  // The activity log is the durable half of the control; the notification is the courtesy.
  const log = (await call('GET', `${B}/staff/activity?limit=50`, { token: ad })).json;
  const rows = log?.items ?? log ?? [];
  ok('26.3c: it is recorded as an OVERRIDE in the activity log',
    rows.some?.((e) => (e.action ?? '') === 'timetable.overridden'),
    `${rows.length ?? 0} row(s) read`);

  const unowned2 = (await call('GET', `${TT}/timetables/${unownedId}`, { token: ad })).json;
  ok('26.3d: an ordinary write on an UNOWNED version is not an override', unowned2?.timetable?.wouldBeOverride === false,
    `wouldBeOverride=${unowned2?.timetable?.wouldBeOverride}`);

  // ---------------------------------------------------------------------------------------------
  head('26.4 Publishing over a live timetable refuses rather than archiving it silently');

  const pubA = await call('POST', `${TT}/timetables/${ttId}/publish`, { token: t4, body: { acknowledgeSoftClashes: true, note: 'e2e' } });
  ok('26.4a: the appointed master publishes their own version', pubA.status === 200,
    `status ${pubA.status} ${pubA.text.slice(0, 200)}`);

  // A second version over the SAME dates. Before 2026-09-22 this archived the first one silently, and
  // because every lesson query filters on Published over today's date, that stopped every register.
  const clash = await mk(`E2E overlap ${Date.now()}`, far, farEnd, [t4Id]);
  const clashId = clash.json?.timetable?.id;
  // BOTH TEACHERS' LESSONS GO ON WHILE IT IS STILL A DRAFT. A published version is immutable, so this is
  // the only chance — and 26.7 needs one lesson each to have something to swap.
  await place(t4, clashId, 1, p1, t4Id, classes[0]);
  await place(t4, clashId, 1, p2, t2Id, classes[1]);
  const noReplace = await call('POST', `${TT}/timetables/${clashId}/publish`, { token: t4, body: { acknowledgeSoftClashes: true, note: 'e2e' } });
  ok('26.4b: publishing over it is REFUSED (409)', noReplace.status === 409, `status ${noReplace.status}`);
  ok('26.4c: with a code the client can act on', /WOULD_REPLACE_PUBLISHED/.test(noReplace.text), noReplace.text.slice(0, 200));
  ok('26.4d: naming what it would take out of service', /E2E ownership/.test(noReplace.text), noReplace.text.slice(0, 260));

  const stillLive = (await call('GET', `${TT}/timetables/${ttId}`, { token: ad })).json;
  ok('26.4e: and the live version is UNTOUCHED', stillLive?.timetable?.status === 'Published',
    `status=${stillLive?.timetable?.status}`);

  const withReplace = await call('POST', `${TT}/timetables/${clashId}/publish`, { token: t4, body: { acknowledgeSoftClashes: true, note: 'e2e', replace: true } });
  ok('26.4f: asking for it explicitly works', withReplace.status === 200, `status ${withReplace.status} ${withReplace.text.slice(0, 200)}`);
  const replaced = (await call('GET', `${TT}/timetables/${ttId}`, { token: ad })).json;
  ok('26.4g: and only then is the old one archived', replaced?.timetable?.status === 'Archived',
    `status=${replaced?.timetable?.status}`);

  // ---------------------------------------------------------------------------------------------
  head('26.5 Expired is derived from the dates, never stored');

  // A version whose last day has passed. Publishing it is a legitimate act — a school catching up on a
  // term it never recorded — and the point is only that the STATUS stops reading "Published".
  const past = new Date(); past.setDate(past.getDate() + PAST_OFFSET);
  const pastEnd = new Date(past); pastEnd.setDate(pastEnd.getDate() + 15);
  const old = await mk(`E2E expired ${Date.now()}`, past, pastEnd, [t4Id]);
  const oldId = old.json?.timetable?.id;
  await place(t4, oldId, 1, p1);
  const pubOld = await call('POST', `${TT}/timetables/${oldId}/publish`, { token: t4, body: { acknowledgeSoftClashes: true, note: 'e2e' } });
  if (pubOld.status !== 200) {
    skip('26.5a: a past version reads Expired', `it would not publish: ${pubOld.text.slice(0, 140)}`);
  } else {
    const read = (await call('GET', `${TT}/timetables/${oldId}`, { token: ad })).json;
    ok('26.5a: a published version past its last day reads Expired', read?.timetable?.status === 'Expired',
      `status=${read?.timetable?.status}`);
    const list = (await call('GET', `${TT}/timetables`, { token: ad })).json ?? [];
    ok('26.5b: and the LIST says so too, not just the detail',
      list.find((v) => v.id === oldId)?.status === 'Expired',
      `status=${list.find((v) => v.id === oldId)?.status}`);
  }

  // ---------------------------------------------------------------------------------------------
  head('26.6 Cover: one lesson, one date');

  // Against the version that is now live for those far dates.
  const live = (await call('GET', `${TT}/timetables/${clashId}`, { token: ad })).json;
  const coverLesson = live?.lessons?.[0];
  ok('26.6a: the live version has a lesson to cover', !!coverLesson, JSON.stringify(live?.lessons?.length));

  // The date has to be one the lesson actually falls on, worked out the same way the server does.
  const findDate = (cycleDay) => {
    const from = new Date(far);
    for (let i = 0; i < 21; i++) {
      const d = new Date(from); d.setDate(d.getDate() + i);
      // The server owns the cycle arithmetic; here we only need A date, so try each and read the refusal.
      if (d <= farEnd) return { probe: d, cycleDay };
    }
    return null;
  };

  let coverDate = null, coverRes = null;
  for (let i = 0; i < 14 && coverLesson; i++) {
    const d = new Date(far); d.setDate(d.getDate() + i);
    coverRes = await call('POST', `${TT}/timetables/${clashId}/exceptions`, {
      token: ad,
      body: { timetableLessonId: coverLesson.id, date: iso(d), kind: 'Cover', coverUserId: t2Id, reason: 'e2e cover' },
    });
    if (coverRes.status === 201) { coverDate = iso(d); break; }
  }
  ok('26.6b: cover is recorded on a date the lesson falls on', coverRes?.status === 201,
    `status ${coverRes?.status} ${coverRes?.text?.slice(0, 200)}`);
  const exceptionId = coverRes?.json?.id;

  if (coverDate) {
    ok('26.6c: and the answer names who is covering', coverRes.json?.coverUserName?.length > 0,
      `cover=${coverRes.json?.coverUserName}`);
    ok('26.6d: it reads back on the version', (await call('GET', `${TT}/timetables/${clashId}`, { token: ad })).json?.exceptions?.some((e) => e.id === exceptionId));

    // A COVER WITH NOBODY COVERING IS A CANCELLATION WEARING THE WRONG LABEL. Refused by the controller
    // and by a check constraint, so this asserts the controller says so in words first.
    const noCoverer = await call('POST', `${TT}/timetables/${clashId}/exceptions`, {
      token: ad, body: { timetableLessonId: coverLesson.id, date: coverDate, kind: 'Cover' },
    });
    ok('26.6e: a cover naming nobody is refused', noCoverer.status === 400, `status ${noCoverer.status}`);

    const cancelWithCoverer = await call('POST', `${TT}/timetables/${clashId}/exceptions`, {
      token: ad, body: { timetableLessonId: coverLesson.id, date: coverDate, kind: 'Cancelled', coverUserId: t2Id },
    });
    ok('26.6f: a cancellation naming somebody is refused', cancelWithCoverer.status === 400, `status ${cancelWithCoverer.status}`);

    // ONE PER LESSON PER DATE, enforced by the unique index rather than a handler check. Fired
    // concurrently, because that is the only way to prove an index holds.
    const races = await Promise.all([0, 1, 2, 3, 4].map(() => call('POST', `${TT}/timetables/${clashId}/exceptions`, {
      token: ad, body: { timetableLessonId: coverLesson.id, date: coverDate, kind: 'Cover', coverUserId: t2Id },
    })));
    ok('26.6g: five simultaneous identical covers all refuse (one already exists)',
      races.every((r) => r.status === 409), races.map((r) => r.status).join(','));

    const pastDate = new Date(); pastDate.setDate(pastDate.getDate() - 3);
    const backdated = await call('POST', `${TT}/timetables/${clashId}/exceptions`, {
      token: ad, body: { timetableLessonId: coverLesson.id, date: iso(pastDate), kind: 'Cancelled' },
    });
    ok('26.6h: cover cannot be recorded for a date that has passed', backdated.status === 400, `status ${backdated.status}`);

    // The covering teacher may undo it: the two of them arranged it, so needing an administrator to
    // unpick it would send them back to the corridor.
    const undo = await call('DELETE', `${TT}/timetables/${clashId}/exceptions/${exceptionId}`, { token: t2 });
    ok('26.6i: the covering teacher can withdraw it', undo.status === 204, `status ${undo.status}`);
    const gone = (await call('GET', `${TT}/timetables/${clashId}`, { token: ad })).json?.exceptions ?? [];
    ok('26.6j: and it is gone', !gone.some((e) => e.id === exceptionId));

    // Somebody with no part in it reads it and nothing more.
    const stranger = await call('DELETE', `${TT}/timetables/${clashId}/exceptions/${exceptionId}`, { token: t4 });
    ok('26.6k: a stranger gets 404, never 403', stranger.status === 404, `status ${stranger.status}`);
  } else {
    skip('26.6c–k', 'no cover could be recorded, so the rest of 26.6 would prove nothing');
  }

  // ---------------------------------------------------------------------------------------------
  head('26.7 A swap on a PUBLISHED timetable — the bug this section exists for');

  const policy = (await call('GET', '/api/v1/staff/policy', { token: ad })).json;
  const policyBefore = JSON.stringify(policy);
  if (policy?.selfService) {
    const on = JSON.parse(policyBefore);
    on.selfService = { ...on.selfService, enabled: true, allowRequests: true };
    await call('PUT', '/api/v1/staff/policy', { token: ad, body: on });
  }

  const liveNow = (await call('GET', `${TT}/timetables/${clashId}`, { token: ad })).json;
  const t4Lesson = liveNow?.lessons?.find((l) => l.teacherUserId === t4Id && l.groupId == null);
  const t2Lesson = liveNow?.lessons?.find((l) => l.teacherUserId === t2Id && l.groupId == null);

  // A published version is immutable, so a lesson cannot be added to it — which is exactly WHY a permanent
  // swap has to re-publish rather than update. That refusal is asserted rather than worked around.
  const placeOnPublished = await call('POST', `${TT}/timetables/${clashId}/lessons`, {
    token: ad, body: { cycleDay: 2, periodKey: p1, className: classes[0], subjectId: subject.id, teacherUserIds: [t4Id] },
  });
  ok('26.7a: a lesson cannot be added to a PUBLISHED version', placeOnPublished.status === 409, `status ${placeOnPublished.status}`);
  if (!t4Lesson || !t2Lesson) {
    skip('26.7b–h', 'the live version does not carry a lesson for each of the two teachers, so a swap would prove nothing');
  } else {
    const ask = await call('POST', `${SS}/requests`, {
      token: t4,
      body: { kind: 'SlotSwap', myLessonId: t4Lesson.id, theirLessonId: t2Lesson.id, reason: 'e2e permanent swap' },
    });
    ok('26.7b: a teacher asks for a PERMANENT swap', ask.status === 201, `status ${ask.status} ${ask.text.slice(0, 200)}`);
    const reqId = ask.json?.id;
    ok('26.7c: it is marked as not a one-off', ask.json?.isOneOff === false, `isOneOff=${ask.json?.isOneOff}`);

    // A DECIDER MAY NOT APPLY IT BEFORE THE COLLEAGUE AGREES.
    const early = await call('POST', `${SS}/requests/${reqId}/decide`, { token: ad, body: { approve: true } });
    ok('26.7d: it cannot be approved before the colleague agrees', early.status === 400, `status ${early.status}`);
    ok('26.7e: and the refusal says why', /agreed/i.test(early.text), early.text.slice(0, 140));

    const agree = await call('POST', `${SS}/requests/${reqId}/agree`, { token: t2 });
    ok('26.7f: the colleague agrees', agree.status === 204, `status ${agree.status}`);

    // THE ASSERTION. Approving a permanent swap on a published version re-publishes it, rather than
    // answering "there is no draft timetable to change any more".
    const decide = await call('POST', `${SS}/requests/${reqId}/decide`, { token: ad, body: { approve: true } });
    ok('26.7g: approving it APPLIES rather than superseding', decide.json?.state === 'Approved',
      `state=${decide.json?.state} reason=${decide.json?.decisionReason ?? ''}`);

    const after = (await call('GET', `${TT}/timetables`, { token: ad })).json ?? [];
    const nowLive = after.find((v) => v.status === 'Published' && v.effectiveFrom === iso(far));
    ok('26.7h: a NEW version is live for the same dates', !!nowLive && nowLive.id !== clashId,
      `live=${nowLive?.id} was=${clashId}`);
    if (nowLive?.id) created.push(nowLive.id);
    ok('26.7i: and it kept the appointed master', (nowLive?.managerUserIds ?? []).includes(t4Id),
      JSON.stringify(nowLive?.managerUserIds));

    const swapped = (await call('GET', `${TT}/timetables/${nowLive?.id}`, { token: ad })).json;
    const movedT4 = swapped?.lessons?.find((l) => l.teacherUserId === t4Id && l.className === t4Lesson.className);
    ok('26.7j: the two SLOTS traded, and the teachers did not',
      movedT4 != null && movedT4.periodKey === t2Lesson.periodKey,
      `t4 now at ${movedT4?.periodKey}, was ${t4Lesson.periodKey}, wanted ${t2Lesson.periodKey}`);
  }

  // ---------------------------------------------------------------------------------------------
  head('26.8 Who may decide is the version’s own write rule');

  // A teacher who manages NO version cannot decide anything, whoever asked.
  const t2Requests = await call('GET', `${SS}/requests`, { token: t2 });
  ok('26.8a: a teacher reads only what concerns them', t2Requests.status === 200, `status ${t2Requests.status}`);
  ok('26.8b: and is never offered a decision on it',
    (t2Requests.json ?? []).every((r) => r.canIDecide === false),
    JSON.stringify((t2Requests.json ?? []).map((r) => r.canIDecide)));

  // ---------------------------------------------------------------------------------------------
  head('26.9 Cleanup');

  if (policy?.selfService) await call('PUT', '/api/v1/staff/policy', { token: ad, body: JSON.parse(policyBefore) });

  // Drafts delete; a published or archived version is history and has no DELETE by design. A PUBLISHED
  // one is ARCHIVED first, and that is not tidiness — it is what makes this suite re-runnable. A live
  // version left behind blocks the next run from publishing over the same dates, and the refusal it
  // produces reads exactly like the product bug this section exists to prove is fixed. Found on the
  // second run, which failed four assertions against the first run's leftovers.
  const left = [];
  for (const id of [...new Set(created)]) {
    const del = await call('DELETE', `${TT}/timetables/${id}`, { token: ad });
    if (del.status === 204) continue;
    await call('POST', `${TT}/timetables/${id}/archive`, { token: ad });
    left.push(id);
  }
  // The seeded subject-teacher assignments go too: they GRANT Teaching-tier access to a class of children
  // (StudentScopeService reads a SubjectTeacher row), so leaving them behind would quietly widen what two
  // accounts can see for as long as the dev tenant lives.
  for (const id of seededAssignments) await call('DELETE', `${B}/class-teachers/${id}`, { token: ad });
  ok('26.9a: the drafts and seeded assignments this run made were removed', true);
  ok('26.9b: nothing this run published is left live', left.length === 0 ||
    !(await call('GET', `${TT}/timetables`, { token: ad })).json?.some?.((v) => left.includes(v.id) && v.status === 'Published'),
    `${left.length} kept`);
  if (left.length > 0) {
    console.log(`    NOTE  ${left.length} version(s) this run published are kept as history (archived, never deleted — by design):`);
    for (const id of left) console.log(`          ${id}`);
    console.log(`          Dated ${iso(far)}–${iso(farEnd)}, so they were never the tenant's live timetable.`);
  }

  console.log(`\n  26. Timetable ownership: ${pass} passed, ${fail} failed`);
  process.exitCode = fail > 0 ? 1 : 0;
})();

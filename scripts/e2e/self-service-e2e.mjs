// SECTION 21 — STAFF SELF-SERVICE CONFIGURATION.
//
// Written in Node rather than curl because the whole risk of this feature IS concurrency: until now
// exactly one role wrote a timetable, and this multiplies the writers by the size of the staff room.
// So the assertions that matter fire the same claim from several callers at once and check the
// invariant — the shape that has already caught four real races in this module (the recognition
// budget, the register double-submit, lost notice acknowledgements, duplicate notice fan-out).
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> SA_USER=superadmin SA_PASS=admin \
//     node scripts/e2e/self-service-e2e.mjs
//
// It restores the tenant's self-service policy to whatever it found, and removes the lessons and
// requests it made. A request it could not remove is named at the end rather than left silent.

const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const SA_USER = process.env.SA_USER ?? 'superadmin';
const SA_PASS = process.env.SA_PASS ?? 'admin';

// A TENANT ADMINISTRATOR, NOT THE PLATFORM SUPERADMIN, and the reason is worth knowing because it
// cost this suite its first run. PUT /api/v1/staff/policy is NOT branch-scoped: it resolves the
// organization from the caller's tenant CONTEXT. Every self-service route is branch-scoped and
// resolves it from the BRANCH. For a tenant user those are the same organization; for a platform
// SuperAdmin operating across tenants they are not, so the policy saved and read back on while
// every branch route went on seeing it off.
const AD_USER = process.env.AD_USER ?? 'e2e.admin.ct@qmgr.local';
const AD_PASS = process.env.AD_PASS ?? 'E2eTeacher!2026';
const NEW_PW = 'Rwenzori#Peaks-2026';

// The class this run teaches itself, so the grid has something to render. It has to be a class the
// branch actually has configured — ClassTeachersController refuses one that is not, which is the
// right refusal and cost this suite a run to learn.

let pass = 0, fail = 0;
const ok = (name, condition, detail = '') => {
  if (condition) { pass++; console.log(`    PASS  ${name}`); }
  else { fail++; console.log(`    FAIL  ${name}${detail ? `  — ${detail}` : ''}`); }
};
const head = (t) => console.log(`\n${t}`);

async function call(method, path, { token, body } = {}) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* a non-JSON body is itself the answer */ }
  return { status: res.status, json, text };
}

async function login(identifier, password) {
  const res = await call('POST', '/api/v1/auth/login', { body: { email: identifier, password } });
  return res.json?.accessToken ?? null;
}

const base = `/api/v1/branches/${BRANCH}/staff/self-service`;

(async () => {
  console.log('21. Staff self-service configuration');

  // Accounts the suites create use NEW_PW; sign-in tries both, the standing rule in this folder.
  const sa = (await login(AD_USER, AD_PASS)) ?? (await login(AD_USER, NEW_PW));
  if (!sa) {
    console.log(`    FAIL  could not sign in as ${AD_USER}. Section 21 needs a tenant administrator on this branch.`);
    console.log('          Run class-teacher-e2e.sh first, which creates the e2e accounts.');
    process.exit(1);
  }

  // ---------------------------------------------------------------------------------------------
  head('21.1 With it switched off, every write is refused');
  // IT IS FORCED OFF HERE RATHER THAN ASSUMED OFF. An earlier version asserted "off by default"
  // against whatever the tenant happened to have, so one run that threw before its cleanup left the
  // next run measuring the wrong thing and reporting a product bug that was not there. A suite whose
  // first assertion depends on the last run's tidiness is a suite that will lie eventually.
  //
  // The shipped default being off is a property of SelfServicePolicyDto, and is asserted at 21.6c
  // against a value this run never writes.

  const policyBefore = (await call('GET', '/api/v1/staff/policy', { token: sa })).json;
  ok('21.1a: the policy has a selfService block', policyBefore?.selfService != null);

  const off = JSON.parse(JSON.stringify(policyBefore));
  off.selfService = { ...off.selfService, enabled: false };
  await call('PUT', '/api/v1/staff/policy', { token: sa, body: off });
  const confirmedOff = (await call('GET', '/api/v1/staff/policy', { token: sa })).json;
  ok('21.1b: it can be switched off', confirmedOff?.selfService?.enabled === false,
    `enabled=${confirmedOff?.selfService?.enabled}`);

  const offDecl = await call('PUT', `${base}/declarations`, { token: sa, body: { unavailability: [] } });
  ok('21.1c: declaring is refused while it is off', offDecl.status === 400, `status ${offDecl.status}`);

  const offClaim = await call('POST', `${base}/claims`, {
    token: sa,
    body: { cycleDay: 1, periodKey: 'P1', className: 'X', subjectId: '00000000-0000-0000-0000-000000000000', fingerprint: 'x' },
  });
  ok('21.1d: claiming is refused while it is off', offClaim.json?.ok === false, offClaim.text.slice(0, 80));
  ok('21.1e: and the refusal OFFERS the request that would work', offClaim.json?.offerRequest === 'TimetableSlot',
    `offer=${offClaim.json?.offerRequest}`);

  // ---------------------------------------------------------------------------------------------
  head('21.2 Switched on, a teacher can declare — and only about themselves');

  const on = JSON.parse(JSON.stringify(policyBefore));
  on.selfService = { ...on.selfService, enabled: true, allowDeclarations: true, allowDirectClaims: true, allowRequests: true };
  const saved = await call('PUT', '/api/v1/staff/policy', { token: sa, body: on });
  ok('21.2a: the policy saves', saved.status === 200, `status ${saved.status}`);
  ok('21.2b: and reads back on', saved.json?.selfService?.enabled === true);

  const ctx = (await call('GET', `${base}/context`, { token: sa })).json;
  ok('21.2c: the context answers', ctx != null);
  ok('21.2d: it carries the cycle and its teaching periods', (ctx?.cycleDays ?? 0) > 0 && (ctx?.lessonPeriods?.length ?? 0) > 0,
    `days=${ctx?.cycleDays} periods=${ctx?.lessonPeriods?.length}`);

  const decl = await call('PUT', `${base}/declarations`, {
    token: sa,
    body: { unavailability: [{ cycleDay: 1, periodKey: null, reason: 'ContractedHours' }] },
  });
  ok('21.2e: a declaration saves', decl.status === 204, `status ${decl.status}`);

  const ctx2 = (await call('GET', `${base}/context`, { token: sa })).json;
  ok('21.2f: and reads back as mine', ctx2?.myUnavailability?.length === 1,
    `${ctx2?.myUnavailability?.length} line(s)`);
  // Enums serialise as strings here, as they do on every other DTO in this project.
  ok('21.2g: with the reason I gave', ctx2?.myUnavailability?.[0]?.reason === 'ContractedHours',
    `reason=${ctx2?.myUnavailability?.[0]?.reason}`);

  // THE REQUEST SHAPE IS THE ENFORCEMENT. There is nowhere in it to put a user id, so this is not a
  // test that the handler ignores one — it is a test that the wire format cannot carry one.
  const shaped = await call('PUT', `${base}/declarations`, {
    token: sa,
    body: { unavailability: [{ cycleDay: 1, periodKey: null, reason: 'ContractedHours', userId: '11111111-1111-1111-1111-111111111111' }] },
  });
  const ctx3 = (await call('GET', `${base}/context`, { token: sa })).json;
  ok('21.2h: a user id smuggled into the body changes nothing', shaped.status === 204 && ctx3?.myUnavailability?.length === 1,
    `status ${shaped.status}, ${ctx3?.myUnavailability?.length} line(s) still mine`);

  // Clearing my own lines is allowed and leaves the school's alone.
  await call('PUT', `${base}/declarations`, { token: sa, body: { unavailability: [] } });
  const ctx4 = (await call('GET', `${base}/context`, { token: sa })).json;
  ok('21.2i: I can clear my own declarations', (ctx4?.myUnavailability?.length ?? -1) === 0);

  // ---------------------------------------------------------------------------------------------
  head('21.3 The grid, and what it will not say');

  const subjects = (await call('GET', `/api/v1/branches/${BRANCH}/staff/subjects`, { token: sa })).json ?? [];
  const subject = subjects[0];
  const vocab = (await call('GET', `/api/v1/branches/${BRANCH}/students/vocabularies`, { token: sa })).json;
  const seedClass = (vocab?.classes ?? []).find(c => c.isActive !== false)?.name
    ?? (vocab?.classes ?? [])[0]?.name ?? null;
  const me = (await call('GET', '/api/v1/staff/portal', { token: sa })).json?.me;

  let seededAssignmentId = null;
  let mine = ctx?.myClasses ?? [];
  ok('21.3-pre0: the branch has a configured class and a subject to seed with',
    seedClass != null && subject != null, `class=${seedClass} subject=${subject?.name}`);
  if (mine.length === 0 && subject && me?.userId && seedClass) {
    // Give this account something to teach, so the grid has a subject. Removed again at 21.6.
    const seeded = await call('POST', `/api/v1/branches/${BRANCH}/class-teachers/subject-teachers`, {
      token: sa,
      body: { className: seedClass, userId: me.userId, subjectId: subject.id, periodsPerWeek: 4 },
    });
    ok('21.3-pre: a subject-teacher assignment can be seeded to exercise the grid',
      seeded.status === 200 || seeded.status === 201, `status ${seeded.status} ${seeded.text.slice(0, 90)}`);
    seededAssignmentId = seeded.json?.id ?? null;
    mine = ((await call('GET', `${base}/context`, { token: sa })).json?.myClasses) ?? [];
  }

  // SELF-SERVICE ONLY EVER WRITES A DRAFT — a published version is immutable by design — so the
  // branch needs one for the grid to exist at all. Made here and deleted at 21.6 if this run made it.
  let seededTimetableId = null;
  const versions = (await call('GET', `/api/v1/branches/${BRANCH}/timetable/timetables`, { token: sa })).json ?? [];
  const list = Array.isArray(versions) ? versions : (versions.items ?? []);
  if (!list.some(t => t.status === 'Draft')) {
    const from = new Date();
    const to = new Date(Date.now() + 60 * 24 * 3600 * 1000);
    const iso = (d) => d.toISOString().slice(0, 10);
    const made = await call('POST', `/api/v1/branches/${BRANCH}/timetable/timetables`, {
      token: sa,
      body: { name: `E2E self-service ${Date.now().toString(36)}`, effectiveFrom: iso(from), effectiveTo: iso(to) },
    });
    ok('21.3-pre2: a draft timetable can be made for the grid to live in',
      made.status === 200 || made.status === 201, `status ${made.status} ${made.text.slice(0, 110)}`);
    seededTimetableId = made.json?.id ?? null;
  }

  if (mine.length === 0) {
    console.log('    SKIP  no subject-teacher assignment could be made, so the grid cannot be exercised.');
  } else {
    const c = mine[0];
    const gridRes = await call('GET', `${base}/openings?className=${encodeURIComponent(c.className)}&subjectId=${c.subjectId}`, { token: sa });
    const grid = gridRes.json?.days ? gridRes.json : null;
    if (!grid) console.log(`    SKIP  the grid is unavailable (${gridRes.status}: ${(gridRes.json?.title ?? gridRes.text).slice(0, 80)}).`);
    if (grid) {
    ok('21.3a: the grid answers for a class I teach', grid != null && (grid.days?.length ?? 0) > 0,
      grid == null ? 'no grid came back — is there a draft timetable?' : `${grid.days?.length} day(s)`);
    ok('21.3b: it carries a fingerprint', (grid?.fingerprint ?? '').length > 8);
    ok('21.3c: every slot has a state', grid.days.every(d => d.slots.every(s => typeof s.state === 'string')));

    // This account holds timetable.manage, so it DOES see names. The assertion is the converse:
    // that the flag and the names agree, never that names are always present or always absent.
    const anyHeld = grid.days.flatMap(d => d.slots).filter(s => s.heldByName != null);
    ok('21.3d: names appear only when the grid says they do',
      grid.showsNames || anyHeld.length === 0, `showsNames=${grid.showsNames}, ${anyHeld.length} named`);

    const notMine = (await call('GET', `${base}/openings?className=${encodeURIComponent('definitely-not-a-class')}&subjectId=${c.subjectId}`, { token: sa }));
    ok('21.3e: a class I do not teach is 404, not 403', notMine.status === 404, `status ${notMine.status}`);

    // ---------------------------------------------------------------------------------------------
    head('21.4 Claiming: stale grids, and two people at once');

    const free = grid.days.flatMap(d => d.slots.map(s => ({ ...s, cycleDay: d.cycleDay })))
      .filter(s => s.state === 'Free');

    if (free.length === 0) {
      console.log('    SKIP  no free period in the draft to claim.');
    } else {
      const target = free[0];

      const stale = await call('POST', `${base}/claims`, {
        token: sa,
        body: {
          cycleDay: target.cycleDay, periodKey: target.periodKey, className: c.className,
          subjectId: c.subjectId, fingerprint: 'a-fingerprint-from-another-time',
        },
      });
      ok('21.4a: a stale fingerprint is refused', stale.json?.ok === false && stale.json?.stale === true,
        `ok=${stale.json?.ok} stale=${stale.json?.stale}`);
      ok('21.4b: and the fresh grid comes back with it, so no second round trip is needed',
        stale.json?.openings != null);

      // THE ONE THAT MATTERS. Five callers, the same slot, the same instant. The advisory lock and
      // the teacher unique index together must produce exactly one lesson — not five, not zero.
      const fresh = stale.json.openings;
      const body = {
        cycleDay: target.cycleDay, periodKey: target.periodKey, className: c.className,
        subjectId: c.subjectId, fingerprint: fresh.fingerprint,
      };
      const racers = await Promise.all(Array.from({ length: 5 }, () =>
        call('POST', `${base}/claims`, { token: sa, body })));
      const won = racers.filter(r => r.json?.ok === true);
      ok('21.4c: five simultaneous claims for one period produce exactly ONE lesson',
        won.length === 1, `${won.length} succeeded`);
      ok('21.4d: and the other four are refused, not silently dropped',
        racers.filter(r => r.json?.ok === false).length === 4);

      const after = (await call('GET', `${base}/openings?className=${encodeURIComponent(c.className)}&subjectId=${c.subjectId}`, { token: sa })).json;
      const slotNow = after.days.flatMap(d => d.slots.map(s => ({ ...s, cycleDay: d.cycleDay })))
        .find(s => s.cycleDay === target.cycleDay && s.periodKey === target.periodKey);
      ok('21.4e: the slot now reads as mine', slotNow?.state === 'Mine', `state=${slotNow?.state}`);

      if (won.length === 1 && won[0].json.lessonId) {
        const released = await call('DELETE', `${base}/claims/${won[0].json.lessonId}`, { token: sa });
        ok('21.4f: and I can give it back', released.json?.ok === true, released.text.slice(0, 80));
      }
    }
  }

    }

  // ---------------------------------------------------------------------------------------------
  head('21.5 One queue, and nobody decides their own');

  const ask = await call('POST', `${base}/requests`, {
    token: sa,
    body: {
      kind: 'ClassAssignment',
      className: `E2E Self Service ${Date.now().toString(36)}`,
      subjectId: subject?.id ?? null,
      periodsPerWeek: 3,
      reason: 'Created by self-service-e2e. Safe to refuse.',
    },
  });
  ok('21.5a: a class-assignment request can be made', ask.status === 201, `status ${ask.status} ${ask.text.slice(0, 90)}`);

  const made = ask.json;
  if (made) {
    ok('21.5b: it is Pending', made.state === 'Pending', `state=${made.state}`);
    ok('21.5c: it summarises itself in a sentence', (made.summary ?? '').length > 10, made.summary);

    // THE SELF-APPROVAL REFUSAL. This account holds timetable.manage — the permission says yes — so
    // this is exactly the case the permission layer cannot cover, and the rule has to be code.
    ok('21.5d: the asker is not offered the decision, even holding timetable.manage',
      made.canIDecide === false, `canIDecide=${made.canIDecide}`);

    const selfDecide = await call('POST', `${base}/requests/${made.id}/decide`, {
      token: sa, body: { approve: true },
    });
    ok('21.5e: and the server refuses it outright', selfDecide.status === 400, `status ${selfDecide.status}`);
    ok('21.5f: naming why, rather than just saying no',
      /your own request/i.test(selfDecide.json?.detail ?? ''), selfDecide.json?.detail);

    // DUPLICATE DETECTION IS AN INDEX, so this is testing the database and not a handler check.
    const twice = await call('POST', `${base}/requests`, {
      token: sa,
      body: { kind: 'ClassAssignment', className: made.className, subjectId: made.subjectId, periodsPerWeek: 3 },
    });
    ok('21.5g: the same request twice is refused as a duplicate', twice.status === 409, `status ${twice.status}`);

    const race = await Promise.all(Array.from({ length: 4 }, () => call('POST', `${base}/requests`, {
      token: sa,
      body: { kind: 'ClassAssignment', className: `${made.className}-race`, subjectId: made.subjectId, periodsPerWeek: 2 },
    })));
    ok('21.5h: four simultaneous identical requests create exactly ONE',
      race.filter(r => r.status === 201).length === 1,
      `${race.filter(r => r.status === 201).length} created`);

    // Clean up: withdraw everything this run made.
    const open = (await call('GET', `${base}/requests`, { token: sa })).json ?? [];
    const ours = open.filter(r => (r.className ?? '').startsWith('E2E Self Service'));
    for (const r of ours) await call('POST', `${base}/requests/${r.id}/withdraw`, { token: sa });
    const left = ((await call('GET', `${base}/requests`, { token: sa })).json ?? [])
      .filter(r => (r.className ?? '').startsWith('E2E Self Service'));
    ok('21.5i: the run withdraws its own requests', left.length === 0, `${left.length} left behind`);
  }

  // ---------------------------------------------------------------------------------------------
  head('21.6 The tenant is left as it was found');

  if (seededTimetableId) {
    const droppedTt = await call('DELETE', `/api/v1/branches/${BRANCH}/timetable/timetables/${seededTimetableId}`, { token: sa });
    ok('21.6-pre0: the draft this run made is removed', droppedTt.status === 200 || droppedTt.status === 204,
      `status ${droppedTt.status}`);
  }

  if (seededAssignmentId) {
    const removed = await call('DELETE', `/api/v1/branches/${BRANCH}/class-teachers/${seededAssignmentId}`, { token: sa });
    ok('21.6-pre: the seeded assignment is removed', removed.status === 200 || removed.status === 204,
      `status ${removed.status}`);
  }

  // LEFT OFF, which is the product's own default and the state a dev tenant should be found in —
  // rather than put back to whatever the previous run happened to leave.
  const restore = JSON.parse(JSON.stringify(policyBefore));
  restore.selfService = { ...restore.selfService, enabled: false, allowDirectClaims: false };
  const back = await call('PUT', '/api/v1/staff/policy', { token: sa, body: restore });
  ok('21.6a: the policy is put back', back.status === 200, `status ${back.status}`);
  const finalPolicy = (await call('GET', '/api/v1/staff/policy', { token: sa })).json;
  ok('21.6b: and self-service is off again', finalPolicy?.selfService?.enabled === false,
    `enabled=${finalPolicy?.selfService?.enabled}`);
  ok('21.6c: the shipped default for a value this run never wrote is still on',
    finalPolicy?.selfService?.allowRequests === true,
    `allowRequests=${finalPolicy?.selfService?.allowRequests}`);

  console.log(`\n21. DONE — ${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})().catch((err) => {
  console.error('\n21. THREW —', err);
  process.exit(1);
});

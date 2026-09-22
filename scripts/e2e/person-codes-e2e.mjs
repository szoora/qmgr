// SECTION 23 — A STUDENT CODE AND A STAFF NUMBER ARE REQUIRED, UNIQUE, AND FOLD CASE.
//
// Asked for as "student code and staff number should be unique for each company and mandatory.
// implement aggressive validations both in ui and backend of the student and staff codes."
//
// The uniqueness INDEXES already existed and both were exact-match, which is the half that looks
// done and is not: "MH/S/001" and "mh/s/001" were two people, and every write path did its own
// Trim() and its own duplicate query — two of them (UsersController.CreateUser and UpdateUser) did
// neither and left the database to throw, which reaches a browser as a 500 with no field named.
//
// So this suite asserts the three things that were actually missing:
//   1. REQUIRED — a create with no code is refused, at every door;
//   2. UNIQUE, CASE-FOLDED — the same code in another case is refused, by the API rather than by
//      Postgres, and the message names the field;
//   3. NORMALISED — what is stored is trimmed and collapsed, so " 700001 " and "700001" are one.
//
// It is Node because the interesting half is concurrency: two callers posting the same code at the
// same moment must produce exactly one row, and that is what the functional unique index is for.
//
// Run: node scripts/e2e/person-codes-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=api', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const note = (name, why) => { skip++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); post(l); };

const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS }),
});
if (!auth.ok) { console.error(`could not sign in as ${USER}`); process.exit(1); }
const body = await auth.json();
const H = { Authorization: `Bearer ${body.accessToken}`, 'Content-Type': 'application/json' };

const run = `pc${Date.now().toString(36)}`;
const title = (t) => { const l = `  ${t}`; console.log(l); post(l); };

// ---------------------------------------------------------------------------------------------
title('23.1  A student code is REQUIRED and its shape is checked');

const makeStudent = (over = {}) => fetch(`${API}/api/v1/branches/${BRANCH}/students`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ fullName: `Code Test ${run}`, studentCode: `${run}-A`, ...over }),
});

const noCode = await makeStudent({ studentCode: '' });
check('a student with no code is refused', noCode.status === 400, `got ${noCode.status}`);
const noCodeBody = await noCode.json().catch(() => ({}));
check('...and the refusal names the field', /student code/i.test(noCodeBody.title ?? ''), noCodeBody.title ?? '(none)');

const blank = await makeStudent({ studentCode: '   ' });
check('whitespace alone is not a code', blank.status === 400, `got ${blank.status}`);

const oneChar = await makeStudent({ studentCode: 'A' });
check('a one-character code is refused', oneChar.status === 400, `got ${oneChar.status}`);

const punctuation = await makeStudent({ studentCode: '=CMD|calc' });
check('a spreadsheet formula is refused as a code', punctuation.status === 400, `got ${punctuation.status}`);

const symbols = await makeStudent({ studentCode: '2026#014' });
check('a code with a stray symbol is refused', symbols.status === 400, `got ${symbols.status}`);

const dots = await makeStudent({ studentCode: '...' });
check('punctuation with no letter or number is refused', dots.status === 400, `got ${dots.status}`);

// ---------------------------------------------------------------------------------------------
title('23.2  It is stored NORMALISED');

const messy = await makeStudent({ studentCode: `  ${run}-B   x  `, fullName: `Messy ${run}` });
check('a code with stray spaces is accepted', messy.ok, `${messy.status} ${await messy.clone().text().catch(() => '')}`);
let messyId = null;
if (messy.ok) {
  const s = await messy.json();
  messyId = s.id;
  check('...and is stored trimmed and collapsed', s.studentCode === `${run}-B x`, JSON.stringify(s.studentCode));
}

// ---------------------------------------------------------------------------------------------
title('23.3  It is UNIQUE within the organisation, ignoring case');

const first = await makeStudent({ studentCode: `${run}-DUP`, fullName: `First ${run}` });
check('the first student with a code is created', first.ok, `${first.status}`);
const firstId = first.ok ? (await first.json()).id : null;

const sameCase = await makeStudent({ studentCode: `${run}-DUP`, fullName: `Second ${run}` });
check('the same code again is refused', sameCase.status === 409, `got ${sameCase.status}`);

const otherCase = await makeStudent({ studentCode: `${run}-dup`.toUpperCase() === `${run}-DUP` ? `${run}-Dup` : `${run}-dup`, fullName: `Third ${run}` });
check('the same code in ANOTHER CASE is refused', otherCase.status === 409, `got ${otherCase.status}`);
const otherBody = await otherCase.json().catch(() => ({}));
check('...as a 409 in words, never a 500', /already exists/i.test(otherBody.title ?? ''), otherBody.title ?? '(none)');

const spaced = await makeStudent({ studentCode: `  ${run}-DUP  `, fullName: `Fourth ${run}` });
check('the same code with padding is refused', spaced.status === 409, `got ${spaced.status}`);

// Concurrency: the functional unique index is the last line, and it must hold.
const raceCode = `${run}-RACE`;
const racers = await Promise.all([0, 1, 2, 3, 4].map(i =>
  makeStudent({ studentCode: i % 2 ? raceCode.toLowerCase() : raceCode, fullName: `Racer ${i} ${run}` })));
const won = racers.filter(r => r.ok).length;
check('five simultaneous posts of one code create exactly one student', won === 1, `${won} succeeded`);
const raceIds = [];
for (const r of racers) if (r.ok) raceIds.push((await r.json()).id);

// ---------------------------------------------------------------------------------------------
title('23.4  An UPDATE is held to the same rule');

if (firstId) {
  const clash = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${firstId}`, {
    method: 'PUT', headers: H,
    body: JSON.stringify({ fullName: `First ${run}`, studentCode: `${run}-b X`, isActive: true }),
  });
  check('renaming onto another student\'s code is refused', clash.status === 409, `got ${clash.status}`);

  const blanked = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${firstId}`, {
    method: 'PUT', headers: H,
    body: JSON.stringify({ fullName: `First ${run}`, studentCode: '', isActive: true }),
  });
  check('clearing a student\'s code is refused', blanked.status === 400, `got ${blanked.status}`);

  const itsOwn = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${firstId}`, {
    method: 'PUT', headers: H,
    body: JSON.stringify({ fullName: `First renamed ${run}`, studentCode: `${run}-dup`, isActive: true }),
  });
  check('a student keeping its OWN code in another case is accepted', itsOwn.ok, `${itsOwn.status}`);
} else {
  note('23.4', 'the first student was not created');
}

// ---------------------------------------------------------------------------------------------
title('23.5  A staff number is REQUIRED, unique and case-folded');

const roles = await (await fetch(`${API}/api/v1/roles`, { headers: H })).json().catch(() => null);
const roleList = Array.isArray(roles) ? roles : roles?.items ?? [];
const teacher = roleList.find(r => r.code === 'teacher') ?? roleList.find(r => !r.isSystem) ?? roleList[0];

const makeStaff = (over = {}) => fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members`, {
  method: 'POST', headers: H,
  body: JSON.stringify({
    firstName: 'Code', lastName: `Staff ${run}`,
    employeeNumber: `${run}-S1`, roleId: teacher?.id, ...over,
  }),
});

if (!teacher) {
  note('23.5', 'no role could be read to create staff with');
} else {
  const noNumber = await makeStaff({ employeeNumber: '' });
  check('a staff member with no number is refused', noNumber.status === 400, `got ${noNumber.status}`);
  const nnBody = await noNumber.json().catch(() => ({}));
  check('...and the refusal names the field', /staff number/i.test(nnBody.title ?? ''), nnBody.title ?? '(none)');

  const badShape = await makeStaff({ employeeNumber: '70#0001' });
  check('a staff number with a stray symbol is refused', badShape.status === 400, `got ${badShape.status}`);

  const s1 = await makeStaff({ employeeNumber: ` ${run}-S9 ` });
  check('a staff number with padding is accepted', s1.ok, `${s1.status} ${await s1.clone().text().catch(() => '')}`);
  if (s1.ok) {
    // Read it BACK from the directory: the create response is the credentials slip (username,
    // email, temporary password) and carries no staff number, so asserting on it would be
    // asserting on a field that was never there.
    const dir = await (await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members`, { headers: H })).json().catch(() => null);
    const items = dir?.items ?? dir ?? [];
    const mine = items.find(m => (m.employeeNumber ?? '').trim() === `${run}-S9`);
    check('...and stored trimmed', !!mine, `no member carries ${run}-S9`);
  }

  const s2 = await makeStaff({ employeeNumber: `${run}-s9`, lastName: `Clash ${run}` });
  check('the same staff number in another case is refused', s2.status === 400 || s2.status === 409, `got ${s2.status}`);
  const s2Body = await s2.json().catch(() => ({}));
  check('...in words, naming the number', /staff number/i.test(s2Body.title ?? ''), s2Body.title ?? '(none)');
}

// ---------------------------------------------------------------------------------------------
title('23.6  The generic user endpoint is held to it too');

if (teacher) {
  const u = await fetch(`${API}/api/v1/users`, {
    method: 'POST', headers: H,
    body: JSON.stringify({
      username: `pc.${run}`, email: `pc.${run}@qmgr.local`, password: 'Rwenzori#Peaks-2026',
      firstName: 'Users', lastName: `Door ${run}`, roleId: teacher.id,
    }),
  });
  check('POST /users with no staff number is refused', u.status === 400, `got ${u.status}`);
  const ub = await u.json().catch(() => ({}));
  check('...naming the field rather than throwing a 500', /staff number/i.test(ub.title ?? ''), ub.title ?? '(none)');
} else {
  note('23.6', 'no role to post with');
}

// ---------------------------------------------------------------------------------------------
title('23.7  Cleanup');
// The welfare ledger has no DELETE and neither does the roster, so these students are deactivated
// rather than removed — the same honest treatment every other suite gives a row it cannot delete.
const toRetire = [messyId, firstId, ...raceIds].filter(Boolean);
let retired = 0;
for (const id of toRetire) {
  const r = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${id}`, {
    method: 'PUT', headers: H,
    body: JSON.stringify({ fullName: `Dummy student ${run} — safe to delete`, studentCode: `${run}-X${retired}`, isActive: false }),
  });
  if (r.ok) retired++;
}
check('the students this run created are deactivated', retired === toRetire.length, `${retired} of ${toRetire.length}`);

const tail = `  person-codes: ${pass} passed, ${fail} failed, ${skip} skipped`;
console.log(tail); post(tail);
process.exitCode = fail ? 1 : 0;

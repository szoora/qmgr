// SECTION 24 — RE-IMPORTING A SHEET: what would change, and who might be one person twice.
//
// Asked for as "importing the file again for both staff and student should detect any student
// account or staff code (unique on this project) and suggest updation, in case the data is
// different". A school re-imports its own sheet every term, so a re-import IS the normal case —
// and until this was built, the reader learned how much of the file landed on people already on
// file only from the summary AFTERWARDS, and never learned WHAT it had changed about them.
//
// Two endpoints, one shape:
//   POST branches/{b}/staff/import-jobs/precheck      (widened)
//   POST branches/{b}/students/import-jobs/precheck   (new)
//
// THE RULE BOTH HALVES REST ON: a value the file LEFT BLANK never changes what is stored. A sheet
// exported from a system that does not hold national IDs must not blank the school ones. The
// comparison is StaffImportChanges / ImportMatching — the same code the import itself runs, so a
// preview that says "nothing will change" and an import that then changes three fields cannot
// happen. That is what 24.3 and 24.8 assert.
//
// The answer names the FIELDS and never the stored values: a reader deciding whether to overwrite
// does not need every colleague national ID read back to their browser to be told so (24.5).
//
// Run: API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/import-precheck-e2e.mjs
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS_WORD = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0, skip = 0;
const check = (n, ok, d = '') => { ok ? pass++ : fail++; console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${n}${ok ? '' : '  — ' + d}`); };
const note = (n, w) => { skip++; console.log(`  SKIP  ${n}  — ${w}`); };
const done = () => { console.log(`\n  ${pass} passed, ${fail} failed, ${skip} skipped`); process.exitCode = fail ? 1 : 0; };

const auth = await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: USER, password: PASS_WORD }),
});
if (!auth.ok) { console.error(`  could not sign in as ${USER}`); process.exit(1); }
const H = { Authorization: `Bearer ${(await auth.json()).accessToken}`, 'Content-Type': 'application/json' };
const run = Date.now().toString(36).slice(-4);

// ---------------------------------------------------------------------------------------------
console.log('  24.1-24.6  Staff: who is already here, and what would move');
// ---------------------------------------------------------------------------------------------
const dir = await (await fetch(`${API}/api/v1/branches/${BRANCH}/staff/structure/members?pageSize=50`, { headers: H })).json().catch(() => null);
const people = (dir?.items ?? dir ?? []).filter(p => p.email || p.employeeNumber);

if (people.length < 2) {
  note('the staff half', `this branch has ${people.length} member(s) of staff with a key — two are needed`);
} else {
  const a = people[0], b = people[1];

  // Rows 1 and 2 carry NO name: the directory returns one combined name and the stored split cannot
  // be derived from it, and a blank never changes what is stored — which is the rule under test.
  const rows = [
    { firstName: '', lastName: '', email: a.email ?? '', employeeNumber: a.employeeNumber ?? '', jobTitle: a.jobTitle ?? '' },
    { firstName: '', lastName: '', email: b.email ?? '', employeeNumber: b.employeeNumber ?? '', jobTitle: `Zz Title ${run}` },
    { firstName: 'Zz', lastName: `Twin ${run}`, email: '', employeeNumber: `ZZ${run}A`, jobTitle: 'Teacher' },
    { firstName: `Twin ${run}`, lastName: 'Zz', email: '', employeeNumber: `ZZ${run}B`, jobTitle: 'Teacher' },
  ];

  const res = await fetch(`${API}/api/v1/branches/${BRANCH}/staff/import-jobs/precheck`, {
    method: 'POST', headers: H, body: JSON.stringify({ rows, nameOrder: 'GivenFirst' }),
  });
  check('24.1  the staff precheck answers', res.ok, String(res.status));

  if (res.ok) {
    const out = await res.json();
    const changed = out.existing.filter(e => e.changes.length > 0);

    check('24.2  both people are reported as already here', out.existing.length >= 2,
      JSON.stringify(out.existing.map(e => e.fullName)));
    check('24.3  a row identical to the record changes nothing',
      out.existing.some(e => e.fullName === (a.fullName ?? '').trim() && e.changes.length === 0),
      JSON.stringify(out.existing.map(e => [e.fullName, e.changes])));
    check('24.4  the row with a different job title names that field',
      changed.length === 1 && changed[0].changes.some(c => /job title/i.test(c)),
      JSON.stringify(changed.map(e => [e.fullName, e.changes])));
    check('24.5  the answer never carries a stored value',
      !JSON.stringify(out.existing).includes(`Zz Title ${run}`),
      'the value from the file came back in the answer');

    const within = out.possibleDuplicates.filter(d => d.withinFile);
    check('24.6  one name under two staff numbers is raised, both ways round',
      within.some(d => d.name.includes(`Twin ${run}`)) && within.some(d => /different staff numbers/i.test(d.detail)),
      JSON.stringify(out.possibleDuplicates));
  }
}

// ---------------------------------------------------------------------------------------------
console.log('  24.7-24.11  Students: the same question about the roll');
// ---------------------------------------------------------------------------------------------
const roll = await (await fetch(`${API}/api/v1/branches/${BRANCH}/students?pageSize=50`, { headers: H })).json().catch(() => null);
const students = (roll?.items ?? roll ?? []).filter(s => s.studentCode && s.fullName);

if (students.length < 2) {
  note('the student half', `this branch has ${students.length} student(s) with an admission number — two are needed`);
} else {
  const same = students[0], moved = students[1];
  const guardian = `Zz Guardian ${run}`;
  const phone = `+25677${Math.floor(1000000 + Math.random() * 8999999)}`;
  const movedTo = `${moved.className ?? 'S1'}-MOVED`;

  // A guardian phone is required on every row, and a guardian the roll does not hold is itself a
  // real change — so the fixture gives BOTH rows the same seeded guardian, leaving the class as the
  // only difference. Without this the unchanged row is not unchanged, and 24.8 fails on the file
  // rather than on the product. That happened on the first run of this suite.
  const cleanup = [];
  for (const st of [same, moved]) {
    const r = await fetch(`${API}/api/v1/branches/${BRANCH}/students/${st.id}/guardians`, {
      method: 'POST', headers: H,
      body: JSON.stringify({ fullName: guardian, phone, relationship: 'Guardian' }),
    });
    if (r.ok) { const l = await r.json().catch(() => null); if (l?.id) cleanup.push([st.id, l.id]); }
  }

  const rows = [
    { studentCode: same.studentCode, studentFullName: same.fullName, className: same.className ?? '', guardianFullName: guardian, guardianPhone: phone },
    { studentCode: moved.studentCode, studentFullName: moved.fullName, className: movedTo, guardianFullName: guardian, guardianPhone: phone },
    { studentCode: `ZZ${run}A`, studentFullName: `Zz Twin Case ${run}`, className: 'S1A', guardianFullName: guardian, guardianPhone: phone },
    { studentCode: `ZZ${run}B`, studentFullName: `${run} Case Twin Zz`, className: 'S1A', guardianFullName: guardian, guardianPhone: phone },
  ];

  const res = await fetch(`${API}/api/v1/branches/${BRANCH}/students/import-jobs/precheck`, {
    method: 'POST', headers: H, body: JSON.stringify({ rows }),
  });
  check('24.7  the student precheck answers', res.ok, String(res.status));

  if (res.ok) {
    const out = await res.json();
    const changed = out.existing.filter(e => e.changes.length > 0);

    check('24.8  a row identical to the roll changes nothing',
      out.existing.some(e => e.studentCode === same.studentCode && e.changes.length === 0),
      JSON.stringify(out.existing.map(e => [e.studentCode, e.changes])));
    check('24.9  the moved child is named, and the field is "class"',
      changed.length === 1 && changed[0].studentCode === moved.studentCode && changed[0].changes.some(c => /class/i.test(c)),
      JSON.stringify(changed.map(e => [e.studentCode, e.changes])));
    check('24.10  never the value it would be changed TO',
      !JSON.stringify(out.existing).includes(movedTo),
      'the new class came back in the answer');

    const within = out.possibleDuplicates.filter(d => d.withinFile);
    check('24.11  one name under two admission numbers is raised, both ways round',
      within.some(d => /Twin/.test(d.name)) && within.some(d => /different admission numbers/i.test(d.detail)),
      JSON.stringify(out.possibleDuplicates));
  }

  for (const [s, l] of cleanup) {
    await fetch(`${API}/api/v1/branches/${BRANCH}/students/${s}/guardians/${l}`, { method: 'DELETE', headers: H });
  }
  check('24.12  CLEANUP: the seeded guardians are removed', cleanup.length === 2, `${cleanup.length} of 2 were seeded`);
}

done();

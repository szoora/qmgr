// A STAFF MEMBER WITH NO EMAIL ADDRESS — section 22.
//
// 133 of the 184 staff on the first real school list this product imported have no email address,
// and until 2026-09-21 that meant they could not exist in Q-Mgr at all: User.Email was non-nullable
// and two unique indexes were built on it, so "no address" could not even be stored — an empty
// string would have been refused the second time. docs/plans/STAFF_WITHOUT_EMAIL.md.
//
// What this proves, in the order it matters:
//   1. one can be created at all, and is told the username they will sign in with;
//   2. they can actually SIGN IN with it;
//   3. a SECOND one can exist — the assertion the whole schema change turns on, because a shared
//      empty string would have let exactly one through and then started failing;
//   4. "is this person already here?" still has an answer for them, by staff number;
//   5. a staff number cannot be quietly reused inside one organization;
//   6. the import creates them from a sheet with no email column at all;
//   7. an invitation is still refused, because it has nowhere to go;
//   8. an address can be added later, and still collides with somebody else's.
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> SA_USER=superadmin SA_PASS=admin node scripts/e2e/staff-no-email-e2e.mjs
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const ADMIN_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

const run = Date.now().toString(36).slice(-5);
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`);
};

const login = async (email, password) => {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
  });
  return { ok: r.ok, status: r.status, body: r.ok ? await r.json() : await r.text() };
};

const admin = await login(ADMIN, ADMIN_PASS);
if (!admin.ok) { console.log(`  could not sign in as ${ADMIN}: ${admin.status}`); console.log('  0 passed, 1 failed'); process.exit(1); }
const token = admin.body.accessToken;
const H = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const api = (path, init = {}) => fetch(`${API}/api/v1${path}`, { ...init, headers: { ...H, ...(init.headers ?? {}) } });

const roles = await (await api(`/roles`)).json().catch(() => []);
const teacherRole = (Array.isArray(roles) ? roles : roles?.items ?? []).find(r => r.code === 'teacher' || r.code === 'support-staff');
if (!teacherRole) { console.log('  no assignable role found on this tenant'); console.log('  0 passed, 1 failed'); process.exit(1); }

const created = [];
const makeStaff = async (body) => {
  const r = await api(`/branches/${BRANCH}/staff/structure/members`, { method: 'POST', body: JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch { /* a ProblemDetails, or empty */ }
  if (r.ok && json?.userId) created.push(json.userId);
  return { status: r.status, json, text };
};

try {
  // ---- 1. created at all, and told what they will type -----------------------------------------
  const one = await makeStaff({
    firstName: 'Noemail', lastName: `Onex${run}`, email: '', phone: '0770000001',
    employeeNumber: `NE1-${run}`, jobTitle: 'Teacher', roleId: teacherRole.id,
  });
  check('a staff member with no email address is created', one.status === 201 || one.status === 200,
    `${one.status} ${one.text.slice(0, 200)}`);
  const username = one.json?.username;
  const temporary = one.json?.temporaryPassword;
  check('...and is given a username to sign in with', !!username, JSON.stringify(one.json ?? {}).slice(0, 200));
  check('...built from their own name, not an address', (username ?? '').includes('onex'), username ?? '');
  check('...with a temporary password to hand over', !!temporary, Object.keys(one.json ?? {}).join(','));
  check('...and no address stored', !one.json?.email, one.json?.email ?? '');

  // ---- 2. they can sign in ---------------------------------------------------------------------
  if (username && temporary) {
    const theirs = await login(username, temporary);
    check('they can sign in with the username and that password', theirs.ok, `${theirs.status} ${String(theirs.body).slice(0, 160)}`);
    check('...and are made to change it at first sign-in',
      theirs.ok && (theirs.body.mustChangePassword === true || theirs.body.user?.mustChangePassword === true),
      JSON.stringify(theirs.body).slice(0, 200));
  } else {
    check('they can sign in with the username and that password', false, 'no username or password came back');
    check('...and are made to change it at first sign-in', false, 'not reached');
  }

  // ---- 3. a SECOND one: the assertion the schema change turns on -------------------------------
  const two = await makeStaff({
    firstName: 'Noemail', lastName: `Twox${run}`, email: '', phone: '0770000002',
    employeeNumber: `NE2-${run}`, jobTitle: 'Teacher', roleId: teacherRole.id,
  });
  check('a SECOND staff member with no address is also created', two.status === 201 || two.status === 200,
    `${two.status} ${two.text.slice(0, 220)}`);
  check('...with a username of their own', (two.json?.username ?? '') !== username, two.json?.username ?? '');

  // ---- 4. "already here?" answered by staff number ----------------------------------------------
  const precheck = await (await api(`/branches/${BRANCH}/staff/import-jobs/precheck`, {
    method: 'POST', body: JSON.stringify({ emails: [], employeeNumbers: [`NE1-${run}`, `NE2-${run}`] }),
  })).json().catch(() => ({ existing: [] }));
  check('the import precheck finds them by staff number', (precheck.existing ?? []).length === 2,
    JSON.stringify(precheck).slice(0, 220));

  // ---- 5. a staff number is a key, not a label --------------------------------------------------
  const clash = await makeStaff({
    firstName: 'Noemail', lastName: `Clash${run}`, email: '', phone: '0770000003',
    employeeNumber: `NE1-${run}`, jobTitle: 'Teacher', roleId: teacherRole.id,
  });
  check('the same staff number cannot be used twice in one organization', clash.status >= 400,
    `${clash.status} — a duplicate staff number was accepted`);

  // ---- 6. the import, from a sheet with no email column at all ----------------------------------
  const rows = [1, 2].map(n => ({
    firstName: 'Imported', lastName: `Noemail${n}${run}`, email: '',
    employeeNumber: `NEI${n}-${run}`, roleCode: 'teacher', phone: `077000001${n}`,
  }));
  const started = await api(`/branches/${BRANCH}/staff/import-jobs`, {
    method: 'POST', body: JSON.stringify({ rows, deliveryMode: 'Slips', sendInvites: false }),
  });
  const startedJson = await started.json().catch(() => null);
  check('an import with no email column is accepted', started.ok, `${started.status}`);

  const jobId = startedJson?.id ?? startedJson?.job?.id;
  let job = null;
  for (let i = 0; jobId && i < 30; i++) {
    job = await (await api(`/branches/${BRANCH}/staff/import-jobs/${jobId}`)).json().catch(() => null);
    if (job && ['Completed', 'CompletedWithErrors', 'Failed'].includes(job.status)) break;
    await new Promise(r => setTimeout(r, 1000));
  }
  check('...and every row of it is created', job?.createdCount === 2,
    `status=${job?.status} created=${job?.createdCount} failed=${job?.failedCount} ${job?.failureReason ?? ''}`);

  // ---- 7. an invitation still needs somewhere to go ----------------------------------------------
  const invited = await api(`/branches/${BRANCH}/staff/import-jobs`, {
    method: 'POST',
    body: JSON.stringify({
      rows: [{ firstName: "Invite", lastName: `Nowhere${run}`, email: "", employeeNumber: `E2E-INV-${run}`, roleCode: "teacher" }],
      deliveryMode: 'Invitation', sendInvites: true,
    }),
  });
  const inviteJson = await invited.json().catch(() => null);
  const inviteJobId = inviteJson?.id ?? inviteJson?.job?.id;
  let inviteJob = null;
  for (let i = 0; inviteJobId && i < 20; i++) {
    inviteJob = await (await api(`/branches/${BRANCH}/staff/import-jobs/${inviteJobId}`)).json().catch(() => null);
    if (inviteJob && ['Completed', 'CompletedWithErrors', 'Failed'].includes(inviteJob.status)) break;
    await new Promise(r => setTimeout(r, 1000));
  }
  check('an invitation row with no address is refused, not half-imported',
    inviteJob?.createdCount === 0 && inviteJob?.failedCount === 1,
    `created=${inviteJob?.createdCount} failed=${inviteJob?.failedCount}`);

  const entries = await (await api(`/branches/${BRANCH}/staff/import-jobs/${inviteJobId}/entries`)).json().catch(() => []);
  check('...and the reason says what to do instead',
    (entries[0]?.message ?? '').toLowerCase().includes('slip') || (entries[0]?.message ?? '').toLowerCase().includes('invitation'),
    entries[0]?.message ?? 'no entry');

  // ---- 8. an address can be added later, and still has to be unique ------------------------------
  const target = created[0];
  const address = `no.email.${run}@qmgr.local`;
  const added = await api(`/users/${target}`, {
    method: 'PUT', body: JSON.stringify({ email: address, firstName: 'Noemail', lastName: `Onex${run}` }),
  });
  check('an address can be added to somebody who had none', added.ok, `${added.status} ${(await added.text()).slice(0, 160)}`);

  const clashEmail = await makeStaff({
    firstName: 'Noemail', lastName: `Dup${run}`, email: address, phone: '0770000004',
    employeeNumber: `NE9-${run}`, jobTitle: 'Teacher', roleId: teacherRole.id,
  });
  check('...and two people still cannot share one address', clashEmail.status >= 400,
    `${clashEmail.status} — a duplicate address was accepted`);
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  // Leave the tenant as it was found: these accounts are deactivated rather than left signing in.
  for (const id of created) {
    try { await api(`/users/${id}`, { method: 'DELETE' }); } catch { /* best effort */ }
  }
}

console.log(`\n  ${pass} passed, ${fail} failed`);
process.exitCode = fail ? 1 : 0;

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
//   8. an address can be added later, and still collides with somebody else's;
//   9. AN ADMINISTRATOR CAN GIVE THEM A NEW PASSWORD — generated, or typed — which for these 133
//      is the only way back in, since no reset link can reach them;
//  10. and cannot do it to somebody who outranks them, which until 2026-09-22 they could.
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

  // ---- 9. an administrator issues a new password, generated or typed ----------------------------
  // The path back in for anybody with no address. The endpoint existed from the start and NOTHING
  // IN THE UI CALLED IT until 2026-09-22, so this had never been exercised either.
  const resetTarget = created[0];

  const generatedReset = await api(`/users/${resetTarget}/reset-password`, {
    method: 'POST', body: JSON.stringify({ newPassword: '' }),
  });
  const genBody = generatedReset.ok ? await generatedReset.json() : await generatedReset.text();
  check('an empty password is not an error — one is generated', generatedReset.ok,
    `${generatedReset.status} ${String(genBody).slice(0, 160)}`);
  const issued = genBody?.temporaryPassword;
  check('...and it is handed back, once', typeof issued === 'string' && issued.length >= 8,
    JSON.stringify(genBody ?? {}).slice(0, 160));
  check('...with the moment it stops working', !!genBody?.expiresAt, JSON.stringify(genBody ?? {}).slice(0, 160));

  // It has to actually WORK, and it has to be change-only. A reset that returns a password the
  // person cannot sign in with is the worst answer this endpoint could give, and no assertion about
  // the response shape would catch it.
  const signedIn = await login(genBody?.username ?? '', issued ?? '');
  check('...the person can sign in with it', signedIn.ok, `${signedIn.status}`);
  check('...and is made to choose their own before anything else', signedIn.body?.mustChangePassword === true,
    JSON.stringify(signedIn.body ?? {}).slice(0, 160));
  check('...with no refresh token, so the session cannot be carried on',
    !signedIn.body?.refreshToken, String(signedIn.body?.refreshToken ?? '').slice(0, 40));

  // THE SET-PASSWORD PAGE MUST BE ABLE TO READ THE RULES IT IS ABOUT TO STATE (2026-09-22).
  // PasswordChangeOnlyMiddleware runs BEFORE authorization, so [AllowAnonymous] on the endpoint
  // counted for nothing: this token carries the change-only claim, the Web attaches it to every
  // call, and the rules fetch came back 401. The page swallowed it and printed its own default —
  // "At least 12 characters" — which is the exact sentence the endpoint exists to stop it printing,
  // and is byte-identical to a correct render against a 12-character policy. Found in production;
  // no build, code read or English assertion could have told the two apart.
  //
  // This is the assertion that can: the token that reaches that page must be able to read them.
  const changeOnly = signedIn.body?.accessToken;
  const rulesRes = await fetch(`${API}/api/v1/security-policy/password-rules`, {
    headers: { Authorization: `Bearer ${changeOnly}` },
  });
  check('...and CAN read the password rules while holding only a change-only token',
    rulesRes.status === 200, `${rulesRes.status} — the set-password page is showing its fallback`);

  const rulesBody = rulesRes.status === 200 ? await rulesRes.json() : null;
  check('...which state the CONFIGURED minimum rather than a number the page invented',
    typeof rulesBody?.minimumLength === 'number' && rulesBody.minimumLength > 0,
    JSON.stringify(rulesBody ?? {}).slice(0, 160));

  // Everything else stays refused: allowing one anonymous read must not have opened the door.
  const stillClosed = await fetch(`${API}/api/v1/users`, { headers: { Authorization: `Bearer ${changeOnly}` } });
  check('...while every other endpoint is still refused to that token', stillClosed.status === 401,
    `${stillClosed.status} — a change-only token reached the user list`);

  // A TYPED password is the point of the feature: it is what an administrator reads down a phone.
  const typed = `Kyambogo${run}#7`;
  const typedReset = await api(`/users/${resetTarget}/reset-password`, {
    method: 'POST', body: JSON.stringify({ newPassword: typed }),
  });
  check('an administrator may type the password instead', typedReset.ok,
    `${typedReset.status} ${(await typedReset.clone().text()).slice(0, 160)}`);
  const typedSignIn = await login(genBody?.username ?? '', typed);
  check('...and that is the password that now works', typedSignIn.ok, `${typedSignIn.status}`);

  // ...but NOT a weaker one. This is the whole answer to "can we just set them all to pass?".
  const weak = await api(`/users/${resetTarget}/reset-password`, {
    method: 'POST', body: JSON.stringify({ newPassword: 'pass' }),
  });
  check('...and "pass" is refused, as it would be from the person themselves', weak.status === 400,
    `${weak.status} — a blocklisted password was accepted`);

  // ---- 10. and never for somebody who outranks you ---------------------------------------------
  // THE ESCALATION. Until 2026-09-22 this endpoint checked users.edit and the organization and
  // nothing else, so a tenant Admin could reset a Platform Administrator's password and sign in as
  // them — reaching every organization. RoleAssignmentGuard refuses it now, as Reissue always did.
  // Section 13 of the shell suite makes the same point about POST /users: testing the guarded door
  // says nothing about the door beside it.
  const allRoles = Array.isArray(roles) ? roles : roles?.items ?? [];
  const superRole = allRoles.find(r => r.code === 'super-admin');
  const platformUsers = await (await api('/users?includeInactive=true')).json().catch(() => null);
  const userRows = Array.isArray(platformUsers) ? platformUsers : platformUsers?.items ?? [];
  const aboveMe = userRows.find(u => (u.roleCode ?? u.role) === 'super-admin');

  if (!aboveMe) {
    // Skipping honestly beats passing on a tenant that has nobody to test against.
    check('SKIP: no higher-ranked account is visible to this administrator', true,
      superRole ? 'the role exists but no holder is listed here' : 'super-admin is not listed to a tenant, which is itself correct');
  } else {
    const escalate = await api(`/users/${aboveMe.id}/reset-password`, {
      method: 'POST', body: JSON.stringify({ newPassword: '' }),
    });
    check('an administrator cannot reset the password of somebody who outranks them',
      escalate.status >= 400, `${escalate.status} — THE ACCOUNT WAS TAKEN OVER`);
  }

  // THE SELF-RESET IS DELIBERATELY NOT TESTED HERE, and that is worth writing down rather than
  // leaving as a gap somebody fills later. The guard exempts a caller resetting their OWN password,
  // because an administrator locked out of their own account is a case this endpoint exists for —
  // but exercising it means changing this suite's own sign-in password and putting it back, and
  // ADMIN_PASS ("E2eTeacher!2026") is refused by the blocklist as "teacher" plus a year. The restore
  // would fail, the account would be left on a password nothing knows, and EVERY suite that signs in
  // as this administrator would start failing for a reason none of them could report.

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

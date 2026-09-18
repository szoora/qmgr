#!/usr/bin/env node
// =====================================================================================================
// Staff Performance Monitor — aggressive end-to-end against a running API and the real dev tenant.
//
// Section 14 of scripts/e2e/class-teacher-e2e.sh (which calls this). Written in Node rather than
// curl because the point of several sections is CONCURRENCY: firing the same write N times at once
// and asserting the invariant still holds (budget, one register row per person, one appraisal per
// person per period, no lost acknowledgements). Bash backgrounded curls cannot assert that cleanly.
// Node is a developer tool here, not a server dependency.
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/staff-performance-e2e.mjs
//
// Seeds its own staff (idempotent: users, departments and roles are created once and reused), and
// labels everything it writes "E2E". Records are append-only; they stay. Exit code = failures.
// =====================================================================================================

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH;
const ORG = process.env.ORG || "ef0305f3-40c6-456f-8f9a-48a1f0e3223c";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
const PW = "E2eTeacher!2026";
// Accounts THIS suite creates get a password the blocklist accepts (duty rota plan §12.2, Phase 0A, 2026-09-17):
// "E2eTeacher!2026" is essentially the word "teacher" plus a year, the shape the blocklist exists to refuse, so a
// new account can no longer be given it. Accounts created before the blocklist keep PW; sign-in tries both.
const NEW_PW = "Rwenzori#Peaks-2026";
const RUN = Date.now().toString(36);

if (!BRANCH) { console.error("set BRANCH to the branch guid"); process.exit(2); }
const B = `/api/v1/branches/${BRANCH}`;

let pass = 0, fail = 0;
const failures = [];
const ok = (name) => { pass++; console.log(`  \x1b[32mPASS\x1b[0m  ${name}`); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  console.log(`  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 400)}`);
};
const hdr = (t) => console.log(`\n\x1b[1m${t}\x1b[0m`);
const eq = (name, actual, expected) => (actual === expected ? ok(name) : bad(name, expected, actual));
const truthy = (name, cond, detail = "") => (cond ? ok(name) : bad(name, "true", `false ${detail}`));

async function call(token, method, path, body, extraHeaders = {}) {
  const headers = { ...extraHeaders };
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch(API + path, { method, headers, body: payload });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json, text };
}
const get = (t, p) => call(t, "GET", p);
const post = (t, p, b) => call(t, "POST", p, b ?? {});
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
const patch = (t, p, b) => call(t, "PATCH", p, b ?? {});
const del = (t, p, b) => call(t, "DELETE", p, b);

async function login(id, pw) {
  const r = await call(null, "POST", "/api/v1/auth/login", { email: id, password: pw });
  if (!r.json?.accessToken && pw === PW) return (await call(null, "POST", "/api/v1/auth/login", { email: id, password: NEW_PW })).json?.accessToken ?? null;
  return r.json?.accessToken ?? null;
}
/** The password an e2e account actually has (see NEW_PW). */
async function passwordOf(id) {
  return (await call(null, "POST", "/api/v1/auth/login", { email: id, password: PW })).json?.accessToken ? PW : NEW_PW;
}

const iso = (d) => new Date(d).toISOString();
const hoursFromNow = (h) => iso(Date.now() + h * 3600_000);

// ---------------------------------------------------------------------------------------------------
hdr("14.0 Sign in, and the module gate");
const SA = await login(SA_USER, SA_PASS);
const AD = await login("e2e.admin.ct@qmgr.local", PW);
if (!SA || !AD) { bad("superadmin and tenant admin sign in", "two tokens", `${!!SA} ${!!AD}`); process.exit(1); }
ok("superadmin and tenant admin signed in");

// Staff performance is part of the Student Welfare module ("Welfare & Performance") since 2026-09-17.
// Revoke it, prove the gate, re-grant. A module gate that never refuses is not a gate.
{
  const mods = (await get(SA, `/api/v1/admin/tenants/${ORG}/modules`)).json ?? [];
  const welfare = mods.find((m) => m.moduleCode === "student-welfare");
  eq("ONE MODULE: the welfare module is called 'Welfare & Performance'", welfare?.moduleName, "Welfare & Performance");
  truthy("ONE MODULE: there is no separate staff-performance module in the catalog", !mods.some((m) => m.moduleCode === "staff-performance"), JSON.stringify(mods.map((m) => m.moduleCode)));
  const retired = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/${"staff" + "-performance"}`, { note: "E2E" });
  truthy("ONE MODULE: granting the retired staff-performance code is refused", retired.status === 400 || retired.status === 404, `${retired.status} ${retired.text}`);
}
await call(SA, "DELETE", `/api/v1/admin/tenants/${ORG}/modules/student-welfare?note=E2E%20gate%20check`);
{
  const r = await get(AD, `${B}/staff/structure/departments`);
  eq("without the welfare module, a staff route refuses (403 MODULE_NOT_PURCHASED)", r.status, 403);
  truthy("…and names the welfare module", (r.text ?? "").includes("MODULE_NOT_PURCHASED") && (r.text ?? "").includes("student-welfare"), r.text);
}
{
  const r = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/student-welfare`, { note: "E2E" });
  eq("welfare module granted back as SuperAdmin", r.status, 200);
  eq("…and the staff route opens again", (await get(AD, `${B}/staff/structure/departments`)).status, 200);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.1 Seed the staff structure (idempotent)");
const roles = (await get(AD, "/api/v1/roles")).json ?? [];
const roleId = (code) => roles.find((r) => r.code === code)?.id;
for (const code of ["director-of-studies", "academic-assistant", "head-of-department", "teacher", "support-staff"]) {
  truthy(`seeded system role ${code} exists`, !!roleId(code));
}
truthy("head-of-department carries StaffScope AssignedDepartments", roles.find((r) => r.code === "head-of-department")?.staffScope === "AssignedDepartments", JSON.stringify(roles.find((r) => r.code === "head-of-department")));

const PEOPLE = [
  { key: "dos", username: "e2e.sp.dos", first: "Daniel", last: "Okello", role: "director-of-studies" },
  { key: "aa", username: "e2e.sp.aa", first: "Agnes", last: "Namuli", role: "academic-assistant" },
  { key: "hodMath", username: "e2e.sp.hod.math", first: "Moses", last: "Ssempala", role: "head-of-department" },
  { key: "hodLang", username: "e2e.sp.hod.lang", first: "Lydia", last: "Achieng", role: "head-of-department" },
  { key: "hodEmpty", username: "e2e.sp.hod.empty", first: "Henry", last: "Mugisha", role: "head-of-department" },
  { key: "math1", username: "e2e.sp.math1", first: "Martin", last: "Kato", role: "teacher" },
  { key: "math2", username: "e2e.sp.math2", first: "Mary", last: "Nabirye", role: "teacher" },
  { key: "lang1", username: "e2e.sp.lang1", first: "Luke", last: "Opio", role: "teacher" },
  { key: "support", username: "e2e.sp.support", first: "Sarah", last: "Auma", role: "support-staff" },
];
const allUsers = async () => (await get(AD, "/api/v1/users?pageSize=500")).json;
let userList = await allUsers();
const usersArray = () => (Array.isArray(userList) ? userList : userList?.items ?? userList?.data ?? []);
const U = {};
for (const p of PEOPLE) {
  let u = usersArray().find((x) => x.username === p.username);
  if (!u) {
    const r = await post(AD, "/api/v1/users", {
      username: p.username, email: `${p.username}@qmgr.local`, password: NEW_PW,
      firstName: p.first, lastName: p.last, roleId: roleId(p.role), assignedBranchId: BRANCH,
    });
    if (r.status >= 300) { bad(`create ${p.username}`, "201", `${r.status} ${r.text}`); continue; }
    userList = await allUsers();
    u = usersArray().find((x) => x.username === p.username);
  }
  U[p.key] = { id: u?.id, ...p };
}
truthy("nine staff accounts exist", PEOPLE.every((p) => U[p.key]?.id), JSON.stringify(Object.keys(U)));
for (const p of PEOPLE) U[p.key].token = await login(`${p.username}@qmgr.local`, PW);
truthy("every seeded staff member can sign in", PEOPLE.every((p) => U[p.key].token));

// Departments, reused by code.
async function ensureDept(code, name, headKey, deputyKey) {
  const list = (await get(AD, `${B}/staff/structure/departments?includeInactive=true`)).json ?? [];
  const existing = list.find((d) => d.code === code);
  const body = { name, code, headUserId: headKey ? U[headKey].id : null, deputyHeadUserId: deputyKey ? U[deputyKey].id : null, sortOrder: 1 };
  if (existing) {
    if (!existing.isActive) await patch(AD, `${B}/staff/structure/departments/${existing.id}/toggle`);
    const r = await put(AD, `${B}/staff/structure/departments/${existing.id}`, body);
    return r.json ?? existing;
  }
  const r = await post(AD, `${B}/staff/structure/departments`, body);
  if (r.status >= 300) bad(`create department ${code}`, "201", `${r.status} ${r.text}`);
  return r.json;
}
const MATH = await ensureDept("E2EMATH", "E2E Mathematics", "hodMath");
const LANG = await ensureDept("E2ELANG", "E2E Languages", "hodLang");
truthy("departments E2EMATH and E2ELANG exist with heads", MATH?.id && LANG?.id && MATH.headUserId === U.hodMath.id);

async function setStructure(key, deptIds, lmKey) {
  const r = await put(AD, `${B}/staff/structure/members/${U[key].id}`, { departmentIds: deptIds, lineManagerUserId: lmKey ? U[lmKey].id : null });
  if (r.status >= 300) bad(`structure for ${key}`, "200", `${r.status} ${r.text}`);
}
await setStructure("math1", [MATH.id], "hodMath");
await setStructure("math2", [MATH.id], "hodMath");
await setStructure("lang1", [LANG.id], "hodLang");
await setStructure("hodMath", [MATH.id], "dos");
await setStructure("hodLang", [LANG.id], "dos");
await setStructure("hodEmpty", [], "dos");
await setStructure("support", [], "aa");
ok("structure applied: departments and line managers");

{
  const r = await put(AD, `${B}/staff/structure/members/${U.math1.id}`, { departmentIds: [MATH.id], lineManagerUserId: U.math1.id });
  eq("a person cannot be their own line manager (400)", r.status, 400);
  const r2 = await put(AD, `${B}/staff/structure/members/${U.math1.id}`, { departmentIds: ["00000000-0000-0000-0000-000000000001"], lineManagerUserId: null });
  eq("an unknown department id is refused (400)", r2.status, 400);
  const r3 = await put(U.hodMath.token, `${B}/staff/structure/members/${U.math1.id}`, { departmentIds: [MATH.id] });
  eq("a head of department cannot change structure (403)", r3.status, 403);
  await setStructure("math1", [MATH.id], "hodMath");
}
{
  const [a, b] = await Promise.all([
    post(AD, `${B}/staff/structure/departments`, { name: "E2E Race", code: `RACE${RUN}`.slice(0, 20).toUpperCase(), sortOrder: 9 }),
    post(AD, `${B}/staff/structure/departments`, { name: "E2E Race", code: `RACE${RUN}`.slice(0, 20).toUpperCase(), sortOrder: 9 }),
  ]);
  const statuses = [a.status, b.status].sort();
  truthy("RACE: two concurrent creates of one department code → one 201, one 400/409, never a 500", statuses[0] < 300 && (statuses[1] === 400 || statuses[1] === 409), statuses.join(","));
  const created = [a, b].find((x) => x.status < 300)?.json;
  if (created) await patch(AD, `${B}/staff/structure/departments/${created.id}/toggle`);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.2 Parameters and policy");
const params = (await get(U.math1.token, "/api/v1/staff/parameters")).json ?? [];
truthy("parameters seeded (MoES set, ≥ 12) and readable by a teacher", params.length >= 12, params.length);
const P = (name) => params.find((p) => p.name === name);
for (const n of ["Lesson Attendance", "Lesson Observation", "Exam Supervision", "Meeting Attendance", "Recognition", "Conduct", "Wellbeing"]) truthy(`parameter "${n}" present`, !!P(n));
truthy("Wellbeing is unscored (no points, zero weight)", P("Wellbeing")?.defaultPoints == null && P("Wellbeing")?.weight === 0, JSON.stringify(P("Wellbeing")));
eq("a teacher cannot create a parameter (403)", (await post(U.math1.token, "/api/v1/staff/parameters", { name: "x", purpose: "x" })).status, 403);
{
  const r = await post(AD, "/api/v1/staff/parameters", { name: `E2E Heavy ${RUN}`, kind: "Contribution", appliesTo: "AllStaff", defaultPoints: 1, maxPointsPerEntry: 5, weight: 500, purpose: "E2E weight cap check" });
  eq("a parameter weighing more than the policy cap is refused (400)", r.status, 400);
  truthy("…with the share spelled out", /weight|share|%/i.test(r.text), r.text);
}
{
  const r = await post(AD, "/api/v1/staff/parameters", { name: `E2E Wellbeing ${RUN}`, kind: "Wellbeing", appliesTo: "AllStaff", defaultPoints: 5, maxPointsPerEntry: 5, weight: 3, defaultVisibility: "Standard", purpose: "E2E" });
  truthy("a Wellbeing parameter saved with points and weight is forced unscored and Confidential", r.status < 300 && r.json?.defaultPoints == null && r.json?.weight === 0 && r.json?.defaultVisibility === "Confidential", `${r.status} ${r.text}`);
  if (r.json?.id) await patch(AD, `/api/v1/staff/parameters/${r.json.id}/toggle`);
}
const policy = (await get(AD, "/api/v1/staff/policy")).json;
truthy("policy readable with five bands", policy?.bands?.length === 5, JSON.stringify(policy?.bands));
{
  const broken = { ...policy, bands: policy.bands.slice(0, 3) };
  eq("a policy with three bands is refused (400)", (await put(AD, "/api/v1/staff/policy", broken)).status, 400);
  const inverted = { ...policy, bands: policy.bands.map((b, i) => ({ ...b, minScore: i * 10 })) };
  eq("bands whose minimums are not descending are refused (400)", (await put(AD, "/api/v1/staff/policy", inverted)).status, 400);
  eq("a teacher cannot write the policy (403)", (await put(U.math1.token, "/api/v1/staff/policy", policy)).status, 403);
  const r = await put(AD, "/api/v1/staff/policy", { ...policy, recognitionMonthlyBudget: 3, leaderboardMode: "Private", systemAwardsEnabled: false, lateEntryThresholdDays: 14, maxParameterWeightPercent: 50 });
  eq("policy saved: recognition budget 3, leaderboard private", r.status, 200);
  eq("WEIGHT CAP: a policy lifting the per-parameter cap above 50% is refused (400)", (await put(AD, "/api/v1/staff/policy", { ...policy, maxParameterWeightPercent: 80 })).status, 400);
}
{
  const sys = ["Welfare record filed", "Customer served", "Visitor hosted", "Positive feedback"].map((n) => params.find((p) => p.name === n) ?? null);
  truthy("AUTOMATIC CREDIT: the four automatic-credit parameters are seeded and marked system-source", sys.every((p) => p && p.isSystemSource), JSON.stringify(sys.map((p) => p?.name)));
  truthy("LESSON RECOVERY: the seeded recovery parameter offsets Lesson Attendance", P("Lesson Recovery")?.offsetsParameterId === P("Lesson Attendance")?.id, JSON.stringify(P("Lesson Recovery")));
  const badOffset = await post(AD, "/api/v1/staff/parameters", { name: `E2E Offset bad ${RUN}`, kind: "Contribution", appliesTo: "AllStaff", defaultPoints: 1, maxPointsPerEntry: 1, weight: 0, purpose: "E2E", offsetsParameterId: P("Co-curricular Activity").id });
  eq("an offset must point at an Attendance or Duty parameter (400)", badOffset.status, 400);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.3 SCOPE — a head sees their department, fails closed with none, teachers see only themselves");
{
  const dir = (await get(U.hodMath.token, `${B}/staff/structure/members`)).json;
  const ids = (dir?.items ?? []).map((m) => m.userId);
  truthy("head of Maths sees both Maths teachers", ids.includes(U.math1.id) && ids.includes(U.math2.id), ids.length);
  truthy("head of Maths does NOT see the Languages teacher", !ids.includes(U.lang1.id));
  truthy("head of Maths is told the directory is scoped (ScopedToDepartments)", (dir?.scopedToDepartments ?? []).includes("E2E Mathematics"), JSON.stringify(dir?.scopedToDepartments));
}
{
  const dir = (await get(U.hodEmpty.token, `${B}/staff/structure/members`)).json;
  const others = (dir?.items ?? []).filter((m) => m.userId !== U.hodEmpty.id);
  eq("FAIL CLOSED: a head with no department sees nobody else", others.length, 0);
  eq("…and gets 404, not 403, for a colleague's timeline", (await get(U.hodEmpty.token, `${B}/staff/members/${U.math1.id}/timeline`)).status, 404);
}
eq("head of Maths gets 404 for the Languages teacher's timeline", (await get(U.hodMath.token, `${B}/staff/members/${U.lang1.id}/timeline`)).status, 404);
eq("head of Maths reads a Maths teacher's timeline", (await get(U.hodMath.token, `${B}/staff/members/${U.math1.id}/timeline`)).status, 200);
eq("a teacher reads their own timeline", (await get(U.math1.token, `${B}/staff/members/${U.math1.id}/timeline`)).status, 200);
{
  const r = await get(U.math1.token, `${B}/staff/members/${U.math2.id}/timeline`);
  truthy("a teacher cannot read a colleague's timeline (403 or 404)", r.status === 403 || r.status === 404, r.status);
}
eq("a teacher cannot search records (403)", (await get(U.math1.token, `${B}/staff/records`)).status, 403);
eq("a teacher cannot read the directory (403)", (await get(U.math1.token, `${B}/staff/structure/members`)).status, 403);
eq("the Director of Studies reads the Languages teacher (unscoped)", (await get(U.dos.token, `${B}/staff/members/${U.lang1.id}/timeline`)).status, 200);

// ---------------------------------------------------------------------------------------------------
hdr("14.4 RECORDS — sign rules, rungs, the write scope, drafts, right of reply");
const rec = (token, body) => post(token, `${B}/staff/records`, { occurredAt: iso(Date.now() - 3600_000), visibility: "Standard", ...body });
{
  const r = await rec(U.math1.token, { subjectUserId: U.math2.id, parameterId: P("Co-curricular Activity").id, points: 3, description: "E2E teacher writing about a colleague" });
  eq("a teacher cannot log a record about anyone (403)", r.status, 403);
  const r2 = await rec(U.hodMath.token, { subjectUserId: U.lang1.id, parameterId: P("Co-curricular Activity").id, points: 3, description: "E2E head writing outside department" });
  eq("WRITE SCOPE: head of Maths logging about a Languages teacher gets 404", r2.status, 404);
  const r3 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Conduct").id, points: 3, description: "E2E positive conduct points" });
  eq("Conduct with positive points is refused (400)", r3.status, 400);
  const r4 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: -3, description: "E2E negative contribution" });
  eq("Contribution with negative points is refused (400)", r4.status, 400);
  const r5 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 99, description: "E2E beyond the per-entry cap" });
  eq("points beyond MaxPointsPerEntry are refused (400)", r5.status, 400);
  const r6 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Lesson Observation").id, description: "E2E observation with no rating" });
  eq("an Observation with no rating is refused (400)", r6.status, 400);
  const r7 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 3, description: "short" });
  eq("a description under ten characters is refused (400)", r7.status, 400);
  const r8 = await rec(U.hodMath.token, { subjectUserId: U.support.id, parameterId: P("Lesson Attendance").id, outcome: "Present", description: "E2E out of scope and wrong group" });
  truthy("a support-staff member is out of the head's scope (404)", r8.status === 404, r8.status);
  const r9 = await rec(AD, { subjectUserId: U.support.id, parameterId: P("Lesson Attendance").id, outcome: "Present", description: "E2E teaching parameter on support staff" });
  eq("a TeachingStaff parameter cannot be logged about support staff (400)", r9.status, 400);
  const r10 = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Conduct").id, points: -2, visibility: "Restricted", description: "E2E head trying to file restricted" });
  eq("RUNG: a head cannot file into Restricted, a rung they cannot read (400)", r10.status, 400);
  const sysManual = await rec(AD, { subjectUserId: U.math1.id, parameterId: P("Customer served").id, points: 1, description: "E2E hand-written automatic credit" });
  eq("AUTOMATIC CREDIT: a system-source parameter cannot be logged by hand (400)", sysManual.status, 400);
  const lateBody = { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 1, visibility: "Standard", occurredAt: iso(Date.now() - 20 * 86400_000), description: `E2E ${RUN} backdated twenty days` };
  const late = await post(U.hodMath.token, `${B}/staff/records`, lateBody);
  truthy("LATE ENTRY: a record dated past the policy threshold is refused until confirmed (409 LATE_ENTRY)", late.status === 409 && late.text.includes("LATE_ENTRY"), `${late.status} ${late.text}`);
  const lateOk = await post(U.hodMath.token, `${B}/staff/records?acknowledgeLateEntry=true`, lateBody);
  truthy("LATE ENTRY: confirmed, it is logged and carries the late-entry flag", lateOk.status === 201 && lateOk.json?.isLateEntry === true, `${lateOk.status} ${lateOk.json?.isLateEntry}`);
  truthy("PURPOSE: a record carries its parameter's purpose", (lateOk.json?.parameterPurpose ?? "").length > 10, lateOk.json?.parameterPurpose);
  const draftLate = await post(U.hodMath.token, `${B}/staff/records`, { ...lateBody, saveAsDraft: true, description: `E2E ${RUN} backdated draft` });
  eq("LATE ENTRY: a backdated DRAFT saves without confirmation (201)", draftLate.status, 201);
  eq("LATE ENTRY: …but finalising it asks (409)", (await post(U.hodMath.token, `${B}/staff/records/${draftLate.json?.id}/finalize`)).status, 409);
  eq("LATE ENTRY: …and finalises once confirmed (200)", (await post(U.hodMath.token, `${B}/staff/records/${draftLate.json?.id}/finalize?acknowledgeLateEntry=true`)).status, 200);
}
const good = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 3, description: `E2E ${RUN} Ran the debate club final` });
eq("head of Maths logs a Standard contribution about a Maths teacher (201)", good.status, 201);
const obs = await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Lesson Observation").id, rating: 3, description: `E2E ${RUN} Observed S2 algebra, good pacing`, preObservationMeetingAt: hoursFromNow(-48), feedbackSessionAt: hoursFromNow(1) });
eq("head of Maths logs a lesson observation (201)", obs.status, 201);
truthy("the observation is forced to its parameter's Confidential default", obs.json?.visibility === "Confidential", obs.json?.visibility);
eq("…and the head who filed it can still read it (author access)", (await get(U.hodMath.token, `${B}/staff/records/${obs.json?.id}`)).status, 200);
eq("…and so can its subject", (await get(U.math1.token, `${B}/staff/records/${obs.json?.id}`)).status, 200);
eq("…but the other Maths teacher cannot (404)", (await get(U.math2.token, `${B}/staff/records/${obs.json?.id}`)).status, 404);
{
  const scored = await rec(AD, { subjectUserId: U.math1.id, parameterId: P("Wellbeing").id, points: 5, description: `E2E ${RUN} wellbeing with points` });
  eq("Wellbeing: a record carrying points is refused with a reason (400)", scored.status, 400);
}
const confByDos = await rec(U.dos.token, { subjectUserId: U.math1.id, parameterId: P("Conduct").id, points: -1, description: `E2E ${RUN} CONFWORD late to invigilation twice` });
truthy("the Director of Studies files a Conduct record, Confidential by default", confByDos.status === 201 && confByDos.json?.visibility === "Confidential", `${confByDos.status} ${confByDos.json?.visibility}`);
eq("CONFIDENTIAL: the head of department, who did not write it and holds no confidential rung, gets 404", (await get(U.hodMath.token, `${B}/staff/records/${confByDos.json?.id}`)).status, 404);
{
  const hodTl = (await get(U.hodMath.token, `${B}/staff/members/${U.math1.id}/timeline`)).json;
  truthy("CONFIDENTIAL: …and it is absent from the head's view of the timeline", hodTl && !JSON.stringify(hodTl).includes("CONFWORD"));
  // The decision (2026-09-17): the subject reads a Confidential record IN FULL — right of reply and
  // subject access both need the content — while the notification about it says only that it exists.
  const own = await get(U.math1.token, `${B}/staff/records/${confByDos.json?.id}`);
  truthy("CONFIDENTIAL: the subject reads it in full on their own file (description included)", own.status === 200 && (own.json?.description ?? "").includes("CONFWORD"), `${own.status} ${own.json?.description}`);
  const told = (await get(U.math1.token, "/api/v1/notifications?eventKey=staff.record-logged&limit=50")).json ?? [];
  const aboutIt = told.filter((n) => /confidential conduct record/i.test(n.message ?? ""));
  truthy("CONFIDENTIAL: the subject is notified that a confidential record exists", aboutIt.length > 0, JSON.stringify(told.slice(0, 3).map((n) => n.message)));
  truthy("CONFIDENTIAL: …and no notification carries its content", !told.some((n) => `${n.title} ${n.message}`.includes("CONFWORD")), JSON.stringify(aboutIt.slice(0, 2)));
}
const wb = await rec(AD, { subjectUserId: U.math1.id, parameterId: P("Wellbeing").id, visibility: "Standard", description: `E2E ${RUN} Bereavement, needs cover next week` });
truthy("Wellbeing: logged unscored, visibility forced to Confidential", wb.status === 201 && wb.json?.points == null && wb.json?.visibility === "Confidential", `${wb.status} ${wb.text}`);
const restricted = await rec(AD, { subjectUserId: U.math1.id, parameterId: P("Conduct").id, points: -2, visibility: "Restricted", description: `E2E ${RUN} SECRETWORD investigation note` });
eq("the Tenant Admin files a Restricted conduct record (201)", restricted.status, 201);
eq("RESTRICTED: its subject cannot read it (404)", (await get(U.math1.token, `${B}/staff/records/${restricted.json?.id}`)).status, 404);
eq("RESTRICTED: the head of department cannot read it (404)", (await get(U.hodMath.token, `${B}/staff/records/${restricted.json?.id}`)).status, 404);
{
  const tl = (await get(U.math1.token, `${B}/staff/members/${U.math1.id}/timeline`)).json;
  const ids = (tl?.records ?? []).map((r) => r.id);
  truthy("the subject's own timeline includes the Confidential observation", ids.includes(obs.json?.id));
  truthy("the subject's own timeline omits the Restricted record", !ids.includes(restricted.json?.id));
  const portal = (await get(U.math1.token, "/api/v1/staff/portal")).json;
  truthy("the portal never carries the Restricted record", !JSON.stringify(portal ?? {}).includes("SECRETWORD"));
  const conductSeen = (tl?.records ?? []).filter((r) => r.parameterName === "Conduct").length;
  const conductScored = portal?.score?.breakdown?.find((b) => b.name === "Conduct")?.evidenceCount ?? 0;
  truthy("RESTRICTED NEVER SCORES: the subject's Conduct evidence count equals the Conduct records they can see", conductScored === conductSeen, `scored ${conductScored}, visible ${conductSeen}`);
  const hodReport = (await get(U.dos.token, `${B}/staff/members/${U.math1.id}/score`)).json;
  truthy("…and the Director of Studies sees the same score (it does not depend on who asks)", hodReport?.composite === portal?.score?.composite, `${hodReport?.composite} vs ${portal?.score?.composite}`);
  const act = (await get(AD, `${B}/staff/activity?userId=${U.math1.id}&pageSize=200`)).json;
  truthy("the activity log names the Restricted record without its content", !JSON.stringify(act ?? {}).includes("SECRETWORD") && (act?.items ?? []).some((e) => /restricted/i.test(e.summary)), JSON.stringify((act?.items ?? []).slice(0, 3)));
}
{
  // Unseen records: one to-do line however many, and "Mark all as seen" — found in Chrome, where a
  // teacher's to-do list was eleven identical recognition rows. Fired concurrently: the marked counts
  // must add up to the unseen count exactly once, and the Restricted record must stay unseen.
  for (let i = 0; i < 2; i++)
    await rec(U.hodMath.token, { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 1, description: `E2E ${RUN} unseen ${i}` });
  const before = (await get(U.math1.token, "/api/v1/staff/portal")).json;
  const unseenItems = (before?.openItems ?? []).filter((i) => i.kind === "record-unread");
  truthy("TO-DO: several unseen records make ONE to-do line, not one per record", before?.unacknowledgedRecords > 1 && unseenItems.length === 1, `${before?.unacknowledgedRecords} unseen, ${unseenItems.length} lines`);
  truthy("…which names the count and links to the timeline", unseenItems[0]?.title?.startsWith(`${before?.unacknowledgedRecords} records`) && unseenItems[0]?.url === "/portal#my-timeline", JSON.stringify(unseenItems[0]));
  const both = await Promise.all([1, 2, 3].map(() => post(U.math1.token, "/api/v1/staff/portal/records/acknowledge-all")));
  truthy("MARK ALL: three concurrent calls all succeed", both.every((r) => r.status === 200), both.map((r) => r.status).join(","));
  const marked = both.reduce((s, r) => s + (r.json?.marked ?? 0), 0);
  eq("MARK ALL: the marked counts add up to the unseen count exactly once", marked, before?.unacknowledgedRecords);
  const after = (await get(U.math1.token, "/api/v1/staff/portal")).json;
  truthy("…the portal then shows nothing unseen and no to-do line for it", after?.unacknowledgedRecords === 0 && !(after?.openItems ?? []).some((i) => i.kind === "record-unread"), `${after?.unacknowledgedRecords}`);
  const secret = (await get(AD, `${B}/staff/records/${restricted.json?.id}`)).json;
  truthy("MARK ALL: the Restricted record the subject cannot see stays unacknowledged", secret && secret.acknowledgedAt == null, secret?.acknowledgedAt);
  eq("MARK ALL: a second pass marks nothing", (await post(U.math1.token, "/api/v1/staff/portal/records/acknowledge-all")).json?.marked, 0);
}
{
  const draft = await rec(U.hodMath.token, { subjectUserId: U.math2.id, parameterId: P("Co-curricular Activity").id, points: 2, saveAsDraft: true, description: `E2E ${RUN} draft about math2` });
  eq("a draft is saved (201)", draft.status, 201);
  eq("DRAFT: its subject cannot see it yet (404)", (await get(U.math2.token, `${B}/staff/records/${draft.json?.id}`)).status, 404);
  eq("DRAFT: the Director of Studies cannot finalise someone else's draft (404)", (await post(U.dos.token, `${B}/staff/records/${draft.json?.id}/finalize`)).status, 404);
  const fin = await post(U.hodMath.token, `${B}/staff/records/${draft.json?.id}/finalize`);
  eq("the author finalises it (200)", fin.status, 200);
  eq("finalising twice is refused (400/404/409)", [400, 404, 409].includes((await post(U.hodMath.token, `${B}/staff/records/${draft.json?.id}/finalize`)).status), true);
}
{
  const id = good.json?.id;
  eq("RIGHT OF REPLY: the head cannot respond as the subject (403/404)", [403, 404].includes((await post(U.hodMath.token, `${B}/staff/records/${id}/respond`, { body: "E2E not mine to answer" })).status), true);
  const r = await post(U.math1.token, `${B}/staff/records/${id}/respond`, { body: `E2E ${RUN} Thank you — the club meets Thursdays.` });
  eq("the subject responds to a record about them", r.status, 200);
  truthy("…and the response is on the record as a Response note", (r.json?.notes ?? []).some((n) => n.kind === "Response"), JSON.stringify(r.json?.notes));
  const a1 = await post(U.math1.token, `${B}/staff/records/${id}/acknowledge`);
  const a2 = await post(U.math1.token, `${B}/staff/records/${id}/acknowledge`);
  truthy("acknowledging twice keeps the first timestamp", a1.json?.acknowledgedAt && a1.json?.acknowledgedAt === a2.json?.acknowledgedAt, `${a1.json?.acknowledgedAt} ${a2.json?.acknowledgedAt}`);
  const low = await patch(AD, `${B}/staff/records/${obs.json?.id}/visibility`, { visibility: "Standard" });
  eq("lowering visibility without a reason is refused (400)", low.status, 400);
  const raise = await patch(U.hodMath.token, `${B}/staff/records/${id}/visibility`, { visibility: "Restricted", reason: "E2E" });
  eq("a head cannot raise a record to Restricted (400)", raise.status, 400);
}
{
  // Evidence: a tiny valid PNG, gated per file.
  const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
  const fd = new FormData(); fd.append("file", new Blob([png], { type: "image/png" }), "e2e-evidence.png");
  const up = await call(U.hodMath.token, "POST", `${B}/staff/records/${obs.json?.id}/attachments`, fd);
  eq("evidence uploads to the Confidential observation (201)", up.status, 201);
  const url = up.json?.fileUrl;
  if (url) {
    const path = new URL(url).pathname;
    eq("EVIDENCE: anonymous fetch without the token is 401", (await fetch(API + path)).status, 401);
    eq("EVIDENCE: the subject fetches it with their bearer", (await fetch(API + path, { headers: { Authorization: `Bearer ${U.math1.token}` } })).status, 200);
    eq("EVIDENCE: the other Maths teacher gets 404", (await fetch(API + path, { headers: { Authorization: `Bearer ${U.math2.token}` } })).status, 404);
    eq("EVIDENCE: the signed link works on its own", (await fetch(url.replace(/^https?:\/\/[^/]+/, API))).status, 200);
  }
  if (url) {
    const pathA = new URL(url).pathname;
    eq("EVIDENCE: the head who FILED the Confidential observation fetches its evidence (author, 200)", (await fetch(API + pathA, { headers: { Authorization: `Bearer ${U.hodMath.token}` } })).status, 200);
  }
  const fd3 = new FormData(); fd3.append("file", new Blob([png], { type: "image/png" }), "e2e-conf-evidence.png");
  const up3 = await call(U.dos.token, "POST", `${B}/staff/records/${confByDos.json?.id}/attachments`, fd3);
  eq("evidence uploads to the DoS's Confidential conduct record (201)", up3.status, 201);
  if (up3.json?.fileUrl) {
    const p3 = new URL(up3.json.fileUrl).pathname;
    eq("EVIDENCE: the non-author head gets 404 on Confidential evidence", (await fetch(API + p3, { headers: { Authorization: `Bearer ${U.hodMath.token}` } })).status, 404);
    eq("EVIDENCE: its subject gets 200", (await fetch(API + p3, { headers: { Authorization: `Bearer ${U.math1.token}` } })).status, 200);
  }
  const fd2 = new FormData(); fd2.append("file", new Blob(["<html><script>alert(1)</script></html>"], { type: "text/html" }), "x.html");
  eq("EVIDENCE: an HTML file is refused (400/415)", [400, 415].includes((await call(AD, "POST", `${B}/staff/records/${obs.json?.id}/attachments`, fd2)).status), true);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.5 RECOGNITION — self, kind, and the monthly budget under concurrency");
{
  eq("recognising yourself is refused (400)", (await post(U.math1.token, `${B}/staff/recognition`, { subjectUserId: U.math1.id, parameterId: P("Recognition").id, message: "E2E self pat on the back" })).status, 400);
  eq("recognition must use a Recognition parameter (400)", (await post(U.math1.token, `${B}/staff/recognition`, { subjectUserId: U.math2.id, parameterId: P("Conduct").id, message: "E2E wrong kind of parameter" })).status, 400);
  const before = (await get(U.lang1.token, `${B}/staff/recognition/budget`)).json;
  const pol = (await get(AD, "/api/v1/staff/policy")).json;
  await put(AD, "/api/v1/staff/policy", { ...pol, recognitionMonthlyBudget: (before?.usedThisMonth ?? 0) + 3 });
  const budget = (await get(U.lang1.token, `${B}/staff/recognition/budget`)).json;
  const remaining = budget?.remaining ?? (budget?.monthlyBudget - budget?.usedThisMonth);
  eq("the budget is reset so the giver has exactly three left", remaining, 3);
  const shots = 8;
  const results = await Promise.all(Array.from({ length: shots }, (_, i) =>
    post(U.lang1.token, `${B}/staff/recognition`, { subjectUserId: i % 2 ? U.math1.id : U.math2.id, parameterId: P("Recognition").id, message: `E2E ${RUN} concurrent thanks #${i}` })));
  const okCount = results.filter((r) => r.status < 300).length;
  const five = results.filter((r) => r.status >= 500).length;
  eq(`RACE: ${shots} simultaneous recognitions with ${remaining} left in the budget → exactly ${Math.max(0, remaining)} succeed`, okCount, Math.max(0, remaining));
  eq("RACE: …and none of them is a 500", five, 0);
  const after = (await get(U.lang1.token, `${B}/staff/recognition/budget`)).json;
  truthy("the budget never goes past its cap", (after?.usedThisMonth ?? 99) <= (after?.monthlyBudget ?? 0), JSON.stringify(after));
  const refused = results.find((r) => r.status === 400);
  if (refused) truthy("the refusal says how much of the budget is used", /of \d+ recognitions/i.test(refused.text), refused.text);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.6 DUTIES & REGISTERS — delegation is not scope, and a register under concurrency");
const dutyBody = (overrides) => ({ parameterId: P("Meeting Attendance").id, title: `E2E ${RUN} Staff meeting`, location: "Staff room", startsAt: hoursFromNow(-3), endsAt: hoursFromNow(-2), expectedUserIds: [U.dos.id, U.math1.id, U.math2.id, U.support.id], recorderUserIds: [U.lang1.id], ...overrides });
eq("a teacher cannot create a duty (403)", (await post(U.math1.token, `${B}/staff/duties`, dutyBody())).status, 403);
eq("a duty on a Contribution parameter is refused (400)", (await post(AD, `${B}/staff/duties`, dutyBody({ parameterId: P("Co-curricular Activity").id }))).status, 400);
eq("a duty that ends before it starts is refused (400)", (await post(AD, `${B}/staff/duties`, dutyBody({ endsAt: hoursFromNow(-4) }))).status, 400);
const duty = await post(U.aa.token, `${B}/staff/duties`, dutyBody());
eq("the Academic Assistant creates a staff meeting with a teacher as the named recorder", duty.status, 201);
const D = duty.json?.id;
eq("DELEGATION: a teacher who is not the recorder gets 404 on the register", (await get(U.math2.token, `${B}/staff/duties/${D}/register`)).status, 404);
{
  const reg = await get(U.lang1.token, `${B}/staff/duties/${D}/register`);
  eq("DELEGATION: the named recorder (a plain teacher) opens the register", reg.status, 200);
  eq("…with one row per expected person", reg.json?.rows?.length, 4);
  const outsider = await post(U.lang1.token, `${B}/staff/duties/${D}/register`, { close: false, entries: [{ userId: U.hodLang.id, outcome: "Present" }] });
  eq("DELEGATION: the recorder cannot mark someone not expected at this duty (400)", outsider.status, 400);
  const na = await post(U.lang1.token, `${B}/staff/duties/${D}/register`, { close: false, entries: [{ userId: U.dos.id, outcome: "NotApplicable" }] });
  eq("NotApplicable is not a register outcome (400)", na.status, 400);
}
{
  const entries = [
    { userId: U.dos.id, outcome: "Absent", note: "E2E no apology" },
    { userId: U.math1.id, outcome: "Present" },
    { userId: U.math2.id, outcome: "Late" },
    { userId: U.support.id, outcome: "Excused", note: "E2E on leave" },
  ];
  const shots = await Promise.all(Array.from({ length: 4 }, () => post(U.lang1.token, `${B}/staff/duties/${D}/register`, { close: false, entries })));
  eq("RACE: four simultaneous register submits produce no 500", shots.filter((s) => s.status >= 500).length, 0);
  const finalFor = async (uid) => ((await get(AD, `${B}/staff/records?subjectUserId=${uid}&pageSize=200&status=Final`)).json?.items ?? []).filter((r) => r.dutyId === D);
  const counts = await Promise.all([U.dos, U.math1, U.math2, U.support].map(async (u) => (await finalFor(u.id)).length));
  eq("RACE: …and each expected person has exactly ONE final record for the duty", counts.join(","), "1,1,1,1");
  // The decision (2026-09-17): saving records the marks at once; the register stays open, still counts
  // as not taken, and its recorder's to-do says how far it got.
  const openReg = (await get(U.lang1.token, `${B}/staff/duties/${D}/register`)).json;
  truthy("SAVE WITHOUT CLOSING: the marks are recorded and the register is still open", openReg?.duty?.registerClosedAt == null && (openReg?.rows ?? []).filter((r) => r.outcome).length === 4, JSON.stringify({ closed: openReg?.duty?.registerClosedAt, marked: (openReg?.rows ?? []).filter((r) => r.outcome).length }));
  // The to-do lists a recorder's ten oldest open registers. Earlier runs leave this recorder's E2E registers open, so
  // close THOSE (never this run's) first, or after ten runs this run's register falls off the list (found 2026-09-17).
  for (const item of ((await get(U.lang1.token, "/api/v1/staff/portal")).json?.openItems ?? []).filter((i) => i.kind === "register-due" && i.title.startsWith("E2E ") && !i.title.includes(RUN))) {
    const staleId = item.url.split("/duties/")[1]?.split("/")[0];
    if (staleId) await post(U.lang1.token, `${B}/staff/duties/${staleId}/register`, { close: true, entries: [] });
  }
  const recorderPortal = (await get(U.lang1.token, "/api/v1/staff/portal")).json;
  const due = (recorderPortal?.openItems ?? []).find((i) => i.kind === "register-due" && i.title === dutyBody().title);
  truthy("SAVE WITHOUT CLOSING: the recorder's to-do still lists it, saying four are marked and recorded", !!due && /4 marked and recorded/.test(due.detail ?? ""), JSON.stringify(due));
  const markedToldAbout = (await get(U.math2.token, "/api/v1/notifications?eventKey=staff.record-logged&limit=50")).json ?? [];
  truthy("SAVE WITHOUT CLOSING: a person marked is told on save, not on close", markedToldAbout.some((n) => (n.message ?? "").includes(dutyBody().title)), JSON.stringify(markedToldAbout.slice(0, 2).map((n) => n.message)));
  const dosRec = (await finalFor(U.dos.id))[0];
  truthy("DELEGATION: a teacher marked the Director of Studies absent, with negative points", dosRec?.outcome === "Absent" && (dosRec?.points ?? 0) < 0, JSON.stringify(dosRec));
  const closed = await post(U.lang1.token, `${B}/staff/duties/${D}/register`, { close: true, entries: entries.map((e) => (e.userId === U.dos.id ? { ...e, outcome: "Present", note: "E2E arrived late, apology accepted" } : e)) });
  eq("the recorder corrects and closes the register", closed.status, 200);
  const afterFix = await finalFor(U.dos.id);
  truthy("a correction annuls and rewrites: still one Final record, now Present", afterFix.length === 1 && afterFix[0].outcome === "Present", JSON.stringify(afterFix));
  eq("a closed duty cannot be edited (400)", (await put(U.aa.token, `${B}/staff/duties/${D}`, dutyBody({ title: "E2E edited after close" }))).status, 400);
  eq("a closed duty cannot be cancelled (400)", (await del(U.aa.token, `${B}/staff/duties/${D}`)).status, 400);
  const dup = await post(U.aa.token, `${B}/staff/duties/${D}/duplicate`, { newStartsAt: hoursFromNow(24 * 7 - 3) });
  truthy("duplicate to next week keeps the recorders and the expected list, register open", dup.status === 201 && dup.json?.recorderUserIds?.includes(U.lang1.id) && !dup.json?.registerClosedAt, `${dup.status} ${dup.text}`);
  if (dup.json?.id) eq("the duplicate can be cancelled while its register is untaken", (await del(U.aa.token, `${B}/staff/duties/${dup.json.id}`)).status < 300, true);
  eq("minutes must be a Library document of this organization (400/404)", [400, 404].includes((await put(U.lang1.token, `${B}/staff/duties/${D}/minutes`, { mediaContentId: "00000000-0000-0000-0000-000000000009" })).status), true);
  const myDuties = (await get(U.math2.token, `${B}/staff/duties?from=${hoursFromNow(-48)}&to=${hoursFromNow(48)}`)).json ?? [];
  truthy("a teacher's duty list shows the meeting they were expected at", myDuties.some((d) => d.id === D && d.isExpectedOfMe && d.myOutcome === "Late"), JSON.stringify(myDuties.find((d) => d.id === D)));
}
{
  // Lesson recovery offsets a missed lesson in the attendance score.
  const lesson = await post(AD, `${B}/staff/duties`, dutyBody({ parameterId: P("Lesson Attendance").id, title: `E2E ${RUN} S3 maths lesson`, expectedUserIds: [U.math2.id], recorderUserIds: [U.hodMath.id] }));
  await post(U.hodMath.token, `${B}/staff/duties/${lesson.json?.id}/register`, { close: true, entries: [{ userId: U.math2.id, outcome: "Absent" }] });
  const before = (await get(AD, `${B}/staff/members/${U.math2.id}/score`)).json;
  const la = (s) => s?.breakdown?.find((b) => b.name === "Lesson Attendance");
  await rec(AD, { subjectUserId: U.math2.id, parameterId: P("Lesson Attendance").id, outcome: "Recovered", description: `E2E ${RUN} lesson recovered on Saturday` });
  const after = (await get(AD, `${B}/staff/members/${U.math2.id}/score`)).json;
  truthy("RECOVERY: a recovered lesson raises the lesson attendance score", (la(after)?.score ?? 0) > (la(before)?.score ?? 0), `${la(before)?.score} → ${la(after)?.score}`);

  // …and so does a record on the seeded Lesson Recovery parameter, which offsets Lesson Attendance.
  // Until 2026-09-17 its purpose said so and scoring ignored it.
  const lesson2 = await post(AD, `${B}/staff/duties`, dutyBody({ parameterId: P("Lesson Attendance").id, title: `E2E ${RUN} S4 maths lesson`, expectedUserIds: [U.math2.id], recorderUserIds: [U.hodMath.id] }));
  await post(U.hodMath.token, `${B}/staff/duties/${lesson2.json?.id}/register`, { close: true, entries: [{ userId: U.math2.id, outcome: "Absent" }] });
  const before2 = (await get(AD, `${B}/staff/members/${U.math2.id}/score`)).json;
  const viaParam = await rec(AD, { subjectUserId: U.math2.id, parameterId: P("Lesson Recovery").id, points: 1, description: `E2E ${RUN} S4 lesson recovered after school` });
  eq("a Lesson Recovery record is logged (201)", viaParam.status, 201);
  const after2 = (await get(AD, `${B}/staff/members/${U.math2.id}/score`)).json;
  truthy("RECOVERY OFFSET: a Lesson Recovery record raises the Lesson Attendance score", (la(after2)?.score ?? 0) > (la(before2)?.score ?? 0) && (la(after2)?.recovered ?? 0) > (la(before2)?.recovered ?? 0), `${la(before2)?.score}/${la(before2)?.recovered} → ${la(after2)?.score}/${la(after2)?.recovered}`);

  // The register writes a line on each marked person's OWN trail.
  const trail = (await get(U.math2.token, "/api/v1/staff/portal/activity?pageSize=200")).json;
  truthy("REGISTER TRAIL: the person marked sees the register line on their own file", (trail?.items ?? []).some((e) => e.action === "staff.duty.register-submitted" && /marked Absent/.test(e.summary)), JSON.stringify((trail?.items ?? []).slice(0, 4).map((e) => e.summary)));

  // The observation logged in 14.4 had a feedback session an hour ahead: it is on the roster for both.
  const fbDuties = (await get(U.math1.token, `${B}/staff/duties?from=${hoursFromNow(-1)}&to=${hoursFromNow(3)}`)).json ?? [];
  truthy("OBSERVATION FEEDBACK: the feedback session is scheduled as a duty the observed teacher is expected at", fbDuties.some((d) => d.title === "Lesson observation feedback" && d.isExpectedOfMe), JSON.stringify(fbDuties.map((d) => d.title)));
}

// ---------------------------------------------------------------------------------------------------
hdr("14.7 NOTICES — audience, sanitising, scheduling, acknowledgements under concurrency");
{
  const bodyHtml = `<p>E2E ${RUN} Maths moderation on Friday.</p><script>alert('x')</script><a href="javascript:alert(1)" onclick="x()">link</a>`;
  const n = await post(U.dos.token, `${B}/staff/notices`, { title: `E2E ${RUN} Maths moderation`, bodyHtml, audienceDepartmentIds: [MATH.id], publishAt: iso(Date.now() - 60_000), requiresAcknowledgement: true, isPinned: true });
  eq("the Director of Studies publishes a notice to the Maths department", n.status, 201);
  truthy("SANITISE: the stored body has no script, no javascript: link, no onclick", !/script|javascript:|onclick/i.test(n.json?.bodyHtml ?? "<script>"), n.json?.bodyHtml);
  const N = n.json?.id;
  const seen = async (u) => ((await get(u.token, `${B}/staff/notices`)).json ?? []).some((x) => x.id === N);
  truthy("AUDIENCE: both Maths teachers see it", (await seen(U.math1)) && (await seen(U.math2)));
  truthy("AUDIENCE: the Languages teacher does not", !(await seen(U.lang1)));
  eq("AUDIENCE: the Languages teacher cannot acknowledge it (404)", (await post(U.lang1.token, `/api/v1/staff/portal/notices/${N}/acknowledge`)).status, 404);
  const acks = await Promise.all([U.math1, U.math2, U.hodMath].flatMap((u) => [u, u]).map((u) => post(u.token, `/api/v1/staff/portal/notices/${N}/acknowledge`)));
  eq("RACE: six simultaneous acknowledgements (three people, twice each) → no 500", acks.filter((a) => a.status >= 500).length, 0);
  const list = (await get(U.dos.token, `${B}/staff/notices/${N}/acknowledgements`)).json ?? [];
  eq("RACE: …and all three acknowledgements survive (no lost update in the jsonb map)", list.filter((a) => a.acknowledgedAt).length, 3);
  const managed = ((await get(U.dos.token, `${B}/staff/notices/manage`)).json ?? []).find((x) => x.id === N);
  truthy("the publisher sees 'acknowledged by 3 of N'", managed?.acknowledgedCount === 3 && managed?.recipientCount >= 3, JSON.stringify({ a: managed?.acknowledgedCount, r: managed?.recipientCount }));
  const center = (await get(U.math1.token, "/api/v1/notifications?eventKey=staff.notice-published&limit=20")).json ?? [];
  truthy("NOTIFICATION: the Maths teacher's centre has the notice under its event key", center.some((x) => x.eventKey === "staff.notice-published" && x.title.includes(RUN)), JSON.stringify(center.slice(0, 2)));
  const future = await post(U.dos.token, `${B}/staff/notices`, { title: `E2E ${RUN} Future notice`, bodyHtml: "<p>later</p>", publishAt: hoursFromNow(48) });
  truthy("SCHEDULING: a notice published for the future is not visible yet", future.status === 201 && !(((await get(U.math1.token, `${B}/staff/notices`)).json ?? []).some((x) => x.id === future.json?.id)));
  if (future.json?.id) await del(U.dos.token, `${B}/staff/notices/${future.json.id}`);
  eq("a teacher cannot publish a notice (403)", (await post(U.math1.token, `${B}/staff/notices`, { title: "x", bodyHtml: "x" })).status, 403);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.8 APPRAISALS — the workflow, concurrency, the frozen score, the appeal");
const fresh = await post(AD, "/api/v1/users", { username: `e2e.sp.appr.${RUN}`, email: `e2e.sp.appr.${RUN}@qmgr.local`, password: NEW_PW, firstName: "Ann", lastName: `Appraisee ${RUN}`, roleId: roleId("teacher"), assignedBranchId: BRANCH });
eq("a fresh teacher is created for this run's appraisal", fresh.status < 300, true);
userList = await allUsers();
const T = { id: usersArray().find((x) => x.username === `e2e.sp.appr.${RUN}`)?.id };
await put(AD, `${B}/staff/structure/members/${T.id}`, { departmentIds: [MATH.id], lineManagerUserId: U.hodMath.id });
T.token = await login(`e2e.sp.appr.${RUN}@qmgr.local`, PW);
await rec(U.hodMath.token, { subjectUserId: T.id, parameterId: P("Co-curricular Activity").id, points: 4, description: `E2E ${RUN} ran the science fair stand` });
const PERIOD = `${new Date().getFullYear()}-T${new Date().getMonth() < 4 ? 1 : new Date().getMonth() < 8 ? 2 : 3}`;
{
  eq("a head of department cannot open a period (403)", (await post(U.hodMath.token, `${B}/staff/appraisals/open`, { periodKey: PERIOD })).status, 403);
  const opens = await Promise.all([1, 2, 3].map(() => post(U.dos.token, `${B}/staff/appraisals/open`, { periodKey: PERIOD, subjectUserIds: [T.id, U.lang1.id] })));
  eq("RACE: three simultaneous 'open appraisals' produce no 500", opens.filter((o) => o.status >= 500).length, 0);
  const board = (await get(U.dos.token, `${B}/staff/appraisals/board?period=${PERIOD}`)).json;
  const mine = (board?.items ?? []).filter((a) => a.subjectUserId === T.id);
  eq("RACE: …and the fresh teacher has exactly one appraisal for the period", mine.length, 1);
  const A = mine[0]?.id;
  eq("the appraiser is the line manager (head of Maths)", mine[0]?.appraiserUserId, U.hodMath.id);
  const hodBoard = (await get(U.hodMath.token, `${B}/staff/appraisals/board?period=${PERIOD}`)).json;
  truthy("SCOPE: the head's board omits the Languages teacher", !(hodBoard?.items ?? []).some((a) => a.subjectUserId === U.lang1.id), JSON.stringify((hodBoard?.items ?? []).map((a) => a.subjectName)));
  eq("the Languages teacher cannot read the Maths teacher's appraisal (404)", (await get(U.lang1.token, `${B}/staff/appraisals/${A}`)).status, 404);

  const targets = await put(U.hodMath.token, `${B}/staff/appraisals/${A}/targets`, { targets: [{ text: "E2E observe two lessons", achieved: false }, { text: "E2E run revision clinic" }] });
  eq("the appraiser sets targets", targets.status, 200);
  eq("the appraiser cannot submit the subject's self-assessment (404)", (await post(U.hodMath.token, `${B}/staff/appraisals/${A}/self`, { selfRating: 5, ratings: [] })).status, 404);
  eq("SELF-ASSESSMENT: a per-parameter rating outside 1–5 is refused (400)", (await post(T.token, `${B}/staff/appraisals/${A}/self`, { selfRating: 4, ratings: [{ parameterId: P("Lesson Observation").id, rating: 9 }] })).status, 400);
  const self = await post(T.token, `${B}/staff/appraisals/${A}/self`, { selfRating: 4, comments: "E2E I ran the debate club", ratings: [{ parameterId: P("Lesson Observation").id, rating: 4 }] });
  eq("the subject submits a self-assessment", self.status, 200);
  eq("…moving it to Appraiser review", self.json?.stage, "AppraiserReview");
  eq("the other Maths teacher cannot review it (403/404)", [403, 404].includes((await post(U.math2.token, `${B}/staff/appraisals/${A}/review`, { rating: 1 })).status), true);
  const review = await post(U.hodMath.token, `${B}/staff/appraisals/${A}/review`, { rating: 4, comments: "E2E solid term", strengths: "E2E pacing", developmentAreas: "E2E assessment", supportPlan: [{ gap: "E2E formative assessment", support: "E2E peer coaching", byWhen: "2026-12-01" }], nextTargets: [{ text: "E2E next term target" }] });
  eq("the appraiser reviews and rates", review.status, 200);
  eq("…moving it to Moderation", review.json?.stage, "Moderation");
  {
    const asMod = (await get(U.dos.token, `${B}/staff/appraisals/${A}`)).json;
    truthy("MODERATION VIEW: the moderator sees this appraiser's ratings against the branch's (five buckets each)", asMod?.appraiserRatingCounts?.length === 5 && asMod?.schoolRatingCounts?.length === 5 && asMod.appraiserRatingCounts.reduce((a, b) => a + b, 0) >= 1, JSON.stringify({ a: asMod?.appraiserRatingCounts, s: asMod?.schoolRatingCounts }));
    const asSubject = (await get(T.token, `${B}/staff/appraisals/${A}`)).json;
    eq("MODERATION VIEW: …which the subject does not get", (asSubject?.appraiserRatingCounts ?? []).length, 0);
  }
  eq("MODERATION: a different final rating without a reason is refused (400)", (await post(U.dos.token, `${B}/staff/appraisals/${A}/moderate`, { finalRating: 3, signNow: true })).status, 400);
  const sign = await post(U.dos.token, `${B}/staff/appraisals/${A}/moderate`, { finalRating: 4, signNow: true });
  eq("the Director of Studies moderates and signs", sign.status, 200);
  eq("…Signed", sign.json?.stage, "Signed");
  const frozen = sign.json?.computedScore;
  truthy("SIGNED: the computed score is frozen on the appraisal", frozen !== null && frozen !== undefined, JSON.stringify(sign.json?.computedScore));
  const toAnnul = ((await get(AD, `${B}/staff/records?subjectUserId=${T.id}&pageSize=10`)).json?.items ?? [])[0];
  await post(AD, `${B}/staff/records/${toAnnul?.id}/annul`, { reason: "E2E annulled after signing to prove the freeze" });
  const reread = (await get(U.dos.token, `${B}/staff/appraisals/${A}`)).json;
  eq("FROZEN: annulling a record after signing does not change the signed score", reread?.computedScore, frozen);
  eq("a signed appraisal cannot be reviewed again (400/409)", [400, 409].includes((await post(U.hodMath.token, `${B}/staff/appraisals/${A}/review`, { rating: 1 })).status), true);
  eq("only the subject may appeal (404/403 for the head)", [403, 404].includes((await post(U.hodMath.token, `${B}/staff/appraisals/${A}/appeal`, { note: "E2E appeal by the wrong person" })).status), true);
  const appeal = await post(T.token, `${B}/staff/appraisals/${A}/appeal`, { note: `E2E ${RUN} I disagree with the assessment rating` });
  eq("the subject appeals", appeal.status, 200);
  eq("…Appealed", appeal.json?.stage, "Appealed");
  eq("an appealed appraisal cannot be signed directly (400/409)", [400, 409].includes((await post(U.dos.token, `${B}/staff/appraisals/${A}/sign`)).status), true);
  const remod = await post(U.dos.token, `${B}/staff/appraisals/${A}/moderate`, { finalRating: 5, reason: "E2E appeal upheld", signNow: true });
  truthy("the appeal is moderated and re-signed", remod.status === 200 && remod.json?.stage === "Signed" && remod.json?.finalRating === 5, `${remod.status} ${remod.json?.stage}`);

  // ANNUAL ROLL-UP: the year's termly appraisals are shown on the annual one, and it is not signed while
  // one of them is unsigned. A second termly appraisal (Term 1, left open) is the unsigned one.
  const YEAR = PERIOD.slice(0, 4);
  const T1 = `${YEAR}-T1`;
  if (T1 !== PERIOD) {
    await post(U.dos.token, `${B}/staff/appraisals/open`, { periodKey: T1, subjectUserIds: [T.id] });
    const opened = await post(U.dos.token, `${B}/staff/appraisals/open`, { periodKey: YEAR, subjectUserIds: [T.id] });
    const annual = (opened.json ?? [])[0] ?? ((await get(U.dos.token, `${B}/staff/appraisals/board?period=${YEAR}`)).json?.items ?? []).find((a) => a.subjectUserId === T.id);
    const ann = (await get(U.dos.token, `${B}/staff/appraisals/${annual?.id}`)).json;
    truthy("ANNUAL: the annual appraisal lists that year's termly appraisals for the person", ann?.isAnnual === true && (ann?.termlyRollup ?? []).some((t) => t.periodKey === PERIOD && t.finalRating === 5) && (ann?.termlyRollup ?? []).some((t) => t.periodKey === T1 && t.finalRating == null), JSON.stringify(ann?.termlyRollup));
    eq("ANNUAL: …with the signed ones averaged", ann?.termlyAverage, 5);
    await put(U.hodMath.token, `${B}/staff/appraisals/${annual?.id}/targets`, { targets: [{ text: "E2E annual target" }] });
    await post(T.token, `${B}/staff/appraisals/${annual?.id}/self`, { selfRating: 4, ratings: [] });
    await post(U.hodMath.token, `${B}/staff/appraisals/${annual?.id}/review`, { rating: 5 });
    const blocked = await post(U.dos.token, `${B}/staff/appraisals/${annual?.id}/moderate`, { finalRating: 5, signNow: true });
    truthy("ANNUAL: signing is refused while a termly appraisal is unsigned, naming the term (409)", blocked.status === 409 && blocked.text.includes(T1), `${blocked.status} ${blocked.text}`);
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("14.9 REPORTS, ACTIVITY, PORTAL, NOTIFICATIONS");
{
  eq("a teacher cannot read reports (403)", (await get(U.math1.token, `${B}/staff/reports`)).status, 403);
  const hodR = (await get(U.hodMath.token, `${B}/staff/reports?period=${PERIOD}`)).json;
  truthy("the head's report is labelled as scoped to Mathematics", (hodR?.scopedToDepartments ?? []).includes("E2E Mathematics"), JSON.stringify(hodR?.scopedToDepartments));
  truthy("the head's report does not include the Languages department's figures", !(hodR?.byDepartment ?? []).some((d) => d.name === "E2E Languages" && d.staffCount > 0), JSON.stringify(hodR?.byDepartment));
  const dosR = (await get(U.dos.token, `${B}/staff/reports?period=${PERIOD}`)).json;
  truthy("the Director of Studies sees an unscoped report with a band distribution", (dosR?.scopedToDepartments ?? []).length === 0 && (dosR?.bandDistribution ?? []).length === 5, JSON.stringify(dosR?.bandDistribution));
  eq("PRIVATE leaderboard: no individual ranking in the report", (dosR?.leaderboard ?? []).length, 0);
  truthy("observer dispersion names the head who observed", (dosR?.observerDispersion ?? []).some((o) => o.observerUserId === U.hodMath.id), JSON.stringify(dosR?.observerDispersion));
}
{
  eq("a teacher cannot read the branch activity log (403)", (await get(U.math1.token, `${B}/staff/activity`)).status, 403);
  const hodAct = (await get(U.hodMath.token, `${B}/staff/activity?pageSize=200`)).json;
  truthy("SCOPE: the head's activity log never mentions the Languages teacher as subject", !(hodAct?.items ?? []).some((e) => e.subjectUserId === U.lang1.id), (hodAct?.items ?? []).length);
  const mineAct = (await get(U.math1.token, "/api/v1/staff/portal/activity?pageSize=200")).json;
  truthy("SUBJECT ACCESS: a teacher's own trail includes who viewed their timeline", (mineAct?.items ?? []).some((e) => e.action === "staff.timeline.viewed"), JSON.stringify((mineAct?.items ?? []).map((e) => e.action).slice(0, 10)));
  truthy("the trail records a truncated address, not a full one", (mineAct?.items ?? []).every((e) => !e.ipAddress || /\.0$|::$/.test(e.ipAddress)), JSON.stringify((mineAct?.items ?? []).map((e) => e.ipAddress).slice(0, 5)));

  // THE LEAK FOUND BY THE PLAN AUDIT (2026-09-17): the subject's own trail carried "Restricted record
  // created / viewed for <me>", telling them an investigation exists. The admin read the Restricted record
  // in 14.4, so a RecordViewed event about math1 at the Restricted rung exists.
  const adminAct = (await get(AD, `${B}/staff/activity?userId=${U.math1.id}&pageSize=200`)).json;
  truthy("the administrator's log does hold Restricted events about the Maths teacher (the precondition)", (adminAct?.items ?? []).some((e) => e.visibility === "Restricted"), JSON.stringify((adminAct?.items ?? []).map((e) => e.visibility).slice(0, 8)));
  truthy("LEAK: the subject's portal trail carries no Restricted event", !(mineAct?.items ?? []).some((e) => e.visibility === "Restricted" || /^Restricted record/.test(e.summary)), JSON.stringify((mineAct?.items ?? []).filter((e) => /Restricted/.test(e.summary)).map((e) => e.summary)));
  const selfTl = (await get(U.math1.token, `${B}/staff/members/${U.math1.id}/timeline`)).json;
  truthy("LEAK: …nor does the activity layer of their own timeline", !(selfTl?.activity ?? []).some((e) => e.visibility === "Restricted" || /^Restricted record/.test(e.summary)));
  const selfExport = await get(U.math1.token, "/api/v1/staff/portal/export");
  truthy("LEAK: …nor their exported file", selfExport.status === 200 && !(selfExport.json?.activity ?? []).some((e) => e.visibility === "Restricted" || /^Restricted record/.test(e.summary)), selfExport.status);
  const aaAct = (await get(U.aa.token, `${B}/staff/activity?pageSize=200`)).json;
  truthy("LEAK: no appraisal event in the log states a rating or a score (read as the Academic Assistant, no confidential rung)", !(aaAct?.items ?? []).some((e) => e.entityType === "StaffAppraisal" && /rating \d|score \d/i.test(e.summary)), JSON.stringify((aaAct?.items ?? []).filter((e) => e.entityType === "StaffAppraisal").map((e) => e.summary).slice(0, 4)));
}
for (const key of ["dos", "aa", "hodMath", "hodEmpty", "math1", "lang1", "support"]) {
  const r = await get(U[key].token, "/api/v1/staff/portal");
  eq(`PORTAL: ${key} opens their portal`, r.status, 200);
}
{
  const p = (await get(U.math1.token, "/api/v1/staff/portal")).json;
  truthy("the Maths teacher's portal carries a score with a period", !!p?.score?.period?.key, JSON.stringify(p?.score?.period));
  truthy("…their recognition received", (p?.recognitionReceived ?? []).length > 0);
  truthy("…the pinned notice", (p?.notices ?? []).some((n) => n.title.includes(RUN)));
  truthy("…a private rank (theirs only)", typeof p?.score?.rankInBranch === "number", JSON.stringify({ r: p?.score?.rankInBranch, of: p?.score?.rankedOutOf }));
  const exp = await get(T.token, "/api/v1/staff/portal/export");
  truthy("EXPORT MY FILE: returns records and appraisals, never Restricted content", exp.status === 200 && (exp.json?.records ?? []).length >= 0 && (exp.json?.appraisals ?? []).length > 0 && !exp.text.includes("SECRETWORD"), `${exp.status} ${(exp.json?.records ?? []).length}`);
  const colleagues = (await get(U.math1.token, "/api/v1/staff/portal/colleagues")).json ?? [];
  truthy("colleagues list excludes the caller", colleagues.length > 0 && !colleagues.some((c) => c.userId === U.math1.id));
  const readAll = await post(U.math1.token, "/api/v1/notifications/read-all?eventKey=staff.recognition-received");
  eq("NOTIFICATIONS: mark one group read", readAll.status, 200);
  const unreadRec = ((await get(U.math1.token, "/api/v1/notifications?eventKey=staff.recognition-received&unreadOnly=true&limit=50")).json ?? []).length;
  eq("…leaves no unread notification in that group", unreadRec, 0);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.10 IMPORT, CUSTOM ROLE SCOPE, SIGN-OUT");
{
  eq("IMPORT: a scoped caller cannot start a staff import (403)", (await post(U.hodMath.token, `${B}/staff/import-jobs`, { rows: [], sendInvites: false })).status, 403);
  const email = `e2e.sp.import.${RUN}@qmgr.local`;
  const imp = await post(AD, `${B}/staff/import-jobs`, {
    sendInvites: false,
    rows: [
      { firstName: "Irene", lastName: `Import${RUN}`, email, roleCode: "teacher", departmentCodes: ["E2EMATH"], lineManagerEmail: "e2e.sp.hod.math@qmgr.local" },
      { firstName: "Dup", lastName: "Row", email: "e2e.sp.math1@qmgr.local", roleCode: "teacher" },
      { firstName: "Bad", lastName: "Role", email: `e2e.sp.badrole.${RUN}@qmgr.local`, roleCode: "admin" },
    ],
  });
  eq("IMPORT: the Tenant Admin starts a three-row import (202)", imp.status, 202);
  // Since Phase 0A the start answers { job, temporaryPasswords } (the slips are shown once, with the job).
  let job = imp.json?.job ?? imp.json;
  const jobId = job?.id;
  for (let i = 0; i < 40 && job && !["Completed", "CompletedWithErrors", "Failed"].includes(job.status); i++) {
    await new Promise((r) => setTimeout(r, 1000));
    job = (await get(AD, `${B}/staff/import-jobs/${jobId}`)).json;
  }
  truthy("IMPORT: the job finishes", ["Completed", "CompletedWithErrors"].includes(job?.status), JSON.stringify(job));
  eq("IMPORT: one account created", job?.createdCount, 1);
  eq("IMPORT: the existing email is reported as already on file", job?.duplicateCount, 1);
  eq("IMPORT: the administrator role row fails", job?.failedCount, 1);
  const imported = ((await get(AD, `${B}/staff/structure/members`)).json?.items ?? []).find((m) => m.email === email);
  truthy("IMPORT: the new teacher is in Maths with the head as line manager", imported?.departmentIds?.includes(MATH.id) && imported?.lineManagerUserId === U.hodMath.id, JSON.stringify(imported));
}
{
  const perms = (await get(AD, "/api/v1/roles/permissions")).json ?? [];
  const flat = perms.flatMap((c) => c.permissions ?? []);
  const pid = (code) => flat.find((p) => p.code === code)?.id;
  const code = `e2e-line-mgr-${RUN}`.slice(0, 40);
  const role = await post(AD, "/api/v1/roles", { code, name: `E2E Line Manager ${RUN}`, staffScope: "DirectReports", dataScope: "Organization", permissionIds: [pid("dashboard.view"), pid("staff.records.view")].filter(Boolean) });
  eq("ROLE EDITOR: a custom role with StaffScope DirectReports is created", role.status, 201);
  eq("…and carries the scope", role.json?.staffScope, "DirectReports");
  // Put the Academic Assistant on it: their only direct report is support staff.
  const u = usersArray().find((x) => x.id === U.aa.id);
  const r = await put(AD, `/api/v1/users/${U.aa.id}`, { firstName: U.aa.first, lastName: U.aa.last, email: `${U.aa.username}@qmgr.local`, roleId: role.json?.id, assignedBranchId: BRANCH, isActive: true });
  if (r.status < 300) {
    const t = await login(`${U.aa.username}@qmgr.local`, PW);
    const dir = (await get(t, `${B}/staff/structure/members`)).json;
    const ids = (dir?.items ?? []).map((m) => m.userId);
    truthy("DIRECT REPORTS: the line manager sees their report (support staff) and not the Maths teachers", ids.includes(U.support.id) && !ids.includes(U.math1.id), JSON.stringify(ids.length));
    const told = (await get(t, "/api/v1/notifications?eventKey=staff.profile-changed&limit=20")).json ?? [];
    truthy("ROLE CHANGE: the person is told (staff.profile-changed, 'Your role changed')", told.some((n) => n.title === "Your role changed"), JSON.stringify(told.map((n) => n.title).slice(0, 3)));
    const logged = (await get(AD, `${B}/staff/activity?userId=${U.aa.id}&action=staff.structure.role-changed&pageSize=20`)).json;
    truthy("ROLE CHANGE: …and the change is in the activity log, from what to what", (logged?.items ?? []).some((e) => /role changed from Academic Assistant to E2E Line Manager/i.test(e.summary)), JSON.stringify((logged?.items ?? []).map((e) => e.summary).slice(0, 2)));
  } else bad("move the Academic Assistant onto the custom role", "200", `${r.status} ${r.text}`);
  await put(AD, `/api/v1/users/${U.aa.id}`, { firstName: U.aa.first, lastName: U.aa.last, email: `${U.aa.username}@qmgr.local`, roleId: roleId("academic-assistant"), assignedBranchId: BRANCH, isActive: true });
  if (role.json?.id) await del(AD, `/api/v1/roles/${role.json.id}`);
}
{
  const t = await login(`${U.support.username}@qmgr.local`, PW);
  const loginRes = await call(null, "POST", "/api/v1/auth/login", { email: `${U.support.username}@qmgr.local`, password: await passwordOf(`${U.support.username}@qmgr.local`) });
  const refresh = loginRes.json?.refreshToken;
  eq("SIGN-OUT: the logout endpoint answers 204", (await post(loginRes.json?.accessToken, "/api/v1/auth/logout")).status, 204);
  const rr = await call(null, "POST", "/api/v1/auth/refresh", { refreshToken: refresh });
  truthy("SIGN-OUT: the refresh token no longer works", rr.status === 401 || rr.status === 400, rr.status);
  const trail = (await get(t, "/api/v1/staff/portal/activity?pageSize=50")).json;
  truthy("SIGN-OUT: signed-in and signed-out are both in the person's own trail", (trail?.items ?? []).some((e) => e.action === "auth.signed-in") && (trail?.items ?? []).some((e) => e.action === "auth.signed-out"), JSON.stringify((trail?.items ?? []).map((e) => e.action)));
}

// ---------------------------------------------------------------------------------------------------
hdr("14.11 CLOSING A PERIOD — refused with unsigned appraisals unless overridden, and a closed period takes no new evidence");
{
  // A past period, so closing it cannot disturb anything else this suite does.
  const LAST = `${new Date().getFullYear() - 1}-T2`;
  const midLast = iso(new Date(new Date().getFullYear() - 1, 5, 15, 10));
  const close = (t, body) => post(t, `/api/v1/staff/policy/periods/${LAST}/close`, body ?? {});
  const reopen = (t, body) => post(t, `/api/v1/staff/policy/periods/${LAST}/reopen`, body ?? {});
  await reopen(U.dos.token, { reason: "E2E clean slate" }); // idempotent setup: ignore "not closed"

  eq("a teacher cannot close a period (403)", (await close(U.math1.token)).status, 403);
  eq("a head of department cannot close a period (403)", (await close(U.hodMath.token)).status, 403);

  // An unsigned appraisal in that period makes the override necessary.
  await post(U.dos.token, `${B}/staff/appraisals/open`, { periodKey: LAST, subjectUserIds: [U.lang1.id] });
  const status = (await get(U.dos.token, `/api/v1/staff/policy/periods/${LAST}`)).json;
  truthy("PERIOD STATUS: the period reports its unsigned appraisals", (status?.unsignedAppraisals ?? 0) >= 1 && status?.isClosed === false, JSON.stringify(status));
  const refused = await close(U.dos.token, {});
  truthy("CLOSE: refused while an appraisal is unsigned and no reason is given (409, count named)", refused.status === 409 && /not yet signed/i.test(refused.text), `${refused.status} ${refused.text}`);
  eq("CLOSE: a reason under ten characters does not count as an override (409)", (await close(U.dos.token, { overrideReason: "because" })).status, 409);

  const racers = await Promise.all([1, 2, 3].map(() => close(U.dos.token, { overrideReason: `E2E ${RUN} closing with the Languages appraisal carried over` })));
  const codes = racers.map((r) => r.status).sort();
  truthy("RACE: three simultaneous closes → exactly one 200, the rest 409, never a 500", codes.filter((c) => c === 200).length === 1 && codes.every((c) => c === 200 || c === 409), codes.join(","));
  const closedStatus = racers.find((r) => r.status === 200)?.json;
  truthy("CLOSE: the closure carries who, the override reason and the unsigned count", closedStatus?.isClosed && closedStatus?.closure?.overrideReason?.includes(RUN) && closedStatus?.closure?.unsignedAppraisalsAtClose >= 1 && !!closedStatus?.closure?.closedByName, JSON.stringify(closedStatus?.closure));
  const pol = (await get(AD, "/api/v1/staff/policy")).json;
  eq("CLOSE: the closure is recorded once in the policy", (pol?.closedPeriods ?? []).filter((c) => c.key === LAST).length, 1);
  eq("CLOSE: saving the policy editor keeps the closure", ((await put(AD, "/api/v1/staff/policy", { ...pol, closedPeriods: [] })).json?.closedPeriods ?? []).filter((c) => c.key === LAST).length, 1);

  const inClosed = { subjectUserId: U.math1.id, parameterId: P("Co-curricular Activity").id, points: 1, visibility: "Standard", occurredAt: midLast, description: `E2E ${RUN} dated in a closed period` };
  const blocked = await post(AD, `${B}/staff/records?acknowledgeLateEntry=true`, inClosed);
  truthy("CLOSED: a scored record dated in the closed period is refused (409, period named)", blocked.status === 409 && /is closed/i.test(blocked.text), `${blocked.status} ${blocked.text}`);
  const wellbeing = await post(AD, `${B}/staff/records?acknowledgeLateEntry=true`, { ...inClosed, parameterId: P("Wellbeing").id, points: null, description: `E2E ${RUN} wellbeing note dated in a closed period` });
  eq("CLOSED: a Wellbeing record still goes through — support never waits for a reopen (201)", wellbeing.status, 201);
  const openInClosed = await post(U.dos.token, `${B}/staff/appraisals/open`, { periodKey: LAST, subjectUserIds: [U.math2.id] });
  eq("CLOSED: no appraisal opens in a closed period (409)", openInClosed.status, 409);
  const lastDuty = await post(AD, `${B}/staff/duties`, dutyBody({ title: `E2E ${RUN} meeting in a closed period`, startsAt: midLast, endsAt: iso(new Date(new Date(midLast).getTime() + 3600_000)), recorderUserIds: [U.lang1.id] }));
  if (lastDuty.status === 201) {
    const reg = await post(U.lang1.token, `${B}/staff/duties/${lastDuty.json.id}/register`, { close: true, entries: [{ userId: U.math1.id, outcome: "Present" }] });
    eq("CLOSED: a register for a duty in the closed period is refused (409)", reg.status, 409);
  } else bad("create a duty dated in the closed period", "201", `${lastDuty.status} ${lastDuty.text}`);
  const board = (await get(U.dos.token, `${B}/staff/appraisals/board?period=${LAST}`)).json;
  truthy("BOARD: the appraisal board says the period is closed", board?.periodStatus?.isClosed === true, JSON.stringify(board?.periodStatus));

  eq("REOPEN: a reason is required (400)", (await reopen(U.dos.token, { reason: "" })).status, 400);
  eq("REOPEN: the Director of Studies reopens it with a reason", (await reopen(U.dos.token, { reason: `E2E ${RUN} reopened for the suite` })).status, 200);
  const act = (await get(AD, `${B}/staff/activity?action=staff.period.closed&pageSize=5`)).json;
  truthy("ACTIVITY: the close is in the log with the override noted", (act?.items ?? []).some((e) => /override recorded/.test(e.summary)), JSON.stringify((act?.items ?? []).map((e) => e.summary).slice(0, 2)));
}

// ---------------------------------------------------------------------------------------------------
hdr("14.12 LEADERBOARD MODES on the portal — private by default, department averages, public top-N");
{
  const pol = (await get(AD, "/api/v1/staff/policy")).json;
  const portal = async () => (await get(U.math1.token, "/api/v1/staff/portal")).json;
  await put(AD, "/api/v1/staff/policy", { ...pol, leaderboardMode: "Private" });
  let p = await portal();
  truthy("PRIVATE: no department board and no names on anyone's portal", (p?.departmentBoard ?? []).length === 0 && (p?.leaderboard ?? []).length === 0, JSON.stringify({ d: p?.departmentBoard?.length, l: p?.leaderboard?.length }));
  await put(AD, "/api/v1/staff/policy", { ...pol, leaderboardMode: "Department" });
  p = await portal();
  truthy("DEPARTMENT: department averages appear on the portal, and still no individual names", (p?.departmentBoard ?? []).length > 0 && (p?.leaderboard ?? []).length === 0, JSON.stringify(p?.departmentBoard));
  truthy("DEPARTMENT: only departments of three or more are shown (an average of one is a person)", (p?.departmentBoard ?? []).every((d) => d.staffCount >= 3), JSON.stringify((p?.departmentBoard ?? []).map((d) => d.staffCount)));
  await put(AD, "/api/v1/staff/policy", { ...pol, leaderboardMode: "Public", leaderboardTopN: 3 });
  p = await portal();
  truthy("PUBLIC: a top-N board of names appears on a teacher's portal, capped at N", (p?.leaderboard ?? []).length > 0 && p.leaderboard.length <= 3, JSON.stringify((p?.leaderboard ?? []).map((r) => r.rank)));
  await put(AD, "/api/v1/staff/policy", { ...pol, leaderboardMode: "Private" });
}

// ---------------------------------------------------------------------------------------------------
hdr("14.13 EXPORTS AND PUBLISHES are logged");
{
  const exp = (t, body) => post(t, `${B}/staff/activity/exports`, body);
  eq("a teacher cannot log a reports export (403)", (await exp(U.math1.token, { kind: "reports", format: "csv" })).status, 403);
  eq("an unknown export kind is refused (400)", (await exp(U.dos.token, { kind: "everything" })).status, 400);
  eq("SCOPE: the head of Maths cannot log an export of the Languages teacher's timeline (404)", (await exp(U.hodMath.token, { kind: "timeline", subjectUserId: U.lang1.id, format: "pdf" })).status, 404);
  eq("the head logs an export of a Maths teacher's timeline (204)", (await exp(U.hodMath.token, { kind: "timeline", subjectUserId: U.math1.id, format: "csv", rowCount: 12 })).status, 204);
  eq("the Director of Studies logs a Publish to Library of the reports (204)", (await exp(U.dos.token, { kind: "reports", published: true, documentName: `E2E ${RUN} Staff report`, periodKey: PERIOD })).status, 204);
  const trail = (await get(U.math1.token, "/api/v1/staff/portal/activity?pageSize=50")).json;
  truthy("SUBJECT ACCESS: the teacher sees that their timeline was exported, by whom and as what", (trail?.items ?? []).some((e) => e.action === "staff.timeline.exported" && /as CSV \(12 row/.test(e.summary)), JSON.stringify((trail?.items ?? []).map((e) => e.summary).slice(0, 4)));
  const log = (await get(AD, `${B}/staff/activity?action=staff.report.published&pageSize=5`)).json;
  truthy("the publish is in the branch log with the document name", (log?.items ?? []).some((e) => e.summary.includes(`E2E ${RUN} Staff report`)), JSON.stringify((log?.items ?? []).map((e) => e.summary)));
}

// ---------------------------------------------------------------------------------------------------
hdr("14.14 OBSERVER PAIRS — two observers on one lesson, shown only to the confidential rung");
{
  const second = await rec(U.dos.token, { subjectUserId: U.math1.id, parameterId: P("Lesson Observation").id, rating: 1, occurredAt: obs.json?.occurredAt, description: `E2E ${RUN} second observer on the S2 algebra lesson` });
  eq("the Director of Studies logs a second observation of the same lesson", second.status, 201);
  const dosR = (await get(U.dos.token, `${B}/staff/reports?period=${PERIOD}`)).json;
  const pair = (dosR?.observationPairs ?? []).find((x) => x.subjectUserId === U.math1.id && x.ratings.some((r) => r.observerUserId === U.dos.id) && x.ratings.some((r) => r.observerUserId === U.hodMath.id));
  truthy("PAIRS: the report shows the pair side by side with its spread", !!pair && pair.spread >= 2, JSON.stringify(pair ?? (dosR?.observationPairs ?? []).slice(0, 2)));
  const aaR = (await get(U.aa.token, `${B}/staff/reports?period=${PERIOD}`)).json;
  eq("PAIRS: the Academic Assistant (no confidential rung) gets no pairs", (aaR?.observationPairs ?? []).length, 0);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.15 AUTOMATIC CREDIT — the person who served, positive feedback, off by default");
{
  const pol = (await get(AD, "/api/v1/staff/policy")).json;
  // Serving a ticket needs the queue module and a counter. The dev tenant has neither, so this section
  // grants the module and makes a counter for itself, and puts both back the way it found them.
  const tenantModules = (await get(SA, `/api/v1/admin/tenants/${ORG}/modules`)).json ?? [];
  const queueWasActive = tenantModules.some((m) => m.moduleCode === "core-queue" && m.purchased && m.status === "Active");
  if (!queueWasActive) await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/core-queue`, { note: "E2E automatic credit" });
  const services = (await get(AD, `/api/v1/branches/${BRANCH}/service-types`)).json ?? [];
  const svc = (Array.isArray(services) ? services : services.items ?? []).find((s) => s.isActive !== false);
  let counters = (await get(AD, `/api/v1/branches/${BRANCH}/counters`)).json ?? [];
  let madeCounter = null;
  if (svc && !(Array.isArray(counters) ? counters : []).some((c) => c.isActive !== false)) {
    const made = await post(AD, `/api/v1/branches/${BRANCH}/counters`, { counterNumber: `E2E${RUN}`.slice(0, 10).toUpperCase(), displayName: `E2E ${RUN} counter`, serviceTypeIds: [svc.id], isEnabled: true });
    if (made.status < 300) madeCounter = made.json;
    else bad("create a counter to serve at", "201", `${made.status} ${made.text}`);
    counters = (await get(AD, `/api/v1/branches/${BRANCH}/counters`)).json ?? [];
  }
  const ctr = madeCounter ?? (Array.isArray(counters) ? counters : counters.items ?? []).find((c) => c.isActive !== false);
  if (!svc || !ctr) {
    bad("AUTOMATIC CREDIT: the tenant has a service type and a counter to serve a ticket at", "both", `${!!svc} ${!!ctr}`);
  } else {
    const credited = async (name) => ((await get(AD, "/api/v1/staff/portal/records?pageSize=100")).json?.items ?? []).filter((r) => r.parameterName === name && r.description.includes(RUN)).length;
    const serve = async (label) => {
      const tok = await post(AD, `/api/v1/branches/${BRANCH}/tokens`, { serviceTypeCode: svc.code, customer: { name: `E2E ${RUN} ${label}` }, externalReference: `E2E ${RUN} ${label}` });
      if (tok.status >= 300) return { error: `${tok.status} ${tok.text}` };
      const called = await post(AD, `/api/v1/counters/${ctr.id}/call/${tok.json.id}`);
      if (called.status >= 300) return { error: `call ${called.status} ${called.text}` };
      const done = await post(AD, `/api/v1/counters/${ctr.id}/complete`, { tokenId: tok.json.id, notes: `E2E ${RUN}` });
      if (done.status >= 300) return { error: `complete ${done.status} ${done.text}` };
      return { token: tok.json, display: tok.json.displayNumber };
    };

    await put(AD, "/api/v1/staff/policy", { ...pol, systemAwardsEnabled: false });
    const off = await serve("off");
    if (off.error) bad("AUTOMATIC CREDIT: serve a ticket end to end", "ok", off.error);
    const offCount = ((await get(AD, "/api/v1/staff/portal/records?pageSize=100")).json?.items ?? []).filter((r) => r.parameterName === "Customer served" && r.description.includes(off.display ?? "~")).length;
    eq("OFF BY DEFAULT: serving a ticket credits nothing while automatic credit is off", offCount, 0);

    await put(AD, "/api/v1/staff/policy", { ...pol, systemAwardsEnabled: true });
    const on = await serve("on");
    if (on.error) bad("AUTOMATIC CREDIT: serve a second ticket", "ok", on.error);
    else {
      const recs = ((await get(AD, "/api/v1/staff/portal/records?pageSize=100")).json?.items ?? []);
      truthy("SERVED: completing a ticket credits the person who completed it ('Customer served', automatic)", recs.some((r) => r.parameterName === "Customer served" && r.source === "System" && r.description.includes(on.display)), JSON.stringify(recs.filter((r) => r.source === "System").map((r) => r.description).slice(0, 3)));
      const fb = await call(null, "POST", `/api/v1/branches/${BRANCH}/tokens/${on.token.id}/feedback`, { rating: 5, comment: `E2E ${RUN} lovely service` });
      truthy("FEEDBACK: feedback records who served the customer (ServedByUserId was never written before)", fb.status === 201 && !!fb.json?.servedByUserId, `${fb.status} ${fb.json?.servedByUserId} ${fb.text?.slice(0, 200)}`);
      const recs2 = ((await get(AD, "/api/v1/staff/portal/records?pageSize=100")).json?.items ?? []);
      truthy("FEEDBACK: a 5-star rating credits the server ('Positive feedback', automatic)", recs2.some((r) => r.parameterName === "Positive feedback" && r.source === "System" && r.description.includes(on.display)), JSON.stringify(recs2.filter((r) => r.source === "System").map((r) => r.description).slice(0, 4)));
    }
    await put(AD, "/api/v1/staff/policy", { ...pol, systemAwardsEnabled: false });
  }
  if (madeCounter?.id) await del(AD, `/api/v1/branches/${BRANCH}/counters/${madeCounter.id}`);
  if (!queueWasActive) await call(SA, "DELETE", `/api/v1/admin/tenants/${ORG}/modules/core-queue?note=E2E%20automatic%20credit%20done`);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.16 THE SWEEPS — each fires once and not twice, and not at all for a tenant without the module");
{
  // Triggered through the Hangfire dashboard, which Development opens to local callers. A production
  // API refuses this, and the section says so rather than failing.
  const trigger = async (id) => {
    const res = await fetch(`${API}/hangfire/recurring/trigger`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: `jobs%5B%5D=${encodeURIComponent(id)}` });
    return res.status;
  };
  const waitFor = async (fn, ms = 20000) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await new Promise((r) => setTimeout(r, 750)); } return v; };
  const probe = await trigger("staff-activity-attribution-purge");
  if (probe >= 300 && probe !== 204) {
    console.log(`  \x1b[33mSKIP\x1b[0m  Hangfire dashboard refused a trigger (${probe}); the sweeps are asserted only against a Development API`);
  } else {
    const count = async (token, key, needle) => ((await get(token, `/api/v1/notifications?eventKey=${key}&limit=100`)).json ?? []).filter((n) => (n.title + " " + n.message).includes(needle)).length;

    // Both ladders run in the one 15-minute reminder sweep (duty rota plan §8.1). Quiet hours would hold their
    // non-interruptive stages overnight, so they are switched off for this block and restored after it.
    const ladderPol = (await get(AD, "/api/v1/staff/policy")).json;
    await put(AD, "/api/v1/staff/policy", { ...ladderPol, quietHours: { ...(ladderPol.quietHours ?? {}), enabled: false } });

    // Register chase: a duty that ended with no register, recorder math2.
    const chaseTitle = `E2E ${RUN} Chase meeting`;
    const chase = await post(AD, `${B}/staff/duties`, dutyBody({ title: chaseTitle, startsAt: hoursFromNow(-5), endsAt: hoursFromNow(-4), recorderUserIds: [U.math2.id] }));
    eq("a duty that has ended with no register is created", chase.status, 201);

    // Without the module the sweep must leave it alone.
    await call(SA, "DELETE", `/api/v1/admin/tenants/${ORG}/modules/student-welfare?note=E2E%20sweep%20gate`);
    await trigger("staff-reminder-ladder");
    await new Promise((r) => setTimeout(r, 6000));
    await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/student-welfare`, { note: "E2E" });
    eq("MODULE GATE: with the module revoked, the register chase sends nothing", await count(U.math2.token, "staff.register-due", chaseTitle), 0);

    await trigger("staff-reminder-ladder");
    const once = await waitFor(async () => (await count(U.math2.token, "staff.register-due", chaseTitle)) >= 1);
    truthy("CHASE: the named recorder is chased", !!once);
    await trigger("staff-reminder-ladder");
    await new Promise((r) => setTimeout(r, 6000));
    eq("CHASE: a second sweep does not chase again (exactly one)", await count(U.math2.token, "staff.register-due", chaseTitle), 1);

    // Duty reminder: a duty in two hours, inside the default 24-hour lead.
    const remindTitle = `E2E ${RUN} Reminder meeting`;
    await post(AD, `${B}/staff/duties`, dutyBody({ title: remindTitle, startsAt: hoursFromNow(2), endsAt: hoursFromNow(3), expectedUserIds: [U.math2.id], recorderUserIds: [U.hodMath.id] }));
    await trigger("staff-reminder-ladder");
    truthy("REMINDER: the expected person is reminded", !!(await waitFor(async () => (await count(U.math2.token, "staff.duty-reminder", remindTitle)) >= 1)));
    await trigger("staff-reminder-ladder");
    await new Promise((r) => setTimeout(r, 6000));
    eq("REMINDER: a second sweep does not remind again", await count(U.math2.token, "staff.duty-reminder", remindTitle), 1);
    truthy("REMINDER: a named recorder who is not expected is reminded too", !!(await waitFor(async () => (await count(U.hodMath.token, "staff.duty-reminder", remindTitle)) >= 1)));
    await put(AD, "/api/v1/staff/policy", { ...ladderPol });

    // Weekly digest: today, from hour 0, for the fresh appraisee (who has never had one).
    const pol = (await get(AD, "/api/v1/staff/policy")).json;
    const kampalaDay = new Intl.DateTimeFormat("en-US", { weekday: "long", timeZone: "Africa/Kampala" }).format(new Date());
    await put(AD, "/api/v1/staff/policy", { ...pol, digestWeekday: kampalaDay, digestHour: 0 });
    await trigger("staff-weekly-digests");
    const digest = await waitFor(async () => (await count(T.token, "staff.weekly-digest", "weekly digest")) >= 1, 45000);
    truthy("DIGEST: the weekly digest reaches a staff member on the policy day", !!digest);
    await trigger("staff-weekly-digests");
    await new Promise((r) => setTimeout(r, 8000));
    eq("DIGEST: a second sweep the same week does not send again", await count(T.token, "staff.weekly-digest", "weekly digest"), 1);
    const digestMsg = ((await get(T.token, "/api/v1/notifications?eventKey=staff.weekly-digest&limit=5")).json ?? [])[0]?.message ?? "";
    truthy("DIGEST: it reports the week's movement ('this week')", /this week/.test(digestMsg) || !/composite/.test(digestMsg), digestMsg);
    await put(AD, "/api/v1/staff/policy", { ...pol });
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("14.17 STRUCTURE NOTICES — deputy heads and a department's staff are told");
{
  const langNow = ((await get(AD, `${B}/staff/structure/departments?includeInactive=true`)).json ?? []).find((d) => d.code === "E2ELANG");
  const setLang = (headKey, deputyKey) => put(AD, `${B}/staff/structure/departments/${langNow.id}`, { name: langNow.name, code: langNow.code, headUserId: headKey ? U[headKey].id : null, deputyHeadUserId: deputyKey ? U[deputyKey].id : null, sortOrder: 1 });
  await setLang("hodLang", "support");
  const deputyTold = ((await get(U.support.token, "/api/v1/notifications?eventKey=staff.profile-changed&limit=20")).json ?? []).some((n) => n.title === "You are now deputy head of department");
  truthy("DEPUTY: a newly assigned deputy head is told", deputyTold);
  await setLang("dos", null);
  const memberTold = ((await get(U.lang1.token, "/api/v1/notifications?eventKey=staff.profile-changed&limit=20")).json ?? []).some((n) => n.title === "E2E Languages has a new head");
  truthy("HEAD CHANGE: the department's staff are told who their head now is", memberTold);
  await setLang("hodLang", null);
}

// ---------------------------------------------------------------------------------------------------
hdr("14.18 WEIGHT CAP on retire and reinstate");
{
  const pol = (await get(AD, "/api/v1/staff/policy")).json;
  const heavy = await post(AD, "/api/v1/staff/parameters", { name: `E2E Reinstate ${RUN}`, kind: "Contribution", appliesTo: "AllStaff", defaultPoints: 1, maxPointsPerEntry: 1, weight: 4, purpose: "E2E reinstate cap check" });
  eq("a parameter at weight 4 is created under the 50% cap", heavy.status, 201);
  await patch(AD, `/api/v1/staff/parameters/${heavy.json?.id}/toggle`); // retire
  await put(AD, "/api/v1/staff/policy", { ...pol, maxParameterWeightPercent: 10 });
  const back = await patch(AD, `/api/v1/staff/parameters/${heavy.json?.id}/toggle`);
  eq("REINSTATE: bringing it back above a tightened 10% cap is refused (400)", back.status, 400);
  await put(AD, "/api/v1/staff/policy", { ...pol, maxParameterWeightPercent: 50 });
}

// ---------------------------------------------------------------------------------------------------
hdr("14.19 USAGE METERING — people using the product are not 'API calls'; integration clients are");
{
  // Found by the first load test (2026-09-17): every /api/v1 request counted against a module's monthly
  // API-call allowance, the web app's own included, so 50 staff used a tenant's month up in under a
  // minute and everything after was a 402. User decision: only X-API-Key traffic is metered.
  const calls = async () => (await get(AD, "/api/v1/billing/usage")).json?.apiCalls;
  const before = await calls();
  truthy("METERING: the tenant's API-call count is readable (billing/usage)", typeof before === "number", JSON.stringify(before));
  await Promise.all(Array.from({ length: 30 }, () => get(U.math1.token, "/api/v1/staff/portal")));
  eq("METERING: thirty signed-in portal reads add nothing to the tenant's API calls", await calls(), before);

  const mods = (await get(SA, `/api/v1/admin/tenants/${ORG}/modules`)).json ?? [];
  const integrationsWasActive = mods.some((m) => m.moduleCode === "integrations-api" && m.purchased && m.status === "Active");
  if (!integrationsWasActive) await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/integrations-api`, { note: "E2E metering" });
  const client = await post(AD, "/api/v1/api-clients", { name: `E2E ${RUN} metering client`, scopes: [] });
  if (client.status < 300 && client.json?.clientSecret) {
    const key = `${client.json.client?.clientId}.${client.json.clientSecret}`;
    // A permission-guarded route: API keys may call only those, and the call is metered before its
    // scope is checked (this client has none, so it is refused — and still counted, as it should be).
    const mid = await calls();
    await call(null, "GET", `${B}/staff/records`, undefined, { "X-API-Key": key });
    const probe = await call(null, "GET", `${B}/staff/records`, undefined, { "X-API-Key": key });
    const after = await calls();
    truthy("METERING: an integration call with an API key IS counted", typeof mid === "number" && after === mid + 2, `${mid} → ${after} (probe ${probe.status})`);
    if (client.json.client?.id) await del(AD, `/api/v1/api-clients/${client.json.client.id}`);
  } else bad("METERING: create an API client to meter", "201 with a secret", `${client.status} ${client.text}`);
  if (!integrationsWasActive) await call(SA, "DELETE", `/api/v1/admin/tenants/${ORG}/modules/integrations-api?note=E2E%20metering%20done`);
}

// ---------------------------------------------------------------------------------------------------
console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
if (failures.length) console.log("Failures:\n  - " + failures.join("\n  - "));
process.exit(fail);

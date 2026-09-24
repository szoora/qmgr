// SECTION 37 — THE RBAC REVIEW'S RECOMMENDATIONS, BUILT (2026-09-24).
//
// "implement your recommendations and decisions": the school chain above the front office (R1), the safeguarding
// lead post (R2), the leaver sweep (R3), house and dormitory posts (R4), the access review (R5), the acting head
// (R6), the Board Member role and its figures-only permission (R7), and the front-office roles offered only to a
// tenant that runs a front office (R8).
//
// Borrows section 14's e2e.sp.dos / e2e.sp.aa / e2e.sp.math1 and puts every role and post back. Creates ONE scratch
// account (a Board Member, then a leaver) and ONE scratch student, and leaves both switched off, labelled with the run.
//
// Run: API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/leadership-e2e.mjs
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH;
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const RUN = Date.now().toString(36).slice(-6);

let pass = 0, fail = 0;
const failures = [];
const viewer = (line) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: line + "\n" }).catch(() => {});
const ok = (name) => { pass++; const l = `  \x1b[32mPASS\x1b[0m  ${name}`; console.log(l); viewer(l); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  const l = `  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 400)}`;
  console.log(l); viewer(l);
};
const skip = (name, why) => { const l = `  \x1b[33mSKIP\x1b[0m  ${name} — ${why}`; console.log(l); viewer(l); };
const hdr = (t) => { console.log(`\n\x1b[1m${t}\x1b[0m`); viewer(t); };
const eq = (name, actual, expected) => (actual === expected ? ok(name) : bad(name, expected, actual));
const truthy = (name, cond, detail = "") => (cond ? ok(name) : bad(name, "true", `false ${detail}`));

async function call(token, method, path, body) {
  const h = { Authorization: `Bearer ${token}` };
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text };
}
const get = (t, p) => call(t, "GET", p);
const put = (t, p, b) => call(t, "PUT", p, b);
const post = (t, p, b) => call(t, "POST", p, b);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) return (await r.json()).accessToken;
  }
  return null;
}
const perms = async (token) => new Set((await get(token, "/api/v1/auth/me")).json?.permissions ?? []);
const waitFor = async (fn, ms = 25000) => { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await new Promise((r) => setTimeout(r, 750)); } return false; };
const today = () => new Date().toISOString().slice(0, 10);
const plusDays = (n) => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

if (!BRANCH) { console.error("BRANCH is required"); process.exit(1); }
const AD = await login(TENANT_ADMIN);
if (!AD) { console.error("the tenant administrator could not sign in"); process.exit(1); }
const B = `/api/v1/branches/${BRANCH}`;

const rolesRes = await get(AD, "/api/v1/roles");
const roles = Array.isArray(rolesRes.json) ? rolesRes.json : rolesRes.json?.items ?? [];
const byCode = Object.fromEntries(roles.map((r) => [r.code, r]));
const usersRes = (await get(AD, "/api/v1/users?pageSize=500")).json;
const users = Array.isArray(usersRes) ? usersRes : usersRes?.items ?? [];
const find = (username) => users.find((u) => u.username === username);
const dos = find("e2e.sp.dos"), aa = find("e2e.sp.aa"), math1 = find("e2e.sp.math1");
if (!dos || !aa || !math1) { console.error("e2e.sp.dos / e2e.sp.aa / e2e.sp.math1 do not exist — run section 14 first"); process.exit(1); }

const restoreRoles = [[dos.id, dos.roleId], [aa.id, aa.roleId], [math1.id, math1.roleId]];
const leadershipBefore = (await get(AD, "/api/v1/leadership")).json;
const postsBefore = (await get(AD, `${B}/pastoral-posts`)).json;
let vocabBefore = null, scratchStudentId = null;
// PUT /users/{id} REPLACES the branch and counter (the edit form always sends them, and "no branch" must be able
// to clear one), so a role change that sends only roleId silently took the person OFF their branch — found by the
// full run of 2026-09-24, when e2e.sp.math1 opened the timetable on "Choose a branch". Every PUT here carries the
// person's placement; an account an earlier run already emptied is put back on the test branch.
const placement = Object.fromEntries([dos, aa, math1].map((u) => [u.id, { assignedBranchId: u.assignedBranchId ?? BRANCH, assignedCounterId: u.assignedCounterId ?? null }]));
const setRole = (token, userId, code) => put(token, `/api/v1/users/${userId}`, { roleId: byCode[code].id, ...placement[userId] });

try {
  // -------------------------------------------------------------------------------------------------
  hdr("37.1 R1 — the school chain ranks above the front office");
  eq("the Administrator makes somebody Head Teacher", (await setRole(AD, dos.id, "head-teacher")).status, 200);
  eq("…and somebody Deputy Head Teacher", (await setRole(AD, aa.id, "deputy-head-teacher")).status, 200);
  const HEAD = await login("e2e.sp.dos@qmgr.local");
  const DEPUTY = await login("e2e.sp.aa@qmgr.local");
  const MATH = await login("e2e.sp.math1@qmgr.local");
  eq("the head may now make somebody Front Office Manager (their role holds everything it grants)", (await setRole(HEAD, math1.id, "manager")).status, 200);
  const deputyToManager = await setRole(DEPUTY, math1.id, "teacher");
  eq("the deputy may not change a Front Office Manager's account — it ranks between them and the DoS now", deputyToManager.status, 400);
  eq("the head puts them back", (await setRole(HEAD, math1.id, "teacher")).status, 200);

  // -------------------------------------------------------------------------------------------------
  hdr("37.2 R8 — the front-office roles are offered only to a tenant with a front office");
  const mods = (await get(AD, "/api/v1/modules/mine")).json;
  const active = (Array.isArray(mods) ? mods : mods?.modules ?? mods?.items ?? []).map((m) => m.moduleCode ?? m.code).filter(Boolean);
  if (active.length === 0) skip("front-office visibility", "the module list could not be read");
  else {
    const hasFrontOffice = active.includes("core-queue") || active.includes("visitor-management");
    eq(`Front Office Manager ${hasFrontOffice ? "is" : "is NOT"} offered (tenant holds: ${active.join(", ")})`, !!byCode["manager"], hasFrontOffice);
    eq("…and Front Desk Staff with it", !!byCode["staff"], hasFrontOffice);
  }
  truthy("Board Member is offered to a school", !!byCode["board-member"]);

  // -------------------------------------------------------------------------------------------------
  hdr("37.3 R2 — the designated safeguarding lead is a post, and only the SLT may hold it");
  const lv = (await get(AD, "/api/v1/leadership")).json;
  truthy("the Administrator may appoint", lv?.canAppointSafeguarding === true);
  truthy("…and is offered only the senior leadership team", (lv?.safeguardingCandidates ?? []).every((p) => ["Administrator", "Head Teacher", "Deputy Head Teacher", "Director of Studies"].includes(p.roleName)),
    JSON.stringify((lv?.safeguardingCandidates ?? []).map((p) => p.roleName)));
  const teacherLead = await put(AD, "/api/v1/leadership/safeguarding", { leadUserId: math1.id, deputyUserIds: [] });
  truthy("a teacher cannot be the lead", teacherLead.status === 400 && /senior leadership/i.test(teacherLead.text), `${teacherLead.status} ${teacherLead.text.slice(0, 160)}`);
  eq("the deputy may not appoint (their role does not hold the restricted rung)", (await put(DEPUTY, "/api/v1/leadership/safeguarding", { leadUserId: aa.id, deputyUserIds: [] })).status, 403);
  truthy("before: the deputy cannot read restricted welfare records", !(await perms(DEPUTY)).has("welfare.restricted.view"));
  const appoint = await put(AD, "/api/v1/leadership/safeguarding", { leadUserId: dos.id, deputyUserIds: [aa.id] });
  eq("the Administrator names the head lead and the deputy a deputy lead", appoint.status, 200);
  truthy("…and the deputy now reads restricted welfare records, through the post", await waitFor(async () => (await perms(DEPUTY)).has("welfare.restricted.view")));
  const staffView = await get(MATH, "/api/v1/leadership");
  truthy("every member of staff can see who the lead is", staffView.status === 200 && staffView.json?.safeguardingLead?.userId === dos.id, `${staffView.status}`);
  truthy("…but is offered nothing to change", staffView.json?.canAppointSafeguarding === false && (staffView.json?.safeguardingCandidates ?? []).length === 0);
  eq("the lead is removed", (await put(AD, "/api/v1/leadership/safeguarding", { leadUserId: dos.id, deputyUserIds: [] })).status, 200);
  truthy("…and the deputy's restricted access ends on the next request", !(await perms(DEPUTY)).has("welfare.restricted.view"));

  // -------------------------------------------------------------------------------------------------
  hdr("37.4 R6 — an acting head holds the head's set, for a fixed period, and it ends on its own");
  const noEnd = await put(HEAD, "/api/v1/leadership/acting-head", { userId: aa.id, startsOn: today(), reason: "Head on study leave" });
  eq("a period without an end date is refused", noEnd.status, 400);
  eq("…and one longer than 120 days", (await put(HEAD, "/api/v1/leadership/acting-head", { userId: aa.id, startsOn: today(), endsOn: plusDays(130), reason: "Head on study leave" })).status, 400);
  eq("…and a teacher cannot act", (await put(HEAD, "/api/v1/leadership/acting-head", { userId: math1.id, startsOn: today(), endsOn: plusDays(5), reason: "Head on study leave" })).status, 400);
  eq("the deputy may not appoint themselves or anyone", (await put(DEPUTY, "/api/v1/leadership/acting-head", { userId: aa.id, startsOn: today(), endsOn: plusDays(5), reason: "Head on study leave" })).status, 403);
  eq("the head appoints the deputy from tomorrow", (await put(HEAD, "/api/v1/leadership/acting-head", { userId: aa.id, startsOn: plusDays(1), endsOn: plusDays(6), reason: "Head on study leave" })).status, 200);
  truthy("…and before it starts the deputy holds nothing extra", !(await perms(DEPUTY)).has("staff.restricted.view"));
  eq("the head brings it forward to today", (await put(HEAD, "/api/v1/leadership/acting-head", { userId: aa.id, startsOn: today(), endsOn: plusDays(6), reason: "Head on study leave" })).status, 200);
  const acting = await perms(DEPUTY);
  truthy("the acting head reads restricted staff and welfare records", acting.has("staff.restricted.view") && acting.has("welfare.restricted.view"));
  truthy("…and still cannot pay, change settings or design roles", !acting.has("billing.manage") && !acting.has("settings.edit") && !acting.has("roles.edit"));
  eq("ending it", (await put(HEAD, "/api/v1/leadership/acting-head", { userId: null })).status, 200);
  truthy("…takes the access away on the next request", !(await perms(DEPUTY)).has("staff.restricted.view"));

  // -------------------------------------------------------------------------------------------------
  hdr("37.5 R4 — a house post reaches that house's students, pastorally, and nothing wider");
  vocabBefore = (await get(AD, `${B}/students/vocabularies`)).json;
  const house = `E2E House ${RUN}`;
  const vocab = structuredClone(vocabBefore);
  vocab.houses = [...(vocab.houses ?? []), { name: house, isActive: true, sortOrder: 999 }];
  const vput = await put(AD, `${B}/students/vocabularies`, { vocabularies: vocab, classRenames: {}, houseRenames: {}, dormitoryRenames: {} });
  eq("a house is added for the test", vput.status, 200);
  const cls = (vocabBefore?.classes ?? []).find((c) => c.isActive)?.name;
  const st = await post(AD, `${B}/students`, { fullName: `E2E House Child ${RUN}`, studentCode: `E2E-H-${RUN}`, className: cls, house });
  scratchStudentId = st.json?.id;
  truthy("a student in that house is created", st.status === 201 || st.status === 200, `${st.status} ${st.text.slice(0, 200)}`);
  const pastoralCheck = async () => (await get(MATH, `${B}/students/${scratchStudentId}/welfare-context`)).status;
  // 403 without the post (no welfare permission at all), 404 once a permission exists but the child is out of scope.
  truthy("before: the teacher cannot open the child's welfare context", [403, 404].includes(await pastoralCheck()));
  const unknown = await put(AD, `${B}/pastoral-posts`, { posts: [{ kind: "House", name: "No Such House", userId: math1.id }] });
  eq("a house the branch does not have is refused", unknown.status, 400);
  const keep = (postsBefore?.posts ?? []).map((p) => ({ kind: p.kind, name: p.name, userId: p.userId }));
  eq("the teacher is made housemaster", (await put(AD, `${B}/pastoral-posts`, { posts: [...keep, { kind: "House", name: house, userId: math1.id }] })).status, 200);
  eq("after: they can open it", await pastoralCheck(), 200);
  truthy("…through the class teacher's grant", (await perms(MATH)).has("welfare.view"));

  // 2026-09-24: a housemaster could READ their house's records but was never TOLD one had been logged — the alert
  // went to class teachers only. It now reaches the post holder, titled by the HOUSE, which is why it is theirs.
  const cats = (await get(AD, `${B}/welfare/categories`)).json ?? [];
  const behaviourCat = cats.find((c) => c.caseType === "Behavior" && c.isActive);
  if (!behaviourCat) skip("the housemaster alert", "this tenant has no active Behaviour category");
  else {
    const logged = await post(AD, `${B}/welfare-records`, {
      studentId: scratchStudentId, categoryId: behaviourCat.id, caseType: "Behavior", tier: "Low", points: null,
      description: `Dummy record ${RUN} — housemaster alert check. Safe to delete.`, occurredAt: new Date().toISOString(),
    });
    truthy("a Standard record is logged for the house's student", logged.status === 201 || logged.status === 200, `${logged.status} ${logged.text.slice(0, 200)}`);
    const told = await waitFor(async () => ((await get(MATH, "/api/v1/notifications?eventKey=welfare.record-logged&limit=50")).json ?? [])
      .some((n) => (n.title ?? "").includes(`House ${house}`)));
    truthy("…and the housemaster is told, the alert naming the house", told);
  }
  eq("the post ends", (await put(AD, `${B}/pastoral-posts`, { posts: keep })).status, 200);
  truthy("…and the child is out of reach again on the next request", [403, 404].includes(await pastoralCheck()));

  // -------------------------------------------------------------------------------------------------
  hdr("37.6 R5 — the access review");
  const review = await get(AD, "/api/v1/access-review");
  truthy("the Administrator reads it, one row per active person", review.status === 200 && (review.json?.rows ?? []).length > 5, `${review.status}`);
  const headRow = (review.json?.rows ?? []).find((r) => r.userId === dos.id);
  truthy("…and it shows the head reads restricted welfare records", headRow?.restrictedWelfare === true);
  eq("a note shorter than ten characters is refused", (await post(AD, "/api/v1/access-review/confirm", { note: "ok" })).status, 400);
  const confirmed = await post(AD, "/api/v1/access-review/confirm", { note: `E2E ${RUN}: checked every restricted reader` });
  truthy("recording the review stamps it", confirmed.status === 200 && !!confirmed.json?.lastReviewedAt && /checked every restricted reader/.test(confirmed.json?.lastReviewNote ?? ""), `${confirmed.status}`);
  const deputyReview = await get(DEPUTY, "/api/v1/access-review");
  truthy("the deputy reads it but may not record it", deputyReview.status === 200 && deputyReview.json?.canConfirm === false, `${deputyReview.status}`);
  eq("…and is refused if they try", (await post(DEPUTY, "/api/v1/access-review/confirm", { note: "Trying to record it" })).status, 403);
  eq("a teacher cannot read it", (await get(MATH, "/api/v1/access-review")).status, 403);

  // -------------------------------------------------------------------------------------------------
  hdr("37.7 R7 — a Board Member reads the figures and never a name");
  const bmName = `e2e.bm.${RUN}`;
  const bm = await post(AD, "/api/v1/users", { username: bmName, email: `${bmName}@qmgr.local`, password: NEW_PW, firstName: "Board", lastName: `Member ${RUN}`, roleId: byCode["board-member"]?.id, assignedBranchId: BRANCH, employeeNumber: `E2E-BM-${RUN}` });
  if (bm.status === 402) skip("the Board Member and the leaver sweep", "the dev tenant has no free user seat");
  else {
    truthy("a Board Member account is created", bm.status === 201 || bm.status === 200, `${bm.status} ${bm.text.slice(0, 200)}`);
    const bmId = bm.json?.id ?? bm.json?.user?.id;
    const BM = await login(`${bmName}@qmgr.local`);
    const summary = await get(BM, `${B}/welfare/summary`);
    truthy("they read the welfare summary", summary.status === 200, `${summary.status}`);
    eq("…with no member of staff named", (summary.json?.byStaff ?? []).length, 0);

    // 2026-09-24: a Welfare-type record is forced to Confidential, and a figures-only reader counted Standard only —
    // so a board read ZERO welfare cases however many the school had. Their summary now counts Confidential too.
    const welfareCat = ((await get(AD, `${B}/welfare/categories`)).json ?? []).find((c) => c.caseType === "Welfare" && c.isActive);
    if (!welfareCat || !scratchStudentId) skip("the governor's confidential count", "no active Welfare category or no scratch student");
    else {
      const before = summary.json?.totalRecords ?? 0;
      const conf = await post(AD, `${B}/welfare-records`, {
        studentId: scratchStudentId, categoryId: welfareCat.id, caseType: "Welfare", tier: "Low", points: null,
        description: `Dummy record ${RUN} — governor count check. Safe to delete.`, occurredAt: new Date().toISOString(),
      });
      truthy("a Welfare record (forced Confidential) is logged", conf.status === 201 || conf.status === 200, `${conf.status} ${conf.text.slice(0, 200)}`);
      const after = (await get(BM, `${B}/welfare/summary`)).json?.totalRecords ?? 0;
      eq("…and the governor's total counts it", after, before + 1);
      eq("…still with nobody named", ((await get(BM, `${B}/welfare/summary`)).json?.byStaff ?? []).length, 0);
    }
    eq("they cannot search the records", (await get(BM, `${B}/welfare-records`)).status, 403);
    eq("…or list the students", (await get(BM, `${B}/students`)).status, 403);

    // -----------------------------------------------------------------------------------------------
    hdr("37.8 R3 — an account whose employment has ended is switched off overnight");
    const profile = (await get(AD, `${B}/staff/structure/members/${bmId}/profile`)).json;
    const setEnd = await put(AD, `${B}/staff/structure/members/${bmId}/profile`, { ...profile, employeeNumber: profile?.employeeNumber ?? `E2E-BM-${RUN}`, employmentStartDate: plusDays(-400), employmentEndDate: plusDays(-1) });
    eq("their end date is set to yesterday", setEnd.status, 200);
    const trig = await fetch(`${API}/hangfire/recurring/trigger`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: "jobs%5B%5D=deactivate-leavers" });
    if (trig.status >= 300 && trig.status !== 204) skip("the leaver sweep", `the Hangfire dashboard refused a trigger (${trig.status}); asserted only against a Development API`);
    else {
      truthy("the sweep switches the account off", await waitFor(async () => (await get(AD, `/api/v1/users/${bmId}`)).json?.isActive === false));
      truthy("…and tells the people who manage accounts, naming them", await waitFor(async () =>
        ((await get(AD, "/api/v1/notifications?eventKey=access.leavers-deactivated&limit=20")).json ?? []).some((n) => (n.message ?? "").includes(`Member ${RUN}`))));
      eq("…and they can no longer sign in", await login(`${bmName}@qmgr.local`), null);
    }
  }
} catch (e) {
  bad("the suite ran to the end", "no exception", e.stack ?? String(e));
} finally {
  hdr("37.9 Put things back");
  const r1 = await put(AD, "/api/v1/leadership/safeguarding", {
    leadUserId: leadershipBefore?.safeguardingLead?.userId ?? null,
    deputyUserIds: (leadershipBefore?.deputySafeguardingLeads ?? []).map((d) => d.userId),
  });
  console.log(`  safeguarding leads restored (${r1.status})`);
  const r2 = await put(AD, "/api/v1/leadership/acting-head", { userId: null });
  console.log(`  acting head cleared (${r2.status})`);
  const r3 = await put(AD, `${B}/pastoral-posts`, { posts: (postsBefore?.posts ?? []).map((p) => ({ kind: p.kind, name: p.name, userId: p.userId })) });
  console.log(`  house posts restored (${r3.status})`);
  if (scratchStudentId) console.log(`  scratch student deactivated (${(await call(AD, "DELETE", `${B}/students/${scratchStudentId}`)).status})`);
  if (vocabBefore) {
    const v = (await get(AD, `${B}/students/vocabularies`)).json;
    v.houses = (v.houses ?? []).map((h) => (h.name.startsWith("E2E House ") ? { ...h, isActive: false } : h));
    console.log(`  test house retired (${(await put(AD, `${B}/students/vocabularies`, { vocabularies: v, classRenames: {}, houseRenames: {}, dormitoryRenames: {} })).status})`);
  }
  for (const [userId, roleId] of restoreRoles) console.log(`  role restored (${(await put(AD, `/api/v1/users/${userId}`, { roleId, ...placement[userId] })).status})`);
}

console.log(`\n${pass} passed, ${fail} failed`);
if (failures.length) console.log("Failures:\n" + failures.map((f) => `  - ${f}`).join("\n"));
viewer(`${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

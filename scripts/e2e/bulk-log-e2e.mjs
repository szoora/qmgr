// SECTION 32 — LOGGING ONE RECORD FOR A GROUP OF STUDENTS, AND TIDYING SHOUTED NAMES (2026-09-23).
//
// Plan STUDENT_ROSTER_AND_LIST_STANDARD §3 and §2. "In the bulk action, include a feature to log a record
// for selected group of students." POST …/welfare-records/bulk runs the SAME creation code as a single
// record for every student, in one transaction, and refuses a batch outright when any student is outside
// the caller's scope — with the unknown-student wording, never a "not your class" one.
//
//   32.1  a class teacher logs an Achievement for their own class: one record each, on each timeline;
//   32.2  one incident involving all of them — Behaviour only — is ONE record, on every timeline;
//   32.3  one student out of scope refuses the WHOLE batch, nothing written, the unknown-student words;
//   32.4  the refusals: a welfare concern, a one-incident achievement, 201 students; duplicates collapse;
//   32.5  alerts are coalesced — the class teacher gets ONE notification for a batch of three;
//   32.6  the batch is ONE line on the welfare activity log;
//   32.7  tidy names: preview writes nothing, a SHOUTED name is listed, commit applies, mixed case untouched;
//   32.8  put things back (students deactivated; the append-only records are labelled as dummies).
//
// The records this suite writes cannot be deleted — the ledger is append-only by design. Each one reads
// "Dummy record — bulk log e2e. Safe to delete." and sits on a scratch student this suite created and
// deactivates at the end.
//
// Run: node scripts/e2e/bulk-log-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const B = `/api/v1/branches/${BRANCH}`;
const DUMMY = "Dummy record — bulk log e2e. Safe to delete.";
const UNKNOWN_TITLE = "Student not found";
const UNKNOWN_DETAIL = "The selected student does not exist in this branch, or is no longer active.";

let pass = 0, fail = 0, skip = 0;
const failures = [];
const viewer = (line) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: line + "\n" }).catch(() => {});
const ok = (name) => { pass++; const l = `  \x1b[32mPASS\x1b[0m  ${name}`; console.log(l); viewer(l); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  const l = `  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 400)}`;
  console.log(l); viewer(l);
};
const skipped = (name, why) => { skip++; const l = `  \x1b[33mSKIP\x1b[0m  ${name} — ${why}`; console.log(l); viewer(l); };
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
const post = (t, p, b) => call(t, "POST", p, b ?? {});
const del = (t, p, b) => call(t, "DELETE", p, b);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}

const ad = await login(TENANT_ADMIN);
const t4 = await login("e2e.teacher.s4@qmgr.local");
if (!ad || !t4) { console.error("sign-in failed"); process.exit(1); }
const AD = ad.token, T4 = t4.token;
const RUN = Date.now().toString(36).toUpperCase().replace(/[^A-Z0-9]/g, "");

const scratch = [];            // students this suite created — deactivated at the end
let createdAssignment = null;  // a class-teacher assignment this suite created — ended at the end

const makeStudent = async (fullName, className, suffix) => {
  const r = await post(AD, `${B}/students`, { fullName, studentCode: `BL${RUN}${suffix}`, className });
  if (r.status !== 201) throw new Error(`could not create a scratch student (${r.status}): ${r.text.slice(0, 200)}`);
  scratch.push(r.json.id);
  return r.json.id;
};
const timeline = async (token, studentId) => {
  const r = await get(token, `${B}/students/${studentId}/welfare-records`);
  return Array.isArray(r.json) ? r.json : [];
};
const welfareNotices = async (token) => {
  const r = await get(token, "/api/v1/notifications?eventKey=welfare.record-logged&limit=200");
  return Array.isArray(r.json) ? r.json : [];
};

const createdCategories = [];
try {
  hdr("32.0 Set up — categories, the S4 class teacher, scratch students");
  const cats = (await get(AD, `${B}/welfare/categories`)).json ?? [];
  // A tenant without the category a bulk log needs is given one for the run and has it retired afterwards —
  // the project's rule: make the data a path needs, never report its absence as a skip.
  const ensure = async (caseType, name) => {
    const found = cats.find((c) => c.caseType === caseType && c.isActive);
    if (found) return found;
    const r = await post(AD, `${B}/welfare/categories`, { caseType, name, defaultTier: "Low", defaultPoints: 1, sortOrder: 99 });
    if (r.status === 201 || r.status === 200) { createdCategories.push(r.json.id); return r.json; }
    return null;
  };
  const achievement = await ensure("Achievement", `E2E Merit ${RUN}`);
  const behaviour = await ensure("Behavior", `E2E Conduct ${RUN}`);
  if (!achievement || !behaviour) throw new Error("this tenant needs an active Achievement and an active Behavior category");
  ok("an Achievement and a Behaviour category exist");

  // The S4 class teacher. Earlier sections usually leave e2e.teacher.s4 holding S4; if nobody does, this suite
  // makes the assignment and ends it afterwards. If somebody ELSE holds S4, the class-teacher half cannot run.
  const live = (await get(AD, `${B}/class-teachers`)).json ?? [];
  const s4Primary = live.find((a) => (a.className ?? "").trim().toLowerCase() === "s4" && a.role === "ClassTeacher");
  let teacherHoldsS4 = s4Primary?.userId === t4.user.id;
  if (!s4Primary) {
    const made = await post(AD, `${B}/class-teachers`, { className: "S4", userId: t4.user.id, role: 0 });
    if (made.status === 201 || made.status === 200) { createdAssignment = made.json?.id; teacherHoldsS4 = true; }
  }
  if (teacherHoldsS4) ok("e2e.teacher.s4 is the class teacher of S4");
  else skipped("the class-teacher half", "somebody else is S4's class teacher on this tenant");

  const a = await makeStudent(`Bulk Log Alpha ${RUN}`, "S4", "A");
  const b = await makeStudent(`Bulk Log Beta ${RUN}`, "S4", "B");
  const c = await makeStudent(`Bulk Log Gamma ${RUN}`, "S4", "C");
  const outside = await makeStudent(`Bulk Log Outside ${RUN}`, "S2", "D");
  ok("four scratch students created (three in S4, one in S2)");

  const record = (caseType, category, over = {}) => ({
    categoryId: category.id, caseType, tier: "Low", points: null, description: DUMMY, occurredAt: new Date().toISOString(), ...over,
  });

  if (teacherHoldsS4) {
    hdr("32.1 A class teacher logs an Achievement for their own class");
    const r = await post(T4, `${B}/welfare-records/bulk`, { studentIds: [a, b, c], oneIncident: false, record: record("Achievement", achievement) });
    eq("POST bulk → 201", r.status, 201);
    eq("three records, one per student", r.json?.created, 3);
    eq("…for three students", r.json?.students, 3);
    truthy("a batch id is returned", /^[0-9a-f-]{36}$/.test(r.json?.batchId ?? ""), JSON.stringify(r.json));
    const ids = r.json?.recordIds ?? [];
    for (const [i, s] of [a, b, c].entries()) {
      const tl = await timeline(T4, s);
      const hits = tl.filter((x) => ids.includes(x.id));
      eq(`student ${i + 1}'s timeline carries exactly their own record`, hits.length, 1);
      if (hits[0]) eq(`…filed against that student`, hits[0].studentId, s);
    }

    hdr("32.2 One incident involving all of them — ONE record, on every timeline");
    const inc = await post(T4, `${B}/welfare-records/bulk`, { studentIds: [a, b, c], oneIncident: true, record: record("Behavior", behaviour) });
    eq("POST bulk, one incident → 201", inc.status, 201);
    eq("ONE record written", inc.json?.created, 1);
    const incId = inc.json?.recordIds?.[0];
    const onA = (await timeline(T4, a)).find((x) => x.id === incId);
    eq("it is filed against the first student", onA?.studentId, a);
    truthy("the others are in AdditionalStudentIds", [b, c].every((s) => (onA?.additionalStudentIds ?? []).includes(s)), JSON.stringify(onA?.additionalStudentIds));
    truthy("…and it is on the second student's timeline", (await timeline(T4, b)).some((x) => x.id === incId));
    truthy("…and on the third's", (await timeline(T4, c)).some((x) => x.id === incId));

    hdr("32.3 One student out of scope refuses the WHOLE batch");
    const beforeA = (await timeline(AD, a)).length;
    const beforeOut = (await timeline(AD, outside)).length;
    const mixed = await post(T4, `${B}/welfare-records/bulk`, { studentIds: [a, outside], oneIncident: false, record: record("Achievement", achievement) });
    eq("a batch holding an S2 student → 400", mixed.status, 400);
    eq("…with the unknown-student title, never a 'not your class' one", mixed.json?.title, UNKNOWN_TITLE);
    eq("…and the unknown-student detail", mixed.json?.detail, UNKNOWN_DETAIL);
    eq("nothing was written for the in-scope student", (await timeline(AD, a)).length, beforeA);
    eq("nothing was written for the out-of-scope student", (await timeline(AD, outside)).length, beforeOut);
    const ghost = await post(T4, `${B}/welfare-records/bulk`, { studentIds: [a, crypto.randomUUID()], record: record("Achievement", achievement) });
    eq("an id that is no student reads exactly the same", `${ghost.status} ${ghost.json?.title}`, `400 ${UNKNOWN_TITLE}`);
  }

  hdr("32.4 The refusals, and duplicates collapsing");
  const welfareCat = cats.find((x) => x.caseType === "Welfare" && x.isActive);
  const w = await post(AD, `${B}/welfare-records/bulk`, { studentIds: [a, b], record: record("Welfare", welfareCat ?? achievement) });
  eq("a welfare concern for a group → 400", w.status, 400);
  truthy("…in the words of decision L7", /one student at a time/.test(w.json?.title ?? ""), w.json?.title);
  const oa = await post(AD, `${B}/welfare-records/bulk`, { studentIds: [a, b], oneIncident: true, record: record("Achievement", achievement) });
  eq("one incident for an achievement → 400", oa.status, 400);
  truthy("…naming the field", !!(oa.json?.errors?.OneIncident ?? oa.json?.errors?.oneIncident), JSON.stringify(oa.json?.errors));
  const many = Array.from({ length: 201 }, () => crypto.randomUUID());
  const cap = await post(AD, `${B}/welfare-records/bulk`, { studentIds: many, record: record("Achievement", achievement) });
  eq("201 students → 400", cap.status, 400);
  truthy("…naming StudentIds", !!(cap.json?.errors?.StudentIds ?? cap.json?.errors?.studentIds), JSON.stringify(cap.json?.errors));
  eq("no students → 400", (await post(AD, `${B}/welfare-records/bulk`, { studentIds: [], record: record("Achievement", achievement) })).status, 400);
  const dup = await post(AD, `${B}/welfare-records/bulk`, { studentIds: [a, a, b], record: record("Achievement", achievement) });
  eq("duplicate ids → 201", dup.status, 201);
  eq("…collapsed to two students", dup.json?.students, 2);
  eq("…and two records", dup.json?.created, 2);

  hdr("32.5 Alerts are coalesced — ONE notification per recipient");
  if (teacherHoldsS4) {
    const before = await welfareNotices(T4);
    const r = await post(AD, `${B}/welfare-records/bulk`, { studentIds: [a, b, c], record: record("Achievement", achievement) });
    eq("the administrator logs for three S4 students → 201", r.status, 201);
    truthy("the result says somebody was told", (r.json?.peopleAlerted ?? 0) >= 1, JSON.stringify(r.json));
    const after = await welfareNotices(T4);
    const fresh = after.filter((n) => !before.some((o) => o.id === n.id));
    eq("the S4 class teacher received exactly ONE notification for the batch", fresh.length, 1);
    truthy("…which says three students", /3 students/.test(fresh[0]?.message ?? ""), fresh[0]?.message);
    truthy("…and names the category", (fresh[0]?.message ?? "").includes(achievement.name), fresh[0]?.message);
  } else skipped("coalescing", "no class teacher of S4 this suite can read the bell of");

  hdr("32.6 The batch is ONE line on the welfare activity log");
  const log = (await get(AD, `${B}/welfare/activity?pageSize=50`)).json?.items ?? [];
  const line = log.find((e) => e.entityId === dup.json?.batchId);
  truthy("the duplicate-ids batch has its line", !!line, JSON.stringify(log.slice(0, 2)));
  eq("…with the bulk-log action", line?.action, "welfare.records.bulk-logged");
  eq("…and only one line for it", log.filter((e) => e.entityId === dup.json?.batchId).length, 1);
  truthy("…naming no child", !(line?.summary ?? "").includes("Bulk Log"), line?.summary);

  hdr("32.7 Tidy names out of capitals");
  const shouted = await makeStudent(`BULKLOG SHOUTED ${RUN}`, "S4", "E");
  const mixedCase = await makeStudent(`Bulklog McDONALD Mixed ${RUN}`, "S4", "F");
  const nameOf = async (id) => ((await get(AD, `${B}/students?includeInactive=true&limit=500`)).json ?? []).find((s) => s.id === id)?.fullName;

  const wide = await post(AD, `${B}/students/tidy-names`, { preview: true });
  eq("a branch-wide preview → 200", wide.status, 200);
  truthy("…lists the SHOUTED name", (wide.json?.changes ?? []).some((x) => x.studentId === shouted), JSON.stringify((wide.json?.changes ?? []).slice(0, 3)));
  truthy("…never the mixed-case one", !(wide.json?.changes ?? []).some((x) => x.studentId === mixedCase));
  eq("…and applies nothing", wide.json?.applied, 0);
  eq("the preview wrote nothing", await nameOf(shouted), `BULKLOG SHOUTED ${RUN}`);

  const pick = await post(AD, `${B}/students/tidy-names`, { preview: true, studentIds: [shouted, mixedCase] });
  eq("a preview of the two scratch students lists exactly one change", pick.json?.changes?.length, 1);
  const to = pick.json?.changes?.[0]?.to;
  truthy("…out of capitals", !!to && /[a-z]/.test(to) && to.startsWith("Bulklog Shouted"), to);

  const commit = await post(AD, `${B}/students/tidy-names`, { preview: false, studentIds: [shouted, mixedCase] });
  eq("commit → 200", commit.status, 200);
  eq("…applied one", commit.json?.applied, 1);
  eq("the SHOUTED name is now the previewed one", await nameOf(shouted), to);
  eq("the mixed-case name is untouched", await nameOf(mixedCase), `Bulklog McDONALD Mixed ${RUN}`);
  eq("a second commit changes nothing", (await post(AD, `${B}/students/tidy-names`, { preview: false, studentIds: [shouted] })).json?.applied, 0);
  eq("an id that is no student → 404", (await post(AD, `${B}/students/tidy-names`, { preview: true, studentIds: [crypto.randomUUID()] })).status, 404);
  const teacherTidy = await post(T4, `${B}/students/tidy-names`, { preview: true, studentIds: [outside] });
  truthy("a class teacher cannot tidy a student outside their class (403 or 404)", [403, 404].includes(teacherTidy.status), teacherTidy.status);
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("32.8 Put things back");
  let off = 0;
  for (const id of scratch) if ((await del(AD, `${B}/students/${id}`)).status === 204) off++;
  eq("every scratch student is deactivated (their dummy records stay — the ledger is append-only)", off, scratch.length);
  for (const id of createdCategories) eq("a category this suite made is retired", (await call(AD, "PATCH", `${B}/welfare/categories/${id}/toggle`)).status, 200);
  if (createdAssignment) eq("the S4 assignment this suite made is ended", (await del(AD, `${B}/class-teachers/${createdAssignment}`, { reason: `E2E bulk log ${RUN} clean-up` })).status, 200);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m${skip ? ` (${skip} skipped)` : ""}`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

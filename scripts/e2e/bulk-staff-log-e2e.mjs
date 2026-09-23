// SECTION 33 — LOGGING ONE RECORD FOR A GROUP OF STAFF (2026-09-23).
//
// The staff twin of section 32. POST …/staff/records/bulk runs the SAME creation code as a single staff
// record (StaffRecordsController.BuildRecordAsync) for every person, in one transaction, and refuses a batch
// outright when any person is outside the caller's staff scope — with the single create's not-found wording,
// never a "not in your department" one.
//
//   33.1  an unscoped holder of staff.records.create logs a Contribution for four people: 201, one record each;
//   33.2  alerts: each subject hears about their own record; a supervisor of two of them hears ONCE;
//   33.3  the batch is ONE line on the staff activity log, naming nobody;
//   33.4  the caller in the list is left out and named in SkippedSelf; only-self is refused;
//   33.5  a head of department: their own department logs, one person outside it refuses the WHOLE batch;
//   33.6  the refusals: every other kind, Confidential, a Confidential-by-default parameter, a draft,
//         201 people (the cap is checked before scope), a parameter that does not apply to one of them;
//   33.7  late entry is the single create's 409 LATE_ENTRY, once for the batch;
//   33.8  two identical presses at once are two whole batches — no lock rule applies, and none is wanted;
//   33.9  put things back: every record this suite wrote is ANNULLED (records are append-only) and the
//         parameters it made are retired.
//
// Uses the accounts section 14 seeds (e2e.sp.*) and creates NO users: the dev tenant is at its user cap.
//
// Run: node scripts/e2e/bulk-staff-log-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const B = `/api/v1/branches/${BRANCH}`;
const RUN = Date.now().toString(36).toUpperCase().replace(/[^A-Z0-9]/g, "");
const TAG = `E2E BSL ${RUN}`;
const CLEANUP = "E2E bulk staff log clean-up";

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
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
const patch = (t, p, b) => call(t, "PATCH", p, b);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}

const iso = (d) => new Date(d).toISOString();
const written = new Set();       // every record id this suite wrote — annulled at the end
const createdParams = [];        // parameters this suite made — retired at the end
const remember = (r) => { for (const id of r?.json?.recordIds ?? []) written.add(id); };
const bulk = (token, ids, record, ack = false) =>
  post(token, `${B}/staff/records/bulk${ack ? "?acknowledgeLateEntry=true" : ""}`, { subjectUserIds: ids, record });
const tagged = async (token) => (await get(token, `${B}/staff/records?q=${encodeURIComponent(TAG)}&pageSize=200`)).json?.totalCount ?? -1;
const notices = async (token, needle) =>
  ((await get(token, "/api/v1/notifications?eventKey=staff.record-logged&limit=200")).json ?? [])
    .filter((n) => `${n.title} ${n.message}`.includes(needle));

const ad = await login(TENANT_ADMIN);
if (!ad) { console.error("the tenant administrator could not sign in"); process.exit(1); }
const AD = ad.token;

try {
  hdr("33.0 Set up — section 14's staff, their structure, and three parameters");
  const users = (await get(AD, "/api/v1/users?pageSize=500")).json;
  const list = Array.isArray(users) ? users : users?.items ?? users?.data ?? [];
  const who = { math1: "e2e.sp.math1", math2: "e2e.sp.math2", lang1: "e2e.sp.lang1", support: "e2e.sp.support", hodMath: "e2e.sp.hod.math", hodLang: "e2e.sp.hod.lang", aa: "e2e.sp.aa", dos: "e2e.sp.dos" };
  const U = {};
  for (const [k, username] of Object.entries(who)) {
    const u = list.find((x) => x.username === username);
    if (!u) throw new Error(`${username} does not exist — run section 14 (staff-performance-e2e.mjs) first; this suite creates no users`);
    const s = await login(`${username}@qmgr.local`);
    if (!s) throw new Error(`${username} could not sign in`);
    U[k] = { id: u.id, token: s.token };
  }
  ok("section 14's eight staff accounts exist and sign in");

  // The structure section 14 applies. Re-applied only where something later moved it, because a supervisor
  // assertion against a structure somebody else rearranged would measure that, not this feature.
  const depts = (await get(AD, `${B}/staff/structure/departments?includeInactive=true`)).json ?? [];
  const MATH = depts.find((d) => d.code === "E2EMATH"), LANG = depts.find((d) => d.code === "E2ELANG");
  if (!MATH || !LANG) throw new Error("the E2EMATH and E2ELANG departments are missing — run section 14 first");
  const dir = (await get(AD, `${B}/staff/structure/members`)).json?.items ?? [];
  const want = [["math1", MATH.id, "hodMath"], ["math2", MATH.id, "hodMath"], ["lang1", LANG.id, "hodLang"], ["support", null, "aa"]];
  for (const [k, dept, lm] of want) {
    const row = dir.find((m) => m.userId === U[k].id);
    const deptOk = dept ? (row?.departmentIds ?? []).length === 1 && row.departmentIds[0] === dept : (row?.departmentIds ?? []).length === 0;
    if (!row || !deptOk || row.lineManagerUserId !== U[lm].id)
      await put(AD, `${B}/staff/structure/members/${U[k].id}`, { departmentIds: dept ? [dept] : [], lineManagerUserId: U[lm].id });
  }
  const mathHeaded = MATH.headUserId === U.hodMath.id;
  truthy("the head of E2E Mathematics is e2e.sp.hod.math", mathHeaded, `${MATH.headUserId}`);

  const makeParam = async (name, kind, extra = {}) => {
    const r = await post(AD, "/api/v1/staff/parameters", {
      name, kind, appliesToGroup: null, defaultPoints: kind === "Conduct" ? -1 : 2, maxPointsPerEntry: 5,
      weight: 0, defaultVisibility: "Standard", purpose: "E2E bulk staff log — evidence only, never scored", ...extra,
    });
    if (r.status >= 300) throw new Error(`could not create parameter ${name} (${r.status}): ${r.text.slice(0, 200)}`);
    createdParams.push(r.json.id);
    return r.json;
  };
  // Weight 0: the suite's evidence never moves anybody's score.
  const contribution = await makeParam(`E2E Bulk Contribution ${RUN}`, "Contribution");
  const conduct = await makeParam(`E2E Bulk Conduct ${RUN}`, "Conduct");
  const teachingOnly = await makeParam(`E2E Bulk Teaching ${RUN}`, "Contribution", { appliesToGroup: "Teaching staff" });
  ok("a Contribution, a Conduct and a teaching-only Contribution parameter exist (Standard, weight 0)");

  const params = (await get(AD, "/api/v1/staff/parameters")).json ?? [];
  const policy = (await get(AD, "/api/v1/staff/policy")).json ?? {};
  const record = (param, over = {}) => ({
    parameterId: param.id, description: `${TAG} — dummy bulk record, safe to annul.`, occurredAt: iso(Date.now() - 60_000),
    visibility: "Standard", outcome: "NotApplicable", points: null, ...over,
  });

  hdr("33.1 An unscoped holder logs a Contribution for four people");
  const hodMathBefore = (await notices(U.hodMath.token, TAG)).length;
  const math1Before = (await notices(U.math1.token, TAG)).length;
  const four = [U.math1.id, U.math2.id, U.lang1.id, U.support.id];
  const first = await bulk(AD, four, record(contribution, { points: 2 }));
  remember(first);
  eq("POST …/staff/records/bulk → 201", first.status, 201);
  eq("four records, one per person", first.json?.created, 4);
  eq("…four record ids", first.json?.recordIds?.length, 4);
  truthy("a batch id is returned", /^[0-9a-f-]{36}$/.test(first.json?.batchId ?? ""), JSON.stringify(first.json));
  eq("nobody was skipped", first.json?.skippedSelf?.length, 0);
  for (const k of ["math1", "math2", "lang1", "support"]) {
    const tl = (await get(AD, `${B}/staff/members/${U[k].id}/timeline`)).json?.records ?? [];
    const mine = tl.filter((r) => (first.json?.recordIds ?? []).includes(r.id));
    truthy(`${who[k]}'s file carries exactly their own record, +2 points`, mine.length === 1 && mine[0].points === 2 && mine[0].subjectUserId === U[k].id, JSON.stringify(mine.map((r) => [r.subjectUserId, r.points])));
  }

  hdr("33.2 Alerts — each subject their own, a supervisor ONE for the batch");
  const math1After = await notices(U.math1.token, TAG);
  eq("the subject e2e.sp.math1 got exactly one notification about their record", math1After.length - math1Before, 1);
  truthy("…in the single create's words ('… logged about you')", math1After.some((n) => /logged about you/.test(n.title)), JSON.stringify(math1After.slice(0, 1)));
  if (mathHeaded) {
    const hodAfter = await notices(U.hodMath.token, TAG);
    eq("the line manager of TWO of them got ONE notification, not two", hodAfter.length - hodMathBefore, 1);
    truthy("…a summary naming the count ('logged for 2 people')", hodAfter.some((n) => /logged for 2 people/.test(n.title)), JSON.stringify(hodAfter.map((n) => n.title)));
  } else skipped("the supervisor summary", "E2E Mathematics is not headed by e2e.sp.hod.math on this tenant");
  // Four subjects plus at least the two distinct supervisors this structure has (hod.math, hod.lang, aa).
  truthy("PeopleAlerted counts each person once (≥ 4 subjects + their supervisors)", (first.json?.peopleAlerted ?? 0) >= 6, first.json?.peopleAlerted);

  hdr("33.3 The batch is ONE line on the staff activity log");
  const log = (await get(AD, `${B}/staff/activity?action=staff.records.bulk-logged&pageSize=50`)).json?.items ?? [];
  const lines = log.filter((e) => e.entityId === first.json?.batchId);
  eq("exactly one bulk line for the batch", lines.length, 1);
  truthy("…naming the parameter and the count", (lines[0]?.summary ?? "").includes(contribution.name) && /4 people/.test(lines[0]?.summary ?? ""), lines[0]?.summary);
  truthy("…naming nobody (no subject on the line)", lines[0] && !lines[0].subjectUserId, JSON.stringify(lines[0]));
  const perRecord = (await get(AD, `${B}/staff/activity?action=staff.record.created&userId=${U.math2.id}&pageSize=50`)).json?.items ?? [];
  truthy("each record still writes its own 'record created' line on its subject's trail", perRecord.some((e) => (first.json?.recordIds ?? []).includes(e.entityId)), perRecord.length);

  hdr("33.4 The caller is left out, and said so");
  const withMe = await bulk(AD, [ad.user.id, U.math1.id], record(contribution, { points: 1 }));
  remember(withMe);
  eq("the caller in the list → still 201", withMe.status, 201);
  eq("…one record, not two", withMe.json?.created, 1);
  truthy("…the caller named in SkippedSelf", (withMe.json?.skippedSelf ?? []).includes(ad.user.id), JSON.stringify(withMe.json?.skippedSelf));
  const onlyMe = await bulk(AD, [ad.user.id], record(contribution));
  eq("only the caller → 400", onlyMe.status, 400);
  truthy("…in the shared words", /about themselves/.test(onlyMe.text), onlyMe.text);

  hdr("33.5 A head of department — their own department, and one person outside it");
  const own = await bulk(U.hodMath.token, [U.math1.id, U.math2.id], record(contribution, { points: 1 }));
  remember(own);
  eq("the head of Maths logs for the two Maths teachers → 201", own.status, 201);
  eq("…two records", own.json?.created, 2);
  const before = await tagged(AD);
  const mixed = await bulk(U.hodMath.token, [U.math1.id, U.math2.id, U.lang1.id], record(contribution, { points: 1 }));
  remember(mixed);
  eq("one Languages teacher in the list refuses the WHOLE batch (404)", mixed.status, 404);
  truthy("…with the single create's words, never 'not in your department'", /Staff member not found/.test(mixed.text) && !/department|scope/i.test(mixed.text), mixed.text);
  eq("…and nothing at all was written", await tagged(AD), before);

  hdr("33.6 The refusals");
  const byKind = (kind) => params.find((p) => p.kind === kind && p.isActive && !p.isSystemSource);
  for (const kind of ["Attendance", "Duty", "Observation", "Recognition", "Wellbeing"]) {
    const p = byKind(kind);
    if (!p) { skipped(`a ${kind} parameter`, "none active on this tenant"); continue; }
    const r = await bulk(AD, [U.math1.id], record(p, { outcome: kind === "Attendance" || kind === "Duty" ? "Present" : "NotApplicable", rating: kind === "Observation" ? 2 : null }));
    remember(r);
    truthy(`a ${kind} parameter is refused (400, the shared sentence)`, r.status === 400 && /contribution or a conduct/i.test(r.text), `${r.status} ${r.text}`);
  }
  const conf = await bulk(AD, [U.math1.id], record(contribution, { visibility: "Confidential" }));
  remember(conf);
  truthy("Confidential → 400, 'one person at a time'", conf.status === 400 && /one person at a time/.test(conf.text), `${conf.status} ${conf.text}`);
  const seededConduct = params.find((p) => p.name === "Conduct" && p.isActive);
  if (seededConduct?.defaultVisibility === "Confidential") {
    const r = await bulk(AD, [U.math1.id], record(seededConduct, { points: -1 }));
    remember(r);
    truthy("a parameter that is Confidential BY DEFAULT is refused too (it would raise every record)", r.status === 400 && /one person at a time/.test(r.text), `${r.status} ${r.text}`);
  } else skipped("a Confidential-by-default parameter", "the seeded Conduct parameter is not Confidential here");
  const draft = await bulk(AD, [U.math1.id], record(contribution, { saveAsDraft: true }));
  remember(draft);
  eq("a draft → 400", draft.status, 400);
  const many = Array.from({ length: 201 }, () => crypto.randomUUID());
  const cap = await bulk(AD, many, record(contribution));
  truthy("201 people → 400 with the cap sentence — BEFORE any of the unknown ids is looked up (not 404)", cap.status === 400 && /at most 200 people/.test(cap.text), `${cap.status} ${cap.text}`);
  const dupes = await bulk(AD, [U.math2.id, U.math2.id, U.math2.id], record(conduct, { points: -1 }));
  remember(dupes);
  truthy("duplicates collapse: three copies of one person → one Conduct record at −1", dupes.status === 201 && dupes.json?.created === 1, `${dupes.status} ${dupes.text}`);
  const group = await bulk(AD, [U.math1.id, U.support.id], record(teachingOnly));
  remember(group);
  truthy("a teaching-only parameter with a support-staff member in the list → 400, the WHOLE batch", group.status === 400 && /1 of the 2 people/.test(group.text), `${group.status} ${group.text}`);
  const unknownPerson = await bulk(AD, [U.math1.id, crypto.randomUUID()], record(contribution));
  eq("an id that is nobody → 404, the whole batch", unknownPerson.status, 404);

  hdr("33.7 Late entry — the single create's 409, once for the batch");
  const threshold = policy.lateEntryThresholdDays ?? 0;
  if (threshold > 0) {
    const old = record(contribution, { points: 1, occurredAt: iso(Date.now() - (threshold + 6) * 86400_000) });
    const beforeLate = await tagged(AD);
    const late = await bulk(AD, [U.math1.id, U.math2.id], old);
    remember(late);
    truthy("a batch dated past the threshold → 409 LATE_ENTRY", late.status === 409 && late.text.includes("LATE_ENTRY"), `${late.status} ${late.text}`);
    eq("…and nothing was written", await tagged(AD), beforeLate);
    const lateOk = await bulk(AD, [U.math1.id, U.math2.id], old, true);
    remember(lateOk);
    eq("confirmed with acknowledgeLateEntry=true → 201, two records", lateOk.status === 201 ? lateOk.json?.created : lateOk.status, 2);
  } else skipped("late entry", "the policy's late-entry threshold is off on this tenant");

  hdr("33.8 Two identical presses at once — two whole batches (no lock rule applies)");
  const beforeRace = await tagged(AD);
  const body = record(contribution, { points: 1 });
  const race = await Promise.all([bulk(AD, [U.math2.id, U.lang1.id], body), bulk(AD, [U.math2.id, U.lang1.id], body)]);
  race.forEach(remember);
  truthy("both presses → 201 (nothing here is 'at most N' or 'exactly once', so neither is refused)", race.every((r) => r.status === 201), race.map((r) => r.status).join(","));
  truthy("…two different batches", race[0].json?.batchId && race[0].json?.batchId !== race[1].json?.batchId);
  eq("…four records between them — each batch complete, none half-written", (await tagged(AD)) - beforeRace, 4);
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("33.9 Put things back");
  let annulled = 0;
  for (const id of written) {
    const r = await post(AD, `${B}/staff/records/${id}/annul`, { reason: CLEANUP });
    if (r.status === 200) annulled++;
  }
  eq(`every record this suite wrote is annulled (${written.size}; append-only, so they stay, out of scoring)`, annulled, written.size);
  for (const id of createdParams) eq("a parameter this suite made is retired", (await patch(AD, `/api/v1/staff/parameters/${id}/toggle`)).status, 200);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m${skip ? ` (${skip} skipped)` : ""}`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

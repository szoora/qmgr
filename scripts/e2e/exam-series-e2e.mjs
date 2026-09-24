// SECTION 35 — EXAM-SUPERVISION SERIES AND THE SCHOOL'S OWN EMPLOYMENT TYPES (2026-09-23).
//
// Exam supervision: a named set of Session duties with its own managers (plan TIMETABLE_OWNERSHIP Phase 3). The
// risks are the ones every delegated grant in this codebase has had: a manager who can do more than their series,
// a manager who can appoint, an outsider who can see it, and a manager change that leaves the old manager holding
// every register. Each is asserted.
//
// Employment types: the enum became the school's list. The risks: a name the list does not carry being stored, a
// held type being removed out from under the people who hold it, and two spellings of one type.
//
// Uses section 14's staff (e2e.sp.*) and creates NO users. Everything it writes is cancelled or put back.
//
// Run: node scripts/e2e/exam-series-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const B = `/api/v1/branches/${BRANCH}`;
const RUN = Date.now().toString(36).toUpperCase();
const NAME = `E2E Exams ${RUN}`;

let pass = 0, fail = 0, skip = 0;
const failures = [];
const viewer = (line) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: line + "\n" }).catch(() => {});
const ok = (name) => { pass++; const l = `  \x1b[32mPASS\x1b[0m  ${name}`; console.log(l); viewer(l); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  const l = `  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 400)}`;
  console.log(l); viewer(l);
};
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
const del = (t, p) => call(t, "DELETE", p);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken }; }
  }
  return null;
}

// A sitting on a day far enough ahead that nothing else is on it, at a chosen local-ish hour (UTC is fine here).
const DAY = 40 + Math.floor(Math.random() * 200);
const at = (dayOffset, hour, minutes = 0) => { const d = new Date(); d.setUTCDate(d.getUTCDate() + DAY + dayOffset); d.setUTCHours(hour, minutes, 0, 0); return d.toISOString(); };
const sitting = (title, dayOffset, hour, invigilators, room = "Main Hall") =>
  ({ title, startsAt: at(dayOffset, hour), endsAt: at(dayOffset, hour + 2), location: room, invigilatorUserIds: invigilators });

const ad = await login(TENANT_ADMIN);
if (!ad) { console.error("the tenant administrator could not sign in"); process.exit(1); }
const AD = ad.token;
let SERIES = null;
let policyBefore = null;
const profilesToRestore = [];

try {
  hdr("35.0 Set up — section 14's staff");
  const users = (await get(AD, "/api/v1/users?pageSize=500")).json;
  const list = Array.isArray(users) ? users : users?.items ?? users?.data ?? [];
  const U = {};
  for (const [k, username] of Object.entries({ math1: "e2e.sp.math1", math2: "e2e.sp.math2", lang1: "e2e.sp.lang1" })) {
    const u = list.find((x) => x.username === username);
    if (!u) throw new Error(`${username} does not exist — run section 14 first; this suite creates no users`);
    const s = await login(`${username}@qmgr.local`);
    if (!s) throw new Error(`${username} could not sign in`);
    U[k] = { id: u.id, token: s.token, name: `${u.firstName} ${u.lastName}` };
  }
  ok("three of section 14's staff sign in (a manager, an invigilator, an outsider)");

  // ===========================================================================================================
  hdr("35.1 Creating a series — the permission holder's act");
  eq("a teacher cannot create a series (403)",
    (await post(U.math1.token, `${B}/staff/duty-series`, { name: NAME, managerUserIds: [], slots: [sitting("x", 0, 8, [U.math2.id])] })).status, 403);
  const noSlots = await post(AD, `${B}/staff/duty-series`, { name: NAME, managerUserIds: [U.math1.id], slots: [] });
  truthy("a series with no sittings is refused (400)", noSlots.status === 400 && /at least one sitting/i.test(noSlots.text), noSlots.text);
  const noInv = await post(AD, `${B}/staff/duty-series`, { name: NAME, managerUserIds: [U.math1.id], slots: [sitting("x", 0, 8, [])] });
  truthy("a sitting with no invigilator is refused, naming the sitting (400)", noInv.status === 400 && /Sitting 1: name at least one invigilator/.test(noInv.text), noInv.text);
  const backwards = await post(AD, `${B}/staff/duty-series`, { name: NAME, managerUserIds: [], slots: [{ ...sitting("x", 0, 10, [U.math2.id]), endsAt: at(0, 9) }] });
  truthy("a sitting that ends before it starts is refused by the duty's own rules, naming the sitting", backwards.status === 400 && /Sitting 1:/.test(backwards.text), backwards.text);

  const created = await post(AD, `${B}/staff/duty-series`, {
    name: NAME, managerUserIds: [U.math1.id],
    slots: [sitting("S.4 Mathematics P1", 0, 8, [U.math2.id]), sitting("S.4 English P1", 1, 8, [U.math2.id], "Room 4")],
  });
  eq("the administrator creates it with two sittings (201)", created.status, 201);
  SERIES = created.json?.detail?.series?.seriesId;
  truthy("…two sittings, run by the teacher appointed", created.json?.detail?.series?.slotCount === 2
    && created.json?.detail?.series?.managerNames?.some((n) => n.includes(U.math1.name.split(" ")[0])), JSON.stringify(created.json?.detail?.series));
  truthy("…scored on the seeded Exam Supervision parameter", /exam/i.test(created.json?.detail?.series?.parameterName ?? ""), created.json?.detail?.series?.parameterName);
  const slot0 = created.json?.detail?.slots?.[0];
  truthy("…each sitting NAMES its invigilators (the Exams table shows them; a Session duty's were counted, not named)",
    (slot0?.expectedNames ?? []).some((n) => n.includes(U.math2.name.split(" ")[0])), JSON.stringify(slot0?.expectedNames));
  truthy("…every sitting carries its manager as a recorder, so the register is theirs", (slot0?.recorderUserIds ?? []).includes(U.math1.id), JSON.stringify(slot0?.recorderUserIds));

  // ===========================================================================================================
  hdr("35.2 Who sees it — 404, never 403, for anybody else");
  const managerList = (await get(U.math1.token, `${B}/staff/duty-series`)).json ?? [];
  const mine = managerList.find((s) => s.seriesId === SERIES);
  truthy("the manager sees it, marked as theirs, able to write and NOT able to appoint",
    !!mine && mine.iAmManager && mine.canWrite && !mine.canAppoint, JSON.stringify(mine));
  const asInvigilator = await get(U.math2.token, `${B}/staff/duty-series/${SERIES}`);
  truthy("an invigilator reads it but cannot write", asInvigilator.status === 200 && asInvigilator.json?.series?.canWrite === false, `${asInvigilator.status}`);
  eq("an outsider gets 404 for it", (await get(U.lang1.token, `${B}/staff/duty-series/${SERIES}`)).status, 404);
  truthy("…and it is not in their list", !((await get(U.lang1.token, `${B}/staff/duty-series`)).json ?? []).some((s) => s.seriesId === SERIES));

  // ===========================================================================================================
  hdr("35.3 A manager runs the series without the permission — and nothing more");
  const added = await post(U.math1.token, `${B}/staff/duty-series/${SERIES}/slots`, [sitting("S.4 Biology P1", 2, 8, [U.math2.id])]);
  truthy("the manager adds a sitting (200, three now)", added.status === 200 && added.json?.detail?.series?.slotCount === 3, `${added.status} ${added.text.slice(0, 160)}`);
  const third = added.json?.detail?.slots?.find((s) => s.title === "S.4 Biology P1");
  const changed = await put(U.math1.token, `${B}/staff/duty-series/${SERIES}/slots/${third?.id}`, { ...sitting("S.4 Biology P1", 2, 8, [U.math2.id]), location: "Laboratory" });
  truthy("…changes it", changed.status === 200 && changed.json?.detail?.slots?.some((s) => s.location === "Laboratory"), `${changed.status}`);
  eq("…and cancels it (204)", (await del(U.math1.token, `${B}/staff/duty-series/${SERIES}/slots/${third?.id}`)).status, 204);
  const appoint = await put(U.math1.token, `${B}/staff/duty-series/${SERIES}`, { name: NAME, managerUserIds: [U.math1.id, U.lang1.id] });
  truthy("the manager may NOT appoint another manager (403, in words)", appoint.status === 403 && /Only an administrator appoints/.test(appoint.text), `${appoint.status} ${appoint.text.slice(0, 160)}`);
  const renamed = await put(U.math1.token, `${B}/staff/duty-series/${SERIES}`, { name: `${NAME} (revised)` });
  truthy("…but may rename it", renamed.status === 200 && renamed.json?.series?.name === `${NAME} (revised)`, `${renamed.status}`);
  eq("an invigilator cannot add a sitting (404)", (await post(U.math2.token, `${B}/staff/duty-series/${SERIES}/slots`, [sitting("x", 3, 8, [U.math2.id])])).status, 404);
  eq("the generic duty editor still refuses the manager (403) — the series is the only door", (await put(U.math1.token, `${B}/staff/duties/${slot0?.id}`, {})).status, 403);

  // ===========================================================================================================
  hdr("35.4 A manager change moves the registers with it");
  const swapped = await put(AD, `${B}/staff/duty-series/${SERIES}`, { name: `${NAME} (revised)`, managerUserIds: [U.lang1.id] });
  const recorders = swapped.json?.slots?.[0]?.recorderUserIds ?? [];
  truthy("the administrator re-appoints (200)", swapped.status === 200, swapped.text.slice(0, 160));
  truthy("…the new manager now records every sitting, and the old one no longer does", recorders.includes(U.lang1.id) && !recorders.includes(U.math1.id), JSON.stringify(recorders));
  eq("…and the old manager, who invigilates nothing, loses sight of it (404)", (await get(U.math1.token, `${B}/staff/duty-series/${SERIES}`)).status, 404);
  eq("the new manager reads a sitting's register", (await get(U.lang1.token, `${B}/staff/duties/${slot0?.id}/register`)).status, 200);
  await put(AD, `${B}/staff/duty-series/${SERIES}`, { name: `${NAME} (revised)`, managerUserIds: [U.math1.id] });
  eq("…and after appointing the first manager back, the second no longer can (404)", (await get(U.lang1.token, `${B}/staff/duties/${slot0?.id}/register`)).status, 404);

  // ===========================================================================================================
  hdr("35.5 An invigilator in two places at once is WARNED, never refused");
  const overlap = await post(U.math1.token, `${B}/staff/duty-series/${SERIES}/slots`, [sitting("S.4 Chemistry P1", 0, 9, [U.math2.id], "Room 7")]);
  truthy("a sitting overlapping one the same invigilator already has is still saved (200)", overlap.status === 200, overlap.text.slice(0, 160));
  truthy("…with a warning naming the person and the other sitting",
    (overlap.json?.warnings ?? []).some((w) => w.includes(U.math2.name.split(" ")[0]) && w.includes("S.4 Mathematics P1")), JSON.stringify(overlap.json?.warnings));

  // ===========================================================================================================
  hdr("35.6 The invigilator is told — once per write, never once per sitting");
  const notes = ((await get(U.math2.token, "/api/v1/notifications?eventKey=staff.invigilation-assigned&limit=100")).json ?? [])
    .filter((n) => (n.title ?? "").includes(NAME));
  truthy("the invigilator has an invigilation notice for the series", notes.length >= 1, `${notes.length}`);
  truthy("…the one for creation counts both sittings in one message (\"2 sittings\")", notes.some((n) => /2 sittings/.test(n.message)), JSON.stringify(notes.map((n) => n.message)));

  // ===========================================================================================================
  hdr("35.7 Employment types are the school's own list");
  const policy = (await get(AD, "/api/v1/staff/policy")).json;
  policyBefore = JSON.parse(JSON.stringify(policy));
  const types = (policy.employmentTypes ?? []).map((t) => t.name);
  truthy("the six the enum had are seeded", ["Permanent", "Contract", "Probation", "Part time", "Volunteer", "Seconded"].every((n) => types.includes(n)), JSON.stringify(types));

  const profileOf = async (u) => (await get(AD, `${B}/staff/structure/members/${u.id}/profile`)).json;
  // A staff number is REQUIRED on every profile save (the person-codes rule, 2026-09-22). Section 14's accounts may
  // have none; they get a stable one here and keep it — a test account without one is the fixture being wrong, and
  // the profile endpoint cannot blank a number it requires.
  const withNumber = (u, profile) => ({ ...profile, employeeNumber: profile.employeeNumber ?? `E2E-${u.name.replace(/[^A-Za-z0-9]/g, "").toUpperCase()}` });
  const saveType = (u, profile, type) => put(AD, `${B}/staff/structure/members/${u.id}/profile`, { ...profile, employmentType: type });
  const p2 = withNumber(U.math2, await profileOf(U.math2)); profilesToRestore.push([U.math2, p2]);
  const p1 = withNumber(U.math1, await profileOf(U.math1)); profilesToRestore.push([U.math1, p1]);
  const lower = await saveType(U.math2, p2, "part-TIME");
  truthy("a spelling of an existing type is stored as the school spells it", lower.status === 200 && lower.json?.employmentType === "Part time", `${lower.status} ${lower.json?.employmentType ?? lower.text.slice(0, 160)}`);
  const unknown = await saveType(U.math2, p2, "Freelance");
  truthy("a name the list does not carry is refused, in words (400)", unknown.status === 400 && /not one of this school/.test(unknown.text), `${unknown.status} ${unknown.text.slice(0, 160)}`);

  const withPta = { ...policy, employmentTypes: [...policy.employmentTypes, { name: "PTA-paid", isActive: true, sortOrder: 99 }] };
  eq("the school adds \"PTA-paid\" (the MoES return separates it from government-paid)", (await put(AD, "/api/v1/staff/policy", withPta)).status, 200);
  const pta = await saveType(U.math2, p2, "pta paid");
  truthy("…and somebody can now hold it", pta.status === 200 && pta.json?.employmentType === "PTA-paid", `${pta.status} ${pta.json?.employmentType ?? pta.text.slice(0, 160)}`);

  const removed = await put(AD, "/api/v1/staff/policy", policy);
  truthy("removing a type somebody holds is refused, naming it (400)", removed.status === 400 && /PTA-paid is still held/.test(removed.text), `${removed.status} ${removed.text.slice(0, 200)}`);
  const retired = { ...withPta, employmentTypes: withPta.employmentTypes.map((t) => t.name === "PTA-paid" ? { ...t, isActive: false } : t) };
  eq("retiring it instead is allowed", (await put(AD, "/api/v1/staff/policy", retired)).status, 200);
  const stillHeld = await saveType(U.math2, { ...p2, employmentType: "PTA-paid" }, "PTA-paid");
  truthy("the person holding a retired type keeps it on an unrelated save", stillHeld.status === 200 && stillHeld.json?.employmentType === "PTA-paid", `${stillHeld.status} ${stillHeld.text.slice(0, 160)}`);
  const newHolder = await saveType(U.math1, p1, "PTA-paid");
  eq("…but nobody new can be given a retired type (400)", newHolder.status, 400);
  const dup = await put(AD, "/api/v1/staff/policy", { ...retired, employmentTypes: [...retired.employmentTypes, { name: "permanent", isActive: true, sortOrder: 100 }] });
  truthy("two spellings of one type are refused (400)", dup.status === 400 && /same name/.test(dup.text), `${dup.status} ${dup.text.slice(0, 160)}`);
} catch (e) {
  bad("the suite ran to the end", "no exception", e.stack ?? String(e));
} finally {
  hdr("35.9 Put things back");
  for (const [u, profile] of profilesToRestore) await put(AD, `${B}/staff/structure/members/${u.id}/profile`, { ...profile, employmentType: profile.employmentType ?? null });
  if (policyBefore) {
    const r = await put(AD, "/api/v1/staff/policy", policyBefore);
    console.log(`  policy put back (${r.status})`);
  }
  if (SERIES) {
    const d = (await get(AD, `${B}/staff/duty-series/${SERIES}`)).json;
    for (const s of d?.slots ?? []) await del(AD, `${B}/staff/duty-series/${SERIES}/slots/${s.id}`);
    console.log(`  cancelled ${d?.slots?.length ?? 0} sitting(s)`);
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
if (failures.length) console.log("Failures:\n" + failures.map((f) => `  - ${f}`).join("\n"));
viewer(`${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

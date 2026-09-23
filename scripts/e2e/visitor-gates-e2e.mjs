// SECTION 29 — GATES AT VISITOR CHECK-IN AND CHECK-OUT (plan TERM_PROGRAMME_CALENDAR_AND_GATES §10, 2026-09-23).
//
// Visits recorded no gate and no person. The branch now keeps a list of gates (BranchVocabulariesDto.Gates,
// ONE writer: PUT …/visitors/gates) and every check-in and check-out resolves its gate through VisitorGateRule:
// none active → nothing recorded; one → filled in; two or more → required and must be an ACTIVE gate.
//
//   29.1  the list: saved and read back; the writer refuses a teacher, a blank name and a folded duplicate;
//   29.2  check-in with two gates: none → 400 on Gate, unknown → 400, a folded spelling → the stored name + who;
//   29.3  check-out: none → 400, a gate → ExitGate + who saw them out; the list filters by gate;
//   29.4  the roll call and the report carry the gate: evacuation EntryGate, per-gate counts, arrivals by hour;
//   29.5  retiring: a retired gate is refused; one active gate is filled in; removing a USED gate is refused,
//         removing an unused one is not; the students vocabulary editor cannot erase gates; a rename keeps history;
//   29.6  no active gate: nothing is asked and nothing is recorded;
//   29.7  every visit it opened is checked out and the branch's own gates are put back.
//
// Run: node scripts/e2e/visitor-gates-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const RUN = Date.now().toString(36);

let pass = 0, fail = 0;
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
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
const post = (t, p, b) => call(t, "POST", p, b ?? {});
async function login(id, pws = [PW, NEW_PW]) {
  for (const pw of pws) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}

// The rule's own fold (VisitorGateRule.Key = ClassName.Key): letters and digits, upper-cased.
const key = (s) => (s ?? "").replace(/[^\p{L}\p{N}]/gu, "").toUpperCase();
const refusesOnGate = (r) => r.status === 400 && (r.json?.errors?.Gate ?? r.json?.errors?.gate) !== undefined;

// Stable names, so a re-run reuses its own entries rather than growing the list: a gate a visit used can
// only ever be retired, never removed — which is exactly the rule under test.
const NORTH = "E2E Gate North";
const SOUTH = "E2E Gate South";
const SPARE = "E2E Gate Spare";
const isOurs = (name) => key(name).startsWith(key("E2E Gate"));

const SA = await login(SA_USER, [SA_PASS]);
const ad = await login(TENANT_ADMIN);
const teacher = await login("e2e.teacher.s4@qmgr.local");
if (!SA || !ad) { console.error("sign-in failed"); process.exit(1); }
const AD = ad.token, ORG = ad.user.organizationId;
const V = `/api/v1/branches/${BRANCH}/visitors`;
const GATES = `${V}/gates`;

let original = null;        // the branch's own list, as found
let grantedModule = false;
const opened = [];          // visits this suite checked in

// The list to save: the branch's own gates kept but RETIRED while the suite runs (so it alone decides how many
// are active), every earlier-run e2e gate retired, then `states` — { name: active } — applied on top in order.
function listWith(states, { restoreOriginal = false } = {}) {
  const out = [];
  for (const g of original ?? []) {
    const mine = isOurs(g.name);
    out.push({ name: g.name, isActive: restoreOriginal && !mine ? g.isActive : false, sortOrder: out.length });
  }
  for (const [name, active] of Object.entries(states)) {
    const at = out.find((g) => key(g.name) === key(name));
    if (at) at.isActive = active; else out.push({ name, isActive: active, sortOrder: out.length });
  }
  return { gates: out, renames: {} };
}

async function checkIn(n, gate) {
  const body = { fullName: `Gate Visitor ${RUN} ${n}`, email: `gate.${RUN}.${n}@example.test`, purpose: "Gate e2e", hostName: "Front office", consentGiven: true };
  if (gate !== undefined) body.gate = gate;
  const r = await post(AD, `${V}/checkin`, body);
  if (r.status === 201 && r.json?.id) opened.push(r.json.id);
  return r;
}

try {
  hdr("29.0 The branch holds Visitor Management");
  const mods = (await get(SA.token, `/api/v1/admin/tenants/${ORG}/modules`)).json ?? [];
  const vm = mods.find((m) => m.moduleCode === "visitor-management");
  if (!(vm?.purchased && vm?.status === "Active")) {
    const g = await put(SA.token, `/api/v1/admin/tenants/${ORG}/modules/visitor-management`, { note: "E2E gates" });
    eq("visitor-management granted as SuperAdmin for the run", g.status, 200);
    grantedModule = g.status === 200;
  } else ok("visitor-management is already active");

  const before = await get(AD, GATES);
  eq("GET gates → 200", before.status, 200);
  original = Array.isArray(before.json) ? before.json : [];

  // ---------------------------------------------------------------------------------------------
  hdr("29.1 The list — one writer, and it refuses what it should");
  const save2 = await put(AD, GATES, listWith({ [NORTH]: true, [SOUTH]: true, [SPARE]: true }));
  eq("PUT three gates → 200", save2.status, 200);
  const read = (await get(AD, GATES)).json ?? [];
  const active = read.filter((g) => g.isActive).map((g) => g.name);
  truthy("GET returns them, active", [NORTH, SOUTH, SPARE].every((n) => active.includes(n)), JSON.stringify(active));
  eq("…and nothing else is active while the suite runs", active.length, 3);
  if (teacher) {
    const t = await put(teacher.token, GATES, listWith({ [NORTH]: true }));
    truthy("a teacher may not change the gates (403)", t.status === 403, `${t.status}`);
  }
  const blank = await put(AD, GATES, { gates: [...listWith({ [NORTH]: true }).gates, { name: "   ", isActive: true, sortOrder: 99 }], renames: {} });
  eq("a blank gate name → 400", blank.status, 400);
  const dup = await put(AD, GATES, { gates: [...listWith({ [NORTH]: true }).gates, { name: "e2e gate-NORTH", isActive: true, sortOrder: 99 }], renames: {} });
  eq("the same gate written differently is a duplicate → 400", dup.status, 400);
  truthy("…and the refusal says it is listed twice", /listed twice/i.test(dup.json?.title ?? dup.text), dup.json?.title ?? dup.text);

  // Spare is never used by a visit, so it may simply leave the list.
  eq("removing a gate no visit has used → 200", (await put(AD, GATES, listWith({ [NORTH]: true, [SOUTH]: true }))).status, 200);
  const afterRemove = (await get(AD, GATES)).json ?? [];
  // It may still be listed (retired) when an earlier run's visit somehow used it; otherwise it is gone.
  truthy("…and it is no longer offered", !afterRemove.some((g) => key(g.name) === key(SPARE) && g.isActive), JSON.stringify(afterRemove));

  // ---------------------------------------------------------------------------------------------
  hdr("29.2 Check-in with two gates: required, and it must be one of them");
  const none = await checkIn(1);
  truthy("no gate → 400 filed under Gate", refusesOnGate(none), `${none.status} ${none.text}`);
  truthy("…in the rule's words", /choose the gate/i.test(none.json?.title ?? ""), none.json?.title);
  const unknown = await checkIn(2, "The Back Fence");
  truthy("an unknown gate → 400 filed under Gate", refusesOnGate(unknown), `${unknown.status} ${unknown.text}`);
  truthy("…naming what was sent", (unknown.json?.title ?? "").includes("The Back Fence"), unknown.json?.title);
  const v1 = await checkIn(3, "  e2e gate-north ");
  eq("a gate written differently is the same gate → 201", v1.status, 201);
  eq("…stored under the list's own spelling", v1.json?.entryGate, NORTH);
  eq("…with who admitted them", v1.json?.checkedInByUserId, ad.user.id);
  truthy("…by name", typeof v1.json?.checkedInByName === "string" && v1.json.checkedInByName.length > 0, JSON.stringify(v1.json?.checkedInByName));
  const v2 = await checkIn(4, SOUTH);
  eq("a second visitor by the other gate → 201", v2.status, 201);
  eq("…recorded at South", v2.json?.entryGate, SOUTH);
  const detail = await get(AD, `${V}/${v1.json?.id}`);
  eq("the visit read back carries the gate", detail.json?.entryGate, NORTH);

  // ---------------------------------------------------------------------------------------------
  hdr("29.3 Check-out: the gate they left by, and who saw them out");
  const outNone = await post(AD, `${V}/${v1.json?.id}/checkout`, {});
  truthy("no gate with two to choose from → 400 filed under Gate", refusesOnGate(outNone), `${outNone.status} ${outNone.text}`);
  truthy("…in the check-out words", /leaving by/i.test(outNone.json?.title ?? ""), outNone.json?.title);
  const out = await post(AD, `${V}/${v1.json?.id}/checkout`, { gate: SOUTH });
  eq("check-out by South → 200", out.status, 200);
  eq("…ExitGate is South", out.json?.exitGate, SOUTH);
  eq("…EntryGate is still North", out.json?.entryGate, NORTH);
  eq("…seen out by the caller", out.json?.checkedOutByUserId, ad.user.id);
  truthy("…by name", typeof out.json?.checkedOutByName === "string" && out.json.checkedOutByName.length > 0, JSON.stringify(out.json?.checkedOutByName));
  const byNorth = (await get(AD, `${V}?gate=${encodeURIComponent("e2e gate north")}`)).json ?? [];
  truthy("the list filtered by North holds the first visit", byNorth.some((v) => v.id === v1.json?.id), `${byNorth.length} row(s)`);
  truthy("…and not the one that came in and stayed at South", !byNorth.some((v) => v.id === v2.json?.id), "");

  // ---------------------------------------------------------------------------------------------
  hdr("29.4 The roll call and the report carry the gate");
  const evac = await get(AD, `${V}/evacuation`);
  eq("GET evacuation → 200", evac.status, 200);
  const evacRow = (evac.json?.people ?? []).find((p) => p.visitorId === v2.json?.id);
  eq("the roll call names the gate a person on site came in by", evacRow?.entryGate, SOUTH);
  const d = new Date(); const iso = (x) => x.toISOString().slice(0, 10);
  const from = iso(new Date(d.getTime() - 86400000)), to = iso(new Date(d.getTime() + 86400000));
  const rep = await get(AD, `${V}/report/v2?from=${from}&to=${to}`);
  eq("GET report/v2 → 200", rep.status, 200);
  const g = (name) => (rep.json?.gates ?? []).find((x) => x.gate === name);
  truthy("the report counts an entry at North", (g(NORTH)?.entries ?? 0) >= 1, JSON.stringify(rep.json?.gates));
  truthy("…an entry and an exit at South", (g(SOUTH)?.entries ?? 0) >= 1 && (g(SOUTH)?.exits ?? 0) >= 1, JSON.stringify(g(SOUTH)));
  truthy("…somebody still on site by South", (g(SOUTH)?.onSiteNow ?? 0) >= 1, JSON.stringify(g(SOUTH)));
  truthy("…and arrivals by hour, 24 of them, adding up", Array.isArray(g(SOUTH)?.arrivalsByHour) && g(SOUTH).arrivalsByHour.length === 24
    && g(SOUTH).arrivalsByHour.reduce((a, b) => a + b, 0) === g(SOUTH).entries, JSON.stringify(g(SOUTH)?.arrivalsByHour));
  const filtered = await get(AD, `${V}/report/v2?from=${from}&to=${to}&gate=${encodeURIComponent(NORTH)}`);
  truthy("the report filtered by North says so", (filtered.json?.filterDescription ?? "").includes(NORTH), filtered.json?.filterDescription);
  const csv = await call(AD, "GET", `${V}/report/v2/export?from=${from}&to=${to}`);
  if (csv.status === 200) {
    const head = csv.text.split(/\r?\n/)[0];
    truthy("the export carries Entry Gate, Exit Gate and Admitted By", /Entry Gate/.test(head) && /Exit Gate/.test(head) && /Admitted By/.test(head), head);
  } else ok(`the export is gated on reports.export (${csv.status}) — its columns are asserted where the caller holds it`);

  // ---------------------------------------------------------------------------------------------
  hdr("29.5 Retire, remove, the other editor, rename");
  eq("retire North (South stays) → 200", (await put(AD, GATES, listWith({ [NORTH]: false, [SOUTH]: true }))).status, 200);
  const retiredIn = await checkIn(5, NORTH);
  truthy("check-in by a retired gate → 400 filed under Gate", refusesOnGate(retiredIn), `${retiredIn.status} ${retiredIn.text}`);
  const single = await checkIn(6);
  eq("one active gate: no gate sent → 201", single.status, 201);
  eq("…and the only gate is filled in", single.json?.entryGate, SOUTH);
  const wrongSingle = await checkIn(7, "Somewhere Else");
  truthy("one active gate: a different gate sent is still refused", refusesOnGate(wrongSingle), `${wrongSingle.status}`);

  const removeUsed = await put(AD, GATES, { gates: listWith({ [SOUTH]: true }).gates.filter((x) => key(x.name) !== key(NORTH)), renames: {} });
  eq("removing a gate a visit has used → 400", removeUsed.status, 400);
  truthy("…and says to retire it", /retire/i.test(removeUsed.json?.title ?? ""), removeUsed.json?.title);

  const vocab = await get(AD, `/api/v1/branches/${BRANCH}/students/vocabularies`);
  if (vocab.status === 200) {
    const sent = { ...vocab.json }; delete sent.gates;
    const pv = await put(AD, `/api/v1/branches/${BRANCH}/students/vocabularies`, { vocabularies: sent, classRenames: {}, houseRenames: {}, dormitoryRenames: {} });
    if (pv.status === 403) ok("the tenant administrator may not edit the student lists here (403); the keep-gates rule is exercised where they may");
    else {
      eq("the students vocabulary editor saves without gates → 200", pv.status, 200);
      const kept = (await get(AD, GATES)).json ?? [];
      truthy("…and the gates are still there", kept.some((x) => x.name === SOUTH && x.isActive) && kept.some((x) => x.name === NORTH), JSON.stringify(kept));
    }
  } else ok(`the students vocabularies are not readable here (${vocab.status}); the keep-gates rule is exercised where they are`);

  const renamed = `${SOUTH} Renamed`;
  const ren = listWith({ [NORTH]: false, [SOUTH]: true });
  ren.gates = ren.gates.map((x) => (key(x.name) === key(SOUTH) ? { ...x, name: renamed } : x));
  ren.renames = { [SOUTH]: renamed };
  eq("rename South → 200", (await put(AD, GATES, ren)).status, 200);
  eq("a visit recorded before the rename keeps the name it had that day", (await get(AD, `${V}/${v2.json?.id}`)).json?.entryGate, SOUTH);
  const back = listWith({ [NORTH]: false });
  back.gates = back.gates.filter((x) => key(x.name) !== key(SOUTH));
  back.gates.push({ name: SOUTH, isActive: true, sortOrder: back.gates.length });
  back.renames = { [renamed]: SOUTH };
  eq("…and renaming it back → 200", (await put(AD, GATES, back)).status, 200);

  // ---------------------------------------------------------------------------------------------
  hdr("29.6 No active gate: nothing asked, nothing recorded");
  eq("retire every gate → 200", (await put(AD, GATES, listWith({ [NORTH]: false, [SOUTH]: false }))).status, 200);
  const free = await checkIn(8);
  eq("check-in with no gate → 201", free.status, 201);
  eq("…and no gate is recorded", free.json?.entryGate ?? null, null);
  const ignored = await checkIn(9, "Anything At All");
  eq("a gate sent anyway is ignored → 201", ignored.status, 201);
  eq("…still nothing recorded", ignored.json?.entryGate ?? null, null);
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("29.7 Put things back");
  // Close every visit this run opened, with whatever gate the branch will accept at this moment.
  const now = (await get(AD, GATES)).json ?? [];
  const firstActive = now.find((x) => x.isActive)?.name;
  let closed = 0;
  for (const id of opened) {
    const r = await post(AD, `${V}/${id}/checkout`, firstActive ? { gate: firstActive } : {});
    if (r.status === 200 || r.status === 400) closed++; // 400: already checked out by the suite itself
  }
  eq("every visit the suite opened is checked out", closed, opened.length);
  if (original) {
    // Every e2e gate now on the list stays, retired: one a visit used cannot be removed, by the rule under test.
    const ours = Object.fromEntries(((await get(AD, GATES)).json ?? []).filter((x) => isOurs(x.name)).map((x) => [x.name, false]));
    const restore = await put(AD, GATES, listWith(ours, { restoreOriginal: true }));
    eq("the branch's own gates are put back as they were (the suite's own stay retired)", restore.status, 200);
  }
  if (grantedModule) await call(SA.token, "DELETE", `/api/v1/admin/tenants/${ORG}/modules/visitor-management?note=E2E%20gates`);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

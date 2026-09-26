// SECTION 42 — IMPORTED MEETINGS THAT BECOME REGISTERS, AND WHAT AN IMPORT LEFT UNFINISHED (2026-09-26).
//
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, E9, E10 and B6, B7, B12, B13. The school's report was
// "some meetings are not appearing in the registers"; the cause was the importer's default, and these are its fixes.
//
//   42.1  two meetings of ONE title on ONE day at different times are two meetings, and a re-import matches each by
//         the line it came from (B7);
//   42.2  an event edited BY HAND is left alone by a re-import, which says which of the document's fields it did not
//         apply (B6);
//   42.3  an event imported before as an event only is LINKED to its meeting when the same line comes back as a
//         meeting — linked, never doubled;
//   42.4  Import health names the meetings an import left as events only (with the reader's own reason), the
//         registers with nobody to take them, and the rows it refused — and "Give it a register" fixes the first;
//   42.5  undoing an EARLIER import keeps what a later import relies on (B13);
//   42.6  an import tells each person ONCE, events and duties in one message;
//   42.7  with the school's documents (E2E_DOCS_DIR): no meeting is left "event only" by default any more;
//   42.8  everything is undone.
//
// Run: node scripts/e2e/import-routing-e2e.mjs   (or through class-teacher-e2e.sh)
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, basename } from "node:path";

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const DOCS = process.env.E2E_DOCS_DIR || "";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const TEACHER = process.env.TEACHER || "e2e.teacher.s4@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";

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
const skipped = (what, why) => { const l = `  \x1b[33mSKIP\x1b[0m  ${what}: ${why}`; console.log(l); viewer(l); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(token, method, path, body) {
  const h = {};
  if (token) h.Authorization = `Bearer ${token}`;
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
async function login(id, pws = [PW, NEW_PW]) {
  for (const pw of pws) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}
const iso = (d) => d.toISOString().slice(0, 10);
const addDays = (d, n) => { const x = new Date(d); x.setUTCDate(x.getUTCDate() + n); return x; };

const ad = await login(TENANT_ADMIN);
const teacher = await login(TEACHER);
if (!ad || !teacher) { console.error("sign-in failed (tenant admin or teacher)"); process.exit(1); }
const AD = ad.token, T = teacher.token, TEACHER_ID = teacher.user.id, ADMIN_ID = ad.user.id;
const B = `/api/v1/branches/${BRANCH}`;
const IMPORT = `${B}/calendar/import`;
const EV = `${B}/calendar/events`;
const run = Date.now().toString(36);
const base = addDays(new Date(), 260 + Math.floor(Math.random() * 60));
const d1 = iso(base), d2 = iso(addDays(base, 1)), d3 = iso(addDays(base, 2));
const jobs = [];
const req = (overrides = {}) => ({ preview: false, sourceFiles: [`e2e-routing-${run}.docx`], notifyPeople: false, events: [], meetings: [], rotaSlots: [], ...overrides });
const ev = (key, title, date, extra = {}) => ({ sourceKey: `e2e:${run}:${key}`, title, startsOn: date, endsOn: date, audience: "Staff", classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [], ...extra });
const commit = async (body) => { const r = await post(AD, IMPORT, body); if (r.json?.jobId) jobs.push(r.json.jobId); return r; };

try {
  hdr("42.1 One title, one day, two times: two meetings (B7)");
  const m = (time, end) => ({ sourceKey: `meeting:e2e-${run}-dorm:${d1}:${time.replace(":", "")}`, title: `E2E ${run} Dorm meeting`, date: d1, startTime: time, endTime: end, expectedUserIds: [TEACHER_ID], recorderUserIds: [ADMIN_ID] });
  const two = await commit(req({ meetings: [m("17:00", "18:00"), m("20:00", "21:00")] }));
  eq("commit → 201", two.status, 201);
  eq("…made TWO meetings", two.json?.meetingsCreated, 2);
  const again = await commit(req({ meetings: [m("17:00", "18:00"), m("20:00", "21:30")] }));
  eq("a re-import with the second one's end moved → 201", again.status, 201);
  eq("…creates nothing", again.json?.meetingsCreated, 0);
  eq("…updates exactly the one that changed", again.json?.meetingsUpdated, 1);
  eq("…and finds the other unchanged", again.json?.unchanged, 1);

  hdr("42.2 A hand edit survives a re-import (B6)");
  const first = await commit(req({ events: [ev("assembly", `E2E ${run} Assembly`, d2, { location: "Main Hall" })] }));
  const eventId = first.json?.rows?.find((r) => r.kind === "event")?.recordId;
  truthy("an event is imported", !!eventId, first.text);
  const current = (await get(AD, `${EV}/${eventId}`)).json;
  eq("the keeper edits its venue by hand → 200", (await put(AD, `${EV}/${eventId}`, { ...current, location: "Chapel", notifyAudience: false, rowVersion: current.rowVersion })).status, 200);
  truthy("…and it is marked edited by hand", !!(await get(AD, `${EV}/${eventId}`)).json?.editedByHandAt);
  const reimport = await commit(req({ events: [ev("assembly", `E2E ${run} Assembly`, d2, { location: "Sports Field" })] }));
  const row = reimport.json?.rows?.find((r) => r.kind === "event");
  eq("the re-import leaves it Unchanged", row?.outcome, "Unchanged");
  truthy("…and says the document's venue was not applied", (row?.warnings ?? []).some((w) => /by hand/i.test(w) && /venue/.test(w)), JSON.stringify(row?.warnings));
  eq("…the hand edit stands", (await get(AD, `${EV}/${eventId}`)).json?.location, "Chapel");

  hdr("42.3 An event-only meeting becomes a meeting: linked, not doubled");
  const key = `activities:e2e-${run}-hods:${d3}`;
  const asEvent = await commit(req({ events: [{ ...ev("x", `E2E ${run} HODs meeting`, d3, { startTime: "15:00", endTime: "16:00", noRegisterReason: "A calendar event only — you chose it." }), sourceKey: key }] }));
  const linkedEventId = asEvent.json?.rows?.find((r) => r.kind === "event")?.recordId;
  truthy("imported first as a calendar event only", !!linkedEventId);
  const asMeeting = await commit(req({
    meetings: [{ sourceKey: key, title: `E2E ${run} HODs meeting`, date: d3, startTime: "15:00", endTime: "16:00", expectedUserIds: [TEACHER_ID], recorderUserIds: [ADMIN_ID] }],
    events: [{ ...ev("x", `E2E ${run} HODs meeting`, d3, { startTime: "15:00", endTime: "16:00", sameAsMeetingKey: key, staffAudience: { allStaff: false, userIds: [TEACHER_ID] } }), sourceKey: key }]
  }));
  eq("…re-imported as a meeting → 201", asMeeting.status, 201);
  eq("…one meeting made", asMeeting.json?.meetingsCreated, 1);
  eq("…NO second event", asMeeting.json?.eventsCreated, 0);
  const linked = (await get(AD, `${EV}/${linkedEventId}`)).json;
  truthy("…the existing event now points at its register", !!linked?.dutyId, JSON.stringify({ dutyId: linked?.dutyId }));
  const tRange = (await get(T, `${B}/calendar?from=${d3}&to=${d3}`)).json;
  const shownEvent = (tRange?.events ?? []).some((e) => e.id === linkedEventId);
  truthy("the teacher's calendar has the event, and the meeting as its duty", shownEvent && (tRange?.myDuties ?? []).some((d) => d.id === linked?.dutyId));

  hdr("42.4 Import health");
  const health = await commit(req({
    events: [ev("dept", `E2E ${run} Departmental meeting`, d2, { startTime: "10:00", endTime: "11:00", noRegisterReason: "Who is expected was not decided." })],
    meetings: [
      { sourceKey: `e2e:${run}:norecorder`, title: `E2E ${run} Games meeting`, date: d2, startTime: "12:00", endTime: "13:00", expectedUserIds: [TEACHER_ID], recorderUserIds: [] },
      { sourceKey: `e2e:${run}:notime`, title: `E2E ${run} Timeless meeting`, date: d2, expectedUserIds: [TEACHER_ID], recorderUserIds: [ADMIN_ID] }
    ]
  }));
  eq("an import with one row the server refuses → 201", health.status, 201);
  const h = await get(AD, `${IMPORT}/jobs/${health.json?.jobId}/health`);
  eq("its health → 200", h.status, 200);
  const unregistered = (h.json?.meetingsWithoutRegister ?? []).find((x) => x.title === `E2E ${run} Departmental meeting`);
  truthy("…names the meeting left as an event only", !!unregistered, JSON.stringify(h.json?.meetingsWithoutRegister));
  eq("…with the reader's own reason", unregistered?.reason, "Who is expected was not decided.");
  truthy("…names the register nobody is named to take", (h.json?.registersWithoutRecorder ?? []).some((x) => x.title === `E2E ${run} Games meeting`));
  truthy("…and the refused row, by title", (h.json?.refused ?? []).some((x) => x.title === `E2E ${run} Timeless meeting`), JSON.stringify(h.json?.refused));
  eq("…and says refusals are recorded", h.json?.refusalsRecorded, true);
  const jobsList = (await get(AD, `${IMPORT}/jobs`)).json ?? [];
  eq("the history counts what needs attention (3)", jobsList.find((j) => j.id === health.json?.jobId)?.needsAttention, 3);
  eq("a teacher cannot read health → 403", (await get(T, `${IMPORT}/jobs/${health.json?.jobId}/health`)).status, 403);
  const give = await post(AD, `${EV}/${unregistered?.eventId}/register`, { recorderUserIds: [ADMIN_ID], notifyPeople: false });
  if (give.status === 403 && give.json?.error === "MODULE_NOT_PURCHASED") skipped("give it a register", "Welfare & Performance is not held");
  else {
    eq("'Give it a register' on it → 200", give.status, 200);
    truthy("…and health no longer lists it", !((await get(AD, `${IMPORT}/jobs/${health.json?.jobId}/health`)).json?.meetingsWithoutRegister ?? []).some((x) => x.eventId === unregistered?.eventId));
  }

  hdr("42.5 Undoing an earlier import keeps what a later one relies on (B13)");
  const a = await commit(req({ events: [ev("relied", `E2E ${run} Prize giving`, d1)] }));
  const reliedId = a.json?.rows?.[0]?.recordId;
  const b = await commit(req({ sourceFiles: [`e2e-routing-${run}-b.docx`], events: [ev("relied", `E2E ${run} Prize giving`, d1), ev("later-own", `E2E ${run} Speech day`, d1)] }));
  eq("the later import finds it unchanged", b.json?.unchanged, 1);
  const undoA = await post(AD, `${IMPORT}/jobs/${a.json?.jobId}/undo`);
  eq("undo the earlier one → 200", undoA.status, 200);
  eq("…removes nothing the later one relies on", undoA.json?.eventsRemoved, 0);
  truthy("…and says why", (undoA.json?.updatesLeft ?? []).some((x) => /relies on it/.test(x)), JSON.stringify(undoA.json?.updatesLeft));
  eq("…the event is still there", (await get(AD, `${EV}/${reliedId}`)).status, 200);

  hdr("42.6 One message per person");
  const toldFrom = Date.now() - 5000; // a message from an earlier run also says "2 events were added"
  const told = await commit(req({
    notifyPeople: true,
    events: [ev("told1", `E2E ${run} Told one`, d1, { staffAudience: { allStaff: false, userIds: [TEACHER_ID] } }),
             ev("told2", `E2E ${run} Told two`, d2, { staffAudience: { allStaff: false, userIds: [TEACHER_ID] } })],
    meetings: [{ sourceKey: `e2e:${run}:told-m`, title: `E2E ${run} Told meeting`, date: d2, startTime: "08:00", endTime: "09:00", expectedUserIds: [TEACHER_ID], recorderUserIds: [ADMIN_ID] }]
  }));
  eq("an import that tells people → 201", told.status, 201);
  await sleep(1500);
  const mine = ((await get(T, "/api/v1/notifications?limit=100")).json ?? []).filter((n) => Date.parse(n.createdAt) >= toldFrom && (n.message.includes(`E2E ${run} Told meeting`) || /2 events were added/.test(n.message)));
  eq("the teacher gets ONE message", mine.length, 1);
  truthy("…naming the meeting AND counting the events", /Told meeting/.test(mine[0]?.message ?? "") && /2 events were added/.test(mine[0]?.message ?? ""), mine[0]?.message);

  hdr("42.7 The school's documents: no meeting is an event only by default");
  if (!DOCS || !existsSync(DOCS)) skipped("the school's documents", "E2E_DOCS_DIR is not set");
  else {
    const files = readdirSync(DOCS).filter((f) => /\.(docx|doc|xlsx|csv)$/i.test(f)).slice(0, 5);
    const form = new FormData();
    for (const f of files) form.append("files", new Blob([readFileSync(join(DOCS, f))]), basename(f));
    const res = await fetch(`${API}/api/v1/dev/import/read-document`, { method: "POST", headers: { Authorization: `Bearer ${AD}` }, body: form });
    if (res.status === 404) skipped("the school's documents", "the Development read endpoint is not available on this API");
    else {
      const json = await res.json();
      const meetings = (json.files ?? []).flatMap((f) => f.candidates ?? []).filter((c) => c.kind === "Meeting" || c.kind === 1);
      truthy("the documents hold meetings", meetings.length > 0, `${meetings.length}`);
      eq("…and none is 'event only' until a reader says so", meetings.filter((c) => c.attendance === "EventOnly" || c.attendance === 0).length, 0);
    }
  }
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("42.8 Undo everything");
  let undone = 0;
  for (const id of [...jobs].reverse()) { const r = await post(AD, `${IMPORT}/jobs/${id}/undo`); if (r.status === 200) undone++; }
  eq(`every import made here is undone (${jobs.length})`, undone, jobs.length);
  // An event a later import relied on survived its own undo; with every import undone now, remove any left.
  const leftover = ((await get(AD, `${B}/calendar?from=${d1}&to=${d3}&scope=all`)).json?.events ?? []).filter((e) => e.title.startsWith(`E2E ${run} `));
  for (const e of leftover) await del(AD, `${EV}/${e.id}`);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

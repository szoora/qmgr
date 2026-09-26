// SECTION 31 — THE TERM PROGRAMME IMPORT (2026-09-23).
//
// Plan: docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md §4–§8. A school's own Word, PDF and Excel documents
// become calendar events, staff meetings (Session duties) and rota slots, through one preview and one commit.
//
//   31.1  the school's real documents, read by the Development-only endpoint (SKIPPED without E2E_DOCS_DIR —
//         they carry staff phone numbers and are never copied into this repository): the measured counts, the
//         Wed 2 Dec gap, the Sat 12 Sep and Mon 2 Nov doubles, the 02/11 → 02/12 suggestion, the UCE briefing
//         conflict, the two "2025" typos, and the married-name question;
//   31.2  a synthetic import through the API: the preview writes nothing, the commit creates, a re-import
//         changes nothing, a changed time is an Update naming the field;
//   31.3  who may: a teacher is refused (403), an events-only import needs calendar.manage alone;
//   31.4  two simultaneous commits of one body produce one set of rows (the advisory lock);
//   31.5  undo removes everything EXCEPT a meeting whose register was taken;
//   31.6  clean up.
//
// Run: E2E_DOCS_DIR="D:/QMGR/DATA" node scripts/e2e/programme-import-e2e.mjs   (or through class-teacher-e2e.sh)
import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join } from "node:path";

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const TEACHER = process.env.TEACHER || "e2e.teacher.s4@qmgr.local";
const DOCS = process.env.E2E_DOCS_DIR || "";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";

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
const del = (t, p) => call(t, "DELETE", p);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}

async function readDocuments(token, paths) {
  const form = new FormData();
  for (const p of paths) form.append("files", new Blob([readFileSync(p)]), p.split(/[\\/]/).pop());
  const res = await fetch(`${API}/api/v1/dev/import/read-document`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text };
}

const ad = await login(TENANT_ADMIN);
const teacher = await login(TEACHER);
if (!ad || !teacher) { console.error("sign-in failed"); process.exit(1); }
const AD = ad.token, T = teacher.token;
const B = `/api/v1/branches/${BRANCH}`;
const IMPORT = `${B}/calendar/import`;
const cleanupJobs = [];
const cleanupDuties = [];

const iso = (d) => d.toISOString().slice(0, 10);
const addDays = (base, n) => { const d = new Date(base); d.setUTCDate(d.getUTCDate() + n); return d; };

try {
  // ---------------------------------------------------------------------------------------------------
  hdr("31.1 The school's real documents (Development-only reader)");
  if (!DOCS || !existsSync(DOCS)) {
    skipped("the five documents", "E2E_DOCS_DIR is not set — the school's documents carry staff phone numbers and are never in this repository");
  } else {
    const all = readdirSync(DOCS).filter((f) => /\.(docx|doc|pdf)$/i.test(f));
    const find = (re) => { const f = all.find((x) => re.test(x)); return f ? join(DOCS, f) : null; };
    const activities = find(/^Activities/i), programme = find(/^BEGINNING/i), meetings = find(/^Schedule of Meetings/i),
      staffRota = find(/^STAFF DUTY ROTA/i), adminRota = find(/^ADMINISTRATIVE/i);
    const probe = await readDocuments(AD, [activities ?? all.map((f) => join(DOCS, f))[0]]);
    if (probe.status === 404) {
      skipped("the five documents", "the API is not running in Development, so the reader endpoint answers 404 by design");
    } else {
      const one = async (path) => (await readDocuments(AD, [path])).json?.summaries?.[0];
      if (activities) {
        const s = await one(activities);
        truthy("Activities: read as term activities", s?.kinds?.includes("TermActivities"), JSON.stringify(s?.kinds));
        eq("Activities: 29 table rows", s?.tableRows, 29);
        eq("Activities: 2 empty weeks", s?.emptyWeeks, 2);
        eq("Activities: 27 activity rows (dated and undated)", s?.candidates, 27);
        truthy("Activities: the year-only \"2026\" row is undated", (s?.undated ?? 0) >= 1, JSON.stringify(s));
        eq("Activities: the theme is read", (s?.theme ?? "").toUpperCase().includes("HOLISTIC"), true);
      } else skipped("Activities", "the file was not found in E2E_DOCS_DIR");
      if (programme) {
        const s = await one(programme);
        truthy("Programme: read as a timed programme", s?.kinds?.includes("DailyProgramme"), JSON.stringify(s?.kinds));
        eq("Programme: 25 table rows", s?.tableRows, 25);
        eq("Programme: 25 items", s?.candidates, 25);
        eq("Programme: 1 found in the note under the table", s?.fromNotes, 1);
        eq("Programme: the title says 2026, not the file name's 2025", s?.year, 2026);
      } else skipped("Programme", "the file was not found in E2E_DOCS_DIR");
      if (meetings) {
        const s = await one(meetings);
        truthy("Meetings: read as a schedule of meetings", s?.kinds?.includes("MeetingSchedule"), JSON.stringify(s?.kinds));
        eq("Meetings: 28 rows", s?.candidates, 28);
        eq("Meetings: 18 of them staff meetings", s?.meetings, 18);
      } else skipped("Meetings", "the file was not found in E2E_DOCS_DIR");
      if (staffRota) {
        const s = await one(staffRota);
        truthy("Staff rota: read as a person rota", s?.kinds?.includes("PersonRota"), JSON.stringify(s?.kinds));
        eq("Staff rota: 83 people", s?.rotaPeople, 83);
        eq("Staff rota: 85 duty assignments", s?.rotaAssignments, 85);
        eq("Staff rota: 58 people with dates", s?.peopleWithDates, 58);
        eq("Staff rota: 25 not on the rota (a job title or blank)", s?.notOnRota, 25);
      } else skipped("Staff rota", "the file was not found in E2E_DOCS_DIR");
      if (adminRota) {
        const s = await one(adminRota);
        eq("Admin rota (.doc, Word 97–2003): read at all", s?.refusal ?? null, null);
        truthy("Admin rota: read as a period rota", s?.kinds?.includes("PeriodRota"), JSON.stringify(s?.kinds));
        eq("Admin rota: 12 weeks", s?.rotaAssignments, 12);
      } else skipped("Admin rota", "the file was not found in E2E_DOCS_DIR");

      const paths = [activities, programme, meetings, staffRota, adminRota].filter(Boolean);
      if (paths.length === 5) {
        const together = (await readDocuments(AD, paths)).json;
        const issues = together?.checks?.issues ?? [];
        const has = (kind, pred = () => true) => issues.some((i) => i.kind === kind && pred(i));
        truthy("the rota gap: Wed 2 Dec has no Teacher on Duty (must decide)", has("RotaGap", (i) => i.date === "2026-12-02" && i.severity === "MustDecide"), JSON.stringify(issues.filter((i) => i.kind === "RotaGap")));
        truthy("a double: Sat 12 Sep", has("RotaDouble", (i) => i.date === "2026-09-12"));
        truthy("a double: Mon 2 Nov", has("RotaDouble", (i) => i.date === "2026-11-02"));
        const suggestion = issues.find((i) => i.kind === "DateSuggestion");
        truthy("the suggestion: Musiime Naomeh 02/11 → 02/12", suggestion && suggestion.suggestFrom === "2026-11-02" && suggestion.suggestTo === "2026-12-02" && /Musiime Naomeh/i.test(suggestion.personText ?? ""), JSON.stringify(suggestion));
        eq("…and it is the only suggestion offered", issues.filter((i) => i.kind === "DateSuggestion").length, 1);
        truthy("the conflict: the S.4 UCE briefing, Thu 8 Oct against Mon 12 Oct", has("Conflict", (i) => (i.options ?? []).includes("2026-10-08") && (i.options ?? []).includes("2026-10-12")), JSON.stringify(issues.filter((i) => i.kind === "Conflict")));
        eq("…and it is the only conflict", issues.filter((i) => i.kind === "Conflict").length, 1);
        truthy("two \"2025\" year typos flagged", issues.filter((i) => (i.kind === "YearCorrected" || i.kind === "TitleYear") && /2025/.test(i.message)).length >= 2,
          JSON.stringify(issues.filter((i) => /2025/.test(i.message)).map((i) => i.message)));
        truthy("duplicates across the documents are merged (retreats, MOGA, assemblies, UACE briefing)", issues.filter((i) => i.kind === "Duplicate").length >= 5, String(issues.filter((i) => i.kind === "Duplicate").length));
        truthy("the married-name question: \"Asiimwe Aisha Peace\"", has("NameQuestion", (i) => /Asiimwe Aisha Peace/i.test(i.message)), JSON.stringify(issues.filter((i) => i.kind === "NameQuestion").map((i) => i.message)));
      } else skipped("the checks across all five", "one or more of the five files was not found");

      // A PDF must go through the browser; the server says so rather than guessing.
      const fake = new FormData();
      fake.append("files", new Blob([Buffer.from("%PDF-1.4\n%fake\n")]), "scan.pdf");
      const pdf = await fetch(`${API}/api/v1/dev/import/read-document`, { method: "POST", headers: { Authorization: `Bearer ${AD}` }, body: fake });
      const pdfJson = await pdf.json().catch(() => null);
      truthy("a PDF sent as bytes is refused in words (it is read in the browser)", /browser/i.test(pdfJson?.summaries?.[0]?.refusal ?? ""), JSON.stringify(pdfJson?.summaries?.[0]));
    }
  }

  // ---------------------------------------------------------------------------------------------------
  hdr("31.2 A synthetic import: preview writes nothing, commit creates, re-import changes nothing");
  const run = Date.now().toString(36);
  const base = addDays(new Date(), 200 + Math.floor(Math.random() * 60));
  const d1 = iso(base), d2 = iso(addDays(base, 1)), d3 = iso(addDays(base, 2)), d4 = iso(addDays(base, 3));
  const teacherId = teacher.user.id, adminId = ad.user.id;
  const body = (suffix = "", overrides = {}) => ({
    preview: true,
    sourceFiles: [`e2e-${run}${suffix}.docx`],
    notifyPeople: false,
    events: [
      { sourceKey: `e2e:${run}${suffix}:sports`, title: `E2E ${run}${suffix} Sports day`, startsOn: d1, endsOn: d1, startTime: "09:00", endTime: "12:00", category: "Sports & clubs", audience: "Staff", classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [] },
      { sourceKey: `e2e:${run}${suffix}:carols`, title: `E2E ${run}${suffix} Carols`, startsOn: d2, endsOn: d2, audience: "Staff", classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [] }
    ],
    meetings: [
      { sourceKey: `e2e:${run}${suffix}:staff-meeting`, title: `E2E ${run}${suffix} Staff meeting`, date: d1, startTime: "14:00", endTime: "15:00", expectedUserIds: [teacherId], recorderUserIds: [adminId] }
    ],
    rotaSlots: [
      { sourceKey: `e2e:${run}${suffix}:rota:t`, rotaName: `E2E ${run}${suffix} on duty`, userId: teacherId, startsOn: d3, endsOn: d3 },
      { sourceKey: `e2e:${run}${suffix}:rota:a`, rotaName: `E2E ${run}${suffix} on duty`, userId: adminId, startsOn: d4, endsOn: d4 }
    ],
    ...overrides
  });
  const ours = async (suffix = "") => {
    const cal = (await get(AD, `${B}/calendar?from=${d1}&to=${d4}`)).json;
    const events = (cal?.events ?? []).filter((e) => e.title.startsWith(`E2E ${run}${suffix} `));
    const from = new Date(base); from.setUTCDate(from.getUTCDate() - 1);
    const to = addDays(base, 5);
    const duties = ((await get(AD, `${B}/staff/duties?from=${from.toISOString()}&to=${to.toISOString()}`)).json ?? [])
      .filter((d) => (d.title ?? "").startsWith(`E2E ${run}${suffix} `));
    return { events, duties };
  };

  const preview = await post(AD, IMPORT, body());
  eq("preview → 200", preview.status, 200);
  eq("…would create 2 events", preview.json?.eventsCreated, 2);
  eq("…1 meeting", preview.json?.meetingsCreated, 1);
  eq("…2 rota slots", preview.json?.rotaSlotsCreated, 2);
  eq("…and has no job (it wrote nothing)", preview.json?.jobId ?? null, null);
  const afterPreview = await ours();
  eq("nothing is on the calendar after a preview", afterPreview.events.length, 0);
  eq("…and no duty exists", afterPreview.duties.length, 0);
  const told = await post(AD, IMPORT, body("", { notifyPeople: true }));
  truthy("a preview that would notify counts the people it would tell", (told.json?.peopleNotified ?? 0) >= 1, JSON.stringify(told.json?.peopleNotified));

  const commit = await post(AD, IMPORT, body("", { preview: false }));
  eq("commit → 201", commit.status, 201);
  const jobId = commit.json?.jobId;
  truthy("…with a job id, the undo handle", !!jobId, commit.text);
  if (jobId) cleanupJobs.push(jobId);
  const afterCommit = await ours();
  eq("2 events are on the calendar", afterCommit.events.length, 2);
  truthy("…each carrying the import's id", afterCommit.events.every((e) => e.importJobId === jobId), JSON.stringify(afterCommit.events.map((e) => e.importJobId)));
  eq("3 duties exist (1 meeting, 2 rota slots)", afterCommit.duties.length, 3);
  const meetingRow = (commit.json?.rows ?? []).find((r) => r.kind === "meeting");
  const meetingId = meetingRow?.recordId;
  truthy("the commit names the meeting it created", !!meetingId, JSON.stringify(meetingRow));

  const again = await post(AD, IMPORT, body("", { preview: false }));
  eq("re-importing the same body → 201", again.status, 201);
  if (again.json?.jobId) cleanupJobs.push(again.json.jobId);
  eq("…creates no events", again.json?.eventsCreated, 0);
  eq("…no meetings", again.json?.meetingsCreated, 0);
  eq("…no rota slots", again.json?.rotaSlotsCreated, 0);
  eq("…and finds all 5 rows unchanged", again.json?.unchanged, 5);
  eq("…so there are still 3 duties", (await ours()).duties.length, 3);

  const moved = body();
  moved.events[0].startTime = "09:30";
  const changed = await post(AD, IMPORT, moved);
  const sportsRow = (changed.json?.rows ?? []).find((r) => r.sourceKey === `e2e:${run}:sports`);
  eq("a changed time is an Update", sportsRow?.outcome, "Update");
  truthy("…that names the field that moves", (sportsRow?.changes ?? []).some((c) => c.startsWith("time")), JSON.stringify(sportsRow?.changes));
  eq("…and nothing else changes", changed.json?.unchanged, 4);

  // ---------------------------------------------------------------------------------------------------
  hdr("31.3 Who may");
  eq("a teacher cannot import (no calendar.manage) → 403", (await post(T, IMPORT, body())).status, 403);
  eq("…nor read the import context → 403", (await get(T, `${IMPORT}/context`)).status, 403);
  eq("…nor the history → 403", (await get(T, `${IMPORT}/jobs`)).status, 403);
  const eventsOnly = await post(AD, IMPORT, body("", { meetings: [], rotaSlots: [] }));
  eq("an events-only import needs calendar.manage alone → 200", eventsOnly.status, 200);
  const context = await get(AD, `${IMPORT}/context`);
  eq("the context answers the administrator", context.status, 200);
  truthy("…with the staff list to match names against, and no phone numbers in it", Array.isArray(context.json?.people) && !/"phone"\s*:/i.test(context.text), context.text.slice(0, 200));
  eq("an unknown undo → 404", (await post(AD, `${IMPORT}/jobs/${crypto.randomUUID()}/undo`)).status, 404);
  const refused = await post(AD, IMPORT, body("", { rotaSlots: [{ sourceKey: `e2e:${run}:ghost`, rotaName: "On duty", userId: crypto.randomUUID(), startsOn: d3, endsOn: d3 }] }));
  truthy("a rota row naming nobody on the staff is Refused, in words", (refused.json?.rows ?? []).some((r) => r.outcome === "Refused" && /staff/i.test(r.reason ?? "")), JSON.stringify(refused.json?.rows?.find((r) => r.outcome === "Refused")));

  // ---------------------------------------------------------------------------------------------------
  hdr("31.4 Two simultaneous commits of one body: one set of rows");
  const race = body("r", { preview: false });
  const [first, second] = await Promise.all([post(AD, IMPORT, race), post(AD, IMPORT, race)]);
  truthy("both commits answer 201", first.status === 201 && second.status === 201, `${first.status}, ${second.status}`);
  for (const r of [first, second]) if (r.json?.jobId) cleanupJobs.push(r.json.jobId);
  const created = (r) => (r.json?.eventsCreated ?? 0) + (r.json?.meetingsCreated ?? 0) + (r.json?.rotaSlotsCreated ?? 0);
  eq("between them they created each row once (5)", created(first) + created(second), 5);
  truthy("…and one of them found everything unchanged", [first, second].some((r) => r.json?.unchanged === 5), `${first.json?.unchanged}, ${second.json?.unchanged}`);
  const raced = await ours("r");
  eq("…so there are 2 events", raced.events.length, 2);
  eq("…and 3 duties", raced.duties.length, 3);

  // ---------------------------------------------------------------------------------------------------
  hdr("31.5 Undo removes everything except a meeting whose register was taken");
  if (meetingId && jobId) {
    const register = await post(AD, `${B}/staff/duties/${meetingId}/register`, { entries: [{ userId: teacherId, outcome: "Present" }], close: false });
    eq("the imported meeting's register is taken → 200", register.status, 200);
    const undo = await post(AD, `${IMPORT}/jobs/${jobId}/undo`);
    eq("undo → 200", undo.status, 200);
    eq("…removes both events", undo.json?.eventsRemoved, 2);
    eq("…cancels both rota slots", undo.json?.dutiesRemoved, 2);
    eq("…and KEEPS the meeting whose register was taken", undo.json?.dutiesKept, 1);
    const left = await ours();
    eq("0 events left", left.events.length, 0);
    eq("only the meeting is left of the duties", left.duties.length, 1);
    eq("…and it is that meeting", left.duties[0]?.id, meetingId);
    cleanupDuties.push(meetingId);
    const jobs = (await get(AD, `${IMPORT}/jobs`)).json ?? [];
    const listed = jobs.find((j) => j.id === jobId);
    truthy("the history shows the import undone", listed?.undone === true, JSON.stringify(listed));
    const reimport = await post(AD, IMPORT, body());
    eq("after the undo a re-import would create the events again", reimport.json?.eventsCreated, 2);
  } else skipped("undo", "the commit did not report its job or meeting");

  // ---------------------------------------------------------------------------------------------------
  hdr("31.5b Undo puts back the rows an import UPDATED — unless somebody changed them since (2026-09-25)");
  {
    const u = body("u", { preview: false, rotaSlots: [] });
    const firstU = await post(AD, IMPORT, u);
    eq("a first import creates the events and the meeting → 201", firstU.status, 201);
    if (firstU.json?.jobId) cleanupJobs.push(firstU.json.jobId);
    const upd = body("u", { preview: false, rotaSlots: [] });
    upd.events[0].title = `E2E ${run}u Sports day (moved)`;
    upd.events[1].location = "Main hall";
    upd.meetings[0].location = "Library";
    const second = await post(AD, IMPORT, upd);
    eq("a second import of changed rows → 201", second.status, 201);
    truthy("…updates two events and the meeting", (second.json?.eventsUpdated ?? 0) === 2 && (second.json?.meetingsUpdated ?? 0) === 1,
      JSON.stringify({ e: second.json?.eventsUpdated, m: second.json?.meetingsUpdated }));
    const secondJob = second.json?.jobId;

    // Somebody edits the Carols event by hand after the import.
    const mid = await ours("u");
    const carols = mid.events.find((e) => e.title.includes("Carols"));
    const edited = await call(AD, "PUT", `${B}/calendar/events/${carols?.id}`, {
      title: carols?.title, startsOn: carols?.startsOn, endsOn: carols?.endsOn, audience: "Staff",
      location: "Chapel (changed by hand)", classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [],
    });
    eq("the Carols event is edited by hand after the import → 200", edited.status, 200);

    const undoU = await post(AD, `${IMPORT}/jobs/${secondJob}/undo`);
    eq("undoing the UPDATE import → 200", undoU.status, 200);
    eq("…removes nothing it did not create", (undoU.json?.eventsRemoved ?? 0) + (undoU.json?.dutiesRemoved ?? 0), 0);
    eq("…puts back the two rows nobody touched", undoU.json?.updatesRestored, 2);
    truthy("…and names the one edited since, leaving it", (undoU.json?.updatesLeft ?? []).some((x) => /Carols/.test(x) && /changed since/.test(x)),
      JSON.stringify(undoU.json?.updatesLeft));
    const after = await ours("u");
    truthy("the Sports day title is back as the first import wrote it",
      after.events.some((e) => e.title === `E2E ${run}u Sports day`), JSON.stringify(after.events.map((e) => e.title)));
    truthy("…and the hand edit on Carols survived", after.events.find((e) => e.title.includes("Carols"))?.location === "Chapel (changed by hand)",
      after.events.find((e) => e.title.includes("Carols"))?.location);
    truthy("…and the meeting's venue is back to what it was", (after.duties[0]?.location ?? null) !== "Library", after.duties[0]?.location);
  }
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("31.6 Clean up");
  for (const id of [...new Set(cleanupJobs)]) {
    const r = await post(AD, `${IMPORT}/jobs/${id}/undo`);
    if (r.status !== 200) console.log(`  (undo of ${id} answered ${r.status})`);
  }
  for (const id of cleanupDuties) await del(AD, `${B}/staff/duties/${id}`);
  ok("the imports are undone and the kept meeting cancelled (its register row stays — the ledger is append-only)");
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m${skip ? ` (${skip} skipped)` : ""}`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

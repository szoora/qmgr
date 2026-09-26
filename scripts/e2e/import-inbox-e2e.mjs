// SECTION 43 — THE IMPORT INBOX: ROUTED, STAGED, APPROVED BY THE SECTION THAT OWNS IT (2026-09-26).
//
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, E11.
//
//   43.1  sending a document stages it and writes NOTHING; a person who approves no part of it cannot send it;
//   43.2  who sees a staged part: its owner reads it, a teacher gets 404;
//   43.3  a second approver (the school's switch): the uploader cannot approve their own upload, somebody else can;
//   43.4  approving runs the programme import's own commit — the events appear once, the part reads approved, and five
//         approvers racing one part commit it ONCE;
//   43.5  reject with a reason; withdraw by the uploader; a handed-off staff list is completed from its own importer;
//   43.6  everything is put back.
//
// Run: node scripts/e2e/import-inbox-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const TEACHER = process.env.TEACHER || "e2e.teacher.s4@qmgr.local";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
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
const sa = await login(SA_USER, [SA_PASS]);
if (!ad || !teacher || !sa) { console.error("sign-in failed (tenant admin, teacher or SuperAdmin)"); process.exit(1); }
const AD = ad.token, T = teacher.token, SA = sa.token;
const B = `/api/v1/branches/${BRANCH}`;
const INBOX = `${B}/imports`;
const IMPORT = `${B}/calendar/import`;
const run = Date.now().toString(36);
const day = iso(addDays(new Date(), 330 + Math.floor(Math.random() * 30)));
const importJobs = [];
let originalSettings = null;

const programme = (key, n) => ({
  preview: false, sourceFiles: [`e2e-inbox-${run}.docx`], notifyPeople: false, meetings: [], rotaSlots: [],
  events: Array.from({ length: n }, (_, i) => ({ sourceKey: `e2e:${run}:${key}:${i}`, title: `E2E inbox ${run} ${key} ${i}`, startsOn: day, endsOn: day, audience: "Staff", classNames: [], responsibleUserIds: [], responsibleDepartmentIds: [] }))
});
const submit = (token, sections) => post(token, INBOX, { sourceFiles: [`e2e-inbox-${run}.docx`], sections });
const eventsOnCalendar = async (needle) => ((await get(AD, `${B}/calendar?from=${day}&to=${day}&scope=all`)).json?.events ?? []).filter((e) => e.title.includes(needle));
const approve = (token, jobId, section, request) => post(token, IMPORT, { ...request, preview: false, inboxJobId: jobId, inboxSection: section });

try {
  originalSettings = (await get(AD, "/api/v1/imports/settings")).json;
  await put(AD, "/api/v1/imports/settings", { requireSecondApprover: false });

  hdr("43.1 Sending stages; nothing is written");
  const sent = await submit(AD, [{ kind: "events", title: "Calendar events", rowCount: 2, programme: programme("a", 2) }]);
  eq("the calendar keeper sends a document → 201", sent.status, 201);
  const jobA = sent.json?.id;
  truthy("…it waits with one part", sent.json?.sections?.length === 1 && sent.json.sections[0].state === "awaiting", JSON.stringify(sent.json?.sections));
  eq("…and NOTHING is on the calendar", (await eventsOnCalendar(`E2E inbox ${run} a`)).length, 0);
  eq("a teacher who approves nothing cannot send → 400", (await submit(T, [{ kind: "events", title: "x", rowCount: 1, programme: programme("t", 1) }])).status, 400);
  eq("a part routed nowhere → 400", (await submit(AD, [{ kind: "nonsense", title: "x", rowCount: 1 }])).status, 400);

  hdr("43.2 Who sees a staged part");
  eq("its owner reads it → 200", (await get(AD, `${INBOX}/${jobA}/sections/s1`)).status, 200);
  eq("a teacher → 404", (await get(T, `${INBOX}/${jobA}/sections/s1`)).status, 404);
  truthy("the teacher's inbox does not list it", !((await get(T, INBOX)).json ?? []).some((j) => j.id === jobA));

  hdr("43.3 A second approver");
  eq("switch on 'a second person approves' → 200", (await put(AD, "/api/v1/imports/settings", { requireSecondApprover: true })).status, 200);
  const staged = (await get(AD, `${INBOX}/${jobA}/sections/s1`)).json?.programme;
  const self = await approve(AD, jobA, "s1", staged);
  eq("the uploader approving their own upload → 409", self.status, 409);
  truthy("…in words", /second person/i.test(self.text), self.text);
  eq("…and still nothing is on the calendar", (await eventsOnCalendar(`E2E inbox ${run} a`)).length, 0);
  const listed = ((await get(AD, INBOX)).json ?? []).find((j) => j.id === jobA);
  truthy("the inbox tells the uploader why", listed?.sections?.[0]?.canDecide === false && /second person/i.test(listed?.sections?.[0]?.whyNot ?? ""), JSON.stringify(listed?.sections?.[0]));

  hdr("43.4 Approving is the importer's own commit — once");
  const racing = await Promise.all(Array.from({ length: 5 }, () => approve(SA, jobA, "s1", staged)));
  racing.forEach((r) => r.json?.jobId && importJobs.push(r.json.jobId));
  eq("five approvers racing: exactly one commit → 201", racing.filter((r) => r.status === 201).length, 1);
  truthy("…the rest are told it is already approved (409) or not waiting (404)", racing.filter((r) => r.status !== 201).every((r) => r.status === 409 || r.status === 404), racing.map((r) => r.status).join(","));
  eq("the two events are on the calendar ONCE", (await eventsOnCalendar(`E2E inbox ${run} a`)).length, 2);
  const after = ((await get(AD, INBOX)).json ?? []).find((j) => j.id === jobA);
  eq("…the part reads approved", after?.sections?.[0]?.state, "approved");
  truthy("…with the programme import's own job as its result (the undo handle)", !!after?.sections?.[0]?.resultJobId);
  eq("…and the document is done", after?.done, true);
  eq("approving it again → 409 (already approved)", (await approve(SA, jobA, "s1", staged)).status, 409);

  hdr("43.5 Reject, withdraw, and a handed-off list");
  const second = await submit(AD, [
    { kind: "events", title: "Calendar events", rowCount: 1, programme: programme("b", 1) },
    { kind: "staff", title: "Staff list", rowCount: 1, csv: "Name,Email\r\nE2E Person,e2e.person@qmgr.local", fileName: "staff.csv" }
  ]);
  const jobB = second.json?.id;
  eq("a document in two parts → 201", second.status, 201);
  const rej = await post(SA, `${INBOX}/${jobB}/sections/s1/reject`, { note: `Wrong term ${run}` });
  eq("another approver rejects the events → 200", rej.status, 200);
  const rejected = rej.json?.sections?.find((s) => s.key === "s1");
  eq("…with the reason kept", `${rejected?.state}|${rejected?.note}`, `rejected|Wrong term ${run}`);
  eq("…and nothing of it is written", (await eventsOnCalendar(`E2E inbox ${run} b`)).length, 0);
  eq("completing an EVENTS part from outside the importer → 400", (await post(SA, `${INBOX}/${jobB}/sections/s1/complete`, {})).status, 400);
  const staffCsv = await get(SA, `${INBOX}/${jobB}/sections/s2`);
  eq("the staff list's owner reads its table", staffCsv.json?.csv?.startsWith("Name,Email"), true);
  eq("…and marks it done once imported → 200", (await post(SA, `${INBOX}/${jobB}/sections/s2/complete`, { note: "imported" })).status, 200);
  const third = await submit(AD, [{ kind: "events", title: "Calendar events", rowCount: 1, programme: programme("c", 1) }]);
  eq("the uploader may WITHDRAW their own upload, even with four eyes on → 200", (await post(AD, `${INBOX}/${third.json?.id}/sections/s1/reject`, { note: "sent by mistake" })).status, 200);
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("43.6 Put things back");
  for (const id of importJobs) await post(AD, `${IMPORT}/jobs/${id}/undo`);
  const left = await eventsOnCalendar(`E2E inbox ${run}`);
  for (const e of left) await del(AD, `${B}/calendar/events/${e.id}`);
  eq("no event of this run is left", (await eventsOnCalendar(`E2E inbox ${run}`)).length, 0);
  if (originalSettings) eq("the approval rule is put back", (await put(AD, "/api/v1/imports/settings", originalSettings)).status, 200);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

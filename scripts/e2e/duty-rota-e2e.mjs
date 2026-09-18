#!/usr/bin/env node
// =====================================================================================================
// e2e section 15 — the duty rota plan (docs/plans/DUTY_ROTA_AND_TIMETABLE.md), built phase by phase.
// Run on its own:
//   API=http://127.0.0.1:5001 BRANCH=<branch guid> node scripts/e2e/duty-rota-e2e.mjs
// Uses the seeded Staff Performance accounts (section 14 creates them). Everything it writes is labelled
// "E2E <run>"; future rota slots it creates are cancelled at the end, closed-out ones stay (records are history).
// =====================================================================================================

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH;
const PW = "E2eTeacher!2026";
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

async function call(token, method, path, body) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload;
  if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch(API + path, { method, headers, body: payload });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json, text };
}
const get = (t, p) => call(t, "GET", p);
const post = (t, p, b) => call(t, "POST", p, b ?? {});
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
const del = (t, p, b) => call(t, "DELETE", p, b);
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await call(null, "POST", "/api/v1/auth/login", { email: id, password: pw });
    if (r.json?.accessToken) return r.json.accessToken;
  }
  return null;
}
const iso = (d) => new Date(d).toISOString();
const hoursFromNow = (h) => iso(Date.now() + h * 3600_000);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const waitFor = async (fn, ms = 20000) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(750); } return v; };
const trigger = async (id) => (await fetch(`${API}/hangfire/recurring/trigger`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: `jobs%5B%5D=${encodeURIComponent(id)}` })).status;
// A trigger only ENQUEUES: the sweep runs when a Hangfire worker is free, so "triggered" is never
// "finished". Measured on this API during a run of this suite: every recurring job stalled for 80
// seconds while the run's publishes, materialisations and dispatches held the pool, and the triggered
// sweeps reported total durations of 48s to 1m23s. A fixed 20-30s poll after one trigger is therefore a
// coin toss, and the failures it produces read exactly like product bugs. So: trigger, poll for the
// OUTCOME, and re-trigger while waiting — every sweep in this module CLAIMS its row with a conditional
// update before it sends, so an extra run can never send twice what one run already sent.
const sweepFor = async (jobId, fn, ms = 180_000) => {
  const end = Date.now() + ms;
  let v = await fn();
  while (!v && Date.now() < end) {
    await trigger(jobId);
    v = await waitFor(fn, Math.max(2000, Math.min(25_000, end - Date.now())));
  }
  return v;
};
const count = async (token, key, needle) => ((await get(token, `/api/v1/notifications?eventKey=${key}&limit=100`)).json ?? []).filter((n) => (n.title + " " + n.message).includes(needle)).length;

// ---------------------------------------------------------------------------------------------------
hdr("15.0 Sign in");
const AD = await login("e2e.admin.ct@qmgr.local");
truthy("tenant admin signs in", !!AD);
const users = (await get(AD, "/api/v1/users?pageSize=500")).json;
const list = Array.isArray(users) ? users : users?.items ?? [];
const U = {};
for (const [key, username] of [["math1", "e2e.sp.math1"], ["math2", "e2e.sp.math2"], ["lang1", "e2e.sp.lang1"], ["hod", "e2e.sp.hod.math"], ["support", "e2e.sp.support"], ["dos", "e2e.sp.dos"]]) {
  const u = list.find((x) => x.username === username);
  U[key] = { id: u?.id, name: `${u?.firstName ?? ""} ${u?.lastName ?? ""}`.trim(), token: u ? await login(`${username}@qmgr.local`) : null };
}
truthy("the seeded staff accounts exist and sign in (run section 14 first on a fresh tenant)", Object.values(U).every((u) => u.id && u.token), JSON.stringify(Object.fromEntries(Object.entries(U).map(([k, v]) => [k, !!v.token]))));
if (fail) { console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`); process.exit(fail); }

const cleanupSeries = [];
const cleanupDuties = [];
const rotaIn = async (token, fromH, toH) => (await get(token, `${B}/staff/duties?kind=Rota&from=${encodeURIComponent(hoursFromNow(fromH))}&to=${encodeURIComponent(hoursFromNow(toH))}`)).json ?? [];

// ---------------------------------------------------------------------------------------------------
hdr("15.1 ROTA — kinds, the seeded parameter, generate with preview, extend");
const defaults = await get(AD, `${B}/staff/rota/defaults`);
eq("the rota defaults answer for a duties manager", defaults.status, 200);
const params = (await get(AD, "/api/v1/staff/parameters")).json ?? [];
const tod = params.find((p) => p.id === defaults.json?.parameterId);
truthy("the \"Teacher on Duty\" Duty parameter is seeded (by name, once)", tod?.name === "Teacher on Duty" && tod?.kind === "Duty" && params.filter((p) => p.name === "Teacher on Duty").length === 1, JSON.stringify(tod));
eq("a teacher cannot generate a rota (403)", (await post(U.math1.token, `${B}/staff/rota/generate`, {})).status, 403);

const baseSlot = (o) => ({ parameterId: tod.id, title: `E2E ${RUN} slot`, startsAt: hoursFromNow(200), endsAt: hoursFromNow(210), kind: "Rota", expectedUserIds: [U.math1.id], supervisorUserIds: [U.hod.id], recorderUserIds: [], reportCadence: "None", ...o });
eq("KIND: a lesson cannot be created by hand (400)", (await post(AD, `${B}/staff/duties`, baseSlot({ kind: "Lesson" }))).status, 400);
eq("KIND: a rota slot must name its people (400)", (await post(AD, `${B}/staff/duties`, baseSlot({ expectedUserIds: null }))).status, 400);
eq("KIND: a rota slot is at most 92 days (400)", (await post(AD, `${B}/staff/duties`, baseSlot({ endsAt: hoursFromNow(200 + 24 * 93) }))).status, 400);
eq("KIND: a session cannot have administrators on duty (400)", (await post(AD, `${B}/staff/duties`, baseSlot({ kind: "Session" }))).status, 400);
const longSlot = await post(AD, `${B}/staff/duties`, baseSlot({ title: `E2E ${RUN} month slot`, endsAt: hoursFromNow(200 + 24 * 30), reportCadence: null }));
eq("KIND: a 30-day rota slot is accepted (sessions stop at 14)", longSlot.status, 201);
if (longSlot.json?.id) cleanupDuties.push(longSlot.json.id);
truthy("…with the supervisor as its recorder, and a monthly report cadence by default", longSlot.json?.recorderUserIds?.includes(U.hod.id) && longSlot.json?.reportCadence === "Monthly", JSON.stringify({ r: longSlot.json?.recorderUserIds, c: longSlot.json?.reportCadence }));
eq("KIND: a rota slot cannot be turned into a session (400)", (await put(AD, `${B}/staff/duties/${longSlot.json?.id}`, baseSlot({ kind: "Session", supervisorUserIds: [] }))).status, 400);

// Generate: three weekly slots starting on a Monday about ten weeks out, rotating three teachers.
const start = new Date(Date.now() + 70 * 86400_000);
start.setUTCDate(start.getUTCDate() + ((8 - start.getUTCDay()) % 7));
const startDate = start.toISOString().slice(0, 10);
const genBody = (o) => ({ parameterId: tod.id, title: `E2E ${RUN} Teacher on duty`, span: "Week", startDate, slots: 3, dayStartLocalTime: "07:00", dayEndLocalTime: "18:00", staffUserIds: [U.math1.id, U.math2.id, U.lang1.id], peoplePerSlot: 1, supervisorUserIds: [U.hod.id], supervisorsPerSlot: 1, skipWeekends: false, skipHolidays: false, preview: true, ...o });
const before = (await rotaIn(AD, 24 * 60, 24 * 100)).length;
const preview = await post(AD, `${B}/staff/rota/generate`, genBody());
eq("PREVIEW: answers 200", preview.status, 200);
truthy("PREVIEW: three weekly slots, rotating the three teachers in order", preview.json?.slots?.length === 3 && preview.json.slots.map((s) => s.userIds[0]).join() === [U.math1.id, U.math2.id, U.lang1.id].join(), JSON.stringify(preview.json?.slots?.map((s) => s.names)));
truthy("PREVIEW: each slot runs Monday to Sunday", preview.json?.slots?.every((s) => Math.round((new Date(s.endsAt) - new Date(s.startsAt)) / 3600_000) === 6 * 24 + 11), JSON.stringify(preview.json?.slots?.map((s) => [s.startsAt, s.endsAt])));
eq("PREVIEW: weekly reports by default for a week-long slot", preview.json?.reportCadence, "Weekly");
eq("PREVIEW: writes nothing", (await rotaIn(AD, 24 * 60, 24 * 100)).length, before);
eq("an unknown person is refused (400)", (await post(AD, `${B}/staff/rota/generate`, genBody({ staffUserIds: ["00000000-0000-0000-0000-000000000001"] }))).status, 400);

const made = await post(AD, `${B}/staff/rota/generate`, genBody({ preview: false }));
eq("GENERATE: 201", made.status, 201);
const series = made.json?.seriesId;
if (series) cleanupSeries.push(series);
const written = (await rotaIn(AD, 24 * 60, 24 * 100)).filter((d) => d.seriesId === series).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
truthy("GENERATE: three Rota duties share the series id", written.length === 3 && written.every((d) => d.kind === "Rota"), JSON.stringify(written.map((d) => d.expectedNames)));
truthy("GENERATE: each names its person and supervisor, and the supervisor records it", written.every((d) => d.expectedUserIds.length === 1 && d.supervisorUserIds[0] === U.hod.id && d.recorderUserIds.includes(U.hod.id)));
truthy("NOTICE: the first teacher is told they are on the rota (staff.rota-assigned)", !!(await waitFor(async () => (await count(U.math1.token, "staff.rota-assigned", `E2E ${RUN} Teacher on duty`)) >= 1)));

const clash = await post(AD, `${B}/staff/rota/generate`, genBody({ title: `E2E ${RUN} clash`, slots: 1, staffUserIds: [U.math1.id] }));
truthy("CHECK: generating over an existing slot warns OverlappingRota (never refuses)", clash.status === 200 && clash.json?.slots?.[0]?.warnings?.some((w) => w.kind === "OverlappingRota"), JSON.stringify(clash.json?.slots?.[0]?.warnings));
const pol = (await get(AD, "/api/v1/staff/policy")).json;
const fair = await post(AD, `${B}/staff/rota/generate`, genBody({ title: `E2E ${RUN} fairness`, span: "Day", slots: (pol?.dutyReportDefaults?.maxRotaSlotsPerTerm ?? 6) + 2, staffUserIds: [U.support.id], supervisorUserIds: [] }));
truthy("CHECK: one person on more slots than the term's limit warns OverFairnessLimit", fair.json?.slots?.some((s) => s.warnings.some((w) => w.kind === "OverFairnessLimit")), JSON.stringify(fair.json?.slots?.map((s) => s.warnings.map((w) => w.kind))));
const check = await post(AD, `${B}/staff/rota/check`, { startsAt: written[0]?.startsAt, endsAt: written[0]?.endsAt, userIds: [U.math1.id], supervisorUserIds: [U.math1.id] });
truthy("CHECK endpoint: the same person on duty and supervising, and an overlap, both warned", check.json?.some((w) => w.kind === "SupervisesSelf") && check.json?.some((w) => w.kind === "OverlappingRota"), JSON.stringify(check.json));

const ext = await post(AD, `${B}/staff/rota/series/${series}/extend`, { slots: 2 });
eq("EXTEND: 201", ext.status, 201);
truthy("EXTEND: continues the rotation after the last person (teacher 1, then teacher 2)", ext.json?.slots?.map((s) => s.userIds[0]).join() === [U.math1.id, U.math2.id].join(), JSON.stringify(ext.json?.slots?.map((s) => s.names)));
const afterExt = (await rotaIn(AD, 24 * 60, 24 * 120)).filter((d) => d.seriesId === series).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
truthy("EXTEND: the series now has five slots, the fourth starting a week after the third", afterExt.length === 5 && (new Date(afterExt[3].startsAt) - new Date(afterExt[2].startsAt)) === 7 * 86400_000, JSON.stringify(afterExt.map((d) => d.startsAt)));

const fairness = await get(AD, `${B}/staff/rota/fairness?period=${encodeURIComponent(pol?.periods?.length ? "" : "")}`);
eq("FAIRNESS: answers 200 for a duties manager", fairness.status, 200);
eq("FAIRNESS: a teacher without the permissions is refused (403)", (await get(U.math1.token, `${B}/staff/rota/fairness`)).status, 403);

// ---------------------------------------------------------------------------------------------------
hdr("15.2 ROTA — visibility, acknowledgement, reschedule, swap, cancel");
const slot1 = afterExt[0];
const listed = (await rotaIn(U.support.token, 24 * 60, 24 * 120)).find((d) => d.id === slot1?.id);
truthy("VISIBLE: support staff not on it can read the displayed rota (names and dates)", listed?.expectedNames?.length === 1, JSON.stringify(listed?.expectedNames));
truthy("…but not who has acknowledged (Acknowledgements withheld)", listed && listed.acknowledgements == null);
truthy("the supervisor does see the acknowledgement map", ((await rotaIn(U.hod.token, 24 * 60, 24 * 120)).find((d) => d.id === slot1?.id))?.acknowledgements != null);
eq("ACK: a colleague cannot acknowledge someone else's slot (404)", (await post(U.lang1.token, `${B}/staff/duties/${slot1?.id}/acknowledge`)).status, 404);
const acks = await Promise.all(Array.from({ length: 4 }, () => post(U.math1.token, `${B}/staff/duties/${slot1?.id}/acknowledge`)));
eq("RACE: four simultaneous acknowledgements produce no 500", acks.filter((a) => a.status >= 500).length, 0);
const ackStamps = new Set(acks.map((a) => a.json?.myAcknowledgedAt).filter(Boolean));
truthy("RACE: …and one timestamp stands for all of them (the first)", ackStamps.size === 1, JSON.stringify([...ackStamps]));
eq("ACK: the slot counts one acknowledgement", ((await rotaIn(AD, 24 * 60, 24 * 120)).find((d) => d.id === slot1?.id))?.acknowledgedCount, 1);
const moved = await put(AD, `${B}/staff/duties/${slot1?.id}`, { parameterId: tod.id, title: slot1?.title, startsAt: iso(new Date(slot1?.startsAt).getTime() + 86400_000), endsAt: iso(new Date(slot1?.endsAt).getTime() + 86400_000), kind: "Rota", expectedUserIds: slot1?.expectedUserIds, supervisorUserIds: slot1?.supervisorUserIds, recorderUserIds: [] });
eq("RESCHEDULE: moving the slot saves", moved.status, 200);
truthy("RESCHEDULE: …and clears the acknowledgements (\"seen\" was for the old dates)", moved.json?.acknowledgedCount === 0 && moved.json?.acknowledgements && Object.keys(moved.json.acknowledgements).length === 0, JSON.stringify(moved.json?.acknowledgements));

const s1 = afterExt[1], s2 = afterExt[2];
eq("SWAP: a teacher cannot swap (403)", (await post(U.math2.token, `${B}/staff/rota/swap`, {})).status, 403);
eq("SWAP: a person must be on the slot they swap out of (400)", (await post(AD, `${B}/staff/rota/swap`, { firstDutyId: s1.id, firstUserId: U.lang1.id, secondDutyId: s2.id, secondUserId: U.math2.id })).status, 400);
const sw = await post(AD, `${B}/staff/rota/swap`, { firstDutyId: s1.id, firstUserId: s1.expectedUserIds[0], secondDutyId: s2.id, secondUserId: s2.expectedUserIds[0], reason: `E2E ${RUN}` });
eq("SWAP: 200", sw.status, 200);
const afterSwap = await rotaIn(AD, 24 * 60, 24 * 120);
truthy("SWAP: each person is now on the other's slot", afterSwap.find((d) => d.id === s1.id)?.expectedUserIds[0] === s2.expectedUserIds[0] && afterSwap.find((d) => d.id === s2.id)?.expectedUserIds[0] === s1.expectedUserIds[0]);
const swapLog = (await get(AD, `${B}/staff/activity?action=staff.rota.swapped&pageSize=10`)).json;
truthy("SWAP: one activity event records the exchange", (swapLog?.items ?? []).some((e) => e.summary.includes("Rota swap")), JSON.stringify((swapLog?.items ?? []).slice(0, 1)));

eq("SESSIONS: the sessions list excludes rota slots", ((await get(AD, `${B}/staff/duties?kind=Session&from=${encodeURIComponent(hoursFromNow(24 * 60))}&to=${encodeURIComponent(hoursFromNow(24 * 120))}`)).json ?? []).filter((d) => d.seriesId === series).length, 0);

// ---------------------------------------------------------------------------------------------------
hdr("15.3 ROTA — the pre-duty ladder, the supervisor's to-do, and close-out");
const probe = await trigger("staff-activity-attribution-purge");
if (probe >= 300 && probe !== 204) {
  console.log(`  \x1b[33mSKIP\x1b[0m  Hangfire dashboard refused a trigger (${probe}); the ladder is asserted only against a Development API`);
} else {
  const polNow = (await get(AD, "/api/v1/staff/policy")).json;
  await put(AD, "/api/v1/staff/policy", { ...polNow, quietHours: { ...(polNow.quietHours ?? {}), enabled: false } });
  try {
    // Tomorrow: the collapsed stage is "1 day before", a bell to the person, nothing to the supervisor yet.
    const soonTitle = `E2E ${RUN} Tomorrow duty`;
    const soon = await post(AD, `${B}/staff/duties`, baseSlot({ title: soonTitle, startsAt: hoursFromNow(20), endsAt: hoursFromNow(30), expectedUserIds: [U.math2.id] }));
    if (soon.json?.id) cleanupDuties.push(soon.json.id);
    // Ninety minutes: collapses straight to the final, interruptive stage, which also tells the supervisor.
    const nowTitle = `E2E ${RUN} Imminent duty`;
    const imminent = await post(AD, `${B}/staff/duties`, baseSlot({ title: nowTitle, startsAt: hoursFromNow(1.5), endsAt: hoursFromNow(10), expectedUserIds: [U.lang1.id] }));
    if (imminent.json?.id) cleanupDuties.push(imminent.json.id);

    truthy("LADDER: the person on tomorrow's duty is reminded", !!(await sweepFor("staff-reminder-ladder", async () => (await count(U.math2.token, "staff.rota-reminder", soonTitle)) >= 1)));
    truthy("LADDER: the imminent duty's person is reminded", !!(await sweepFor("staff-reminder-ladder", async () => (await count(U.lang1.token, "staff.rota-reminder", nowTitle)) >= 1)));
    truthy("LADDER: the final stage tells the supervisor, by count and without names", !!(await sweepFor("staff-reminder-ladder", async () => {
      const n = ((await get(U.hod.token, "/api/v1/notifications?eventKey=staff.rota-unacknowledged&limit=50")).json ?? []).find((x) => x.title.includes(nowTitle));
      return n && /1 person has not acknowledged/.test(n.message) && !n.message.includes(U.lang1.name.split(" ")[0]);
    })));
    await trigger("staff-reminder-ladder");
    await sleep(6000);
    eq("LADDER: a second sweep sends nothing again (exactly one reminder)", await count(U.math2.token, "staff.rota-reminder", soonTitle), 1);
    eq("LADDER: …for the imminent duty too", await count(U.lang1.token, "staff.rota-reminder", nowTitle), 1);
    const hodPortal = (await get(U.hod.token, "/api/v1/staff/portal")).json;
    truthy("TO-DO: the supervisor's portal names who has not acknowledged", (hodPortal?.openItems ?? []).some((i) => i.kind === "rota-unacknowledged" && i.title.includes(nowTitle) && i.title.includes(U.lang1.name)), JSON.stringify((hodPortal?.openItems ?? []).filter((i) => i.kind === "rota-unacknowledged")));
    const langPortal = (await get(U.lang1.token, "/api/v1/staff/portal")).json;
    truthy("PORTAL: the person's On duty card carries the slot, not yet acknowledged", (langPortal?.onDuty ?? []).some((d) => d.id === imminent.json?.id && d.myAcknowledgedAt == null), JSON.stringify((langPortal?.onDuty ?? []).map((d) => d.title)));
    truthy("PORTAL: …and Coming up no longer repeats rota slots", !(langPortal?.comingUp ?? []).some((d) => d.kind === "Rota"));
    await post(U.lang1.token, `${B}/staff/duties/${imminent.json?.id}/acknowledge`);
    const hodAfter = (await get(U.hod.token, "/api/v1/staff/portal")).json;
    truthy("TO-DO: acknowledging clears the supervisor's line", !(hodAfter?.openItems ?? []).some((i) => i.kind === "rota-unacknowledged" && i.title.includes(nowTitle)));
  } finally {
    await put(AD, "/api/v1/staff/policy", polNow);
  }
}

// Close-out: a slot that has ended, taken by its supervisor.
const pastTitle = `E2E ${RUN} Finished duty`;
const past = await post(AD, `${B}/staff/duties`, baseSlot({ title: pastTitle, startsAt: hoursFromNow(-30), endsAt: hoursFromNow(-2), expectedUserIds: [U.math1.id] }));
eq("a rota slot in the past can be created (for close-out)", past.status, 201);
const reg = `${B}/staff/duties/${past.json?.id}/register`;
eq("CLOSE-OUT: the person on duty cannot close out their own slot (404)", (await post(U.math1.token, reg, { close: true, entries: [{ userId: U.math1.id, outcome: "Completed" }] })).status, 404);
eq("CLOSE-OUT: Present is not a rota outcome (400)", (await post(U.hod.token, reg, { close: true, entries: [{ userId: U.math1.id, outcome: "Present" }] })).status, 400);
eq("CLOSE-OUT: Not completed without a reason is refused (400)", (await post(U.hod.token, reg, { close: true, entries: [{ userId: U.math1.id, outcome: "NotCompleted" }] })).status, 400);
const closed = await post(U.hod.token, reg, { close: true, entries: [{ userId: U.math1.id, outcome: "Completed", note: `E2E ${RUN}` }] });
eq("CLOSE-OUT: the supervisor records Completed and closes (200)", closed.status, 200);
const rec = ((await get(AD, `${B}/staff/records?subjectUserId=${U.math1.id}&parameterId=${tod.id}&pageSize=50&status=Final`)).json?.items ?? []).find((r) => r.dutyId === past.json?.id);
truthy("CLOSE-OUT: one Final record on Teacher on Duty, Completed, with its points", rec?.outcome === "Completed" && rec?.parameterName === "Teacher on Duty" && rec?.points === 2, JSON.stringify(rec));

// ---------------------------------------------------------------------------------------------------
hdr("15.4 DUTY REPORTS — rows, write, submit, who reads, review, return, evidence, the overdue ladder");
{
  // A daily-report slot that began three days ago and runs to tomorrow: four started periods, the oldest two long overdue.
  const day = 24;
  const repSlot = await post(AD, `${B}/staff/duties`, baseSlot({ title: `E2E ${RUN} Reported duty`, startsAt: hoursFromNow(-3 * day), endsAt: hoursFromNow(day), expectedUserIds: [U.math1.id, U.math2.id], supervisorUserIds: [U.hod.id], reportCadence: "Daily", reportDueLocalTime: "18:00" }));
  eq("a daily-report rota slot is created", repSlot.status, 201);
  if (repSlot.json?.id) cleanupDuties.push(repSlot.json.id);
  const SLOT = repSlot.json?.id;
  const mine = async (u) => ((await get(u.token, `${B}/staff/duty-reports/mine`)).json ?? []).filter((r) => r.dutyId === SLOT).sort((a, b) => a.periodStart.localeCompare(b.periodStart));
  const m1 = await mine(U.math1);
  truthy("ROWS: the person on duty has one report per started day (4)", m1.length === 4 && m1.every((r) => r.authorRole === "OnDuty"), JSON.stringify(m1.map((r) => [r.periodStart, r.status])));
  const hodRows = await mine(U.hod);
  truthy("ROWS: the administrator on duty writes their own, as Supervisor", hodRows.length === 4 && hodRows.every((r) => r.authorRole === "Supervisor"), JSON.stringify(hodRows.map((r) => r.authorRole)));
  truthy("ROWS: the two oldest are overdue", m1[0]?.isOverdue && m1[1]?.isOverdue);

  const R = m1[0]?.id, R2 = m1[3]?.id;
  const url = (id) => `${B}/staff/duty-reports/${id}`;
  const open = await get(U.math1.token, url(R));
  truthy("the author opens their report with the template and may edit", open.status === 200 && open.json?.canEdit === true && open.json?.template?.length >= 5, `${open.status} ${JSON.stringify(open.json?.template?.map((t) => t.key))}`);
  eq("DRAFT: a colleague on the same slot cannot read it (404)", (await get(U.math2.token, url(R))).status, 404);
  eq("DRAFT: even the supervisor cannot read a draft's text (404)", (await get(U.hod.token, url(R))).status, 404);

  eq("SAVE: an unknown section is refused (400)", (await put(U.math1.token, url(R), { sections: { nope: "x" } })).status, 400);
  eq("SAVE: a choice outside the list is refused (400)", (await put(U.math1.token, url(R), { sections: { cleanliness: "Sparkling" } })).status, 400);
  const welfareList = (await get(AD, `${B}/welfare-records?pageSize=5`)).json;
  const foreignRecord = (welfareList?.items ?? welfareList ?? []).find?.((w) => w.id);
  if (foreignRecord) eq("SAVE: linking a welfare record the author cannot see is refused (400)", (await put(U.math1.token, url(R), { sections: {}, linkedWelfareRecordIds: [foreignRecord.id] })).status, 400);
  eq("SUBMIT: the required section is enforced (400)", (await post(U.math1.token, `${url(R)}/submit`, { sections: { meals: `E2E ${RUN} lunch on time` } })).status, 400);
  const secret = `E2E ${RUN} SECRET-TEXT arrival late`;
  const sub = await post(U.math1.token, `${url(R)}/submit`, { sections: { arrival: secret, cleanliness: "Good" }, summary: `E2E ${RUN} summary` });
  eq("SUBMIT: with the required section answered (200)", sub.status, 200);
  eq("SUBMIT: …status Submitted, and late (it was due days ago)", `${sub.json?.status}/${sub.json?.submittedLate}`, "Submitted/true");
  eq("APPEND-ONLY: a submitted report cannot be edited (400)", (await put(U.math1.token, url(R), { sections: { arrival: "changed" } })).status, 400);
  eq("SUBMIT: a second submit is refused (409)", (await post(U.math1.token, `${url(R)}/submit`, {})).status, 409);

  const races = await Promise.all(Array.from({ length: 3 }, () => post(U.math1.token, `${url(R2)}/submit`, { sections: { arrival: `E2E ${RUN} race` } })));
  eq("RACE: three simultaneous submits of one report → exactly one 200", races.filter((r) => r.status === 200).length, 1);
  eq("RACE: …and no 500", races.filter((r) => r.status >= 500).length, 0);

  eq("READ: a colleague on the same slot still cannot read the submitted report (404)", (await get(U.math2.token, url(R))).status, 404);
  eq("READ: an unrelated teacher cannot (404)", (await get(U.lang1.token, url(R))).status, 404);
  const supRead = await get(U.hod.token, url(R));
  truthy("READ: the supervisor reads it, and may comment and review", supRead.status === 200 && supRead.json?.canComment && supRead.json?.canReview, `${supRead.status} ${JSON.stringify({ c: supRead.json?.canComment, r: supRead.json?.canReview })}`);
  eq("READ: the Director of Studies (reports view) reads it", (await get(U.dos.token, url(R))).status, 200);
  eq("REVIEW: the author cannot mark their own report reviewed (403)", (await post(U.math1.token, `${url(R)}/review`, {})).status, 403);

  const hodR = hodRows[0]?.id;
  await post(U.hod.token, `${B}/staff/duty-reports/${hodR}/submit`, { sections: { arrival: `E2E ${RUN} supervised the arrival` } });
  eq("SUPERVISOR REPORT: the teacher on duty cannot read the supervisor's report about them (404)", (await get(U.math1.token, `${B}/staff/duty-reports/${hodR}`)).status, 404);
  eq("REVIEW: a supervisor cannot review their own report (403)", (await post(U.hod.token, `${B}/staff/duty-reports/${hodR}/review`, {})).status, 403);

  eq("COMMENT: a colleague cannot comment (404)", (await post(U.math2.token, `${url(R)}/notes`, { body: "x" })).status, 404);
  eq("COMMENT: the supervisor comments (200)", (await post(U.hod.token, `${url(R)}/notes`, { body: `E2E ${RUN} COMMENT-WORDS please add times` })).status, 200);
  const commentNote = await waitFor(async () => ((await get(U.math1.token, "/api/v1/notifications?eventKey=staff.duty-report-comment&limit=50")).json ?? []).find((n) => n.message.includes(`E2E ${RUN} Reported duty`)));
  truthy("CONTENT-FREE: the author is told of the comment without its words or the report's text", !!commentNote && !commentNote.message.includes("COMMENT-WORDS") && !commentNote.message.includes("SECRET-TEXT") && !commentNote.title.includes("COMMENT-WORDS"), JSON.stringify(commentNote));
  eq("RESPOND: the author responds (200)", (await post(U.math1.token, `${url(R)}/notes`, { body: `E2E ${RUN} added the times` })).status, 200);

  const ret = await post(U.hod.token, `${url(R)}/return`, { body: `E2E ${RUN} RETURN-WORDS needs the roll call` });
  eq("RETURN: the supervisor returns it for changes (200, Returned)", `${ret.status}/${ret.json?.status}`, "200/Returned");
  truthy("RETURN: the returned version is kept in the note", (ret.json?.notes ?? []).some((n) => n.kind === "Return" && n.snapshotSections?.arrival === secret), JSON.stringify((ret.json?.notes ?? []).map((n) => [n.kind, n.snapshotSections])));
  const retNote = await waitFor(async () => ((await get(U.math1.token, "/api/v1/notifications?eventKey=staff.duty-report-returned&limit=50")).json ?? []).find((n) => n.message.includes(`E2E ${RUN} Reported duty`)));
  truthy("CONTENT-FREE: the return notice carries no reason and no report text", !!retNote && !retNote.message.includes("RETURN-WORDS") && !retNote.message.includes("SECRET-TEXT"), JSON.stringify(retNote));
  const resub = await post(U.math1.token, `${url(R)}/submit`, { sections: { arrival: `${secret} (roll call 07:05)` } });
  eq("RESUBMIT: the author corrects and resubmits (200)", `${resub.status}/${resub.json?.status}`, "200/Submitted");
  const rev = await post(U.hod.token, `${url(R)}/review`, { body: "Thank you" });
  eq("REVIEW: the supervisor marks it reviewed", `${rev.status}/${rev.json?.status}`, "200/Reviewed");

  // Evidence on today's draft of the other teacher (math2 still has open drafts).
  const m2 = await mine(U.math2);
  const E = m2[3]?.id;
  const png = Uint8Array.from(Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64"));
  const fd = new FormData();
  fd.append("file", new Blob([png], { type: "image/png" }), "e2e-evidence.png");
  const up = await fetch(`${API}${B}/staff/duty-reports/${E}/attachments`, { method: "POST", headers: { Authorization: `Bearer ${U.math2.token}` }, body: fd });
  const upJson = await up.json().catch(() => null);
  eq("EVIDENCE: the author uploads evidence to their draft (201)", up.status, 201);
  const fileUrl = upJson?.fileUrl ?? "";
  const path = fileUrl.replace(/^https?:\/\/[^/]+/, "").split("?")[0];
  await post(U.math2.token, `${B}/staff/duty-reports/${E}/submit`, { sections: { arrival: `E2E ${RUN} evidence day` } });
  const fetchAs = async (token) => (await fetch(`${API}${path}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} })).status;
  eq("EVIDENCE: the author reads it (200)", await fetchAs(U.math2.token), 200);
  eq("EVIDENCE: the supervisor reads it (200)", await fetchAs(U.hod.token), 200);
  eq("EVIDENCE: a colleague on the same slot gets 404", await fetchAs(U.math1.token), 404);
  eq("EVIDENCE: anonymous gets 401", await fetchAs(null), 401);

  const queue = await get(U.dos.token, `${B}/staff/duty-reports?dutyId=${SLOT}&from=${encodeURIComponent(hoursFromNow(-5 * day))}&to=${encodeURIComponent(hoursFromNow(2 * day))}`);
  truthy("QUEUE: the Director of Studies sees the slot's reports with the figures", queue.status === 200 && queue.json?.items?.some((i) => i.id === R) && typeof queue.json?.awaitingReview === "number" && queue.json?.overdue >= 1, `${queue.status} ${JSON.stringify({ n: queue.json?.items?.length, o: queue.json?.overdue, a: queue.json?.awaitingReview, t: queue.json?.onTimePercent })}`);
  eq("QUEUE: a teacher who supervises nothing is refused (403)", (await get(U.lang1.token, `${B}/staff/duty-reports`)).status, 403);

  const nd = await post(U.hod.token, `${B}/staff/duty-reports/${m2[2]?.id}/no-duty`, { body: `E2E ${RUN} school closed for the sports day` });
  eq("NO DUTY: the supervisor marks a day \"no duty\" (200)", nd.status, 200);
  eq("NO DUTY: a colleague cannot (404)", (await post(U.math1.token, `${B}/staff/duty-reports/${m2[1]?.id}/no-duty`, { body: "x" })).status, 404);

  // The overdue ladder, on math2's oldest report (due three days ago, never written).
  const probe2 = await trigger("staff-activity-attribution-purge");
  if (probe2 >= 300 && probe2 !== 204) {
    console.log(`  \x1b[33mSKIP\x1b[0m  Hangfire dashboard refused a trigger (${probe2}); the report ladder is asserted only against a Development API`);
  } else {
    const polNow = (await get(AD, "/api/v1/staff/policy")).json;
    await put(AD, "/api/v1/staff/policy", { ...polNow, quietHours: { ...(polNow.quietHours ?? {}), enabled: false } });
    try {
      const needle = `E2E ${RUN} Reported duty`;
      truthy("LADDER: the author of an overdue report is chased", !!(await sweepFor("staff-reminder-ladder", async () => (await count(U.math2.token, "staff.duty-report-overdue", needle)) >= 1)));
      truthy("LADDER: two days overdue escalates to the supervisor, content-free", !!(await sweepFor("staff-reminder-ladder", async () => {
        const n = ((await get(U.hod.token, "/api/v1/notifications?eventKey=staff.duty-report-overdue&limit=100")).json ?? []).find((x) => x.title.includes(needle));
        return n && !n.message.includes(U.math2.name.split(" ")[0]);
      })));
      const once = await count(U.math2.token, "staff.duty-report-overdue", needle);
      await trigger("staff-reminder-ladder");
      await sleep(6000);
      eq("LADDER: a second sweep sends nothing new to the author", await count(U.math2.token, "staff.duty-report-overdue", needle), once);
      const hodPortal = (await get(U.hod.token, "/api/v1/staff/portal")).json;
      truthy("TO-DO: the supervisor's portal lists the overdue report", (hodPortal?.openItems ?? []).some((i) => i.kind === "duty-report-overdue" && i.title.includes(needle)), JSON.stringify((hodPortal?.openItems ?? []).filter((i) => i.kind === "duty-report-overdue").map((i) => i.title)));
      const m2Portal = (await get(U.math2.token, "/api/v1/staff/portal")).json;
      truthy("PORTAL: the author's Reports to write lists the unwritten ones", (m2Portal?.reportsToWrite ?? []).some((r) => r.dutyId === SLOT), JSON.stringify((m2Portal?.reportsToWrite ?? []).map((r) => r.dutyTitle)));
    } finally {
      await put(AD, "/api/v1/staff/policy", polNow);
    }
  }
  const log = (await get(AD, `${B}/staff/activity?action=staff.duty-report.viewed&pageSize=20`)).json;
  truthy("ACTIVITY: a read by someone other than the author is logged, without the report's text", (log?.items ?? []).some((e) => e.summary.includes(`E2E ${RUN} Reported duty`)) && !(log?.items ?? []).some((e) => e.summary.includes("SECRET-TEXT")), JSON.stringify((log?.items ?? []).slice(0, 2).map((e) => e.summary)));
}

// ---------------------------------------------------------------------------------------------------
hdr("15.5 TIMETABLE — settings under the branch lock, drafts, placement, clashes, publish, coverage, the integrity sweep");
{
  const T = `${B}/timetable`;
  const subjects = (await get(AD, `${B}/staff/subjects`)).json ?? [];
  const MATHS = subjects.find((s) => /^mathematics$/i.test(s.name));
  const PHYS = subjects.find((s) => /^physics$/i.test(s.name));
  const cov0 = (await get(AD, `${B}/class-teachers/coverage`)).json;
  const classNames = (cov0?.classes ?? []).map((c) => c.className);
  truthy("SETUP: Mathematics and Physics exist, and classes S2A and S2B are configured", MATHS && PHYS && classNames.includes("S2A") && classNames.includes("S2B"), JSON.stringify({ m: !!MATHS, p: !!PHYS, classNames }));
  const vocab0 = (await get(AD, `${B}/students/vocabularies`)).json;
  const ROOM = (vocab0?.rooms ?? []).find((r) => r.isActive)?.name;
  truthy("SETUP: a room is configured", !!ROOM, JSON.stringify(vocab0?.rooms));

  // Assignments this block owns, ended at the end.
  const endIds = [];
  const assign = async (userId, className, subjectId, periodsPerWeek) => {
    const r = await post(AD, `${B}/class-teachers/subject-teachers`, { className, userId, subjectId, periodsPerWeek });
    if (r.json?.id) { endIds.push(r.json.id); return r.json.id; }
    const existing = ((await get(AD, `${B}/class-teachers/coverage`)).json?.classes ?? []).flatMap((c) => c.subjectTeachers ?? [])
      .find((a) => a.userId === userId && a.className === className && a.subjectId === subjectId);
    if (existing) { await call(AD, "PATCH", `${B}/class-teachers/${existing.id}/periods`, { periodsPerWeek }); return existing.id; }
    return null;
  };
  const a1 = await assign(U.math1.id, "S2B", PHYS?.id, 2);
  const a2 = await assign(U.math2.id, "S2B", MATHS?.id, 2);
  const a3 = await assign(U.math1.id, "S2A", PHYS?.id, 1);
  truthy("SETUP: Martin Kato teaches Physics in S2B (2 a week) and S2A (1), the second Maths teacher S2B Mathematics (2)", !!a1 && !!a2 && !!a3);

  const published = [];
  const drafts = [];
  let supportRoleId = null, scopedRoleId = null;
  try {
    // ---- Settings ----
    const s0 = await get(U.math1.token, `${T}/settings`);
    truthy("SETTINGS: any member of staff reads the bell schedule (teaching periods present)", s0.status === 200 && s0.json?.dayTypes?.[0]?.periods?.some((p) => p.kind === "Lesson"), `${s0.status}`);
    eq("SETTINGS: a head of department cannot change it (403)", (await put(U.hod.token, `${T}/settings`, s0.json)).status, 403);
    const base = (await get(AD, `${T}/settings`)).json;
    const bad1 = structuredClone(base); bad1.dayTypes[0].periods[1].start = "07:50";
    eq("SETTINGS: overlapping periods are refused (400)", (await put(AD, `${T}/settings`, bad1)).status, 400);
    const bad2 = structuredClone(base); bad2.dayTypes.push({ key: "sat", name: "Saturday", days: ["Monday"], periods: [{ key: "P1", label: "P1", start: "08:00", end: "08:40", kind: "Lesson" }] });
    eq("SETTINGS: a weekday in two day types is refused (400)", (await put(AD, `${T}/settings`, bad2)).status, 400);
    const withUnavail = { ...base, unavailability: [{ userId: U.math2.id, cycleDay: 3, periodKey: "P5", note: `E2E ${RUN} clinic` }] };
    const saved = await put(AD, `${T}/settings`, withUnavail);
    truthy("SETTINGS: saved with an unavailability line (Wed P5 for the second Maths teacher)", saved.status === 200 && saved.json?.isSaved && saved.json?.unavailability?.length === 1, `${saved.status} ${saved.text?.slice(0, 200)}`);
    eq("SETTINGS: a teacher does not see who declared themselves unavailable", ((await get(U.math1.token, `${T}/settings`)).json?.unavailability ?? []).length, 0);

    // The branch-settings lock: a timetable save racing a vocabulary save must not lose either.
    let lost = 0;
    for (let i = 0; i < 5; i++) {
      const v = (await get(AD, `${B}/students/vocabularies`)).json;
      const minutes = 41 + i;
      await Promise.all([
        put(AD, `${T}/settings`, { ...withUnavail, defaultPeriodMinutes: minutes }),
        put(AD, `${B}/students/vocabularies`, { vocabularies: v, classRenames: {}, houseRenames: {}, dormitoryRenames: {} }),
      ]);
      if ((await get(AD, `${T}/settings`)).json?.defaultPeriodMinutes !== minutes) lost++;
    }
    eq("CONCURRENT: five timetable saves each racing a vocabulary save — none lost", lost, 0);

    // ---- Who may build ----
    const mk = (name, extra = {}) => post(AD, `${T}/timetables`, { name: `E2E ${RUN} ${name}`, ...extra });
    const today = new Date(); const ymd = (d) => d.toISOString().slice(0, 10); const addDays = (n) => new Date(Date.now() + n * 86400_000);
    eq("BUILD: a head of department cannot create a timetable (403, no timetable.manage)", (await post(U.hod.token, `${T}/timetables`, { name: `E2E ${RUN} nope` })).status, 403);
    {
      // A scoped caller WITH timetable.manage is still refused: publishing is bulk work for the whole school.
      const perms = (await get(AD, "/api/v1/roles/permissions")).json ?? [];
      const pid = (code) => perms.flatMap((c) => c.permissions ?? []).find((p) => p.code === code)?.id;
      const role = await post(AD, "/api/v1/roles", { code: `e2e-tt-${RUN}`.slice(0, 40), name: `E2E Scoped Timetabler ${RUN}`, staffScope: "AssignedDepartments", dataScope: "Organization", permissionIds: [pid("dashboard.view"), pid("timetable.manage")].filter(Boolean) });
      scopedRoleId = role.json?.id;
      const all = (await get(AD, "/api/v1/users?pageSize=500")).json; const me = (Array.isArray(all) ? all : all?.items ?? []).find((x) => x.id === U.support.id);
      supportRoleId = me?.roleId;
      const moved = await put(AD, `/api/v1/users/${U.support.id}`, { firstName: me?.firstName, lastName: me?.lastName, email: me?.email, roleId: scopedRoleId, assignedBranchId: BRANCH, isActive: true });
      if (moved.status < 300) {
        const tk = await login("e2e.sp.support@qmgr.local");
        const r = await post(tk, `${T}/timetables`, { name: `E2E ${RUN} scoped` });
        eq("BUILD: a department-scoped caller holding timetable.manage is refused (403)", r.status, 403);
      } else bad("move the support account onto a scoped timetabling role", "2xx", `${moved.status} ${moved.text}`);
    }

    // ---- A draft, and placing lessons ----
    const t1r = await mk("Future", { effectiveFrom: ymd(addDays(60)), effectiveTo: ymd(addDays(150)) });
    eq("DRAFT: created (201)", t1r.status, 201);
    const t1 = t1r.json?.timetable; if (t1?.id) drafts.push(t1.id);
    eq("DRAFT: a five-day cycle from the bell schedule", t1?.cycleDays, 5);
    eq("DRAFT: a teacher cannot read a draft (404)", (await get(U.math1.token, `${T}/timetables/${t1?.id}`)).status, 404);
    truthy("DRAFT: …nor see it listed", !((await get(U.math1.token, `${T}/timetables`)).json ?? []).some((x) => x.id === t1?.id));

    const L = `${T}/timetables/${t1?.id}/lessons`;
    const place = (o) => post(AD, L, { cycleDay: 1, periodKey: "P1", className: "S2B", subjectId: PHYS?.id, teacherUserIds: [U.math1.id], room: ROOM, alsoClassNames: [], ...o });
    const p1 = await place({});
    eq("PLACE: Martin Kato, S2B Physics, Mon P1 (201)", p1.status, 201);
    eq("PLACE: …with no hard clash", p1.json?.diagnosis?.hardCount, 0);
    eq("PLACE: an unknown class is refused (400)", (await place({ className: "Z9Z", cycleDay: 2 })).status, 400);
    eq("PLACE: a break is not a teaching period (400)", (await place({ periodKey: "BRK", cycleDay: 2 })).status, 400);
    eq("PLACE: cycle day 9 of a five-day cycle is refused (400)", (await place({ cycleDay: 9 })).status, 400);
    eq("PLACE: an unconfigured room is refused (400)", (await place({ cycleDay: 2, room: `Nowhere ${RUN}` })).status, 400);

    // ---- Rooms: edited with the timetable (plan §6.1), not the student lists; a rename moves the lessons ----
    {
      const R = `${T}/rooms`;
      const rooms0 = (await get(AD, R)).json ?? [];
      truthy("ROOMS: listed with how many draft or published lessons use each", rooms0.some((r) => r.name === ROOM && r.lessonCount >= 1), JSON.stringify(rooms0.slice(0, 3)));
      eq("ROOMS: a teacher can read the room list (200)", (await get(U.math1.token, R)).status, 200);
      eq("ROOMS: a head of department cannot change rooms (403, no timetable.manage)", (await put(U.hod.token, R, { rooms: rooms0, renames: {} })).status, 403);
      const extra = `E2E ${RUN} Lab`;
      const added = await put(AD, R, { rooms: [...rooms0, { name: extra, roomType: "Lab", capacity: 40, isActive: true }], renames: {} });
      truthy("ROOMS: a master adds a room with a type and seats", added.status === 200 && (added.json ?? []).some((r) => r.name === extra && r.capacity === 40 && r.roomType === "Lab"), `${added.status} ${added.text?.slice(0, 200)}`);
      eq("ROOMS: the same name twice (any case) is refused (400)", (await put(AD, R, { rooms: [...(added.json ?? []), { name: extra.toUpperCase(), isActive: true }], renames: {} })).status, 400);
      const v = (await get(AD, `${B}/students/vocabularies`)).json;
      await put(AD, `${B}/students/vocabularies`, { vocabularies: { ...v, rooms: [] }, classRenames: {}, houseRenames: {}, dormitoryRenames: {} });
      truthy("ROOMS: a student lists save sending no rooms leaves the rooms alone (one writer)", ((await get(AD, R)).json ?? []).some((r) => r.name === extra));
      const renamed = `${ROOM} ${RUN}`;
      const lessonId = p1.json?.lessons?.[0]?.id;
      const ren = await put(AD, R, { rooms: (added.json ?? []).map((r) => (r.name === ROOM ? { ...r, name: renamed } : r)), renames: { [ROOM]: renamed } });
      const moved = ((await get(AD, `${T}/timetables/${t1?.id}`)).json?.lessons ?? []).find((l) => l.id === lessonId);
      truthy("ROOMS: renaming a room moves the draft lesson held in it", ren.status === 200 && moved?.room === renamed, `${ren.status} ${ren.text?.slice(0, 160)} lesson room ${moved?.room}`);
      const gone = await put(AD, R, { rooms: (ren.json ?? []).filter((r) => r.name !== renamed), renames: {} });
      truthy("ROOMS: a room lessons are held in cannot be removed — retire it (400)", gone.status === 400 && /Retire/.test(gone.text ?? ""), `${gone.status} ${gone.text?.slice(0, 160)}`);
      const back = await put(AD, R, { rooms: (ren.json ?? []).map((r) => (r.name === renamed ? { ...r, name: ROOM } : r)).filter((r) => r.name !== extra), renames: { [renamed]: ROOM } });
      truthy("ROOMS: renamed back, and the unused added room removed", back.status === 200 && (back.json ?? []).some((r) => r.name === ROOM) && !(back.json ?? []).some((r) => r.name === extra), `${back.status} ${back.text?.slice(0, 160)}`);
      const log = (await get(AD, `${B}/staff/activity?action=timetable.rooms-saved&pageSize=5`)).json;
      truthy("ROOMS: saves are logged", (log?.items ?? []).some((e) => /renamed/.test(e.summary)), JSON.stringify((log?.items ?? []).slice(0, 2).map((e) => e.summary)));
    }

    // Two masters placing the same teacher in the same period at once: one row, never two, never a 500.
    const race = await Promise.all(["S2A", "S2B", "S2A", "S2B", "S2A", "S2B"].map((c) => place({ cycleDay: 2, periodKey: "P2", className: c, subjectId: MATHS?.id, teacherUserIds: [U.math2.id], room: null })));
    const codes = race.map((r) => r.status).sort().join(",");
    eq("CONCURRENT: six placements of one teacher into Tue P2 — one 201, five 409", codes, "201,409,409,409,409,409");
    const raced = race.find((r) => r.status === 201)?.json?.lessons?.[0];

    const dbl = await place({ className: "S2B", subjectId: MATHS?.id, teacherUserIds: [U.math2.id] });
    eq("PLACE: a second S2B lesson in Mon P1, in the same room (201 — a draft may hold a clash)", dbl.status, 201);
    const kinds = (d) => (d?.issues ?? []).filter((i) => i.severity === "Hard").map((i) => i.kind);
    truthy("CLASH: S2B double-booked and the room double-booked, both hard", kinds(dbl.json?.diagnosis).includes("ClassDoubleBooked") && kinds(dbl.json?.diagnosis).includes("RoomDoubleBooked"), JSON.stringify(kinds(dbl.json?.diagnosis)));
    const un = await place({ cycleDay: 3, periodKey: "P5", className: "S2B", subjectId: MATHS?.id, teacherUserIds: [U.math2.id], room: null });
    truthy("CLASH: placing the teacher in their declared unavailability is a hard clash", kinds(un.json?.diagnosis).includes("TeacherUnavailable"), JSON.stringify(kinds(un.json?.diagnosis)));
    // Its own S2A lesson: the race above places S2A or S2B, whichever wins, so it cannot be relied on for this.
    const na = await place({ cycleDay: 4, periodKey: "P6", className: "S2A", subjectId: MATHS?.id, teacherUserIds: [U.math2.id], room: null });
    truthy("CLASH: teaching S2A Mathematics without being assigned it is a hard clash", kinds(na.json?.diagnosis).includes("TeacherNotAssigned"), `${na.status} ${JSON.stringify(kinds(na.json?.diagnosis))}`);
    await del(AD, `${L}/${na.json?.lessons?.[0]?.id}`);
    eq("PUBLISH: refused while hard clashes remain (409 HARD_CLASHES)", (await post(AD, `${T}/timetables/${t1?.id}/publish`, { acknowledgeSoftClashes: true, note: "x" })).json?.code, "HARD_CLASHES");

    const mv = await put(AD, `${L}/${dbl.json?.lessons?.[0]?.id}`, { cycleDay: 1, periodKey: "P3", room: null });
    truthy("MOVE: the second S2B lesson to Mon P3 clears the class and room clashes", mv.status === 200 && !kinds(mv.json?.diagnosis).includes("ClassDoubleBooked") && !kinds(mv.json?.diagnosis).includes("RoomDoubleBooked"), `${mv.status} ${JSON.stringify(kinds(mv.json?.diagnosis))}`);
    await del(AD, `${L}/${un.json?.lessons?.[0]?.id}`);
    const rm = await del(AD, `${L}/${raced?.id}`);
    eq("REMOVE: with the unavailable and unassigned lessons removed, no hard clash remains", rm.json?.diagnosis?.hardCount, 0);

    const joint = await place({ cycleDay: 4, periodKey: "P1", className: "S2B", alsoClassNames: ["S2A"], room: null });
    truthy("JOINT: one Physics lesson for S2B and S2A together — two rows, one group, no clash with itself", joint.status === 201 && joint.json?.lessons?.length === 2 && joint.json.lessons[0].groupId && joint.json.lessons[0].groupId === joint.json.lessons[1].groupId && joint.json?.diagnosis?.hardCount === 0, `${joint.status} ${JSON.stringify(kinds(joint.json?.diagnosis))}`);
    const soft = (d) => (d?.issues ?? []).filter((i) => i.severity === "Soft").map((i) => i.kind);
    truthy("SOFT: planned against placed is reported (S2B Mathematics: 2 planned, 1 placed)", soft(joint.json?.diagnosis).includes("PlannedNotPlaced"), JSON.stringify(soft(joint.json?.diagnosis)));
    truthy("UNPLACED: the side list carries S2B Mathematics at 1 of 2", (joint.json?.diagnosis?.unplaced ?? []).some((u) => u.className === "S2B" && u.subjectId === MATHS?.id && u.planned === 2 && Number(u.placed) === 1), JSON.stringify((joint.json?.diagnosis?.unplaced ?? []).filter((u) => u.className === "S2B")));

    eq("PUBLISH: soft clashes must be acknowledged (409 SOFT_CLASHES)", (await post(AD, `${T}/timetables/${t1?.id}/publish`, {})).json?.code, "SOFT_CLASHES");
    eq("PUBLISH: …and acknowledged with a note (400 without one)", (await post(AD, `${T}/timetables/${t1?.id}/publish`, { acknowledgeSoftClashes: true })).status, 400);
    const pub1 = await post(AD, `${T}/timetables/${t1?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN} under-load is the start of term` });
    eq("PUBLISH: published (200)", pub1.json?.timetable?.status, "Published");
    if (pub1.json?.timetable?.status === "Published") { published.push(t1.id); drafts.splice(drafts.indexOf(t1.id), 1); }
    truthy("NOTICE: Martin Kato is told his timetable is published — a link, not the lessons", !!(await waitFor(async () => ((await get(U.math1.token, "/api/v1/notifications?eventKey=staff.timetable-published&limit=50")).json ?? []).some((n) => n.title.includes(`E2E ${RUN} Future`) && !/Physics|S2B|P1/.test(n.message)), 15000)));
    const asTeacher = await get(U.math1.token, `${T}/timetables/${t1?.id}`);
    truthy("READ: a teacher reads the published timetable, with no diagnosis and no build rights", asTeacher.status === 200 && asTeacher.json?.lessons?.length >= 4 && asTeacher.json?.diagnosis == null && asTeacher.json?.canManage === false, `${asTeacher.status}`);
    eq("IMMUTABLE: a published timetable cannot be edited (409)", (await place({ cycleDay: 5, periodKey: "P9" })).status, 409);
    eq("EXPORT: a teacher's print of the timetable is logged (204)", (await post(U.math1.token, `${B}/staff/activity/exports`, { kind: "timetable", documentName: `E2E ${RUN} Future — by teacher`, format: "Print" })).status, 204);
    const pubLog = (await get(AD, `${B}/staff/activity?action=timetable.published&pageSize=10`)).json;
    truthy("ACTIVITY: the publication is logged, with the soft clashes acknowledged", (pubLog?.items ?? []).some((e) => e.summary.includes(`E2E ${RUN} Future`) && /soft clash/.test(e.summary)), JSON.stringify((pubLog?.items ?? []).slice(0, 1).map((e) => e.summary)));

    // ---- In force today: a Session duty over a lesson, coverage, "My teaching" ----
    let d = addDays(2); while ([0, 6].includes(d.getUTCDay())) d = new Date(d.getTime() + 86400_000);
    const cycleDay = d.getUTCDay(); // Mon=1 … Fri=5 in a one-week cycle
    const t2r = await mk("Today", { effectiveFrom: ymd(today), effectiveTo: ymd(addDays(30)) });
    const t2 = t2r.json?.timetable; if (t2?.id) drafts.push(t2.id);
    const L2 = `${T}/timetables/${t2?.id}/lessons`;
    await post(AD, L2, { cycleDay, periodKey: "P6", className: "S2B", subjectId: MATHS?.id, teacherUserIds: [U.math2.id], alsoClassNames: [] });
    const session = await post(AD, `${B}/staff/duties`, baseSlot({ kind: "Session", title: `E2E ${RUN} Timetable clash meeting`, startsAt: `${ymd(d)}T03:00:00Z`, endsAt: `${ymd(d)}T15:00:00Z`, expectedUserIds: [U.math2.id], supervisorUserIds: [], recorderUserIds: [U.hod.id] }));
    if (session.json?.id) cleanupDuties.push(session.json.id);
    const withSession = (await get(AD, `${T}/timetables/${t2?.id}`)).json;
    truthy("SESSION: a meeting the teacher is expected at, over their lesson in the next fortnight, is a hard clash", (withSession?.diagnosis?.issues ?? []).some((i) => i.kind === "TeacherOnSessionDuty" && i.message.includes(`E2E ${RUN} Timetable clash meeting`)), `${session.status} ${JSON.stringify((withSession?.diagnosis?.issues ?? []).map((i) => i.kind))}`);
    await del(AD, `${B}/staff/duties/${session.json?.id}`);
    const pub2 = await post(AD, `${T}/timetables/${t2?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
    eq("PUBLISH: with the meeting cancelled, the timetable in force today publishes", pub2.json?.timetable?.status, "Published");
    if (pub2.json?.timetable?.status === "Published") { published.push(t2.id); drafts.splice(drafts.indexOf(t2.id), 1); }
    eq("CURRENT: it is the timetable in force today", (await get(U.math2.token, `${T}/current`)).json?.timetable?.id, t2?.id);

    const cov = (await get(AD, `${B}/class-teachers/coverage`)).json;
    truthy("COVERAGE: S2B Mathematics is a planned/timetabled mismatch (2 against 1)", (cov?.plannedPeriodMismatches ?? []).some((m) => m.assignment?.id === a2 && m.planned === 2 && m.timetabled === 1), JSON.stringify(cov?.plannedPeriodMismatches?.map((m) => [m.assignment?.className, m.planned, m.timetabled])));
    truthy("COVERAGE: S2B Physics has no lesson in the timetable in force", (cov?.subjectTeachersWithNoLessons ?? []).some((a) => a.id === a1), JSON.stringify(cov?.subjectTeachersWithNoLessons?.map((a) => a.className)));
    const mine = (await get(U.math2.token, `${B}/class-teachers/teaching`)).json ?? [];
    truthy("MY TEACHING: the second Maths teacher's Mathematics shows 1 period timetabled", mine.some((t) => t.subjectId === MATHS?.id && t.timetabledPeriodsPerWeek >= 1), JSON.stringify(mine));

    // A replacement over the same dates archives the old version.
    const t3r = await mk("Today v2", { effectiveFrom: ymd(addDays(1)), effectiveTo: ymd(addDays(30)), copyFromTimetableId: t2?.id });
    const t3 = t3r.json?.timetable; if (t3?.id) drafts.push(t3.id);
    eq("COPY: a new draft copies the published version's lessons", t3r.json?.lessons?.length, 1);
    const pub3 = await post(AD, `${T}/timetables/${t3?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
    if (pub3.json?.timetable?.status === "Published") { published.push(t3.id); drafts.splice(drafts.indexOf(t3.id), 1); }
    eq("REPLACE: publishing over overlapping dates archives the old version", (await get(AD, `${T}/timetables/${t2?.id}`)).json?.timetable?.status, "Archived");

    // ---- The integrity sweep ----
    const probe = await trigger("staff-activity-attribution-purge");
    if (probe >= 300 && probe !== 204) {
      console.log(`  \x1b[33mSKIP\x1b[0m  Hangfire dashboard refused a trigger (${probe}); the integrity sweep is asserted only against a Development API`);
    } else {
      // Settle anything already reported. Not load-bearing, and deliberately not waited on: the sweep
      // stores the hard keys it last announced ON the timetable, so whether this run lands before or
      // after the assignment is ended below, exactly one announcement follows the new clash.
      await trigger("timetable-integrity");
      const needle = `E2E ${RUN} Today v2`;
      const before = await count(AD, "staff.timetable-clash", needle);
      await del(AD, `${B}/class-teachers/${a2}`, { reason: `E2E ${RUN} ended to break the timetable` });
      endIds.splice(endIds.indexOf(a2), 1);
      truthy("SWEEP: ending the assignment makes a new hard clash, and the timetable masters are told once", !!(await sweepFor("timetable-integrity", async () => (await count(AD, "staff.timetable-clash", needle)) === before + 1)), `${before} → ${await count(AD, "staff.timetable-clash", needle)}`);
      // "Not announced again" has to be asserted against a sweep that actually RAN, or a queued one makes
      // it pass vacuously: every check writes one activity line, so wait for a second one on this version.
      const checks = async () => ((await get(AD, `${B}/staff/activity?action=timetable.checked&pageSize=50`)).json?.items ?? []).filter((e) => e.summary?.includes(`E2E ${RUN} Today v2`)).length;
      const checked = await checks();
      await sweepFor("timetable-integrity", async () => (await checks()) > checked, 60_000);
      eq("SWEEP: an unchanged clash is not announced again", await count(AD, "staff.timetable-clash", needle), before + 1);
    }

    // ---- A class rename carries the lessons ----
    {
      const v = (await get(AD, `${B}/students/vocabularies`)).json;
      const renamed = `S2B-${RUN}`.slice(0, 40);
      const cls = v.classes.find((c) => c.name === "S2B");
      const ren = await put(AD, `${B}/students/vocabularies`, { vocabularies: { ...v, classes: v.classes.map((c) => (c === cls ? { ...c, name: renamed } : c)) }, classRenames: { S2B: renamed }, houseRenames: {}, dormitoryRenames: {} });
      const after = (await get(AD, `${T}/timetables/${t3?.id}`)).json?.lessons ?? [];
      truthy("RENAME: renaming S2B moves the published timetable's lessons in the same save", ren.status === 200 && after.length > 0 && after.every((l) => l.className === renamed), `${ren.status} ${JSON.stringify(after.map((l) => l.className))}`);
      const v2 = (await get(AD, `${B}/students/vocabularies`)).json;
      await put(AD, `${B}/students/vocabularies`, { vocabularies: { ...v2, classes: v2.classes.map((c) => (c.name === renamed ? { ...c, name: "S2B" } : c)) }, classRenames: { [renamed]: "S2B" }, houseRenames: {}, dormitoryRenames: {} });
      eq("RENAME: …and back", ((await get(AD, `${T}/timetables/${t3?.id}`)).json?.lessons ?? [])[0]?.className, "S2B");
    }
  } finally {
    for (const id of published) await post(AD, `${T}/timetables/${id}/archive`);
    for (const id of drafts) await del(AD, `${T}/timetables/${id}`);
    for (const id of endIds) await del(AD, `${B}/class-teachers/${id}`, { reason: `E2E ${RUN} clean-up` });
    const s = (await get(AD, `${T}/settings`)).json;
    if (s) await put(AD, `${T}/settings`, { ...s, defaultPeriodMinutes: 40, unavailability: [] });
    if (supportRoleId) {
      const all = (await get(AD, "/api/v1/users?pageSize=500")).json; const me = (Array.isArray(all) ? all : all?.items ?? []).find((x) => x.id === U.support.id);
      await put(AD, `/api/v1/users/${U.support.id}`, { firstName: me?.firstName, lastName: me?.lastName, email: me?.email, roleId: supportRoleId, assignedBranchId: BRANCH, isActive: true });
    }
    if (scopedRoleId) await del(AD, `/api/v1/roles/${scopedRoleId}`);
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("15.6 LESSONS — materialised from the timetable, My Day, Taught / Not taught, supervisors, recovery, cancel, reminders, the digest");
{
  const T = `${B}/timetable`;
  const LS = `${B}/staff/lessons`;
  const branchInfo = (await get(AD, `/api/v1/branches/${BRANCH}`)).json ?? {};
  const TZ = branchInfo.timezone ?? branchInfo.timeZone ?? "Africa/Kampala";
  const localParts = (d) => Object.fromEntries(new Intl.DateTimeFormat("en-GB", { timeZone: TZ, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", weekday: "long" }).formatToParts(d).map((p) => [p.type, p.value]));
  const hm = (d) => { const p = localParts(d); return `${p.hour}:${p.minute}`; };
  const nowLocal = localParts(new Date());
  const minutesNow = Number(nowLocal.hour) * 60 + Number(nowLocal.minute);
  if (minutesNow < 60 || minutesNow > 20 * 60 + 30) {
    console.log(`  \x1b[33mSKIP\x1b[0m  15.6 builds periods around the branch-local time (${nowLocal.hour}:${nowLocal.minute}); it needs a time between 01:00 and 20:30`);
  } else {
    const at = (m) => new Date(Date.now() + m * 60_000);
    const settings0 = (await get(AD, `${T}/settings`)).json;
    const policy0 = (await get(AD, "/api/v1/staff/policy")).json;
    const weekday = nowLocal.weekday; // "Thursday"
    const P = (key, from, to) => ({ key, label: key, start: hm(at(from)), end: hm(at(to)), kind: "Lesson" });
    const temp = {
      cycleWeeks: 1, defaultPeriodMinutes: 40, unavailability: [],
      dayTypes: [{ key: "e2e", name: `E2E ${RUN} lessons`, days: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"],
        periods: [P("LA", -30, 4), P("LB", 6, 46), P("LC", 60, 100), P("LD", 120, 160), P("LE", 170, 210)] }]
    };
    const subjects = (await get(AD, `${B}/staff/subjects`)).json ?? [];
    const MATHS = subjects.find((s) => /^mathematics$/i.test(s.name));
    const PHYS = subjects.find((s) => /^physics$/i.test(s.name));
    const endIds = [];
    const roles = (await get(AD, "/api/v1/roles")).json ?? [];
    const depts = (await get(AD, `${B}/staff/structure/departments`)).json ?? [];
    const MATHD = depts.find((d) => /mathematics/i.test(d.name));
    const made = [];
    const newTeacher = async (tag, first) => {
      const username = `e2e.ls.${tag}.${RUN}`;
      const r = await post(AD, "/api/v1/users", { username, email: `${username}@qmgr.local`, password: NEW_PW, firstName: first, lastName: `Lessons ${RUN}`, roleId: roles.find((x) => x.code === "teacher")?.id, assignedBranchId: BRANCH });
      if (!r.json?.id) return { id: null, token: null, name: first };
      made.push({ id: r.json.id, first, username });
      await put(AD, `${B}/staff/structure/members/${r.json.id}`, { departmentIds: MATHD ? [MATHD.id] : [], lineManagerUserId: null });
      return { id: r.json.id, name: `${first} Lessons ${RUN}`, token: await login(`${username}@qmgr.local`) };
    };
    const TA = await newTeacher("a", "Tina");
    const TB = await newTeacher("b", "Tom");
    const assign = async (userId, className, subjectId, periodsPerWeek) => {
      const r = await post(AD, `${B}/class-teachers/subject-teachers`, { className, userId, subjectId, periodsPerWeek });
      if (r.json?.id) endIds.push(r.json.id);
    };
    const published = [];
    const drafts = [];
    try {
      const saved = await put(AD, `${T}/settings`, temp);
      eq("SETUP: a temporary bell schedule with periods around the current time is saved", saved.status, 200);
      truthy("SETUP: two new Mathematics teachers with nothing else on their day (a colleague's meeting over a lesson is a real hard clash)", TA.token && TB.token && MATHD, JSON.stringify({ a: !!TA.token, b: !!TB.token, d: !!MATHD }));
      await assign(TA.id, "S2B", PHYS?.id, 3);
      await assign(TB.id, "S2B", MATHS?.id, 3);
      const cycleDay = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"].indexOf(weekday) + 1;
      const today = new Date().toISOString().slice(0, 10);
      const plus = (n) => new Date(Date.now() + n * 86400_000).toISOString().slice(0, 10);

      const draft = await post(AD, `${T}/timetables`, { name: `E2E ${RUN} Lessons`, effectiveFrom: plus(-1), effectiveTo: plus(20) });
      const tt = draft.json?.timetable; if (tt?.id) drafts.push(tt.id);
      const L = `${T}/timetables/${tt?.id}/lessons`;
      const place = (o) => post(AD, L, { className: "S2B", alsoClassNames: [], room: null, ...o });
      const placements = await Promise.all([
        place({ cycleDay, periodKey: "LA", subjectId: PHYS?.id, teacherUserIds: [TA.id] }),
        place({ cycleDay, periodKey: "LB", subjectId: MATHS?.id, teacherUserIds: [TB.id] }),
        place({ cycleDay, periodKey: "LC", subjectId: PHYS?.id, teacherUserIds: [TA.id] }),
        place({ cycleDay, periodKey: "LD", subjectId: MATHS?.id, teacherUserIds: [TB.id] }),
      ]);
      truthy("SETUP: four lessons placed for today (Physics now and in an hour; Mathematics in six minutes and in two hours)", placements.every((p) => p.status === 201), placements.map((p) => p.status).join(","));
      const pub = await post(AD, `${T}/timetables/${tt?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
      truthy("SETUP: published", pub.json?.timetable?.status === "Published", pub.text?.slice(0, 300));
      if (pub.json?.timetable?.status === "Published") { published.push(tt.id); drafts.splice(drafts.indexOf(tt.id), 1); }

      // ---- Materialisation ----
      eq("GENERATE: a head of department cannot generate lessons (403)", (await post(U.hod.token, `${LS}/generate`)).status, 403);
      const gens = await Promise.all([post(AD, `${LS}/generate`), post(AD, `${LS}/generate`), post(AD, `${LS}/generate`)]);
      truthy("GENERATE: three simultaneous runs all succeed", gens.every((g) => g.status === 200), gens.map((g) => `${g.status}:${g.text?.slice(0, 80)}`).join(" | "));
      const again = await post(AD, `${LS}/generate`);
      eq("GENERATE: a further run creates nothing (idempotent)", again.json?.created, 0);
      const mine = (tok, id) => get(tok, `${LS}?teacherUserId=${id}&from=${encodeURIComponent(iso(Date.now() - 3600_000))}&to=${encodeURIComponent(iso(Date.now() + 5 * 3600_000))}`);
      const m1 = (await mine(TA.token, TA.id)).json?.items ?? [];
      const LA = m1.find((i) => i.periodLabel === "LA"), LC = m1.find((i) => i.periodLabel === "LC");
      eq("GENERATE: the Physics teacher has exactly his two lessons today, not six", m1.filter((i) => ["LA", "LC"].includes(i.periodLabel)).length, 2);
      truthy("STATUS: the lesson in progress reads Unrecorded, the later one Scheduled", LA?.status === "Unrecorded" && LC?.status === "Scheduled", JSON.stringify(m1.map((i) => [i.periodLabel, i.status])));

      // ---- My Day ----
      const day = (await get(TA.token, `${LS}/my-day`)).json;
      truthy("MY DAY: today's lessons in order, a timetable in force, and the next lesson's start", day?.hasTimetable && day?.lessons?.length >= 2 && day.lessons[0].periodLabel === "LA" && day.nextLessonStartsAt === LC?.startsAt,
        JSON.stringify({ h: day?.hasTimetable, l: day?.lessons?.map((i) => i.periodLabel), n: day?.nextLessonStartsAt }));
      truthy("MY DAY: the teacher may self-report the started lesson, not the future one", day?.lessons?.find((i) => i.periodLabel === "LA")?.canSelfReport === true && day?.lessons?.find((i) => i.periodLabel === "LC")?.canSelfReport === false);

      // ---- Who sees ----
      eq("PEERS: another teacher asking for the Physics teacher's lessons gets 404", (await mine(U.lang1.token, TA.id)).status, 404);
      eq("PEERS: …and cannot flag his lesson (404)", (await post(U.lang1.token, `${LS}/${LA?.id ?? LA?.dutyId}/flag`, { outcome: "Present" })).status, 404);
      const hodList = (await get(U.hod.token, `${LS}?from=${encodeURIComponent(iso(Date.now() - 3600_000))}&to=${encodeURIComponent(iso(Date.now() + 5 * 3600_000))}`)).json;
      truthy("SCOPE: the head of Mathematics sees the department's lessons, may flag them, and is told the list is scoped", (hodList?.items ?? []).some((i) => i.dutyId === LA?.dutyId && i.canFlag) && (hodList?.scopedToDepartments ?? []).length > 0,
        JSON.stringify({ n: hodList?.items?.length, s: hodList?.scopedToDepartments }));

      // ---- Flags ----
      eq("SELF: a lesson not yet started cannot be flagged (400)", (await post(TA.token, `${LS}/${LC?.dutyId}/flag`, { outcome: "Present" })).status, 400);
      eq("SELF: a teacher cannot record their own lesson as missed with permission (400)", (await post(TA.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Excused", note: "x" })).status, 400);
      eq("SELF: 'not taught' needs a reason (400)", (await post(TA.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Absent" })).status, 400);
      const self = await post(TA.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Present" });
      eq("SELF: the Physics teacher marks the lesson taught — a self-report", `${self.status}/${self.json?.status}`, "200/TaughtSelfReported");
      const hodSees = ((await get(U.hod.token, `${LS}?teacherUserId=${TA.id}&from=${encodeURIComponent(iso(Date.now() - 3600_000))}&to=${encodeURIComponent(iso(Date.now() + 3600_000))}`)).json);
      truthy("CONFIRM: the head's list counts a self-report to confirm", (hodSees?.selfReportsToConfirm ?? 0) >= 1, JSON.stringify(hodSees?.selfReportsToConfirm));
      eq("CONFIRM: a teacher cannot bulk-confirm (403)", (await post(TA.token, `${LS}/confirm`, { dutyIds: [LA?.dutyId] })).status, 403);
      const conf = await post(U.hod.token, `${LS}/confirm`, { dutyIds: [LA?.dutyId] });
      eq("CONFIRM: the head confirms it", conf.json?.confirmed, 1);
      eq("CONFIRM: …and it now reads Taught", (await mine(TA.token, TA.id)).json?.items?.find((i) => i.dutyId === LA?.dutyId)?.status, "Taught");
      eq("SELF: the teacher cannot replace the head's record with their own (409)", (await post(TA.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Absent", note: "changed my mind" })).status, 409);
      eq("SUPERVISOR: missed without a reason is refused (400)", (await post(U.hod.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Excused" })).status, 400);

      // Four simultaneous overrides of the same lesson: one Final record, never four.
      const racers = await Promise.all(["Excused", "Absent", "Excused", "Present"].map((o) => post(U.hod.token, `${LS}/${LA?.dutyId}/flag`, { outcome: o, note: `E2E ${RUN} race` })));
      truthy("CONCURRENT: four simultaneous flags by the head all answer 200", racers.every((r) => r.status === 200), racers.map((r) => r.status).join(","));
      const finalRecs = ((await get(AD, `${B}/staff/records?subjectUserId=${TA.id}&from=${encodeURIComponent(iso(Date.now() - 2 * 3600_000))}&to=${encodeURIComponent(iso(Date.now() + 3600_000))}&status=Final&pageSize=100`)).json?.items ?? [])
        .filter((r) => r.dutyId === LA?.dutyId);
      eq("CONCURRENT: …and exactly one Final record stands for the lesson", finalRecs.length, 1);
      const excused = await post(U.hod.token, `${LS}/${LA?.dutyId}/flag`, { outcome: "Excused", note: `E2E ${RUN} called to a parents' meeting` });
      eq("SUPERVISOR: recorded as missed with permission", excused.json?.status, "MissedWithPermission");
      const log = (await get(AD, `${B}/staff/activity?action=staff.lesson.flag-overridden&pageSize=20`)).json;
      truthy("ACTIVITY: the override is logged against the teacher, naming what it replaced", (log?.items ?? []).some((e) => e.summary.includes("replacing")), JSON.stringify((log?.items ?? []).slice(0, 1).map((e) => e.summary)));

      // ---- Recovery ----
      eq("RECOVERY: a lesson that was not missed cannot be recovered (409)", (await post(TA.token, `${LS}/${LC?.dutyId}/recovery`, { startsAt: iso(Date.now() + 3 * 3600_000), endsAt: iso(Date.now() + 3.5 * 3600_000) })).status, 409);
      const rec = await post(TA.token, `${LS}/${LA?.dutyId}/recovery`, { startsAt: iso(Date.now() - 60_000), endsAt: iso(Date.now() + 40 * 60_000), room: null });
      eq("RECOVERY: the Physics teacher schedules a recovery lesson for the missed one (201)", rec.status, 201);
      eq("RECOVERY: a second recovery for the same lesson is refused (409)", (await post(TA.token, `${LS}/${LA?.dutyId}/recovery`, { startsAt: iso(Date.now() + 3600_000), endsAt: iso(Date.now() + 2 * 3600_000) })).status, 409);
      eq("RECOVERY: the missed lesson reads Recovery scheduled", (await mine(TA.token, TA.id)).json?.items?.find((i) => i.dutyId === LA?.dutyId)?.status, "RecoveryScheduled");
      const taughtRecovery = await post(TA.token, `${LS}/${rec.json?.dutyId}/flag`, { outcome: "Present" });
      eq("RECOVERY: the recovery lesson is marked taught", taughtRecovery.status, 200);
      eq("RECOVERY: …and the missed lesson now reads Recovered", (await mine(TA.token, TA.id)).json?.items?.find((i) => i.dutyId === LA?.dutyId)?.status, "Recovered");

      // ---- Cancel ----
      eq("CANCEL: a teacher cannot cancel a lesson (403)", (await post(TA.token, `${LS}/${LC?.dutyId}/cancel`, { reason: "sports day" })).status, 403);
      const cancel = await post(AD, `${LS}/${LC?.dutyId}/cancel`, { reason: `E2E ${RUN} sports day` });
      eq("CANCEL: the administrator cancels the later lesson with a reason", cancel.json?.status, "Cancelled");
      truthy("CANCEL: the Physics teacher is told, without the reason", !!(await waitFor(async () => ((await get(TA.token, "/api/v1/notifications?eventKey=staff.lesson-changed&limit=30")).json ?? []).some((n) => n.title === "A lesson of yours was cancelled" && !n.message.includes("sports day")), 15000)));
      eq("CANCEL: a cancelled lesson cannot be flagged (409)", (await post(TA.token, `${LS}/${LC?.dutyId}/flag`, { outcome: "Present" })).status, 409);

      // ---- Reminders and the digest (the Hangfire trigger works only against a Development API) ----
      const probe = await trigger("staff-activity-attribution-purge");
      if (probe >= 300 && probe !== 204) {
        console.log(`  \x1b[33mSKIP\x1b[0m  Hangfire dashboard refused a trigger (${probe}); lesson reminders and My Day are asserted only against a Development API`);
      } else {
        const digestAt = hm(at(-5));
        // The lesson reminder is due only inside the policy's lead window AND only while the lesson is
        // still to start (the sweep reads StartsAt > now). LB was placed six minutes out, and everything
        // above easily outlives it — which is exactly how this assertion failed, on a lesson that had
        // already begun. So take the Mathematics teacher's next lesson as it stands NOW and set the lead
        // so that lesson's reminder is due; when LB is still ahead, that is the original ten minutes.
        const ahead = ((await mine(TB.token, TB.id)).json?.items ?? [])
          .filter((i) => i.status !== "Cancelled" && new Date(i.startsAt).getTime() > Date.now() + 90_000)
          .sort((a, b) => a.startsAt.localeCompare(b.startsAt));
        const next = ahead[0];
        const lead = next ? Math.min(120, Math.max(10, Math.ceil((new Date(next.startsAt).getTime() - Date.now()) / 60_000) + 2)) : 10;
        const nextTitle = next ? `${next.className} ${next.subjectName} at ${hm(new Date(next.startsAt))}` : "S2B";
        await put(AD, "/api/v1/staff/policy", { ...policy0, quietHours: { ...(policy0.quietHours ?? {}), enabled: false }, myDayLocalTime: digestAt, lessonReminderMinutes: lead });
        try {
          truthy(`REMINDER: the Mathematics teacher is reminded of the lesson starting at ${next ? hm(new Date(next.startsAt)) : "?"}, before it starts`,
            !!next && !!(await sweepFor("staff-reminder-ladder", async () => (await count(TB.token, "staff.lesson-reminder", nextTitle)) >= 1)),
            `no reminder matching "${nextTitle}" (lead ${lead} min); the teacher's day: ${JSON.stringify(((await mine(TB.token, TB.id)).json?.items ?? []).map((i) => [i.periodLabel, i.startsAt, i.status]))}`);
          truthy("MY DAY DIGEST: the Physics teacher gets one morning line for today", !!(await sweepFor("staff-reminder-ladder", async () => ((await get(TA.token, "/api/v1/notifications?eventKey=staff.my-day&limit=20")).json ?? []).some((n) => /^Today: /.test(n.title)))));
          const reminders = await count(TB.token, "staff.lesson-reminder", "S2B");
          const digests = ((await get(TA.token, "/api/v1/notifications?eventKey=staff.my-day&limit=20")).json ?? []).length;
          await trigger("staff-reminder-ladder");
          await sleep(6000);
          eq("REMINDER: a second sweep does not remind again", await count(TB.token, "staff.lesson-reminder", "S2B"), reminders);
          eq("MY DAY DIGEST: …nor send a second digest", ((await get(TA.token, "/api/v1/notifications?eventKey=staff.my-day&limit=20")).json ?? []).length, digests);
        } finally {
          await put(AD, "/api/v1/staff/policy", policy0);
        }
      }

      // ---- A replacement version moves a lesson: the old one is cancelled, the new one made, the teacher told ----
      const copy = await post(AD, `${T}/timetables`, { name: `E2E ${RUN} Lessons v2`, effectiveFrom: plus(-1), effectiveTo: plus(20), copyFromTimetableId: tt?.id });
      const v2 = copy.json?.timetable; if (v2?.id) drafts.push(v2.id);
      const ld = (copy.json?.lessons ?? []).find((l) => l.periodKey === "LD");
      await put(AD, `${T}/timetables/${v2?.id}/lessons/${ld?.id}`, { cycleDay, periodKey: "LE", room: null });
      const pub2 = await post(AD, `${T}/timetables/${v2?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
      if (pub2.json?.timetable?.status === "Published") { published.push(v2.id); drafts.splice(drafts.indexOf(v2.id), 1); }
      const regen = await post(AD, `${LS}/generate`);
      // Publishing enqueues its own run, which usually beats this one: assert the outcome, not who produced it.
      truthy("REPUBLISH: generation after the re-publish succeeds, whichever run moved the lesson", regen.status === 200, JSON.stringify(regen.json));
      const m2 = (await mine(TB.token, TB.id)).json?.items ?? [];
      const m1b = (await mine(TA.token, TA.id)).json?.items ?? [];
      eq("REPUBLISH: a lesson cancelled for a school event stays cancelled in the new version", m1b.filter((i) => i.periodLabel === "LC").map((i) => i.status).join(","), "Cancelled");
      truthy("REPUBLISH: the Mathematics teacher's day now has LE and no longer LD", m2.some((i) => i.periodLabel === "LE" && i.status === "Scheduled") && !m2.some((i) => i.periodLabel === "LD" && i.status !== "Cancelled"), JSON.stringify(m2.map((i) => [i.periodLabel, i.status])));
      const settingsDigest = (policy0.myDayLocalTime ?? "06:30");
      if (hm(new Date()) >= settingsDigest) {
        truthy("REPUBLISH: after the morning digest, the teacher is told today's lesson moved", !!(await waitFor(async () => ((await get(TB.token, "/api/v1/notifications?eventKey=staff.lesson-changed&limit=30")).json ?? []).some((n) => n.title === "A lesson today moved"), 15000)));
      }
    } finally {
      for (const id of published) await post(AD, `${T}/timetables/${id}/archive`);
      for (const id of drafts) await del(AD, `${T}/timetables/${id}`);
      // With no version in force, a run cancels the future, unflagged lessons this block made.
      await post(AD, `${LS}/generate`);
      for (const id of endIds) await del(AD, `${B}/class-teachers/${id}`, { reason: `E2E ${RUN} clean-up` });
      if (settings0) await put(AD, `${T}/settings`, { ...settings0, isSaved: undefined });
      for (const m of made) await put(AD, `/api/v1/users/${m.id}`, { firstName: m.first, lastName: `Lessons ${RUN}`, email: `${m.username}@qmgr.local`, roleId: roles.find((x) => x.code === "teacher")?.id, assignedBranchId: BRANCH, isActive: false });
    }
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("15.7 REPORTS — teaching load, lessons taught, recovery schedule, rota compliance, timetable health, dashboard tiles");
{
  const R = `${B}/staff/reports/teaching`;
  const T = `${B}/timetable`;
  const subjects = (await get(AD, `${B}/staff/subjects`)).json ?? [];
  const MATHS = subjects.find((s) => /^mathematics$/i.test(s.name));
  const assigned = await post(AD, `${B}/class-teachers/subject-teachers`, { className: "S2B", userId: U.math2.id, subjectId: MATHS?.id, periodsPerWeek: 22 });
  const assignmentId = assigned.json?.id ?? (((await get(AD, `${B}/class-teachers/coverage`)).json?.classes ?? []).flatMap((c) => c.subjectTeachers ?? [])
    .find((a) => a.userId === U.math2.id && a.className === "S2B" && a.subjectId === MATHS?.id)?.id);
  if (!assigned.json?.id && assignmentId) await call(AD, "PATCH", `${B}/class-teachers/${assignmentId}/periods`, { periodsPerWeek: 22 });
  let published = null;
  try {
    eq("ACCESS: a teacher cannot read the teaching reports (403)", (await get(U.lang1.token, R)).status, 403);
    eq("ACCESS: …nor the dashboard tiles (403)", (await get(U.lang1.token, `${R}/dashboard`)).status, 403);

    const hod = await get(U.hod.token, R);
    eq("SCOPE: the head of Mathematics reads them (200)", hod.status, 200);
    truthy("SCOPE: …and is told they are scoped", (hod.json?.scopedToDepartments ?? []).length > 0, JSON.stringify(hod.json?.scopedToDepartments));
    const names = (list) => (list ?? []).map((r) => r.name);
    truthy("SCOPE: no row for a teacher outside Mathematics — load, lessons or rota", ![...names(hod.json?.load), ...names(hod.json?.lessonsByTeacher), ...names(hod.json?.rota)].includes(U.lang1.name),
      JSON.stringify({ load: names(hod.json?.load), rota: names(hod.json?.rota) }));
    eq("SCOPE: timetable health is for an unscoped timetable master only", hod.json?.timetable, null);

    const row = (hod.json?.load ?? []).find((r) => r.userId === U.math2.id);
    truthy("LOAD: 22 planned periods a week reads Within the 20–24 band (no timetable in force, so planned is the measure)", row?.planned >= 22 && row?.band === "Within", JSON.stringify(row));
    truthy("LOAD: the by-subject breakdown carries S2B Mathematics", (row?.bySubject ?? []).some((s) => s.classes.includes("S2B") && s.planned >= 22), JSON.stringify(row?.bySubject));
    truthy("LOAD: aggregates by department, subject and level are present", (hod.json?.loadByDepartment ?? []).length > 0 && (hod.json?.loadBySubject ?? []).length > 0 && (hod.json?.loadByLevel ?? []).length > 0);

    // Section 15.6's own teacher: a lesson missed with permission and recovered, in this term.
    const tina = (hod.json?.lessonsByTeacher ?? []).find((r) => r.name === `Tina Lessons ${RUN}`);
    if (tina) {
      truthy("LESSONS: the lesson 15.6 recovered counts as Recovered and taught % is 100", tina.recovered >= 1 && tina.taughtPercent === 100, JSON.stringify(tina));
      truthy("RECOVERY SCHEDULE: the missed lesson is on it, with its reason and its recovery", (hod.json?.recoverySchedule ?? []).some((r) => r.teacherName === `Tina Lessons ${RUN}` && r.recoveryAt && /parents/.test(r.reason ?? "")),
        JSON.stringify((hod.json?.recoverySchedule ?? []).filter((r) => r.teacherName === `Tina Lessons ${RUN}`)));
    } else {
      console.log("  \x1b[33mSKIP\x1b[0m  lessons-taught figures for 15.6's teacher (run 15.6 first in the same run)");
    }
    truthy("LESSONS: totals, by week, by subject and by class are built", hod.json?.lessonsTotal && Array.isArray(hod.json?.lessonsByWeek) && Array.isArray(hod.json?.lessonsBySubject) && Array.isArray(hod.json?.lessonsByClass));
    truthy("ROTA: rota and report compliance rows exist for the department (15.1–15.4 put its teachers on duty)", (hod.json?.rota ?? []).length > 0, JSON.stringify(names(hod.json?.rota)));

    // A timetable in force, so the master sees health, rooms and the sweep's trend.
    const plus = (n) => new Date(Date.now() + n * 86400_000).toISOString().slice(0, 10);
    const tt = (await post(AD, `${T}/timetables`, { name: `E2E ${RUN} Reports`, effectiveFrom: plus(-1), effectiveTo: plus(6) })).json?.timetable;
    const settings = (await get(AD, `${T}/settings`)).json;
    const firstKey = settings?.dayTypes?.[0]?.periods?.find((p) => p.kind === "Lesson")?.key;
    const vocab = (await get(AD, `${B}/students/vocabularies`)).json;
    const room = (vocab?.rooms ?? []).find((r) => r.isActive)?.name ?? null;
    await post(AD, `${T}/timetables/${tt?.id}/lessons`, { cycleDay: 1, periodKey: firstKey, className: "S2B", subjectId: MATHS?.id, teacherUserIds: [U.math2.id], room, alsoClassNames: [] });
    const pub = await post(AD, `${T}/timetables/${tt?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
    if (pub.json?.timetable?.status === "Published") published = tt.id;
    truthy("SETUP: a timetable in force is published", !!published, pub.text?.slice(0, 200));

    const probe = await trigger("staff-activity-attribution-purge");
    // The sweep is a Hangfire job, so "triggered" is not "finished": a fixed sleep made this
    // assertion fail on a cold or busy API while the sweep was still running. Poll for the trend
    // instead — what is under test is that the sweep records one, not how fast it gets there.
    if (probe < 300 || probe === 204)
      await sweepFor("timetable-integrity", async () => ((await get(AD, R)).json?.timetable?.trend ?? []).length >= 1);
    const admin = (await get(AD, R)).json;
    truthy("HEALTH: the master sees the version in force, its clash counts, and the load still to place", admin?.timetable?.timetableName === `E2E ${RUN} Reports` && typeof admin?.timetable?.hard === "number" && admin?.timetable?.unplacedAssignments >= 1,
      JSON.stringify({ n: admin?.timetable?.timetableName, h: admin?.timetable?.hard, u: admin?.timetable?.unplacedAssignments }));
    truthy("HEALTH: room use is reported per configured room", !room || (admin?.timetable?.rooms ?? []).some((r) => r.room === room && r.periodsUsed >= 1), JSON.stringify(admin?.timetable?.rooms?.slice(0, 3)));
    if (probe < 300 || probe === 204)
      truthy("HEALTH: the integrity sweep's daily check is the trend", (admin?.timetable?.trend ?? []).length >= 1, JSON.stringify(admin?.timetable?.trend));
    const load2 = (admin?.load ?? []).find((r) => r.userId === U.math2.id);
    truthy("LOAD: with a timetable in force the measure is what it places — 1 of 22 reads Under", load2?.timetabled === 1 && load2?.band === "Under", JSON.stringify(load2));

    const dashAdmin = (await get(AD, `${R}/dashboard`)).json;
    truthy("DASHBOARD: the master's tiles carry this week's lessons and a hard-clash count", typeof dashAdmin?.lessonsThisWeek === "number" && typeof dashAdmin?.hardClashes === "number", JSON.stringify(dashAdmin));
    const dashHod = (await get(U.hod.token, `${R}/dashboard`)).json;
    truthy("DASHBOARD: the head's tiles are scoped and carry no clash count", dashHod?.hardClashes == null && (dashHod?.scopedToDepartments ?? []).length > 0, JSON.stringify(dashHod));
  } finally {
    if (published) await post(AD, `${T}/timetables/${published}/archive`);
    await post(AD, `${B}/staff/lessons/generate`);
    if (assigned.json?.id) await del(AD, `${B}/class-teachers/${assigned.json.id}`, { reason: `E2E ${RUN} clean-up` });
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("15.8 IMPORT — a timetable export into a draft: resolution, joint lessons, refusals, replace, one import at a time");
{
  const T = `${B}/timetable`;
  const plus = (n) => new Date(Date.now() + n * 86400_000).toISOString().slice(0, 10);
  const vocab = (await get(AD, `${B}/students/vocabularies`)).json;
  const room = (vocab?.rooms ?? []).find((r) => r.isActive)?.name ?? "";
  const drafts = [];
  let published = null;
  const importSubjects = (await get(AD, `${B}/staff/subjects`)).json ?? [];
  const importAssign = await post(AD, `${B}/class-teachers/subject-teachers`, { className: "S2B", userId: U.math2.id, subjectId: importSubjects.find((x) => /^mathematics$/i.test(x.name))?.id, periodsPerWeek: 1 });
  try {
    const draft = (await post(AD, `${T}/timetables`, { name: `E2E ${RUN} Import`, effectiveFrom: plus(40), effectiveTo: plus(60) })).json?.timetable;
    if (draft?.id) drafts.push(draft.id);
    const I = `${T}/timetables/${draft?.id}/import`;
    const m2email = `e2e.sp.math2@qmgr.local`;
    const rows = [
      { day: "Mon", period: "P1", class: "S2B", subject: "Mathematics", teacher: m2email, room },
      { day: "Monday", period: "08:40", class: "S2A", subject: "Mathematics", teacher: "e2e.sp.math2", room: "" },
      { day: "Tue", period: "P1", class: "S2A", subject: "Physics", teacher: "e2e.sp.math1@qmgr.local", room: "" },
      { day: "2", period: "P1", class: "S2B", subject: "Physics", teacher: "e2e.sp.math1", room: "" },
      { day: "Mon", period: "P1", class: "S2B", subject: "Mathematics", teacher: m2email, room },
      { day: "Mon", period: "P1", class: "S2A", subject: "Physics", teacher: m2email, room: "" },
      { day: "Sunday", period: "P1", class: "S2B", subject: "Mathematics", teacher: m2email, room: "" },
      { day: "Mon", period: "BRK", class: "S2B", subject: "Mathematics", teacher: m2email, room: "" },
      { day: "Mon", period: "P3", class: "Z9Z", subject: "Mathematics", teacher: m2email, room: "" },
      { day: "Mon", period: "P3", class: "S2B", subject: `NOPE${RUN}`, teacher: m2email, room: "" },
      { day: "Mon", period: "P3", class: "S2B", subject: "Mathematics", teacher: "Nobody Here", room: "" },
      { day: "Mon", period: "P3", class: "S2B", subject: "Mathematics", teacher: m2email, room: `Nowhere ${RUN}` },
    ];
    eq("ACCESS: a head of department cannot import a timetable (403)", (await post(U.hod.token, I, { rows })).status, 403);

    const both = await Promise.all([post(AD, I, { rows, sourceFileName: "asc-export.csv" }), post(AD, I, { rows, sourceFileName: "asc-export.csv" })]);
    eq("CONCURRENT: two uploads into the same draft at once — one 202, one 409", both.map((r) => r.status).sort().join(","), "202,409");
    const jobId = both.find((r) => r.status === 202)?.json?.id;
    const done = await waitFor(async () => { const j = (await get(AD, `${T}/import-jobs/${jobId}`)).json; return j && /Completed/.test(j.status) ? j : null; }, 30000);
    truthy("JOB: the import completes, with errors reported", done?.status === "CompletedWithErrors", JSON.stringify(done));
    eq("JOB: 4 lessons created, 1 duplicate skipped, 7 rows refused", `${done?.createdCount}/${done?.duplicateCount}/${done?.failedCount}`, "4/1/7");
    const entries = (await get(AD, `${T}/import-jobs/${jobId}/entries`)).json ?? [];
    const msg = (n) => entries.find((e) => e.rowNumber === n)?.message ?? "";
    truthy("RESOLVE: 'Monday' and a start time '08:40' find Mon P2; a username finds the teacher", /Mon P2: S2A MATH/.test(msg(2)), msg(2));
    truthy("RESOLVE: day '2' is Tuesday, and a second class for the same teacher and subject is a joint lesson", /Tue P1: S2B PHY \(joint lesson\)/.test(msg(4)), msg(4));
    truthy("REFUSE: the same teacher teaching another subject then is refused, naming the class", /already teaches S2B then/.test(msg(6)), msg(6));
    truthy("REFUSE: an unknown day, a break, an unknown class, subject, teacher and room each say why", /not a day/.test(msg(7)) && /not a teaching period/.test(msg(8)) && /not a configured, active class/.test(msg(9)) && /not an active subject/.test(msg(10)) && /not on this branch's staff/.test(msg(11)) && /not a configured room/.test(msg(12)),
      JSON.stringify([7, 8, 9, 10, 11, 12].map(msg)));
    const detail = (await get(AD, `${T}/timetables/${draft?.id}`)).json;
    const lessons = detail?.lessons ?? [];
    const joint = lessons.filter((l) => l.cycleDay === 2 && l.periodKey === "P1");
    truthy("DRAFT: four lesson rows, the two Tuesday Physics rows sharing one group", lessons.length === 4 && joint.length === 2 && joint[0].groupId && joint[0].groupId === joint[1].groupId, JSON.stringify(lessons.map((l) => [l.cycleDay, l.periodKey, l.className, l.groupId])));

    const replace = await post(AD, I, { rows: [rows[0]], replaceExisting: true });
    const done2 = await waitFor(async () => { const j = (await get(AD, `${T}/import-jobs/${replace.json?.id}`)).json; return j && /Completed/.test(j.status) ? j : null; }, 30000);
    eq("REPLACE: importing with 'replace' leaves exactly the file's lessons", `${done2?.createdCount}/${((await get(AD, `${T}/timetables/${draft?.id}`)).json?.lessons ?? []).length}`, "1/1");

    const pub = await post(AD, `${T}/timetables/${draft?.id}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN}` });
    if (pub.json?.timetable?.status === "Published") { published = draft.id; drafts.splice(drafts.indexOf(draft.id), 1); }
    truthy("SETUP: the imported draft publishes (its one lesson is assigned)", !!published, pub.text?.slice(0, 200));
    eq("IMMUTABLE: a published timetable cannot be imported into (409)", (await post(AD, I, { rows })).status, 409);
    const log = (await get(AD, `${B}/staff/activity?action=timetable.imported&pageSize=10`)).json;
    truthy("ACTIVITY: the import is logged with its row count", (log?.items ?? []).some((e) => e.summary.includes(`E2E ${RUN} Import`) && /12 row/.test(e.summary)), JSON.stringify((log?.items ?? []).slice(0, 2).map((e) => e.summary)));
  } finally {
    if (published) await post(AD, `${T}/timetables/${published}/archive`);
    for (const id of drafts) await del(AD, `${T}/timetables/${id}`);
    if (importAssign.json?.id) await del(AD, `${B}/class-teachers/${importAssign.json.id}`, { reason: `E2E ${RUN} clean-up` });
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("15.9 WEEKLY LESSON ANALYSIS — the Monday report, forced on any day, in each reader's own scope");
{
  const T = `${B}/timetable`;
  const RPT = `${B}/staff/reports/teaching`;
  const WEEKLY = `${RPT}/weekly-analysis/run`;
  const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
  const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  // Branch-local dates throughout: the job anchors its week on the branch's own Monday, and lessons
  // materialise per branch-local day. The month names are spelled out rather than taken from Intl,
  // which gives en-GB "Sept" where the API's InvariantCulture writes "Sep".
  const branchInfo = (await get(AD, `/api/v1/branches/${BRANCH}`)).json ?? {};
  const TZ = branchInfo.timezone ?? branchInfo.timeZone ?? "Africa/Kampala";
  const localOf = (d, opts) => new Intl.DateTimeFormat("en-CA", { timeZone: TZ, ...opts }).format(d);
  const todayYmd = localOf(new Date(), { year: "numeric", month: "2-digit", day: "2-digit" });
  const nowHm = localOf(new Date(), { hourCycle: "h23", hour: "2-digit", minute: "2-digit" });
  const minutesOf = (hm) => Number(hm.slice(0, 2)) * 60 + Number(hm.slice(3, 5));
  const shift = (ymd, n) => { const d = new Date(`${ymd}T00:00:00Z`); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
  const dow = new Date(`${todayYmd}T00:00:00Z`).getUTCDay();
  // weekStart names the Monday whose PREVIOUS week is reported, so next Monday reports THIS week —
  // the week the lesson placed below lands in.
  const weekStart = shift(todayYmd, ((8 - dow) % 7) || 7);
  const ddMmm = (ymd) => `${ymd.slice(8, 10)} ${MONTHS[Number(ymd.slice(5, 7)) - 1]}`;
  const range = `${ddMmm(shift(weekStart, -7))} – ${ddMmm(shift(weekStart, -1))} ${shift(weekStart, -1).slice(0, 4)}`;

  const analyses = async (token) => ((await get(token, "/api/v1/notifications?eventKey=staff.lesson-analysis&limit=100")).json ?? []);
  const clearAnalyses = async (token) => { for (const n of await analyses(token)) await del(token, `/api/v1/notifications/${n.id}`); };

  // The Development-only trigger 404s elsewhere, and that check runs before any permission check —
  // so the teacher's refusal is also the probe for which environment this API is.
  const teacherRun = await post(U.math1.token, `${WEEKLY}?weekStart=${weekStart}`);
  if (teacherRun.status === 404) {
    console.log(`  \x1b[33mSKIP\x1b[0m  the weekly-analysis trigger is Development-only (404); the Monday report is asserted only against a Development API`);
  } else {
    eq("ACCESS: a teacher cannot run the weekly lesson analysis (403)", teacherRun.status, 403);
    eq("ACCESS: nor a department-scoped head — it reports for the whole branch (403)", (await post(U.hod.token, `${WEEKLY}?weekStart=${weekStart}`)).status, 403);

    const settings = (await get(AD, `${T}/settings`)).json;
    const teachingDays = [...new Set((settings?.dayTypes ?? []).flatMap((d) => d.days))].sort((a, b) => (DAYS.indexOf(a) + 6) % 7 - ((DAYS.indexOf(b) + 6) % 7));
    const cycleDay = teachingDays.indexOf(DAYS[dow]) + 1;
    const dayType = (settings?.dayTypes ?? []).find((d) => d.days.includes(DAYS[dow]));
    // Materialisation writes today forward and only periods that have not finished, so the lesson
    // has to be one still to come in the branch's own day.
    const periods = (dayType?.periods ?? []).filter((p) => p.kind === "Lesson" && minutesOf(p.end) >= minutesOf(nowHm) + 3)
      .sort((a, b) => minutesOf(a.start) - minutesOf(b.start));
    // …and a period the teacher is already expected at a MEETING during is a TeacherOnSessionDuty hard
    // clash, which never publishes. This tenant keeps section 14's staff meetings for ever — they carry
    // registers, so the API refuses to delete them — and every run of that suite adds another at whatever
    // time of day it ran, so "the first period left today" silently fills up for the seeded teachers as
    // the tenant ages. Choose the (teacher, period) pair from what each candidate actually has on today.
    // Both candidates sit in the Mathematics department, which is what makes the head's own-scope
    // assertion below mean anything: a lesson outside their scope would be reported to nobody.
    const utcAt = (hm) => {
      // The wall time hm on the branch's own today, as an instant: read back what the branch calls the
      // naive instant and correct by the difference, normalised so 23:00 does not read as minus 21 hours.
      const naive = new Date(`${todayYmd}T${hm}:00Z`);
      const drift = ((minutesOf(localOf(naive, { hourCycle: "h23", hour: "2-digit", minute: "2-digit" })) - minutesOf(hm) + 1440 + 720) % 1440) - 720;
      return new Date(naive.getTime() - drift * 60_000).getTime();
    };
    const midnight = utcAt("00:00");
    const sessionsOf = async (userId) => ((await get(AD, `${B}/staff/duties?kind=Session&from=${encodeURIComponent(iso(midnight - 86400_000))}&to=${encodeURIComponent(iso(midnight + 2 * 86400_000))}`)).json ?? [])
      .filter((d) => (d.expectedUserIds ?? []).includes(userId))
      .map((d) => [new Date(d.startsAt).getTime(), new Date(d.endsAt).getTime()]);
    let teacher = null, period = null;
    for (const candidate of [U.math1, U.math2]) {
      const busy = await sessionsOf(candidate.id);
      period = periods.find((p) => !busy.some(([s, e]) => s < utcAt(p.end) && e > utcAt(p.start)));
      if (period) { teacher = candidate; break; }
    }
    if (!cycleDay || !period || !teacher) {
      console.log(`  \x1b[33mSKIP\x1b[0m  15.9 reports the week a lesson lands in; at ${nowHm} the branch's ${DAYS[dow]} has no teaching period left that either Mathematics teacher is free for (${periods.map((p) => p.key).join(", ") || "none left"})`);
    } else {
      const subjects = (await get(AD, `${B}/staff/subjects`)).json ?? [];
      const MATHS = subjects.find((s) => /^mathematics$/i.test(s.name));
      // A teacher not assigned the subject in the class is a HARD clash, which never publishes. A 409 here
      // means the teacher already holds it, so this block created nothing and ends nothing at the close.
      const assigned = await post(AD, `${B}/class-teachers/subject-teachers`, { className: "S2B", userId: teacher.id, subjectId: MATHS?.id, periodsPerWeek: 2 });
      let published = null, draft = null;
      try {
        const tt = await post(AD, `${T}/timetables`, { name: `E2E ${RUN} Weekly analysis`, effectiveFrom: todayYmd, effectiveTo: shift(todayYmd, (7 - dow) % 7) });
        draft = tt.json?.timetable?.id ?? null;
        const place = await post(AD, `${T}/timetables/${draft}/lessons`, { cycleDay, periodKey: period.key, className: "S2B", subjectId: MATHS?.id, teacherUserIds: [teacher.id], room: null, alsoClassNames: [] });
        truthy(`SETUP: a lesson for ${teacher.name} is placed in ${DAYS[dow]} ${period.key}, with no hard clash`, place.status === 201 && place.json?.diagnosis?.hardCount === 0, `${place.status} ${place.text?.slice(0, 200)}`);
        const pub = await post(AD, `${T}/timetables/${draft}/publish`, { acknowledgeSoftClashes: true, note: `E2E ${RUN} weekly analysis` });
        if (pub.json?.timetable?.status === "Published") { published = draft; draft = null; }
        truthy("SETUP: the timetable publishes and is in force for the rest of the week", !!published, pub.text?.slice(0, 220));
        await post(AD, `${B}/staff/lessons/generate`);
        // Publishing enqueues its own materialisation, which usually beats this one: poll for the
        // lesson rather than sleeping on whichever run wrote it.
        truthy(`SETUP: the week under report (${range}) has lessons to report`, !!(await waitFor(async () => ((await get(AD, `${RPT}/dashboard`)).json?.lessonsThisWeek ?? 0) > 0)), JSON.stringify((await get(AD, `${RPT}/dashboard`)).json));

        // The once-a-week guard is anchored to the REAL current week, so a second run of this suite in
        // the same week would otherwise send nothing and every assertion below would pass vacuously.
        for (const token of [AD, U.hod.token, teacher.token]) await clearAnalyses(token);

        const run1 = await post(AD, `${WEEKLY}?weekStart=${weekStart}`);
        eq("RUN: the unscoped timetable master runs it (202)", run1.status, 202);
        truthy(`REPORT: the master holds an analysis titled for the week just gone (${range})`,
          !!(await waitFor(async () => (await count(AD, "staff.lesson-analysis", `Weekly lesson analysis — ${range}`)) >= 1)),
          JSON.stringify((await analyses(AD)).map((n) => n.title)));
        truthy("SCOPE: the head of Mathematics holds one for the same week, built in their own scope", (await count(U.hod.token, "staff.lesson-analysis", `Weekly lesson analysis — ${range}`)) >= 1, JSON.stringify((await analyses(U.hod.token)).map((n) => n.title)));
        eq("SCOPE: a teacher is sent none — a scope of only themselves is not oversight", (await analyses(teacher.token)).length, 0);

        const held = (await analyses(AD)).length;
        const run2 = await post(AD, `${WEEKLY}?weekStart=${weekStart}`);
        // The trigger awaits the job, so a 202 means whatever it was going to send is already written.
        eq("ONCE: a second run answers 202", run2.status, 202);
        eq("ONCE: …and sends nothing more this calendar week", (await analyses(AD)).length, held);
      } finally {
        if (published) await post(AD, `${T}/timetables/${published}/archive`);
        if (draft) await del(AD, `${T}/timetables/${draft}`);
        // With no version in force, a run cancels the future lessons this block's publish made.
        await post(AD, `${B}/staff/lessons/generate`);
        if (assigned.json?.id) await del(AD, `${B}/class-teachers/${assigned.json.id}`, { reason: `E2E ${RUN} clean-up` });
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------------
hdr("15.x Clean up");
for (const id of cleanupSeries) {
  const r = await del(AD, `${B}/staff/rota/series/${id}`);
  truthy("CANCEL SERIES: upcoming slots are cancelled", r.status === 200 && r.json?.cancelled >= 1, `${r.status} ${r.text}`);
  eq("CANCEL SERIES: none of the series is left active", (await rotaIn(AD, 0, 24 * 150)).filter((d) => d.seriesId === id).length, 0);
}
for (const id of cleanupDuties) await del(AD, `${B}/staff/duties/${id}`);

console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
if (failures.length) console.log("Failures:\n  - " + failures.join("\n  - "));
process.exit(fail);

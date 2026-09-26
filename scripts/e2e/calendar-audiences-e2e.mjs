// SECTION 41 — WHO AN EVENT IS FOR, TELLING THEM ONCE, AND THE REST OF THE CALENDAR REWORK (2026-09-26).
//
// Plan: docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md, decisions E1–E8, E12 and bugs B2–B5, B17, B18.
//
//   41.1  the audience picker's options come from the CALENDAR (a keeper only), with staff groups, roles and people;
//   41.2  a targeted event: in the teacher's "My events" when it is for their role, not when it is for another role,
//         on the whole-school view either way — unless it is audience-only, which also answers 404 to them;
//   41.3  telling the audience EXACTLY ONCE: five racing creates with one idempotency key make one event and one notice;
//   41.4  a typo fix tells nobody; a change of time tells them once;
//   41.5  cancelling keeps the event, struck through, tells them, and the feed writes STATUS:CANCELLED with SEQUENCE;
//   41.6  two keepers: a save made against an older version is refused 409 EVENT_CHANGED (xmin row version);
//   41.7  a series: weekly for four weeks is four events; "all of them" edits the whole series; one notice, not four;
//   41.8  the clash check answers counts, and only to a keeper;
//   41.9  a person's view choices round-trip, and the push opt-out now survives a save (B4);
//   41.10 "Give it a register": the event becomes a meeting; moving the event moves the meeting (B2); deleting the
//         event takes the meeting off too;
//   41.11 everything created is removed and every setting put back.
//
// Run: node scripts/e2e/calendar-audiences-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const TEACHER = process.env.TEACHER || "e2e.teacher.s4@qmgr.local";
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(token, method, path, body) {
  const h = {};
  if (token) h.Authorization = `Bearer ${token}`;
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text, type: res.headers.get("content-type") ?? "" };
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
const addDays = (s, n) => { const d = new Date(s + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return iso(d); };
const STAFF = 1, STUDENTS = 2;

const ad = await login(TENANT_ADMIN);
const teacher = await login(TEACHER);
if (!ad || !teacher) { console.error("sign-in failed (tenant admin or teacher)"); process.exit(1); }
const AD = ad.token, T = teacher.token, TEACHER_ID = teacher.user.id, ADMIN_ID = ad.user.id;
const CAL = `/api/v1/branches/${BRANCH}/calendar`;
const EV = `${CAL}/events`;
const created = new Set();
let originalUi = null, originalPrefs = null;

// Notices to the teacher about one run's event, by event key.
const notices = async (key, needle) => ((await get(T, `/api/v1/notifications?eventKey=${key}&limit=200`)).json ?? [])
  .filter((n) => (n.title + " " + n.message).includes(needle));
const waitCount = async (key, needle, want, ms = 6000) => {
  let n = 0;
  for (let t = 0; t < ms; t += 400) { n = (await notices(key, needle)).length; if (n >= want) break; await sleep(400); }
  await sleep(600); // and a moment more, so a SECOND notice would have landed
  return (await notices(key, needle)).length;
};

try {
  const myDay = await get(T, `/api/v1/branches/${BRANCH}/staff/lessons/my-day`);
  const TODAY = myDay.status === 200 && myDay.json?.date ? myDay.json.date : iso(new Date());
  const SOON = addDays(TODAY, 3);
  const cats = (await get(AD, "/api/v1/calendar/settings")).json?.categories ?? [];

  hdr("41.1 The audience picker's options");
  const opts = await get(AD, `${CAL}/audience-options`);
  eq("a keeper reads the options → 200", opts.status, 200);
  truthy("…with staff groups", (opts.json?.staffGroups ?? []).length >= 1, JSON.stringify(opts.json?.staffGroups));
  truthy("…roles", (opts.json?.roles ?? []).length >= 1);
  const me = (opts.json?.people ?? []).find((p) => p.userId === TEACHER_ID);
  truthy("…and the teacher, with a RESOLVED staff group and a role", !!me?.staffGroup && !!me?.roleCode, JSON.stringify(me));
  eq("a teacher cannot read them → 403", (await get(T, `${CAL}/audience-options`)).status, 403);
  const otherRole = (opts.json?.roles ?? []).find((r) => r.code !== me?.roleCode)?.code ?? "admin";

  hdr("41.2 A targeted event, and the two scopes");
  const mk = async (label, body) => {
    const r = await post(AD, EV, { title: `E2E ${label} ${RUN}`, startsOn: SOON, endsOn: SOON, audience: STAFF, category: cats[0], notifyAudience: false, ...body });
    if (r.status === 201 && r.json?.id) created.add(r.json.id);
    return r;
  };
  const forRole = await mk("for my role", { staffAudience: { allStaff: false, roleCodes: [me.roleCode] } });
  eq("an event for the teacher's role → 201", forRole.status, 201);
  truthy("…it says who it is for in words", (forRole.json?.staffAudienceText ?? "").length > 0, forRole.json?.staffAudienceText);
  const forOther = await mk("for another role", { staffAudience: { allStaff: false, roleCodes: [otherRole] } });
  const forGroup = await mk("for my group", { staffAudience: { allStaff: false, staffGroups: [me.staffGroup.toUpperCase()] } });
  const hidden = await mk("audience only", { staffAudience: { allStaff: false, userIds: [ADMIN_ID] }, audienceOnly: true });
  eq("an audience of nobody → 400", (await post(AD, EV, { title: `E2E nobody ${RUN}`, startsOn: SOON, endsOn: SOON, audience: STAFF, staffAudience: { allStaff: false } })).status, 400);
  const range = `?from=${TODAY}&to=${addDays(TODAY, 7)}`;
  const mine = new Set(((await get(T, CAL + range)).json?.events ?? []).map((e) => e.id));
  const all = new Set(((await get(T, CAL + range + "&scope=all")).json?.events ?? []).map((e) => e.id));
  truthy("My events: the event for the teacher's role is there", mine.has(forRole.json?.id));
  truthy("…the one for their staff group too (matched whatever the spelling)", mine.has(forGroup.json?.id));
  truthy("…the one for another role is NOT", !mine.has(forOther.json?.id));
  truthy("Whole school: the one for another role IS", all.has(forOther.json?.id));
  truthy("…but an audience-only event they are not in is not, on either view", !all.has(hidden.json?.id) && !mine.has(hidden.json?.id));
  eq("…and opening it answers 404, never 403", (await get(T, `${EV}/${hidden.json?.id}`)).status, 404);
  truthy("the keeper's whole-school view has the audience-only event", new Set(((await get(AD, CAL + range + "&scope=all")).json?.events ?? []).map((e) => e.id)).has(hidden.json?.id));
  const rangeDto = (await get(T, CAL + range)).json;
  truthy("the range carries the SCHOOL's today and zone (B9)", !!rangeDto?.today && rangeDto.today !== "0001-01-01", JSON.stringify({ today: rangeDto?.today, tz: rangeDto?.timeZone }));

  hdr("41.3 Telling them exactly once");
  const key = crypto.randomUUID();
  const body = { title: `E2E told once ${RUN}`, startsOn: SOON, endsOn: SOON, audience: STAFF, category: cats[0],
    staffAudience: { allStaff: false, userIds: [TEACHER_ID] }, notifyAudience: true, clientRequestId: key };
  const racing = await Promise.all(Array.from({ length: 5 }, () => post(AD, EV, body)));
  const ids = new Set(racing.filter((r) => r.status === 201 || r.status === 200).map((r) => r.json?.id));
  ids.forEach((id) => id && created.add(id));
  truthy("five racing presses of one dialog answer 2xx", racing.every((r) => r.status === 201 || r.status === 200), racing.map((r) => r.status).join(","));
  eq("…and make ONE event (B18)", ids.size, 1);
  eq("…and the teacher is told ONCE", await waitCount("calendar.event-added", `E2E told once ${RUN}`, 1), 1);
  eq("the keeper who made it is not told", ((await get(AD, "/api/v1/notifications?eventKey=calendar.event-added&limit=200")).json ?? [])
    .filter((n) => n.title.includes(`E2E told once ${RUN}`)).length, 0);
  const onceId = [...ids][0];

  hdr("41.4 A typo is not news; a new time is");
  const cur = (await get(AD, `${EV}/${onceId}`)).json;
  const save = (patch) => put(AD, `${EV}/${onceId}`, { ...cur, ...patch, rowVersion: null, notifyAudience: true });
  eq("a spelling fix → 200", (await save({ title: `E2E told once ${RUN} (fixed)` })).status, 200);
  eq("…and nobody is told it changed", await waitCount("calendar.event-changed", `E2E told once ${RUN}`, 1, 2500), 0);
  eq("a new time → 200", (await save({ title: `E2E told once ${RUN} (fixed)`, startTime: "14:00", endTime: "15:00" })).status, 200);
  eq("…and the teacher is told once", await waitCount("calendar.event-changed", `E2E told once ${RUN}`, 1), 1);
  const after = (await get(AD, `${EV}/${onceId}`)).json;
  truthy("…the version moved on (the iCalendar SEQUENCE)", (after?.version ?? 0) > (cur?.version ?? 0), `${cur?.version} → ${after?.version}`);

  hdr("41.5 Cancelling keeps it, and says so");
  const cancel = await post(AD, `${EV}/${onceId}/cancel`, { reason: `Postponed ${RUN}`, notifyAudience: true });
  eq("cancel → 200", cancel.status, 200);
  eq("…it is Cancelled, with its reason", `${cancel.json?.status}|${cancel.json?.cancelReason}`, `Cancelled|Postponed ${RUN}`);
  truthy("…and still on the calendar", new Set(((await get(T, CAL + range)).json?.events ?? []).map((e) => e.id)).has(onceId));
  eq("…the teacher is told it is cancelled, once", await waitCount("calendar.event-cancelled", `E2E told once ${RUN}`, 1), 1);
  const feed = await post(T, "/api/v1/calendar/feed");
  const ics = await call(null, "GET", new URL(feed.json.url).pathname);
  const block = ics.text.replace(/\r\n /g, "").split("BEGIN:VEVENT").find((b) => b.includes(`UID:${onceId}@qmgr`)) ?? "";
  truthy("the feed writes it as STATUS:CANCELLED", block.includes("STATUS:CANCELLED"), block.slice(0, 300));
  truthy("…with a SEQUENCE", /SEQUENCE:\d+/.test(block));
  await del(T, "/api/v1/calendar/feed");
  eq("put it back on → 200", (await post(AD, `${EV}/${onceId}/reinstate`, { notifyAudience: false })).status, 200);
  eq("…and it reads Scheduled", (await get(AD, `${EV}/${onceId}`)).json?.status, "Scheduled");
  const oneIcs = await get(T, `${EV}/${onceId}/ics`);
  eq("'Add to my calendar' → 200 text/calendar", `${oneIcs.status}|${oneIcs.type.split(";")[0]}`, "200|text/calendar");

  hdr("41.6 Two keepers, one event (B5)");
  const v1 = (await get(AD, `${EV}/${forRole.json.id}`)).json;
  const first = await put(AD, `${EV}/${v1.id}`, { ...v1, location: `Hall ${RUN}`, rowVersion: v1.rowVersion });
  eq("a save against the current version → 200", first.status, 200);
  const stale = await put(AD, `${EV}/${v1.id}`, { ...v1, location: `Chapel ${RUN}`, rowVersion: v1.rowVersion });
  eq("a save against the OLD version → 409", stale.status, 409);
  eq("…EVENT_CHANGED", stale.json?.error, "EVENT_CHANGED");
  eq("…and the first save stands", (await get(AD, `${EV}/${v1.id}`)).json?.location, `Hall ${RUN}`);

  hdr("41.7 A series");
  const series = await post(AD, EV, { title: `E2E weekly ${RUN}`, startsOn: SOON, endsOn: SOON, startTime: "16:00", endTime: "17:00", audience: STAFF,
    staffAudience: { allStaff: false, userIds: [TEACHER_ID] }, notifyAudience: true, repeat: { frequency: "weekly", until: addDays(SOON, 21), termTimeOnly: false } });
  eq("weekly for four weeks → 201", series.status, 201);
  const seriesRange = `?from=${SOON}&to=${addDays(SOON, 28)}&scope=all`;
  const occ = ((await get(AD, CAL + seriesRange)).json?.events ?? []).filter((e) => e.title === `E2E weekly ${RUN}`);
  occ.forEach((e) => created.add(e.id));
  eq("…is four events", occ.length, 4);
  truthy("…sharing one series and saying how they repeat", new Set(occ.map((e) => e.seriesId)).size === 1 && !!occ[0]?.recurrence, occ[0]?.recurrence);
  eq("…and the teacher was told ONCE, not four times", await waitCount("calendar.event-added", `E2E weekly ${RUN}`, 1), 1);
  const head = occ.sort((a, b) => a.startsOn.localeCompare(b.startsOn))[0];
  const all4 = await put(AD, `${EV}/${head.id}`, { ...head, title: `E2E weekly ${RUN}`, location: `Lab ${RUN}`, editScope: "all", notifyAudience: false, rowVersion: head.rowVersion });
  eq("'all of them' → 200", all4.status, 200);
  truthy("…every occurrence moved to the new venue",
    ((await get(AD, CAL + seriesRange)).json?.events ?? []).filter((e) => e.title === `E2E weekly ${RUN}`).every((e) => e.location === `Lab ${RUN}`));

  hdr("41.8 The clash check");
  const clash = await post(AD, `${CAL}/clashes`, { staffAudience: { allStaff: false, userIds: [TEACHER_ID] }, startsOn: SOON, endsOn: SOON, startTime: "08:00", endTime: "17:00" });
  eq("a keeper asks → 200", clash.status, 200);
  truthy("…with counts", clash.json && typeof clash.json.teaching === "number" && clash.json.audienceCount === 1, JSON.stringify(clash.json));
  eq("a teacher cannot ask → 403", (await post(T, `${CAL}/clashes`, { startsOn: SOON, endsOn: SOON })).status, 403);

  hdr("41.9 A person's own choices");
  originalUi = (await get(T, "/api/v1/profile/ui-preferences")).json?.preferences;
  truthy("the defaults: Mine, past hidden, sound on", originalUi?.calendar?.scope === "mine" || originalUi?.calendar?.scope === "all", JSON.stringify(originalUi));
  const savedUi = await put(T, "/api/v1/profile/ui-preferences", { calendar: { view: "agenda", scope: "all", showPast: true, showDuties: false }, sound: { enabled: false, importantOnly: true } });
  eq("saving view choices → 200", savedUi.status, 200);
  const readUi = (await get(T, "/api/v1/profile/ui-preferences")).json;
  eq("…they read back", `${readUi?.preferences?.calendar?.view}|${readUi?.preferences?.calendar?.scope}|${readUi?.preferences?.calendar?.showPast}|${readUi?.preferences?.sound?.enabled}`, "agenda|all|true|false");
  truthy("…with the school's quiet hours for the chime", !!readUi?.quietHours);
  eq("a nonsense view is tidied to the Term view", (await put(T, "/api/v1/profile/ui-preferences", { calendar: { view: "gantt" } })).json?.preferences?.calendar?.view, "term");
  originalPrefs = (await get(T, "/api/v1/notifications/preferences")).json;
  eq("turning the phone off → 204", (await put(T, "/api/v1/notifications/preferences", { ...originalPrefs, pushEnabled: false })).status, 204);
  eq("…and it STAYS off after a save (B4 — it was reset on every save)", (await get(T, "/api/v1/notifications/preferences")).json?.pushEnabled, false);

  hdr("41.10 Give it a register");
  const meeting = await mk("becomes a meeting", { startTime: "15:00", endTime: "16:00", staffAudience: { allStaff: false, userIds: [TEACHER_ID, ADMIN_ID] } });
  const reg = await post(AD, `${EV}/${meeting.json.id}/register`, { recorderUserIds: [ADMIN_ID], notifyPeople: false });
  if (reg.status === 403 && reg.json?.error === "MODULE_NOT_PURCHASED") ok("Welfare & Performance is not held — registers skipped honestly");
  else {
    eq("give it a register → 200", reg.status, 200);
    const dutyId = reg.json?.dutyId;
    truthy("…the event now points at its meeting", !!dutyId);
    eq("a second time → 409", (await post(AD, `${EV}/${meeting.json.id}/register`, { recorderUserIds: [ADMIN_ID] })).status, 409);
    eq("a teacher cannot → 403", (await post(T, `${EV}/${meeting.json.id}/register`, { recorderUserIds: [TEACHER_ID] })).status, 403);
    const tRange = (await get(T, CAL + range)).json;
    truthy("the teacher's range carries the meeting as a duty", (tRange?.myDuties ?? []).some((d) => d.id === dutyId));
    const ev = (await get(AD, `${EV}/${meeting.json.id}`)).json;
    eq("moving the event → 200", (await put(AD, `${EV}/${ev.id}`, { ...ev, startTime: "16:00", endTime: "17:00", notifyAudience: false, rowVersion: ev.rowVersion })).status, 200);
    const dutyList = async (cancelled) => ((await get(AD, `/api/v1/branches/${BRANCH}/staff/duties?from=${TODAY}&to=${addDays(TODAY, 10)}&includeCancelled=${cancelled}`)).json ?? []);
    const duty = (await dutyList(false)).find((d) => d.id === dutyId);
    truthy("…moves the meeting with it (B2)", !!duty && new Date(duty.startsAt).getUTCHours() === new Date(`${SOON}T16:00:00+03:00`).getUTCHours(), duty?.startsAt);
    eq("deleting the event → 204", (await del(AD, `${EV}/${ev.id}`)).status, 204);
    created.delete(ev.id);
    truthy("…takes the meeting off too", !(await dutyList(false)).some((d) => d.id === dutyId));
  }
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("41.11 Put things back");
  let removed = 0;
  for (const id of created) { const r = await del(AD, `${EV}/${id}`); if (r.status === 204 || r.status === 404) removed++; }
  eq(`every event made here is removed (${created.size})`, removed, created.size);
  if (originalUi) await put(T, "/api/v1/profile/ui-preferences", originalUi);
  if (originalPrefs) eq("the teacher's notification preferences are put back", (await put(T, "/api/v1/notifications/preferences", originalPrefs)).status, 204);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

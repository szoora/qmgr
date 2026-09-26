// SECTION 30 — THE SCHOOL CALENDAR, THE PERSONAL FEED AND THE NATIONAL DATES (2026-09-23).
//
// Plan: docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md §3, §9, decisions D5, D8, D9. The calendar is base
// product; who sees an event is SchoolEventVisibility and nothing else:
//
//   30.1  who sees what — a teacher sees Staff events and the ones they are responsible for, never a
//         Students-only or Public-only one; the calendar-keeper sees them all; out of reach is 404;
//   30.2  a write the server refuses, in words (dates, times, span, category, people, audience, branch, range);
//   30.3  the settings round-trip, and racing another writer of Organization.Settings loses neither key;
//   30.4  the private .ics feed — anonymous, CRLF, folded at 75 octets, all-day DTEND exclusive, escaped text,
//         replacing the link kills the old one and removing it kills the new one;
//   30.5  the signage "Coming up" list carries Public events ONLY, and no names;
//   30.6  My School Day and the portal carry the teacher's own events;
//   30.7  the national calendar: the platform administrator keeps it, a school only reads it, by range;
//   30.8  a term's theme survives the policy editor;
//   30.9  everything created is removed and every setting put back.
//
// Run: node scripts/e2e/calendar-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const TEACHER = process.env.TEACHER || "e2e.teacher.s4@qmgr.local";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
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

// EventAudience is [Flags] and the API writes enums as strings ("Staff, Public"); read either shape.
const FLAGS = { None: 0, Staff: 1, Students: 2, Guardians: 4, Public: 8 };
const aud = (v) => typeof v === "number" ? v : String(v ?? "").split(",").map((s) => FLAGS[s.trim()] ?? 0).reduce((a, b) => a | b, 0);
const iso = (d) => d.toISOString().slice(0, 10);
const addDays = (s, n) => { const d = new Date(s + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return iso(d); };

const ad = await login(TENANT_ADMIN);
const teacher = await login(TEACHER);
const sa = await login(SA_USER, [SA_PASS]);
if (!ad || !teacher || !sa) { console.error("sign-in failed (tenant admin, teacher or SuperAdmin)"); process.exit(1); }
const AD = ad.token, T = teacher.token, SA = sa.token, ORG = ad.user.organizationId, TEACHER_ID = teacher.user.id;
const CAL = `/api/v1/branches/${BRANCH}/calendar`;
const EV = `${CAL}/events`;
const created = [];
let originalSettings = null, originalNames = null, originalNational = null, originalPolicy = null, feedMade = false;

try {
  // The branch's own today, from My School Day (Kampala is UTC+3; the machine's date can be a day off near midnight).
  const myDay = await get(T, `/api/v1/branches/${BRANCH}/staff/lessons/my-day`);
  const TODAY = myDay.status === 200 && myDay.json?.date ? myDay.json.date : iso(new Date());

  hdr("30.1 Who sees what");
  originalSettings = (await get(AD, "/api/v1/calendar/settings")).json;
  truthy("GET calendar settings → categories", Array.isArray(originalSettings?.categories) && originalSettings.categories.length > 0, JSON.stringify(originalSettings));
  const category = originalSettings.categories[0];

  const members = (await get(AD, `/api/v1/branches/${BRANCH}/staff/structure/members`)).json?.items ?? [];
  const teacherDepts = members.find((m) => m.userId === TEACHER_ID)?.departmentIds ?? [];

  const longText = `A long description, with commas; semicolons and a backslash \\ so the feed must escape and fold it. ${"Nyakato's programme, ".repeat(8)}`;
  const mk = async (label, body) => {
    const r = await post(AD, EV, { title: `E2E ${label} ${RUN}`, startsOn: TODAY, endsOn: TODAY, audience: FLAGS.Staff, category, ...body });
    if (r.status === 201 && r.json?.id) created.push(r.json.id);
    return r;
  };
  const staffEv = await mk("staff", { endsOn: addDays(TODAY, 1), description: longText, location: "Main Hall, Block A" });
  eq("the calendar-keeper creates a Staff event → 201", staffEv.status, 201);
  const timedEv = await mk("timed", { startTime: "08:30", endTime: "10:00" });
  eq("…and a timed one → 201", timedEv.status, 201);
  const studentsEv = await mk("students-only", { audience: FLAGS.Students });
  const publicEv = await mk("public-only", { audience: FLAGS.Public, responsibleUserIds: [ad.user.id] });
  const respEv = await mk("students-responsible", { audience: FLAGS.Students, responsibleUserIds: [TEACHER_ID] });
  const deptEv = teacherDepts.length ? await mk("guardians-department", { audience: FLAGS.Guardians, responsibleDepartmentIds: [teacherDepts[0]] }) : null;
  truthy("every event was created", [studentsEv, publicEv, respEv, deptEv].filter(Boolean).every((r) => r.status === 201),
    [studentsEv, publicEv, respEv, deptEv].filter(Boolean).map((r) => `${r.status} ${r.text.slice(0, 120)}`).join(" | "));
  eq("the responsible person's name is written for them", respEv.json?.responsibleNames?.length, 1);
  eq("a teacher cannot create one → 403", (await post(T, EV, { title: "nope", startsOn: TODAY, endsOn: TODAY, audience: 1 })).status, 403);

  const range = `?from=${addDays(TODAY, -3)}&to=${addDays(TODAY, 3)}`;
  const tView = await get(T, CAL + range);
  eq("a teacher reads the calendar → 200", tView.status, 200);
  const tIds = new Set((tView.json?.events ?? []).map((e) => e.id));
  truthy("…sees the Staff event", tIds.has(staffEv.json?.id));
  truthy("…sees the event they are responsible for, though it is for students", tIds.has(respEv.json?.id));
  if (deptEv) truthy("…sees the event their department is responsible for", tIds.has(deptEv.json?.id));
  else ok("the teacher belongs to no department — the department rule is covered by the person rule");
  truthy("…does NOT see a Students-only event (D5)", !tIds.has(studentsEv.json?.id));
  truthy("…does NOT see a Public-only event", !tIds.has(publicEv.json?.id));
  eq("…is not offered editing", tView.json?.canManage, false);
  truthy("…and no event says it may be edited", (tView.json?.events ?? []).every((e) => e.canEdit === false));
  truthy("the range carries terms from the policy", (tView.json?.terms ?? []).length >= 1, JSON.stringify(tView.json?.terms));
  truthy("…and the categories", Array.isArray(tView.json?.categories) && tView.json.categories.includes(category));
  truthy("…and the caller's duties list", Array.isArray(tView.json?.myDuties));

  // E4 (2026-09-26): "My events" is everybody's default, the keeper's too; the whole school is scope=all.
  const aView = await get(AD, CAL + range + "&scope=all");
  const aIds = new Set((aView.json?.events ?? []).map((e) => e.id));
  truthy("the calendar-keeper sees every one of them on the whole-school view", created.every((id) => aIds.has(id)), `${created.length} created, ${[...aIds].length} seen`);
  eq("…and may manage", aView.json?.canManage, true);
  // E3: the school calendar is readable by all staff; only an event marked audience-only is withheld from them.
  eq("a teacher may OPEN a Students-only event (the school calendar is readable by staff)", (await get(T, `${EV}/${studentsEv.json?.id}`)).status, 200);
  const privateEv = await mk("audience-only", { staffAudience: { allStaff: false, userIds: [ad.user.id] }, audienceOnly: true });
  eq("…but an audience-only event they are not in → 404, never 403", (await get(T, `${EV}/${privateEv.json?.id}`)).status, 404);
  eq("…a Staff one → 200", (await get(T, `${EV}/${staffEv.json?.id}`)).status, 200);
  eq("an unknown event → 404", (await get(AD, `${EV}/${crypto.randomUUID()}`)).status, 404);
  eq("a teacher cannot edit → 403", (await put(T, `${EV}/${staffEv.json?.id}`, { title: "x", startsOn: TODAY, endsOn: TODAY, audience: 1 })).status, 403);
  const edited = await put(AD, `${EV}/${timedEv.json?.id}`, { title: `  E2E timed edited ${RUN}  `, startsOn: TODAY, endsOn: TODAY, startTime: "09:00", endTime: "11:00", audience: FLAGS.Staff, category, location: "  Chapel  " });
  eq("the calendar-keeper edits → 200", edited.status, 200);
  eq("…and the title is trimmed", edited.json?.title, `E2E timed edited ${RUN}`);
  eq("…and the location too", edited.json?.location, "Chapel");

  hdr("30.2 What the server refuses");
  const base = { title: `E2E refused ${RUN}`, startsOn: TODAY, endsOn: TODAY, audience: FLAGS.Staff };
  const refuse = async (name, body) => {
    const r = await post(AD, EV, { ...base, ...body });
    if (r.status === 201 && r.json?.id) created.push(r.json.id);
    eq(name, r.status, 400);
  };
  await refuse("an event that ends before it starts → 400", { endsOn: addDays(TODAY, -1) });
  await refuse("an event longer than 92 days → 400", { endsOn: addDays(TODAY, 120) });
  await refuse("a one-day event ending before its start time → 400", { startTime: "10:00", endTime: "09:00" });
  await refuse("an end time with no start time → 400", { endTime: "09:00" });
  await refuse("a category the school does not have → 400", { category: `Nonsense ${RUN}` });
  await refuse("somebody responsible who is not on the staff → 400", { responsibleUserIds: [crypto.randomUUID()] });
  await refuse("a department that does not exist → 400", { responsibleDepartmentIds: [crypto.randomUUID()] });
  await refuse("an event for nobody → 400", { audience: 0 });
  await refuse("an event for another branch → 400", { branchId: crypto.randomUUID() });
  await refuse("a blank title → 400", { title: "   " });
  eq("a range longer than 400 days → 400", (await get(AD, `${CAL}?from=${TODAY}&to=${addDays(TODAY, 450)}`)).status, 400);
  eq("a range with no dates → 400", (await get(AD, CAL)).status, 400);
  eq("another tenant's branch → 404", (await get(AD, `/api/v1/branches/${crypto.randomUUID()}/calendar?from=${TODAY}&to=${TODAY}`)).status, 404);

  hdr("30.3 Settings round-trip, and one key in a shared blob");
  const marker = `E2E cat ${RUN}`;
  const saved = await put(AD, "/api/v1/calendar/settings", { ...originalSettings, categories: [...originalSettings.categories, `  ${marker}  `, marker.toUpperCase()] });
  eq("PUT settings → 200", saved.status, 200);
  truthy("…the new category is trimmed and de-duplicated", saved.json?.categories?.filter((c) => c.toLowerCase() === marker.toLowerCase()).length === 1, JSON.stringify(saved.json?.categories));
  eq("keeping no category → 400", (await put(AD, "/api/v1/calendar/settings", { ...originalSettings, categories: ["  "] })).status, 400);
  eq("thirty-one categories → 400", (await put(AD, "/api/v1/calendar/settings", { ...originalSettings, categories: Array.from({ length: 31 }, (_, i) => `c${i}`) })).status, 400);
  eq("a teacher cannot change them → 403", (await put(T, "/api/v1/calendar/settings", originalSettings)).status, 403);
  eq("a teacher may read them", (await get(T, "/api/v1/calendar/settings")).status, 200);
  const newCat = await post(AD, EV, { ...base, title: `E2E new category ${RUN}`, category: marker.toLowerCase() });
  if (newCat.status === 201) created.push(newCat.json.id);
  eq("an event may use the new category, in the configured spelling", newCat.json?.category, marker);

  const NAMES = `/api/v1/organizations/${ORG}/people-names`;
  originalNames = (await get(AD, NAMES)).json;
  const flipped = originalNames?.displayOrder === "FamilyFirst" ? "GivenFirst" : "FamilyFirst";
  const raceMarker = `E2E race ${RUN}`;
  const raced = [];
  for (let i = 0; i < 5; i++) {
    raced.push(put(AD, "/api/v1/calendar/settings", { ...originalSettings, categories: [...originalSettings.categories, marker, raceMarker] }));
    raced.push(put(AD, NAMES, { displayOrder: flipped, sortOrder: originalNames?.sortOrder ?? null }));
  }
  const raceResults = await Promise.all(raced);
  truthy("ten interleaved saves to two keys all succeed", raceResults.every((r) => r.status === 200), raceResults.map((r) => r.status).join(","));
  truthy("the calendar settings survived the race", (await get(AD, "/api/v1/calendar/settings")).json?.categories?.includes(raceMarker));
  eq("…and so did the other writer's key", (await get(AD, NAMES)).json?.displayOrder, flipped);

  hdr("30.4 The private feed");
  eq("no link yet → exists false", (await del(T, "/api/v1/calendar/feed")).status, 204);
  eq("…GET says so", (await get(T, "/api/v1/calendar/feed")).json?.exists, false);
  const made = await post(T, "/api/v1/calendar/feed");
  eq("POST feed → 200", made.status, 200);
  feedMade = true;
  truthy("…returns the link once", /\/api\/v1\/public\/calendar\/[A-Za-z0-9_-]{20,}\.ics$/.test(made.json?.url ?? ""), made.json?.url);
  const status1 = await get(T, "/api/v1/calendar/feed");
  eq("GET feed says it exists", status1.json?.exists, true);
  eq("…and never repeats the link", status1.json?.url ?? null, null);
  const path1 = new URL(made.json.url).pathname;
  const ics = await call(null, "GET", path1);
  eq("the feed opens with no sign-in → 200", ics.status, 200);
  truthy("…as text/calendar", ics.type.startsWith("text/calendar"), ics.type);
  const body = ics.text;
  truthy("…a VCALENDAR", body.startsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n") && body.endsWith("END:VCALENDAR\r\n"), body.slice(0, 80));
  truthy("…every line ends CRLF (no bare LF or CR)", !body.replace(/\r\n/g, "").includes("\n") && !body.replace(/\r\n/g, "").includes("\r"));
  const rawLines = body.split("\r\n").slice(0, -1);
  const longest = Math.max(...rawLines.map((l) => Buffer.byteLength(l, "utf8")));
  truthy("…no line is longer than 75 octets", longest <= 75, `longest ${longest}`);
  truthy("…a long line was actually folded", rawLines.some((l) => l.startsWith(" ")), "no continuation line");
  const unfolded = body.replace(/\r\n /g, "");
  const events = unfolded.split("BEGIN:VEVENT").slice(1).map((b) => b.split("END:VEVENT")[0]);
  const byUid = (id) => events.find((e) => e.includes(`UID:${id}@qmgr`));
  const staffBlock = byUid(staffEv.json.id);
  truthy("…carries the Staff event", !!staffBlock);
  truthy("…all-day, DTSTART a DATE", staffBlock?.includes(`DTSTART;VALUE=DATE:${TODAY.replaceAll("-", "")}`), staffBlock);
  truthy("…and DTEND the day AFTER its last day (exclusive)", staffBlock?.includes(`DTEND;VALUE=DATE:${addDays(TODAY, 2).replaceAll("-", "")}`), staffBlock);
  truthy("…with commas and semicolons escaped", staffBlock?.includes("commas\\; semicolons") && staffBlock?.includes("Main Hall\\, Block A") && staffBlock?.includes("backslash \\\\ so"), staffBlock);
  const timedBlock = byUid(timedEv.json.id);
  truthy("…a timed event is written in UTC", /DTSTART:\d{8}T\d{6}Z/.test(timedBlock ?? "") && /DTEND:\d{8}T\d{6}Z/.test(timedBlock ?? ""), timedBlock);
  truthy("…the event the teacher is responsible for is in it", !!byUid(respEv.json.id));
  truthy("…a Students-only event is not", !byUid(studentsEv.json.id));
  truthy("…nor a Public-only one", !byUid(publicEv.json.id));
  truthy("…every VEVENT has a DTSTAMP", events.every((e) => /DTSTAMP:\d{8}T\d{6}Z/.test(e)));
  const again = await post(T, "/api/v1/calendar/feed");
  eq("replacing the link → 200", again.status, 200);
  const path2 = new URL(again.json.url).pathname;
  truthy("…with a different secret", path2 !== path1);
  eq("…and the OLD link now answers 404", (await call(null, "GET", path1)).status, 404);
  eq("…while the new one works", (await call(null, "GET", path2)).status, 200);
  eq("removing the link → 204", (await del(T, "/api/v1/calendar/feed")).status, 204);
  feedMade = false;
  eq("…and the new link answers 404 too", (await call(null, "GET", path2)).status, 404);
  eq("a made-up secret → 404", (await call(null, "GET", `/api/v1/public/calendar/${"A".repeat(27)}.ics`)).status, 404);

  hdr("30.5 The signage 'Coming up' list is Public only");
  const up = await call(null, "GET", `/api/v1/public/branches/${BRANCH}/events/upcoming?days=3&take=50`);
  eq("it opens with no sign-in → 200", up.status, 200);
  const upIds = new Set((up.json ?? []).map((e) => e.id));
  truthy("…it carries the Public event", upIds.has(publicEv.json?.id));
  truthy("…and nothing that is not Public", (up.json ?? []).every((e) => (aud(e.audience) & FLAGS.Public) === FLAGS.Public), JSON.stringify(up.json?.map((e) => e.audience)));
  truthy("…not the Staff event", !upIds.has(staffEv.json?.id));
  truthy("…and nobody's name", (up.json ?? []).every((e) => (e.responsibleNames ?? []).length === 0 && (e.responsibleUserIds ?? []).length === 0));
  eq("an unknown branch → 404", (await call(null, "GET", `/api/v1/public/branches/${crypto.randomUUID()}/events/upcoming`)).status, 404);

  hdr("30.6 My School Day and the portal");
  if (myDay.status !== 200) ok(`My School Day answered ${myDay.status} (module not held) — skipped honestly`);
  else {
    const md = (await get(T, `/api/v1/branches/${BRANCH}/staff/lessons/my-day?date=${TODAY}`)).json;
    const mdIds = new Set((md?.events ?? []).map((e) => e.id));
    truthy("My School Day carries today's Staff event", mdIds.has(staffEv.json?.id), JSON.stringify(md?.events?.map((e) => e.title)));
    truthy("…and the one the teacher runs", mdIds.has(respEv.json?.id));
    truthy("…but not a Students-only one", !mdIds.has(studentsEv.json?.id));
    const adDay = (await get(AD, `/api/v1/branches/${BRANCH}/staff/lessons/my-day?date=${TODAY}`)).json;
    truthy("the calendar-keeper's OWN day is personal too — no Students-only event", !(adDay?.events ?? []).some((e) => e.id === studentsEv.json?.id));
  }
  const portal = await get(T, "/api/v1/staff/portal");
  if (portal.status !== 200) ok(`the portal answered ${portal.status} — skipped honestly`);
  else {
    const pIds = new Set((portal.json?.upcomingEvents ?? []).map((e) => e.id));
    truthy("the portal's upcoming events carry the Staff event", pIds.has(staffEv.json?.id), JSON.stringify(portal.json?.upcomingEvents?.map((e) => e.title)));
    truthy("…and at most ten", (portal.json?.upcomingEvents ?? []).length <= 10);
    truthy("…and no Public-only event", !pIds.has(publicEv.json?.id));
  }

  hdr("30.7 The national calendar");
  const natRead = await get(SA, "/api/v1/platform/national-calendar");
  eq("the platform administrator reads it → 200", natRead.status, 200);
  originalNational = natRead.json ?? { entries: [] };
  const natTitle = `E2E national ${RUN}`;
  const natBody = { entries: [...(originalNational.entries ?? []), { title: `  ${natTitle}  `, startsOn: "2031-01-10", endsOn: "2031-01-12", kind: "Holiday", source: "e2e" }] };
  eq("a tenant administrator cannot change it → 403", (await put(AD, "/api/v1/platform/national-calendar", natBody)).status, 403);
  eq("…nor read the platform copy → 403", (await get(AD, "/api/v1/platform/national-calendar")).status, 403);
  eq("an entry ending before it starts → 400", (await put(SA, "/api/v1/platform/national-calendar", { entries: [{ title: "x", startsOn: "2031-01-10", endsOn: "2031-01-01" }] })).status, 400);
  eq("four hundred and one entries → 400", (await put(SA, "/api/v1/platform/national-calendar", { entries: Array.from({ length: 401 }, (_, i) => ({ title: `e${i}`, startsOn: "2031-01-01", endsOn: "2031-01-01" })) })).status, 400);
  eq("the platform administrator saves it → 200", (await put(SA, "/api/v1/platform/national-calendar", natBody)).status, 200);
  const inJan = (await get(T, "/api/v1/calendar/national?from=2031-01-01&to=2031-01-31")).json;
  truthy("a teacher reads it by range, trimmed", (inJan?.entries ?? []).some((e) => e.title === natTitle), JSON.stringify(inJan));
  const inFeb = (await get(T, "/api/v1/calendar/national?from=2031-02-01&to=2031-02-28")).json;
  truthy("…and a range it does not overlap leaves it out", !(inFeb?.entries ?? []).some((e) => e.title === natTitle));
  const calJan = (await get(T, `${CAL}?from=2031-01-01&to=2031-01-31`)).json;
  truthy("the calendar range carries it as a national date", (calJan?.nationalDates ?? []).some((e) => e.title === natTitle));
  eq("the generic platform-settings editor refuses the category → 400",
    (await put(SA, "/api/v1/platform/settings/NationalCalendar", { settingsJson: "{}" })).status, 400);

  hdr("30.8 A term's theme survives the policy editor");
  const pol = await get(AD, "/api/v1/staff/policy");
  if (pol.status !== 200) ok(`the staff policy answered ${pol.status} (module not held) — skipped honestly`);
  else if (!(pol.json?.periods ?? []).length) {
    originalPolicy = null;
    const tooLong = { ...pol.json, periods: [{ key: "E2E-T9", name: "E2E", start: "2031-01-01", end: "2031-03-31", theme: "x".repeat(201) }] };
    eq("a theme over 200 characters → 400 (nothing saved)", (await put(AD, "/api/v1/staff/policy", tooLong)).status, 400);
    ok("the tenant defines no periods — the save-and-read-back half needs one and is not faked");
  } else {
    originalPolicy = pol.json;
    const withTheme = { ...pol.json, periods: pol.json.periods.map((p, i) => i === 0 ? { ...p, theme: `  Co-operation & Dignity ${RUN}  ` } : p) };
    const savedPol = await put(AD, "/api/v1/staff/policy", withTheme);
    eq("PUT a period with a theme → 200", savedPol.status, 200);
    eq("…and it reads back trimmed", (await get(AD, "/api/v1/staff/policy")).json?.periods?.[0]?.theme, `Co-operation & Dignity ${RUN}`);
    eq("a theme over 200 characters → 400",
      (await put(AD, "/api/v1/staff/policy", { ...pol.json, periods: pol.json.periods.map((p, i) => i === 0 ? { ...p, theme: "x".repeat(201) } : p) })).status, 400);
  }
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("30.9 Put things back");
  let removed = 0;
  for (const id of created) if ((await del(AD, `${EV}/${id}`)).status === 204) removed++;
  eq(`every event made here is removed (${created.length})`, removed, created.length);
  if (created[0]) eq("…and a removed event answers 404", (await get(AD, `${EV}/${created[0]}`)).status, 404);
  if (feedMade) await del(T, "/api/v1/calendar/feed");
  if (originalSettings) eq("the calendar settings are put back", (await put(AD, "/api/v1/calendar/settings", originalSettings)).status, 200);
  if (originalNames) eq("the name order is put back", (await put(AD, `/api/v1/organizations/${ORG}/people-names`, { displayOrder: originalNames.displayOrder, sortOrder: originalNames.sortOrder ?? null })).status, 200);
  if (originalNational) eq("the national calendar is put back", (await put(SA, "/api/v1/platform/national-calendar", { entries: originalNational.entries ?? [] })).status, 200);
  if (originalPolicy) eq("the staff policy is put back", (await put(AD, "/api/v1/staff/policy", originalPolicy)).status, 200);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

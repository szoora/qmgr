// SECTION 28 — A SCHOOL CHOOSES HOW A PERSON'S NAME IS WRITTEN (2026-09-23).
//
// "Why is the name display starting with first name? Is it a configuration on the project? Where is
// the ui to control name display order?" It was not, and there was none: User.FullName and about
// seventy hand-written `$"{FirstName} {LastName}"` fixed the order in code. The setting is
// Organization.Settings["People"], read on the API only through PersonNames.
//
//   28.1  it reads, and only a settings editor may change it;
//   28.2  surname first reaches every door: the users list, the staff directory, onboarding, auth/me;
//   28.3  the SORT order is its own choice, carried as SortName beside the name shown;
//   28.4  it is one key in a blob other writers share — racing them loses neither;
//   28.5  it is put back exactly as it was found.
//
// Run: node scripts/e2e/name-order-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
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
  const h = { Authorization: `Bearer ${token}` };
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text };
}
const get = (t, p) => call(t, "GET", p);
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, user: j.user }; }
  }
  return null;
}

const ad = await login(TENANT_ADMIN);
const teacher = await login("e2e.teacher.s4@qmgr.local");
if (!ad || !teacher) { console.error("sign-in failed"); process.exit(1); }
const AD = ad.token, ORG = ad.user.organizationId;
const NAMES = `/api/v1/organizations/${ORG}/people-names`;
let original = null, originalIndustry = null;

try {
  hdr("28.1 It reads, and only a settings editor may change it");
  const read = await get(AD, NAMES);
  eq("GET people-names → 200", read.status, 200);
  original = read.json;
  truthy("it names a display order", ["GivenFirst", "FamilyFirst", 0, 1].includes(original?.displayOrder), JSON.stringify(original));
  eq("a teacher may read it (the Web writes a preview with it)", (await get(teacher.token, NAMES)).status, 200);
  eq("a teacher may not change it → 403", (await put(teacher.token, NAMES, { displayOrder: "FamilyFirst" })).status, 403);
  eq("an order that does not exist → 400", (await put(AD, NAMES, { displayOrder: 7 })).status, 400);
  eq("another organisation's → 404", (await get(AD, `/api/v1/organizations/${crypto.randomUUID()}/people-names`)).status, 404);

  // The administrator's own halves, from the users list, are what every assertion below is about.
  const users = async () => { const r = (await get(AD, "/api/v1/users?includeInactive=true")).json; return Array.isArray(r) ? r : r?.items ?? []; };
  const me = (await users()).find((u) => u.id === ad.user.id);
  const given = me?.firstName, family = me?.lastName;
  if (!given || !family) throw new Error("the administrator has no first and last name to test with");

  hdr("28.2 Surname first reaches every door");
  const setFamily = await put(AD, NAMES, { displayOrder: "FamilyFirst", sortOrder: null });
  eq("PUT surname first → 200", setFamily.status, 200);
  eq("…and it is what the endpoint now answers", (await get(AD, NAMES)).json?.displayOrder, "FamilyFirst");
  const fam = `${family} ${given}`;
  eq("the users list writes the administrator surname first", (await users()).find((u) => u.id === ad.user.id)?.fullName, fam);
  eq("auth/me does too — the header's name", (await get(AD, "/api/v1/auth/me")).json?.fullName, fam);
  const dir = (await get(AD, `/api/v1/branches/${BRANCH}/staff/structure/members`)).json?.items ?? [];
  const inDir = dir.find((m) => m.userId === ad.user.id);
  if (inDir) eq("the staff directory does too", inDir.fullName, fam);
  else ok("the administrator is not in this branch's directory — the users list and auth/me stand for it");
  const onb = (await get(AD, `/api/v1/staff-onboarding/status?branchId=${BRANCH}`)).json;
  const onbRows = [...(onb?.notSignedIn ?? []), ...(onb?.notAcknowledged ?? []), ...(onb?.temporaryPasswordExpired ?? [])];
  truthy("onboarding rows carry a sort key alongside the name", onbRows.length === 0 || onbRows.every((r) => typeof r.sortName === "string"), JSON.stringify(onbRows[0]));

  hdr("28.3 The sort order is its own choice");
  eq("PUT shown first-name-first, filed by surname → 200", (await put(AD, NAMES, { displayOrder: "GivenFirst", sortOrder: "FamilyFirst" })).status, 200);
  const row = (await users()).find((u) => u.id === ad.user.id);
  eq("the name is shown first-name-first", row?.fullName, `${given} ${family}`);
  eq("…and sorted on the surname", row?.sortName, `${family} ${given}`);
  eq("sort order null follows the display order", (await put(AD, NAMES, { displayOrder: "FamilyFirst", sortOrder: null })).json?.sortOrder ?? null, null);
  eq("…so the sort key is surname first again", (await users()).find((u) => u.id === ad.user.id)?.sortName, fam);

  hdr("28.4 One key in a shared blob — racing another writer loses neither");
  const industryPath = `/api/v1/organizations/${ORG}/industry-settings`;
  originalIndustry = (await get(AD, industryPath)).json;
  if (!originalIndustry) throw new Error("could not read the industry settings to race against");
  const marker = `nameorder${Date.now().toString(36)}`;
  const writes = [];
  for (let i = 0; i < 5; i++) {
    writes.push(put(AD, NAMES, { displayOrder: i % 2 ? "GivenFirst" : "FamilyFirst", sortOrder: null }));
    writes.push(put(AD, industryPath, { ...originalIndustry, features: { ...(originalIndustry.features ?? {}), [marker]: i % 2 === 0 } }));
  }
  const results = await Promise.all(writes);
  truthy("ten interleaved saves to two keys all succeed", results.every((r) => r.status === 200), results.map((r) => r.status).join(","));
  const finalNames = (await get(AD, NAMES)).json;
  const finalIndustry = (await get(AD, industryPath)).json;
  truthy("the name order survived the race", ["GivenFirst", "FamilyFirst"].includes(finalNames?.displayOrder), JSON.stringify(finalNames));
  truthy("…and so did the other writer's key", marker in (finalIndustry?.features ?? {}), JSON.stringify(finalIndustry?.features));
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("28.5 Put things back");
  if (original) eq("the name order is put back", (await put(AD, NAMES, { displayOrder: original.displayOrder, sortOrder: original.sortOrder ?? null })).status, 200);
  if (originalIndustry) eq("the industry settings are put back", (await put(AD, `/api/v1/organizations/${ORG}/industry-settings`, originalIndustry)).status, 200);
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

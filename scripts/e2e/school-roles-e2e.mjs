// SECTION 36 — THE SCHOOL CHAIN: HEAD TEACHER, DEPUTY, DIRECTOR OF STUDIES (2026-09-24).
//
// "seed head teacher and deputy head teacher roles. fix the display labels. a school may not use manager. next to
// DOS is Deputy, then head." Two seeded roles, their permission sets, the labels a school reads, and — the part that
// is a security decision rather than a label — the ORDER, as RoleAssignmentGuard applies it when somebody assigns a
// role: a head may make somebody deputy, but not Administrator; a deputy may make somebody Director of Studies, but
// not head.
//
// It borrows two of section 14's accounts (e2e.sp.dos, e2e.sp.aa), gives them the roles under test, and puts both
// back as they were.
//
// Run: node scripts/e2e/school-roles-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
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
async function login(id) {
  for (const pw of [PW, NEW_PW]) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) return (await r.json()).accessToken;
  }
  return null;
}

const AD = await login(TENANT_ADMIN);
if (!AD) { console.error("the tenant administrator could not sign in"); process.exit(1); }
const restore = [];

try {
  hdr("36.1 The roles a school is shown, and what each one reads as");
  const rolesRes = await call(AD, "GET", "/api/v1/roles");
  const roles = Array.isArray(rolesRes.json) ? rolesRes.json : rolesRes.json?.items ?? [];
  const byCode = Object.fromEntries(roles.map((r) => [r.code, r]));
  truthy("Head Teacher and Deputy Head Teacher are offered to a school", !!byCode["head-teacher"] && !!byCode["deputy-head-teacher"], Object.keys(byCode).join(", "));
  eq("…named for what a school calls them", `${byCode["head-teacher"]?.name} / ${byCode["deputy-head-teacher"]?.name}`, "Head Teacher / Deputy Head Teacher");
  eq("\"Manager\" reads as the front office it is", byCode["manager"]?.name, "Front Office Manager");
  eq("\"Staff\" reads as the front desk, not the teaching staff", byCode["staff"]?.name, "Front Desk Staff");
  eq("the Administrator keeps its name", byCode["admin"]?.name, "Administrator");

  hdr("36.2 What each holds");
  const permsOf = async (code) => {
    const r = await call(AD, "GET", `/api/v1/roles/${byCode[code]?.id}`);
    const raw = r.json?.permissions ?? r.json?.permissionCodes ?? [];
    return new Set(raw.map((p) => (typeof p === "string" ? p : p.code)));
  };
  const head = await permsOf("head-teacher"), deputy = await permsOf("deputy-head-teacher"), dos = await permsOf("director-of-studies");
  truthy("the head reads RESTRICTED welfare and staff records — the school's accountable safeguarding authority",
    head.has("welfare.restricted.view") && head.has("staff.restricted.view"), [...head].filter((c) => c.includes("restricted")).join(","));
  truthy("…but not payments, system settings, API keys, or what a role grants",
    !["billing.manage", "settings.edit", "api-clients.create", "roles.edit", "roles.create"].some((c) => head.has(c)),
    ["billing.manage", "settings.edit", "api-clients.create", "roles.edit"].filter((c) => head.has(c)).join(","));
  truthy("the deputy reads CONFIDENTIAL welfare, never restricted", deputy.has("welfare.confidential.view") && !deputy.has("welfare.restricted.view"));
  truthy("the deputy holds everything the Director of Studies holds", [...dos].every((c) => deputy.has(c)), [...dos].filter((c) => !deputy.has(c)).join(","));
  truthy("…and the head everything the deputy holds", [...deputy].every((c) => head.has(c)), [...deputy].filter((c) => !head.has(c)).join(","));

  hdr("36.3 The order, as it is enforced when a role is assigned");
  const users = (await call(AD, "GET", "/api/v1/users?pageSize=500")).json;
  const list = Array.isArray(users) ? users : users?.items ?? [];
  const dosUser = list.find((u) => u.username === "e2e.sp.dos"), aaUser = list.find((u) => u.username === "e2e.sp.aa");
  if (!dosUser || !aaUser) throw new Error("e2e.sp.dos / e2e.sp.aa do not exist — run section 14 first");
  restore.push([dosUser.id, dosUser.roleId], [aaUser.id, aaUser.roleId]);
  const setRole = (token, userId, code) => call(token, "PUT", `/api/v1/users/${userId}`, { roleId: byCode[code].id });

  eq("the Administrator makes somebody Head Teacher", (await setRole(AD, dosUser.id, "head-teacher")).status, 200);
  const HEAD = await login("e2e.sp.dos@qmgr.local");
  eq("the head makes somebody Deputy Head Teacher", (await setRole(HEAD, aaUser.id, "deputy-head-teacher")).status, 200);
  const toAdmin = await setRole(HEAD, aaUser.id, "admin");
  truthy("…but not Administrator", toAdmin.status >= 400 && /role|Administrator/i.test(toAdmin.text), `${toAdmin.status} ${toAdmin.text.slice(0, 160)}`);
  const toManager = await setRole(HEAD, aaUser.id, "manager");
  // R1 (2026-09-24): the school chain ranks ABOVE the front office, so a head may give the role — their own role
  // holds everything it grants. Until that day this asserted the opposite.
  eq("…but may make somebody Front Office Manager, which ranks below the head since R1", toManager.status, 200);
  eq("…and puts them back to Deputy Head Teacher", (await setRole(HEAD, aaUser.id, "deputy-head-teacher")).status, 200);

  const DEPUTY = await login("e2e.sp.aa@qmgr.local");
  await setRole(AD, dosUser.id, "academic-assistant");
  eq("the deputy makes somebody Director of Studies", (await setRole(DEPUTY, dosUser.id, "director-of-studies")).status, 200);
  await setRole(AD, dosUser.id, "head-teacher");
  const deputyUp = await setRole(DEPUTY, dosUser.id, "director-of-studies");
  truthy("…but may not touch a head: moving the head DOWN is refused too", deputyUp.status === 400 && /above what you may change/i.test(deputyUp.text), `${deputyUp.status} ${deputyUp.text.slice(0, 160)}`);
  const offHead = await call(DEPUTY, "PATCH", `/api/v1/users/${dosUser.id}/toggle`);
  eq("…nor switch the head's account off", offHead.status, 400);
  const mailHead = await call(DEPUTY, "PUT", `/api/v1/users/${dosUser.id}`, { email: "not.the.head@qmgr.local" });
  eq("…nor change the email address the head's password-reset link goes to", mailHead.status, 400);
  const delHead = await call(DEPUTY, "DELETE", `/api/v1/users/${dosUser.id}`);
  truthy("…nor remove the head", delHead.status === 400 || delHead.status === 403, `${delHead.status}`);
  const mgrToAdmin = await call(AD, "GET", `/api/v1/users/${dosUser.id}`);
  eq("…and the head's account is exactly as it was", `${mgrToAdmin.json?.isActive}/${mgrToAdmin.json?.roleId}`, `true/${byCode["head-teacher"].id}`);

  hdr("36.4 The same rule in bulk");
  const BRANCH = process.env.BRANCH;
  if (BRANCH) {
    const bulkAdmin = await call(DEPUTY, "POST", `/api/v1/branches/${BRANCH}/batch/preview`, { operation: "SetUserRole", ids: [aaUser.id], targetUserId: byCode["admin"].id });
    truthy("a bulk change may not hand out a role above the asker's", bulkAdmin.status >= 400 || !!bulkAdmin.json?.blockingError, `${bulkAdmin.status} ${bulkAdmin.text.slice(0, 200)}`);
    const bulkOff = await call(DEPUTY, "POST", `/api/v1/branches/${BRANCH}/batch/preview`, { operation: "SetUserActive", ids: [dosUser.id], value: "false" });
    const row = bulkOff.json?.rows?.find((r) => r.id === dosUser.id);
    truthy("…and a bulk switch-off refuses the head's row by name", row && /fail/i.test(String(row.outcome)) && /above what you may change/i.test(row.message), `${bulkOff.status} ${bulkOff.text.slice(0, 240)}`);
  } else console.log("  SKIP  36.4 — BRANCH not set");
  const toHead = await setRole(DEPUTY, aaUser.id, "head-teacher");
  truthy("the deputy may not make anybody Head Teacher — not even themselves", toHead.status >= 400 && /rank|permission/i.test(toHead.text), `${toHead.status} ${toHead.text.slice(0, 160)}`);
} catch (e) {
  bad("the suite ran to the end", "no exception", e.stack ?? String(e));
} finally {
  hdr("36.9 Put things back");
  for (const [userId, roleId] of restore) {
    const r = await call(AD, "PUT", `/api/v1/users/${userId}`, { roleId });
    console.log(`  role restored (${r.status})`);
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
if (failures.length) console.log("Failures:\n" + failures.map((f) => `  - ${f}`).join("\n"));
viewer(`${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

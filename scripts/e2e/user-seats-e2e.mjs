// SECTION 34 — THE USER LIMIT COUNTS ACTIVE PEOPLE, AND EVERY WAY BACK IN ASKS FIRST.
//
// Until 2026-09-23 the limit counted every user row, deactivated ones included, while the only way the
// product removes a person — DELETE /users/{id} — is a SOFT delete. So a school that removed a teacher who
// had left never got the seat back: once full, full for good. The dev tenant reached 260 of 250 that way
// and every suite that creates a user failed at its first step.
//
// Counting ACTIVE people makes re-enabling an ADD, so this suite proves both halves:
//   1  a disabled account frees its seat (a create that was refused then succeeds);
//   2  every path that turns somebody back on refuses when there is no room — the toggle, the bulk
//      "enable accounts" batch (preview AND run), and undoing a bulk disable;
//   3  disabling never needs room.
//
// It REGISTERS ITS OWN TENANT (Core Queue on trial: 10 users) and PURGES it afterwards, because the dev
// tenant's limit is 250 and there is no endpoint to lower it. Development only, like section 20.
//
// Run: API=http://127.0.0.1:5001 node scripts/e2e/user-seats-e2e.mjs

const API = process.env.API ?? "http://127.0.0.1:5001";
const SA_USER = process.env.SA_USER ?? "superadmin";
const SA_PASS = process.env.SA_PASS ?? "admin";
const RUN = Date.now().toString(36);
const ORG_NAME = `Seats Check ${RUN}`;
const ADMIN_EMAIL = `seats.${RUN}@seats-${RUN}.sch.ug`;
const PASS = "SeatsCheck!2026x";

let pass = 0, fail = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const call = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};
const login = async (email, password) =>
  (await (await fetch(`${API}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password }),
  })).json().catch(() => ({}))).accessToken;

const SA = await login(SA_USER, SA_PASS);
if (!SA) { console.error("could not sign in as the platform administrator"); process.exit(1); }

hdr(`34. USER LIMIT — active people only, and every way back in asks first (run ${RUN})`);

// Development-only probe, as in section 20: this suite registers and destroys a tenant.
const budget = await call(SA, "POST", "/api/v1/admin/registration-budget/reset", {});
if (budget.status === 404) {
  console.error(`REFUSING TO RUN: ${API} is not a Development API (it registers and purges a tenant).`);
  process.exit(1);
}

const registered = await call(null, "POST", "/api/v1/register", {
  organizationName: ORG_NAME, email: ADMIN_EMAIL, password: PASS, confirmPassword: PASS,
  firstName: "Seats", lastName: "Check", preferredCurrency: "UGX", acceptTerms: true,
  selectedModuleCodes: ["core-queue"],
});
if (registered.status === 429) {
  console.log("    SKIP — sign-ups from this address are rate-limited this hour.");
  post("    SKIP  section 34 — registration is rate-limited this hour");
  process.exit(0);
}
const ORG = registered.json?.organizationId ?? registered.json?.organization?.id;
ok("34.0a: a scratch tenant is registered", !!ORG, `status ${registered.status} ${registered.text?.slice(0, 200)}`);
if (!ORG) process.exit(1);
await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/verify`, {});
const AD = await login(ADMIN_EMAIL, PASS);
ok("34.0b: its administrator can sign in", !!AD, "no token");

const branches = await call(AD, "GET", "/api/v1/branches");
const BRANCH = (branches.json?.items ?? branches.json ?? [])[0]?.id;
const roles = await call(AD, "GET", "/api/v1/roles");
const STAFF_ROLE = (roles.json?.items ?? roles.json ?? []).find(r => r.code === "staff")?.id;
ok("34.0c: the tenant has a branch and a staff role", !!BRANCH && !!STAFF_ROLE, `${BRANCH} ${STAFF_ROLE}`);

let n = 0;
const createUser = () => {
  n++;
  return call(AD, "POST", "/api/v1/users", {
    username: `seat${RUN}${n}`, email: `seat${n}.${RUN}@seats-${RUN}.sch.ug`, password: PASS,
    firstName: "Seat", lastName: `Holder${n}`, employeeNumber: `S-${RUN}-${n}`,
    roleId: STAFF_ROLE, assignedBranchId: BRANCH,
  });
};

try {
  // ===================================================================================
  hdr("34.1 Fill the limit");
  const made = [];
  let refused = null;
  for (let i = 0; i < 30 && !refused; i++) {
    const r = await createUser();
    if (r.status === 201 || r.status === 200) made.push(r.json.id);
    else if (r.status === 402) refused = r;
    else { ok("34.1x: a user is created", false, `status ${r.status} ${r.text?.slice(0, 200)}`); break; }
  }
  ok("34.1a: users are created until the limit, then the next is refused with 402", !!refused && made.length >= 3,
    `${made.length} created, refused=${refused?.status}`);
  ok("34.1b: …as LIMIT_EXCEEDED on users", refused?.json?.error === "LIMIT_EXCEEDED" && refused?.json?.limitType === "users",
    refused?.text?.slice(0, 200));
  const cap = refused?.json?.limit;
  console.log(`    limit ${cap}; ${made.length} created beside the administrator`);

  // ===================================================================================
  hdr("34.2 A disabled account gives its seat back");
  const [a, b, c] = made;
  const del = await call(AD, "DELETE", `/api/v1/users/${a}`);
  ok("34.2a: 'delete' (a soft deactivate) succeeds", del.status === 204, `status ${del.status}`);
  const again = await createUser();
  ok("34.2b: …and the create that was refused now succeeds — the seat came back",
    again.status === 201 || again.status === 200, `status ${again.status} ${again.text?.slice(0, 200)}`);
  if (again.json?.id) made.push(again.json.id);

  // ===================================================================================
  hdr("34.3 Every way back in asks for room first");
  const toggleOn = await call(AD, "PATCH", `/api/v1/users/${a}/toggle`);
  ok("34.3a: re-enabling a disabled account when full → 402", toggleOn.status === 402, `status ${toggleOn.status} ${toggleOn.text?.slice(0, 200)}`);
  ok("34.3b: …saying how many are allowed and what to do", /active users/.test(toggleOn.json?.message ?? "") && /Disable somebody/.test(toggleOn.json?.message ?? ""),
    toggleOn.json?.message);
  const stillOff = await call(AD, "GET", `/api/v1/users/${a}`);
  ok("34.3c: …and the account stayed disabled", stillOff.json?.isActive === false, JSON.stringify(stillOff.json ?? {}).slice(0, 120));

  const bulkOn = { operation: "SetUserActive", ids: [a], value: "true" };
  const preview = await call(AD, "POST", `/api/v1/branches/${BRANCH}/batch/preview`, bulkOn);
  ok("34.3d: the bulk 'enable accounts' PREVIEW says there is no room", /active users/.test(preview.json?.blockingError ?? ""),
    `status ${preview.status} ${preview.text?.slice(0, 200)}`);
  const run = await call(AD, "POST", `/api/v1/branches/${BRANCH}/batch`, bulkOn);
  ok("34.3e: …and the RUN refuses the same way (400, nothing queued)", run.status === 400 && /active users/.test(run.text ?? ""),
    `status ${run.status} ${run.text?.slice(0, 200)}`);

  // Disabling needs no room, even when full.
  const bulkOff = await call(AD, "POST", `/api/v1/branches/${BRANCH}/batch`, { operation: "SetUserActive", ids: [b, c], value: "false" });
  ok("34.3f: a bulk DISABLE is never refused for room", bulkOff.status === 202, `status ${bulkOff.status} ${bulkOff.text?.slice(0, 200)}`);
  const JOB = bulkOff.json?.jobId;
  for (let i = 0; i < 60; i++) {
    const bb = await call(AD, "GET", `/api/v1/users/${b}`), cc = await call(AD, "GET", `/api/v1/users/${c}`);
    if (bb.json?.isActive === false && cc.json?.isActive === false) break;
    await sleep(1000);
  }
  // Take the two freed seats, so undoing the disable would need room that is not there.
  const fill1 = await createUser(), fill2 = await createUser();
  ok("34.3g: the two freed seats are taken by new people", [fill1, fill2].every(r => r.status === 201 || r.status === 200),
    `${fill1.status} ${fill2.status}`);
  for (const r of [fill1, fill2]) if (r.json?.id) made.push(r.json.id);

  let undo = null;
  for (let i = 0; i < 30; i++) {
    undo = await call(AD, "POST", `/api/v1/branches/${BRANCH}/batch/${JOB}/undo`);
    if (!/Only a finished batch/.test(undo.json?.message ?? "")) break;
    await sleep(1000);
  }
  ok("34.3h: UNDOING the bulk disable when full is refused whole", undo?.json?.success === false && /active users/.test(undo?.json?.message ?? ""),
    undo?.text?.slice(0, 240));
  const bAfter = await call(AD, "GET", `/api/v1/users/${b}`);
  ok("34.3i: …and nobody was re-enabled by it", bAfter.json?.isActive === false, JSON.stringify(bAfter.json ?? {}).slice(0, 120));

  // Room again: disable one, then the toggle succeeds.
  await call(AD, "DELETE", `/api/v1/users/${fill2.json?.id}`);
  const toggleNow = await call(AD, "PATCH", `/api/v1/users/${a}/toggle`);
  ok("34.3j: with a seat free, re-enabling succeeds", toggleNow.status === 200 && toggleNow.json?.isActive === true,
    `status ${toggleNow.status} ${toggleNow.text?.slice(0, 160)}`);
  const toggleOff = await call(AD, "PATCH", `/api/v1/users/${a}/toggle`);
  ok("34.3k: and disabling at the limit is never refused", toggleOff.status === 200 && toggleOff.json?.isActive === false,
    `status ${toggleOff.status}`);
} finally {
  // ===================================================================================
  hdr("34.9 Put things back — the scratch tenant is purged");
  await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "Cancelled", reason: "e2e section 34" });
  await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "PendingDeletion", reason: "e2e section 34" });
  const purged = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/purge`, { confirmName: ORG_NAME, reason: "e2e section 34" });
  ok("34.9a: the scratch tenant is purged and verified", purged.status === 200 && purged.json?.verificationPassed === true,
    `status ${purged.status} ${purged.text?.slice(0, 200)}`);
}

console.log(`\n34. DONE — ${pass} passed, ${fail} failed`);
post(`34. DONE — ${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

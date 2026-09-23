// SECTION 18 — THE TWO DOORS. Registering an organisation, and joining one that already exists.
//
// Reported 2026-09-20 from the sign-in page: "Don't have an account? Create one" led to registering
// a whole new ORGANIZATION with a trial. A teacher at a school that already uses Q-Mgr reads that
// line, tells the truth, and ends up with a duplicate school — themselves as its administrator, no
// connection to the real one. The join link that actually serves them existed all along and was
// unreachable from the sign-in page.
//
// What this proves, in order: the hint answers ONLY for a tenant that published the domain itself;
// it never names one that did not; it never returns the join code; a free mailbox is never matched;
// and continuing past the warning raises the risk score rather than being refused.
//
// Run: API=http://127.0.0.1:5001 node scripts/e2e/registration-doors-e2e.mjs
const API = process.env.API ?? "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH ?? "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
// A TENANT ADMINISTRATOR, AND SA_USER MUST NOT REACH IT (fixed 2026-09-22).
//
// This read process.env.SA_USER, so it passed standalone and failed inside class-teacher-e2e.sh, which
// passes SA_USER=superadmin. The organisation this suite then set a join link and allowed domains on was
// the PLATFORM organization rather than the tenant, so 18.4b compared the hint's answer against the wrong
// school and reported "Q-Mgr Platform vs Platform Administration" — a mismatch that was entirely the
// suite's own doing. Same trap as section 17 and the one section 21 already documents.
const AD_USER = process.env.AD_USER ?? "e2e.admin.ct@qmgr.local";
const AD_PASS = process.env.AD_PASS ?? "E2eTeacher!2026";
const RUN = Date.now().toString(36);
const DOMAIN = `doors-${RUN}.sch.ug`;

let pass = 0, fail = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
const eq = (name, actual, expected) => ok(name, actual === expected, `got ${JSON.stringify(actual)}, wanted ${JSON.stringify(expected)}`);
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const login = async (email, password) => {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  return (await r.json().catch(() => ({}))).accessToken;
};
const call = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, {
    method,
    headers: token ? { Authorization: `Bearer ${token}`, "Content-Type": "application/json" } : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};

const AD = await login(AD_USER, AD_PASS);
if (!AD) { console.error("could not sign in"); process.exit(1); }
const B = `/api/v1/branches/${BRANCH}/staff`;

hdr(`18. THE TWO DOORS — the organisation hint and the join page (run ${RUN})`);

// ---------------------------------------------------------------- the tenant's starting state
const before = await call(AD, "GET", `/api/v1/staff-onboarding/settings`);
const restore = before.json ? {
  joinEnabled: before.json.joinEnabled,
  allowedEmailDomains: before.json.allowedEmailDomains ?? [],
  requestExpiryDays: before.json.requestExpiryDays,
  purgeAfterDays: before.json.purgeAfterDays,
  defaultBranchId: before.json.defaultBranchId,
} : null;
ok("SETUP: the tenant's onboarding settings read back", before.status === 200, `status ${before.status}`);

// ---------------------------------------------------------------- 18.1 silence before consent
hdr("18.1 A tenant that has not invited anyone is never named");
const off = await call(AD, "PUT", `/api/v1/staff-onboarding/settings`, { ...restore, joinEnabled: false, allowedEmailDomains: [DOMAIN] });
ok("18.1a: staff sign-up switched OFF with the domain listed", off.status === 200, `status ${off.status}`);

const silent = await call(null, "GET", `/api/v1/register/organization-hint?email=${encodeURIComponent("a.teacher@" + DOMAIN)}`);
eq("18.1b: the hint answers (200) without a sign-in", silent.status, 200);
eq("18.1c: …and finds nothing, because the school has not invited anyone", silent.json?.found, false);
eq("18.1d: …and names nobody", silent.json?.organizationName, null);

// ---------------------------------------------------------------- 18.2 consent, then the hint
hdr("18.2 With sign-up on and the domain published, the school is named");
const on = await call(AD, "PUT", `/api/v1/staff-onboarding/settings`, { ...restore, joinEnabled: true, allowedEmailDomains: [DOMAIN] });
ok("18.2a: staff sign-up switched ON", on.status === 200, `status ${on.status}`);
// A link must exist too: a school with sign-up on and no link has nothing to join.
const issued = await call(AD, "POST", `/api/v1/staff-onboarding/join-link/rotate`, { validDays: 30 });
ok("18.2b: a join link was issued", issued.status === 200 || issued.status === 201, `status ${issued.status}`);
const code = issued.json?.code ?? issued.json?.joinCode;
ok("18.2c: …and the code came back once, to the administrator", typeof code === "string" && code.length > 0, JSON.stringify(issued.json)?.slice(0, 120));

const hinted = await call(null, "GET", `/api/v1/register/organization-hint?email=${encodeURIComponent("a.teacher@" + DOMAIN)}`);
eq("18.2d: the hint now finds the school", hinted.json?.found, true);
ok("18.2e: …and names it", (hinted.json?.organizationName ?? "").length > 0, JSON.stringify(hinted.json));
eq("18.2f: …and says which domain matched, so the page can say why", hinted.json?.matchedDomain, DOMAIN);
ok("18.2g: THE JOIN CODE IS NEVER IN THE ANSWER — it is the school's secret",
  !JSON.stringify(hinted.json ?? {}).toLowerCase().includes(String(code ?? "\u0000").toLowerCase()),
  JSON.stringify(hinted.json));

// ---------------------------------------------------------------- 18.3 what is never matched
hdr("18.3 What is never matched");
const other = await call(null, "GET", `/api/v1/register/organization-hint?email=${encodeURIComponent("someone@not-" + DOMAIN)}`);
eq("18.3a: a domain nobody published finds nothing", other.json?.found, false);

const free = await call(null, "GET", `/api/v1/register/organization-hint?email=${encodeURIComponent("teacher@gmail.com")}`);
eq("18.3b: a free mailbox finds nothing — it would enumerate which schools use the product", free.json?.found, false);

const empty = await call(null, "GET", `/api/v1/register/organization-hint?email=`);
eq("18.3c: no address finds nothing, and does not error", empty.status, 200);
eq("18.3d: …", empty.json?.found, false);

const nonsense = await call(null, "GET", `/api/v1/register/organization-hint?email=${encodeURIComponent("not-an-address")}`);
eq("18.3e: a malformed address finds nothing, and does not error", nonsense.status, 200);

// ---------------------------------------------------------------- 18.4 the join page resolves the code
hdr("18.4 The code the administrator issued opens the join page");
const linkInfo = await call(null, "GET", `/api/v1/public/join/${code}`);
eq("18.4a: the join link resolves for an anonymous visitor", linkInfo.status, 200);
ok("18.4b: …to the same school the hint named", (linkInfo.json?.organizationName ?? "") === (hinted.json?.organizationName ?? ""),
  `${linkInfo.json?.organizationName} vs ${hinted.json?.organizationName}`);

const badCode = await call(null, "GET", `/api/v1/public/join/ZZZZZZZZ`);
ok("18.4c: an unknown code reads as not valid, the same as a revoked one", badCode.status === 404 || badCode.json?.organizationName == null,
  `status ${badCode.status}`);

// ---------------------------------------------------------------- 18.5 continuing past the warning
hdr("18.5 Continuing past the warning is allowed, and flagged");
const base = {
  email: `doors.${RUN}@${DOMAIN}`,
  password: "Rwenzori#Peaks-2026", confirmPassword: "Rwenzori#Peaks-2026",
  firstName: "Doors", lastName: "Check", acceptTerms: true,
  selectedModuleCodes: ["core-queue"], formRenderedAt: new Date(Date.now() - 60000).toISOString(),
};
const created = await call(null, "POST", "/api/v1/register", {
  ...base,
  organizationName: `Doors Check ${RUN}`,
  acknowledgedExistingOrganization: true,
  acknowledgedOrganizationName: hinted.json?.organizationName,
});
ok("18.5a: registering anyway is ALLOWED, never refused", created.status === 200 || created.status === 201,
  `status ${created.status}: ${created.text?.slice(0, 200)}`);

// The reviewer must be able to see WHY. SuperAdmin reads the review queue.
const SA = await login(process.env.PLATFORM_USER ?? "superadmin", process.env.PLATFORM_PASS ?? "admin");
if (!SA) {
  const l = "    SKIP  18.5b: no platform sign-in, so the review queue was not read";
  console.log(l); post(l);
} else {
  const queue = await call(SA, "GET", "/api/v1/admin/registration-attempts?pendingOnly=false&pageSize=50");
  const items = queue.json?.items ?? queue.json ?? [];
  const mine = items.find(a => (a.email ?? "").includes(`doors.${RUN}`));
  ok("18.5b: the attempt is on the platform's review list", !!mine, `${items.length} attempts read, status ${queue.status}`);
  if (mine) {
    const signals = JSON.stringify(mine.signals ?? mine.Signals ?? []);
    ok("18.5c: …carrying the reason, in words a reviewer can act on",
      /already uses Q-Mgr/i.test(signals), signals.slice(0, 220));
    ok("18.5d: …and a score above zero", (mine.riskScore ?? mine.RiskScore ?? 0) > 0, JSON.stringify(mine.riskScore));
  }
}

// ---------------------------------------------------------------- cleanup
hdr("Cleanup");
if (restore) {
  const back = await call(AD, "PUT", `/api/v1/staff-onboarding/settings`, restore);
  ok("CLEANUP: the tenant's onboarding settings were put back as they were", back.status === 200, `status ${back.status}`);
}
const line = `    NOTE  the organisation "Doors Check ${RUN}" was created by 18.5 and is flagged for review. A platform administrator removes it.`;
console.log(line); post(line);

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

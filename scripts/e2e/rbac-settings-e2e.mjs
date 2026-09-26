// SECTION 40 — WHAT A ROLE CAN SEE IS WHAT IT MAY USE (the RBAC close-out, 2026-09-25).
//
// Reported from production with a screenshot: a teacher at Maryhill opened Administration → Settings
// and read the school's SMS gateway configuration. The API was worse than the page: GET
// notifications/settings was gated on notifications.view — held by nine seeded roles — and returned
// the SMS API key and password, the SMTP password, the Telegram token and the WhatsApp token as stored.
// A sweep of the same shape then found eleven more (docs/plans/CLOSE_OUT_RBAC_AND_ACCOUNT.md §1.5).
//
// This suite signs in as EVERY seeded tenant role and asks each question role by role:
//   40.1  the messaging secrets: 403 for everyone but the administrator; masks, never secrets, for them;
//         a save that round-trips the mask keeps the secret, an empty value clears it
//   40.2  the flags-only channel read the Integrations tab uses
//   40.3  printing a ticket is a write (tokens.create), never a view
//   40.4  a colleague's national ID / date of birth / next of kin: HR readers only
//   40.5  module prices and trial dates: billing readers only
//   40.6  generating a feedback link is a write
//   40.7  the account list's phone numbers: account editors only
//   40.8  the account: one contact writer with no module, names are the school's, email checked on
//         its canonical form, the old portal contact route is gone
//   40.9  a changed phone is unconfirmed again, whoever changes it (needs PSQL — see below)
//
// It REGISTERS ITS OWN TENANT and PURGES it afterwards (Development only, like sections 20 and 34).
// 40.9 sets a confirmation directly in the database, because a real one needs an SMS; set PSQL to the
// psql executable and PGPASSWORD/PGDATABASE to run it, otherwise it SKIPS and says so.
//
// Run: API=http://127.0.0.1:5001 node scripts/e2e/rbac-settings-e2e.mjs

import { execFileSync } from "node:child_process";

const API = process.env.API ?? "http://127.0.0.1:5001";
const SA_USER = process.env.SA_USER ?? "superadmin";
const SA_PASS = process.env.SA_PASS ?? "admin";
const RUN = Date.now().toString(36);
const ORG_NAME = `Rbac Check ${RUN}`;
const DOMAIN = `rbac-${RUN}.sch.ug`;
const ADMIN_EMAIL = `admin.${RUN}@${DOMAIN}`;
const PASS = "RbacCheck!2026x";
const MASK = "••••••••";

let pass = 0, fail = 0, skip = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
const skipped = (name, why) => { skip++; const l = `    SKIP  ${name} — ${why}`; console.log(l); post(l); };
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

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

hdr(`40. RBAC — what a role can see is what it may use (run ${RUN})`);

const budget = await call(SA, "POST", "/api/v1/admin/registration-budget/reset", {});
if (budget.status === 404) {
  console.error(`REFUSING TO RUN: ${API} is not a Development API (it registers and purges a tenant).`);
  process.exit(1);
}

const registered = await call(null, "POST", "/api/v1/register", {
  organizationName: ORG_NAME, email: ADMIN_EMAIL, password: PASS, confirmPassword: PASS,
  firstName: "Rbac", lastName: "Check", preferredCurrency: "UGX", acceptTerms: true,
  selectedModuleCodes: ["student-welfare"],
});
if (registered.status === 429) { console.log("    SKIP — sign-ups are rate-limited this hour."); process.exit(0); }
const ORG = registered.json?.organizationId ?? registered.json?.organization?.id;
ok("40.0a: a scratch tenant is registered", !!ORG, `status ${registered.status} ${registered.text?.slice(0, 200)}`);
if (!ORG) process.exit(1);
await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/verify`, {});
// A trial is one module; the platform grants the others the checks below touch.
for (const code of ["core-queue", "engagement-communications", "visitor-management"]) {
  const g = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/${code}`, { note: "e2e section 40" });
  if (g.status >= 300) ok(`40.0x: ${code} is granted`, false, `status ${g.status} ${g.text?.slice(0, 150)}`);
}
const AD = await login(ADMIN_EMAIL, PASS);
ok("40.0b: its administrator can sign in", !!AD, "no token");

const branches = await call(AD, "GET", "/api/v1/branches");
const BRANCH = (branches.json?.items ?? branches.json ?? [])[0]?.id;
const roleList = (await call(AD, "GET", "/api/v1/roles")).json;
const ROLES = Object.fromEntries((roleList?.items ?? roleList ?? []).map(r => [r.code, r.id]));
const WANTED = ["teacher", "viewer", "staff", "manager", "academic-assistant", "director-of-studies",
                "deputy-head-teacher", "head-teacher", "support-staff", "board-member"];
const present = WANTED.filter(c => ROLES[c]);
ok("40.0c: the tenant has a branch and the seeded school and front-office roles", !!BRANCH && present.length >= 8,
  `branch ${BRANCH}; roles ${Object.keys(ROLES).join(", ")}`);

// One person per role, each with a phone number, so every read below has something to leak.
const people = {};
let n = 0;
for (const code of present) {
  n++;
  const username = `rb${RUN}${n}`;
  const r = await call(AD, "POST", "/api/v1/users", {
    username, email: `${code}.${RUN}@${DOMAIN}`, password: PASS, firstName: "Role", lastName: code.replace(/-/g, " "),
    phone: `07700${String(10000 + n).slice(-5)}`, employeeNumber: `R-${RUN}-${n}`, roleId: ROLES[code], assignedBranchId: BRANCH,
  });
  if (r.status === 201 || r.status === 200) {
    people[code] = { id: r.json.id, token: await login(`${code}.${RUN}@${DOMAIN}`, PASS) };
  } else {
    ok(`40.0d: a ${code} is created`, false, `status ${r.status} ${r.text?.slice(0, 200)}`);
  }
}
ok("40.0d: one account per role is created and signs in", Object.values(people).every(p => p.token) && Object.keys(people).length === present.length,
  Object.entries(people).filter(([, p]) => !p.token).map(([c]) => c).join(", "));
const T = (code) => people[code]?.token;

try {
  // ===================================================================================
  hdr("40.1 The school's messaging secrets");
  const seed = await call(AD, "PUT", "/api/v1/notifications/settings", {
    organizationId: ORG, smsEnabled: true, smsGatewayUrl: "https://sms.example.test", smsApiKey: `key-${RUN}`,
    smsUsername: "gateway-user", smsPassword: `smspw-${RUN}`, smsSenderId: "SACC", smsLeadTokens: 3,
    emailEnabled: true, smtpHost: "smtp.example.test", smtpPort: 587, smtpUseSsl: true, smtpUsername: "mailer",
    smtpPassword: `smtppw-${RUN}`, emailFromAddress: `noreply@${DOMAIN}`, emailFromName: "Rbac Check",
    telegramEnabled: false, telegramBotToken: `tg-${RUN}`, whatsAppEnabled: false, whatsAppPhoneNumberId: "123",
    whatsAppAccessToken: `wa-${RUN}`, inAppEnabled: true, inAppPlaySound: true, inAppRetentionDays: 30,
  });
  ok("40.1a: the administrator saves messaging settings with five secrets", seed.status === 200, `status ${seed.status} ${seed.text?.slice(0, 200)}`);
  ok("40.1b: …and the save's own answer carries masks, not the secrets", !seed.text?.includes(RUN.toString()) || !/key-|smspw-|smtppw-|tg-|wa-/.test(seed.text ?? ""),
    seed.text?.slice(0, 300));

  const read = await call(AD, "GET", `/api/v1/notifications/settings/${ORG}`);
  ok("40.1c: the administrator reads the settings", read.status === 200, `status ${read.status}`);
  const secrets = ["smsApiKey", "smsPassword", "smtpPassword", "telegramBotToken", "whatsAppAccessToken"];
  ok("40.1d: every stored secret comes back as the mask", secrets.every(k => read.json?.[k] === MASK),
    secrets.map(k => `${k}=${read.json?.[k]}`).join(" "));
  ok("40.1e: …and no secret appears anywhere in the response", !/key-|smspw-|smtppw-|tg-|wa-/.test(read.text ?? ""), read.text?.slice(0, 200));

  for (const code of Object.keys(people)) {
    const r = await call(T(code), "GET", `/api/v1/notifications/settings/${ORG}`);
    ok(`40.1f: a ${code} is refused the settings (403)`, r.status === 403, `status ${r.status} ${r.text?.slice(0, 120)}`);
  }

  // Round-trip: send back exactly what was read (masks), change one ordinary field, clear one secret.
  const roundTrip = { ...read.json, emailFromName: "Renamed sender", whatsAppAccessToken: "" };
  const saved = await call(AD, "PUT", "/api/v1/notifications/settings", roundTrip);
  ok("40.1g: a save that returns the masks succeeds", saved.status === 200, `status ${saved.status} ${saved.text?.slice(0, 200)}`);
  const after = await call(AD, "GET", `/api/v1/notifications/settings/${ORG}`);
  ok("40.1h: …the secrets it did not retype are still set", ["smsApiKey", "smsPassword", "smtpPassword", "telegramBotToken"].every(k => after.json?.[k] === MASK),
    ["smsApiKey", "smsPassword", "smtpPassword", "telegramBotToken"].map(k => `${k}=${after.json?.[k]}`).join(" "));
  ok("40.1i: …the one sent empty is cleared", !after.json?.whatsAppAccessToken, `whatsAppAccessToken=${after.json?.whatsAppAccessToken}`);
  ok("40.1j: …and the ordinary field changed", after.json?.emailFromName === "Renamed sender", after.json?.emailFromName);

  // ===================================================================================
  hdr("40.2 The channel flags the Integrations tab reads");
  const flags = await call(AD, "GET", `/api/v1/notifications/settings/${ORG}/channels`);
  ok("40.2a: the administrator reads four on/off flags", flags.status === 200 && flags.json?.smsEnabled === true && flags.json?.emailEnabled === true,
    `status ${flags.status} ${flags.text?.slice(0, 200)}`);
  ok("40.2b: …and nothing else — no secret, no address", Object.keys(flags.json ?? {}).length === 4, Object.keys(flags.json ?? {}).join(", "));
  const tFlags = await call(T("teacher"), "GET", `/api/v1/notifications/settings/${ORG}/channels`);
  ok("40.2c: a teacher is refused even the flags (403)", tFlags.status === 403, `status ${tFlags.status}`);

  // ===================================================================================
  hdr("40.3 Printing a ticket is a write");
  const fakeToken = "00000000-0000-0000-0000-00000000abcd";
  const vPrint = await call(T("viewer"), "POST", `/api/v1/branches/${BRANCH}/tokens/${fakeToken}/print`, { printerIpAddress: "10.255.255.1" });
  ok("40.3a: a Viewer (tokens.view only) cannot print (403)", vPrint.status === 403, `status ${vPrint.status} ${vPrint.text?.slice(0, 120)}`);
  const aPrint = await call(AD, "POST", `/api/v1/branches/${BRANCH}/tokens/${fakeToken}/print`, { printerIpAddress: "10.255.255.1" });
  ok("40.3b: the administrator reaches it (404 for a ticket that does not exist, not 403)", aPrint.status === 404, `status ${aPrint.status}`);

  // ===================================================================================
  hdr("40.4 A colleague's identity and next of kin");
  const subject = people["teacher"].id;
  const base = await call(AD, "GET", `/api/v1/branches/${BRANCH}/staff/structure/members/${subject}/profile`);
  ok("40.4a: the administrator reads the teacher's staff record", base.status === 200, `status ${base.status} ${base.text?.slice(0, 200)}`);
  if (base.status === 200) {
    const p = base.json;
    const put = await call(AD, "PUT", `/api/v1/branches/${BRANCH}/staff/structure/members/${subject}/profile`, {
      phone: p.phone, alternatePhone: p.alternatePhone, officeLocation: p.officeLocation,
      emergencyContactName: "Next Of Kin", emergencyContactPhone: "0772000111",
      jobTitle: p.jobTitle, employeeNumber: p.employeeNumber, employmentStartDate: p.employmentStartDate,
      employmentEndDate: p.employmentEndDate, employmentType: p.employmentType, qualification: p.qualification,
      teachingRegistrationNumber: p.teachingRegistrationNumber, dateOfBirth: "1990-05-17", sex: p.sex, nationalId: `CM${RUN}`.toUpperCase(),
    });
    ok("40.4b: …and records a national ID, a date of birth and a next of kin", put.status === 200, `status ${put.status} ${put.text?.slice(0, 200)}`);
    const aa = await call(T("academic-assistant"), "GET", `/api/v1/branches/${BRANCH}/staff/structure/members/${subject}/profile`);
    ok("40.4c: the Academic Assistant (whole-school staff reader) can open the record", aa.status === 200, `status ${aa.status}`);
    ok("40.4d: …with the national ID, date of birth and next of kin BLANKED",
      aa.json && !aa.json.nationalId && !aa.json.dateOfBirth && !aa.json.emergencyContactName && !aa.json.emergencyContactPhone,
      JSON.stringify({ n: aa.json?.nationalId, d: aa.json?.dateOfBirth, e: aa.json?.emergencyContactName }));
    const adm = await call(AD, "GET", `/api/v1/branches/${BRANCH}/staff/structure/members/${subject}/profile`);
    ok("40.4e: the administrator still reads them", adm.json?.nationalId === `CM${RUN}`.toUpperCase() && adm.json?.emergencyContactName === "Next Of Kin",
      JSON.stringify({ n: adm.json?.nationalId, e: adm.json?.emergencyContactName }));
    const self = await call(T("teacher"), "GET", `/api/v1/staff/portal/profile`);
    ok("40.4f: the teacher reads their OWN national ID on their file", self.json?.nationalId === `CM${RUN}`.toUpperCase(), `status ${self.status} ${self.json?.nationalId}`);
  }

  // ===================================================================================
  hdr("40.5 What the school pays");
  const mineT = await call(T("teacher"), "GET", "/api/v1/modules/mine");
  ok("40.5a: a teacher still reads which modules are on (every page's gate needs it)", mineT.status === 200 && Array.isArray(mineT.json) && mineT.json.length > 0,
    `status ${mineT.status}`);
  ok("40.5b: …with no price, cycle, trial or activation date", (mineT.json ?? []).every(m => m.agreedPriceUgx == null && m.billingCycle == null && m.trialEndsAt == null && m.activatedAt == null),
    JSON.stringify((mineT.json ?? [])[0] ?? {}).slice(0, 200));
  const mineA = await call(AD, "GET", "/api/v1/modules/mine");
  ok("40.5c: the administrator (billing.view) reads the trial dates", (mineA.json ?? []).some(m => m.trialEndsAt != null || m.activatedAt != null),
    JSON.stringify((mineA.json ?? [])[0] ?? {}).slice(0, 200));

  // ===================================================================================
  hdr("40.6 A feedback link is a write");
  const vLink = await call(T("viewer"), "POST", `/api/v1/branches/${BRANCH}/tokens/${fakeToken}/feedback-link`);
  ok("40.6a: a Viewer (feedback.view) cannot generate one (403)", vLink.status === 403, `status ${vLink.status} ${vLink.text?.slice(0, 120)}`);
  const aLink = await call(AD, "POST", `/api/v1/branches/${BRANCH}/tokens/${fakeToken}/feedback-link`);
  ok("40.6b: the administrator reaches it (404 for a ticket that does not exist)", aLink.status === 404, `status ${aLink.status}`);

  // ===================================================================================
  hdr("40.7 The account list's phone book");
  // Every role that may READ the account list: it sees other people's phones exactly when it may
  // EDIT accounts (users.edit). The Front Office Manager edits front-desk accounts, so it does.
  for (const code of Object.keys(people)) {
    const me = await call(T(code), "GET", "/api/v1/auth/me");
    const perms = me.json?.permissions ?? me.json?.user?.permissions ?? [];
    if (!perms.includes("users.view")) continue;
    const mayEdit = perms.includes("users.edit");
    const list = await call(T(code), "GET", "/api/v1/users");
    const others = (list.json ?? []).filter(u => u.id !== people[code].id);
    ok(`40.7a: a ${code} (users.view${mayEdit ? " + users.edit" : ""}) reads the list`, list.status === 200 && others.length > 0, `status ${list.status}`);
    ok(`40.7b: …and ${mayEdit ? "SEES" : "does not see"} other people's phone numbers`,
      mayEdit ? others.some(u => u.phone) : others.every(u => !u.phone),
      others.filter(u => u.phone).map(u => u.username).join(", ") || "none shown");
    const one = await call(T(code), "GET", `/api/v1/users/${people["teacher"].id}`);
    ok(`40.7c: …and the by-id read agrees`, one.status === 200 && (mayEdit ? !!one.json?.phone : !one.json?.phone), `status ${one.status} ${one.json?.phone}`);
  }
  const adList = await call(AD, "GET", "/api/v1/users");
  ok("40.7d: the administrator (users.edit) reads phone numbers", (adList.json ?? []).some(u => u.phone), "no phone in the admin's list");

  // ===================================================================================
  hdr("40.8 The account: one contact writer, names are the school's, email on its canonical form");
  const c0 = await call(T("teacher"), "GET", "/api/v1/profile/contact");
  ok("40.8a: GET profile/contact answers for the person themselves", c0.status === 200 && !!c0.json?.phone, `status ${c0.status} ${c0.text?.slice(0, 150)}`);
  const c1 = await call(T("teacher"), "PUT", "/api/v1/profile/contact", {
    phone: c0.json?.phone, alternatePhone: "0701111222", officeLocation: "Staff room C",
    emergencyContactName: "Kin", emergencyContactPhone: "0772333444", jobTitle: "Principal (ignored)",
  });
  ok("40.8b: PUT profile/contact saves the four contact fields", c1.status === 200 && c1.json?.officeLocation === "Staff room C", `status ${c1.status} ${c1.text?.slice(0, 150)}`);
  const title = await call(AD, "GET", `/api/v1/branches/${BRANCH}/staff/structure/members/${people["teacher"].id}/profile`);
  ok("40.8c: …and ignores a job title sent with it", title.json?.jobTitle !== "Principal (ignored)", title.json?.jobTitle);
  const oldRoute = await call(T("teacher"), "PUT", "/api/v1/staff/portal/profile/contact", { phone: "0700000000" });
  ok("40.8d: the old portal contact route is gone (404 or 405)", oldRoute.status === 404 || oldRoute.status === 405, `status ${oldRoute.status}`);

  const me0 = await call(T("teacher"), "GET", "/api/v1/profile");
  const renamed = await call(T("teacher"), "PUT", "/api/v1/profile", { firstName: "Renamed", lastName: "Myself" });
  ok("40.8e: a name sent to PUT profile is ignored (the school's record)", renamed.status === 200 && renamed.json?.firstName === me0.json?.firstName,
    `status ${renamed.status} ${renamed.json?.firstName}`);
  const bad = await call(T("teacher"), "PUT", "/api/v1/profile", { email: "not-an-address" });
  ok("40.8f: a malformed email is refused in words (400)", bad.status === 400 && /valid/i.test(bad.text ?? ""), `status ${bad.status} ${bad.text?.slice(0, 150)}`);
  const clash = await call(T("teacher"), "PUT", "/api/v1/profile", { email: `VIEWER.${RUN}@${DOMAIN.toUpperCase()}` });
  ok("40.8g: a colleague's address in different case is refused as in use (400, never a 500)", clash.status === 400 && /in use/i.test(clash.text ?? ""),
    `status ${clash.status} ${clash.text?.slice(0, 150)}`);

  // ===================================================================================
  hdr("40.9 A changed number is unconfirmed again, whoever changes it");
  const psql = process.env.PSQL;
  if (!psql || !process.env.PGPASSWORD) {
    skipped("40.9", "set PSQL (psql executable) and PGPASSWORD/PGDATABASE to run it — a real confirmation needs an SMS");
  } else {
    const sql = (q) => execFileSync(psql, ["-h", process.env.PGHOST ?? "localhost", "-U", process.env.PGUSER ?? "postgres",
      "-d", process.env.PGDATABASE ?? "qmgr", "-Atc", q], { encoding: "utf8" }).trim();
    const id = people["teacher"].id;
    sql(`UPDATE qmgr.users SET "PhoneVerifiedAt" = now() WHERE "Id" = '${id}'`);
    const v0 = await call(T("teacher"), "GET", "/api/v1/profile/contact");
    ok("40.9a: the number reads as confirmed", !!v0.json?.phoneVerifiedAt, JSON.stringify(v0.json));
    await call(T("teacher"), "PUT", "/api/v1/profile/contact", { ...v0.json, officeLocation: "Staff room D" });
    const v1 = await call(T("teacher"), "GET", "/api/v1/profile/contact");
    ok("40.9b: saving the contact card WITHOUT changing the number keeps the confirmation", !!v1.json?.phoneVerifiedAt, JSON.stringify(v1.json));
    await call(T("teacher"), "PUT", "/api/v1/profile/contact", { ...v1.json, phone: "0703999888" });
    const v2 = await call(T("teacher"), "GET", "/api/v1/profile/contact");
    ok("40.9c: changing the number on the contact card clears it", v2.json?.phone === "0703999888" && !v2.json?.phoneVerifiedAt, JSON.stringify(v2.json));

    sql(`UPDATE qmgr.users SET "PhoneVerifiedAt" = now() WHERE "Id" = '${id}'`);
    const adminEdit = await call(AD, "GET", `/api/v1/branches/${BRANCH}/staff/structure/members/${id}/profile`);
    const pp = adminEdit.json ?? {};
    await call(AD, "PUT", `/api/v1/branches/${BRANCH}/staff/structure/members/${id}/profile`, {
      phone: "0704555666", alternatePhone: pp.alternatePhone, officeLocation: pp.officeLocation,
      emergencyContactName: pp.emergencyContactName, emergencyContactPhone: pp.emergencyContactPhone,
      jobTitle: pp.jobTitle, employeeNumber: pp.employeeNumber, employmentStartDate: pp.employmentStartDate,
      employmentEndDate: pp.employmentEndDate, employmentType: pp.employmentType, qualification: pp.qualification,
      teachingRegistrationNumber: pp.teachingRegistrationNumber, dateOfBirth: pp.dateOfBirth, sex: pp.sex, nationalId: pp.nationalId,
    });
    const v3 = await call(T("teacher"), "GET", "/api/v1/profile/contact");
    ok("40.9d: an ADMINISTRATOR changing the number clears it too (the rule is the database layer's)", v3.json?.phone === "0704555666" && !v3.json?.phoneVerifiedAt, JSON.stringify(v3.json));
  }
} finally {
  hdr("40.10 Put things back — the scratch tenant is purged");
  await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "Cancelled", reason: "e2e section 40" });
  await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "PendingDeletion", reason: "e2e section 40" });
  const purged = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/purge`, { confirmName: ORG_NAME, reason: "e2e section 40" });
  ok("40.10a: the scratch tenant is purged and verified", purged.status === 200 && purged.json?.verificationPassed === true,
    `status ${purged.status} ${purged.text?.slice(0, 200)}`);
}

console.log(`\n40. DONE — ${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ""}`);
post(`40. DONE — ${pass} passed, ${fail} failed`);
process.exitCode = fail > 0 ? 1 : 0;

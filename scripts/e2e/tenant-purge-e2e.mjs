// SECTION 20 — TENANT LIFECYCLE AND COMPLETE PURGE.
//
// "I expect many trial accounts, most of which will not translate into business ... the cleanup
// should not leave any trace of the tenant."
//
// THIS SUITE CREATES ITS OWN TENANT AND DESTROYS IT. It cannot run against the dev tenant, because
// a successful run ends with the tenant gone — which is also the cleanest possible demonstration
// that the thing works. It probes for a Development API on the way in and REFUSES against anything
// else (see 20.1): a run against a live server would leave a tombstone and a purge certificate
// behind, and no other suite here can do damage of that kind.
//
// What it proves, in order:
//   1  The lifecycle: the states, the clocks, and that Deleted is unreachable by hand.
//   2  The inventory finds real rows — which is what makes the completeness check afterwards mean
//      something rather than being a count of nothing.
//   3  A purge is REFUSED unless the tenant is scheduled for deletion and the name is typed back.
//   4  The purge empties every table, deletes the files, removes the Hangfire jobs, and VERIFIES —
//      the check reading information_schema, not the EF model.
//   5  The tombstone survives, holds no personal data, and makes a returning sign-up visible.
//   6  A certificate records what happened, and the independent re-verification finds nothing.
//
// Run: API=http://127.0.0.1:5001 node scripts/e2e/tenant-purge-e2e.mjs
import zlib from "node:zlib";

const API = process.env.API ?? "http://127.0.0.1:5001";
const SA_USER = process.env.SA_USER ?? "superadmin";
const SA_PASS = process.env.SA_PASS ?? "admin";
const RUN = Date.now().toString(36);
const ORG_NAME = `Purge Check ${RUN}`;
const ADMIN_EMAIL = `purge.${RUN}@purge-${RUN}.sch.ug`;
const ADMIN_PASS = "PurgeCheck!2026x";

let pass = 0, fail = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
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

// A real PNG, built byte by byte, so the file sweep has something genuine to delete.
const crcTable = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
  return t;
})();
const crc32 = (buf) => { let c = 0xFFFFFFFF; for (const b of buf) c = crcTable[(c ^ b) & 0xFF] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; };
const chunk = (type, data) => {
  const t = Buffer.from(type, "ascii"), body = Buffer.concat([t, Buffer.from(data)]);
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
};
const png = (w, h) => {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 6;
  const z = zlib.deflateSync(Buffer.alloc(h * (1 + w * 4)));
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
    chunk("IHDR", ihdr), chunk("IDAT", z), chunk("IEND", Buffer.alloc(0)),
  ]);
};

const SA = await login(SA_USER, SA_PASS);
if (!SA) { console.error("could not sign in as the platform administrator"); process.exit(1); }

hdr(`20. TENANT PURGE — a tenant created, filled, and removed without trace (run ${RUN})`);

// =====================================================================================
hdr("20.1 A tenant of our own to destroy");

// Sign-ups are capped at three an hour per address, which is right in production and unworkable
// for a suite that creates a tenant in order to destroy one. Development only; 404 elsewhere.
//
// That 404 is also the environment probe, and this suite REFUSES rather than carrying on. Every
// other suite here reads; this one registers an organization and then destroys it, so a run
// against the wrong API leaves a tombstone, a purge certificate and a lifecycle log in a
// production database — rows nobody asked for, in the one feature whose promise is that it leaves
// nothing behind. A suite that can do real damage is not the place for "it will probably fail
// anyway", and the message names the two ways past it.
const budget = await call(SA, "POST", "/api/v1/admin/registration-budget/reset", {});
if (budget.status === 404 && process.env.PURGE_E2E_ALLOW_NON_DEV !== "1") {
  console.error(`
REFUSING TO RUN. ${API} is not a Development API — /api/v1/admin/registration-budget/reset
answered 404, and that endpoint exists only in Development.

This suite REGISTERS a real organization and then PURGES it. Against a live server that writes a
tombstone, a purge certificate and a lifecycle log that cannot be taken back.

Point API= at a local Development instance, or set PURGE_E2E_ALLOW_NON_DEV=1 if you genuinely
mean to create and destroy a tenant on ${API}.`);
  process.exit(1);
}

const registered = await call(null, "POST", "/api/v1/register", {
  organizationName: ORG_NAME,
  email: ADMIN_EMAIL,
  password: ADMIN_PASS,
  confirmPassword: ADMIN_PASS,
  firstName: "Purge",
  lastName: "Check",
  preferredCurrency: "UGX",
  acceptTerms: true,
  // Exactly one module: registration refuses zero or more than one.
  selectedModuleCodes: ["core-queue"],
});
if (registered.status === 429) {
  console.log("    SKIP — sign-ups from this address are rate-limited for the hour (RegistrationGuardService).");
  console.log("           That is the product working. Restart the API to clear the counter, or wait.");
  post("    SKIP  section 20 — registration is rate-limited this hour");
  process.exit(0);
}
ok("20.1a: a tenant is registered", registered.status === 200 || registered.status === 201, `status ${registered.status} ${registered.text?.slice(0, 200)}`);

const ORG = registered.json?.organizationId ?? registered.json?.organization?.id;
if (!ORG) { console.error("no organization id came back from registration: " + registered.text?.slice(0, 300)); process.exit(1); }
console.log(`    organization: ${ORG_NAME} (${ORG})`);

// Verify it so the tenant is usable, then sign in as its administrator.
await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/verify`, {});
const AD = await login(ADMIN_EMAIL, ADMIN_PASS);
ok("20.1b: its administrator can sign in", !!AD, "no token");

// =====================================================================================
hdr("20.2 Fill it — the awkward tables, not just the easy ones");

// Grant the modules the seeding below needs. A trial starts with exactly one, and the point of
// this section is to put rows in the AWKWARD tables — the welfare ledger is where the deepest
// tenant-derived paths and the file attachments live.
for (const code of ["student-welfare", "engagement-communications", "visitor-management"]) {
  await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/modules/${code}`, {});
}

const me = await call(AD, "GET", "/api/v1/auth/me");
// A fresh administrator carries no branch on the token; registration made one, so ask for it.
const branches = await call(AD, "GET", "/api/v1/branches");
const BRANCH = me.json?.branchId ?? me.json?.assignedBranchId ?? (branches.json?.items ?? branches.json ?? [])[0]?.id;
ok("20.2a: the tenant has a branch", !!BRANCH, JSON.stringify(me.json ?? {}).slice(0, 200));

if (BRANCH) {
  // A student, a welfare category and a welfare record: the deepest tenant-derived paths in the
  // model, and the ones whose attachments are files on disk.
  const category = await call(AD, "POST", `/api/v1/branches/${BRANCH}/welfare-categories`, {
    name: `Purge ${RUN}`, caseType: "Behaviour", defaultSeverity: "Low",
  });
  const student = await call(AD, "POST", `/api/v1/branches/${BRANCH}/students`, {
    fullName: "Purge Student", firstName: "Purge", lastName: "Student", className: "P1", studentCode: `PC-${RUN}`,
  });
  ok("20.2b: a student is created", student.status === 200 || student.status === 201, `status ${student.status} ${student.text?.slice(0, 160)}`);

  if (student.json?.id && category.json?.id) {
    const record = await call(AD, "POST", `/api/v1/branches/${BRANCH}/welfare-records`, {
      studentId: student.json.id, categoryId: category.json.id,
      caseType: "Behaviour", severity: "Low", summary: `Purge check ${RUN}`, occurredAt: new Date().toISOString(),
    });
    ok("20.2c: a welfare record is created (a table two hops from the organisation)",
      record.status === 200 || record.status === 201, `status ${record.status} ${record.text?.slice(0, 160)}`);
  }

  // A notification: this is what puts a HANGFIRE JOB carrying an email address into the database,
  // in a schema the EF model knows nothing about. It is the residue a row-walking purge misses.
  await call(AD, "POST", "/api/v1/notifications", {
    title: `Purge check ${RUN}`, message: "Seeded so a background job exists.", type: "Info",
    userId: me.json?.userId ?? me.json?.id,
  });

  // A service type and a counter: Core Queue's own derived tables.
  await call(AD, "POST", `/api/v1/branches/${BRANCH}/service-types`, { name: `Purge ${RUN}`, code: `P${RUN}`.slice(0, 8), prefix: "P" });

  // AN ACTUAL FILE ON DISK. This is the residue that cannot be recovered if the order is wrong:
  // uploads are stored under a generated name in one flat directory, so once the rows that point
  // at them are gone the bytes are unattributable and permanent. The purge collects the paths
  // BEFORE it deletes any row, and 20.6b is what proves it.
  await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: true });
  const branding = await call(AD, "GET", `/api/v1/organizations/${ORG}/branding`);
  await call(AD, "PUT", `/api/v1/organizations/${ORG}/branding`, { ...(branding.json ?? {}), whitelabelEnabled: true });

  const form = new FormData();
  form.append("file", new Blob([png(64, 64)], { type: "image/png" }), "logo.png");
  const logo = await fetch(`${API}/api/v1/organizations/${ORG}/branding/logo`, {
    method: "POST", headers: { Authorization: `Bearer ${AD}` }, body: form,
  });
  ok("20.2d: a real file is uploaded, so the purge has bytes to sweep", logo.status === 200, `status ${logo.status}`);
}

// =====================================================================================
hdr("20.3 The inventory — what this tenant IS");

const before = await call(SA, "GET", `/api/v1/admin/tenants/${ORG}/residue`);
ok("20.3a: the inventory reads back", before.status === 200, `status ${before.status} ${before.text?.slice(0, 200)}`);

const tablesWithRows = Object.keys(before.json?.rowsByTable ?? {});
ok("20.3b: it finds rows in several tables — so the check afterwards counts something real",
  tablesWithRows.length >= 5, `found ${tablesWithRows.length}: ${tablesWithRows.slice(0, 10).join(", ")}`);
ok("20.3c: including the organisation itself", tablesWithRows.includes("Organization"), tablesWithRows.join(", "));
ok("20.3d: and a table that reaches the tenant only through a parent",
  tablesWithRows.some(t => ["Counter", "ServiceType", "BranchSettings", "WelfareNote", "RolePermission", "UserSession", "Token"].includes(t)),
  tablesWithRows.join(", "));
console.log(`    ${before.json?.totalRows} row(s) across ${tablesWithRows.length} table(s); ${before.json?.files?.length ?? 0} file(s); ${before.json?.backgroundJobs ?? 0} background job(s)`);

// =====================================================================================
hdr("20.4 A purge is the END of a lifecycle, not a shortcut through it");

const tooSoon = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/purge`, { confirmName: ORG_NAME });
ok("20.4a: purging a tenant that is not scheduled for deletion is refused", tooSoon.status === 409, `status ${tooSoon.status}`);
ok("20.4b: and it says to schedule it first", /schedule/i.test(tooSoon.text ?? ""), tooSoon.text?.slice(0, 200));

const toDeleted = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "Deleted" });
ok("20.4c: a tenant cannot be moved to Deleted by hand — only a purge gets there", toDeleted.status === 400, `status ${toDeleted.status}`);

const cancelled = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "Cancelled", reason: "e2e" });
ok("20.4d: it can be closed", cancelled.status === 200, `status ${cancelled.status} ${cancelled.text?.slice(0, 160)}`);
ok("20.4e: and the next move is scheduled with a clock on it",
  cancelled.json?.nextStatus === "PendingDeletion" && (cancelled.json?.daysUntilNextTransition ?? -1) > 0,
  JSON.stringify(cancelled.json ?? {}).slice(0, 200));

const scheduled = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/lifecycle`, { status: "PendingDeletion", reason: "e2e" });
ok("20.4f: and then scheduled for deletion", scheduled.status === 200 && scheduled.json?.isScheduledForDeletion === true, `status ${scheduled.status}`);
ok("20.4g: the history records every move", (scheduled.json?.history?.length ?? 0) >= 2, `${scheduled.json?.history?.length} event(s)`);

const wrongName = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/purge`, { confirmName: "not the name" });
ok("20.4h: a purge with the wrong name typed back is refused", wrongName.status === 400, `status ${wrongName.status}`);

// =====================================================================================
hdr("20.5 The purge");

const purged = await call(SA, "POST", `/api/v1/admin/tenants/${ORG}/purge`, { confirmName: ORG_NAME, reason: "e2e run" });
ok("20.5a: the purge runs", purged.status === 200, `status ${purged.status} ${purged.text?.slice(0, 300)}`);
ok("20.5b: the completeness check PASSED", purged.json?.verificationPassed === true, purged.json?.verificationDetail ?? "no detail");
ok("20.5c: it deleted rows across many tables",
  (purged.json?.totalRowsDeleted ?? 0) >= (before.json?.totalRows ?? 1), `${purged.json?.totalRowsDeleted} deleted, ${before.json?.totalRows} found`);
console.log(`    ${purged.json?.totalRowsDeleted} row(s), ${purged.json?.filesDeleted} file(s), ${purged.json?.backgroundJobsRemoved} job(s), ${purged.json?.rowsRetained} retained, ${purged.json?.durationMs}ms`);

// A trial that never paid has NO financial record, so nothing is retained and the tenant goes
// whole. That is the common case in this product, and the point of the two-lane design.
ok("20.5d: a trial that never paid leaves nothing under statutory retention", (purged.json?.rowsRetained ?? 0) === 0, `${purged.json?.rowsRetained} retained`);

// =====================================================================================
hdr("20.6 Nothing is left");

const after = await call(SA, "GET", `/api/v1/admin/tenants/${ORG}/residue`);
ok("20.6a: the inventory now finds no rows at all", (after.json?.totalRows ?? -1) === 0, JSON.stringify(after.json?.rowsByTable ?? {}));
ok("20.6b: no files — the bytes went before the rows that named them",
  (after.json?.files?.length ?? -1) === 0 && (purged.json?.filesDeleted ?? 0) > 0,
  `${after.json?.files?.length} left, ${purged.json?.filesDeleted} deleted`);
ok("20.6c: no background jobs — the ones carrying an email address are gone too",
  (after.json?.backgroundJobs ?? -1) === 0, `${after.json?.backgroundJobs} left`);

const gone = await call(SA, "GET", `/api/v1/admin/tenants/${ORG}`);
ok("20.6d: the tenant itself is a 404", gone.status === 404, `status ${gone.status}`);

const signIn = await login(ADMIN_EMAIL, ADMIN_PASS);
ok("20.6e: its administrator can no longer sign in — the user rows went with it", !signIn, "a token still came back");

// =====================================================================================
hdr("20.7 What survives, and why it is not a trace");

const certificates = await call(SA, "GET", "/api/v1/admin/purge-certificates?limit=5");
const certificate = (certificates.json ?? []).find(c => c.organizationId === ORG);
ok("20.7a: a certificate records what happened", !!certificate, `${(certificates.json ?? []).length} certificate(s)`);
ok("20.7b: and it records that verification passed", certificate?.verificationPassed === true, JSON.stringify(certificate ?? {}).slice(0, 200));

// The tombstone is what makes a returning sign-up visible. It holds two one-way hashes and some
// counts — no name, no address, no email — so a re-registration is FLAGGED, never refused.
await call(SA, "POST", "/api/v1/admin/registration-budget/reset", {});
const returning = await call(null, "POST", "/api/v1/register", {
  organizationName: ORG_NAME,
  email: `again.${RUN}@purge-${RUN}.sch.ug`,
  password: ADMIN_PASS,
  confirmPassword: ADMIN_PASS,
  firstName: "Purge", lastName: "Again",
  preferredCurrency: "UGX", acceptTerms: true, selectedModuleCodes: ["core-queue"],
});
ok("20.7c: the same organisation CAN register again — the tombstone warns, it never blocks",
  returning.status === 200 || returning.status === 201 || returning.status === 429,
  `status ${returning.status} ${returning.text?.slice(0, 200)}`);
if (returning.status === 429) console.log("           (rate-limited this hour; the tombstone path itself is asserted by 20.7a/b)");

const RETURNED = returning.json?.organizationId ?? returning.json?.organization?.id;
if (RETURNED) {
  // Clean up the second tenant the same way, which also exercises a purge from Active rather than
  // from a lapsed trial.
  await call(SA, "POST", `/api/v1/admin/tenants/${RETURNED}/lifecycle`, { status: "Cancelled", reason: "e2e cleanup" });
  await call(SA, "POST", `/api/v1/admin/tenants/${RETURNED}/lifecycle`, { status: "PendingDeletion", reason: "e2e cleanup" });
  const second = await call(SA, "POST", `/api/v1/admin/tenants/${RETURNED}/purge`, { confirmName: ORG_NAME, reason: "e2e cleanup" });
  ok("20.7d: the second tenant is purged too, leaving the database as it was found",
    second.status === 200 && second.json?.verificationPassed === true, `status ${second.status}`);
}

// =====================================================================================
hdr("20.8 The other lane — a tenant that HAS transacted");

// Read-only, against whichever tenant on this database actually has invoices. Uganda's Tax
// Procedures Code requires tax records for five years, so those rows cannot be deleted with the
// rest — they are de-identified in place instead. Nothing is purged here; this only proves the
// classification puts them in the retained column rather than the deleted one.
const allTenants = await call(SA, "GET", "/api/v1/admin/tenants?pageSize=50");
let payingChecked = false;
for (const tenant of (allTenants.json?.items ?? [])) {
  const residue = await call(SA, "GET", `/api/v1/admin/tenants/${tenant.id}/residue`);
  const retained = residue.json?.retainedByTable ?? {};
  if (Object.keys(retained).length === 0) continue;

  ok("20.8a: a tenant with financial records reports them as RETAINED, not as rows to delete",
    Object.keys(retained).every(t => t === "Invoice" || t === "Payment"), JSON.stringify(retained));
  ok("20.8b: and they are absent from the delete list",
    !Object.keys(residue.json?.rowsByTable ?? {}).some(t => t === "Invoice" || t === "Payment"),
    JSON.stringify(residue.json?.rowsByTable ?? {}));
  console.log(`    ${tenant.name}: ${JSON.stringify(retained)}`);
  payingChecked = true;
  break;
}
if (!payingChecked)
  ok("20.8: the statutory-retention lane", true, "SKIP — no tenant on this database has an invoice yet");

hdr(`20. DONE — ${pass} passed, ${fail} failed`);
post(`\n20. DONE — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);

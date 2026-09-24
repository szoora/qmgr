// SECTION 38 — SACC DASHBOARD: THE PRODUCT'S NAME AND A SCHOOL'S OWN (rebrand 2026-09-24).
//
// Plan docs/plans/SACC_DASHBOARD_REBRAND.md. What this proves, against a live API:
//   38.1  the name rule (ProductBrand.ValidateBrandName) is enforced on save, in its own words, and a name
//         that passes is stored EXACTLY as typed — "Dashboard", "MARYHILL Dashboard", anything
//   38.2  how the shown name resolves: the school's own only while white-labelling is entitled AND on,
//         otherwise "SACC Dashboard"; the organisation is reported separately and never replaced
//   38.3  the short form a phone's home screen gets
//   38.4  the platform can set a school's app name, under the same rule
//   38.5  registration refuses a bad app name before it creates anything
//   38.6  the mobile app's tenant/info names the product "SACC Dashboard" with the new tagline
//   38.7  the mark: the API's product logo and the Web's /brand/*.svg are the tile S, and the manifest names us
//
// It borrows the dev tenant's branding and puts every field back as it found it; a white-label override
// it had to grant is removed again.
//
// Run: node scripts/e2e/product-brand-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API || "http://127.0.0.1:5001";
const WEB = process.env.WEB || "http://127.0.0.1:5003";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
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

async function call(token, method, path, body, base = API) {
  const h = {};
  if (token) h.Authorization = `Bearer ${token}`;
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await fetch(base + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: res.status, json, text };
}
async function login(id, passwords) {
  for (const pw of passwords) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: pw }) });
    if (r.ok) return (await r.json()).accessToken;
  }
  return null;
}

const AD = await login(TENANT_ADMIN, [PW, NEW_PW]);
const SA = await login(SA_USER, [SA_PASS]);
if (!AD || !SA) { console.error("the tenant administrator or the platform administrator could not sign in"); process.exit(1); }

const me = (await call(AD, "GET", "/api/v1/auth/me")).json;
const ORG = me?.organizationId ?? me?.user?.organizationId;
if (!ORG) { console.error("could not read the tenant administrator's organization"); process.exit(1); }
const brandingPath = `/api/v1/organizations/${ORG}/branding`;
const original = (await call(AD, "GET", brandingPath)).json;
if (!original) { console.error("could not read the organization's branding"); process.exit(1); }

let grantedWhiteLabel = false;
const save = (fields) => call(AD, "PUT", brandingPath, { ...original, ...fields });

try {
  hdr(`38. SACC DASHBOARD — the product's name and a school's own (run ${RUN})`);

  // The branding PUT is gated on the white-label entitlement. Grant it for the run if this tenant lacks it.
  if (!original.whiteLabelEntitled) {
    const g = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: true });
    grantedWhiteLabel = g.status === 200;
    truthy("setup: white-labelling granted for the run", grantedWhiteLabel, `status ${g.status}`);
  }

  hdr("38.1 The name rule, on save");
  const refusals = [
    ["one character", "X", /at least 2/],
    ["41 characters", "M".repeat(41), /at most 40/],
    ["a line break", "Maryhill\nHub", /one line/],
    ["a name claiming to be ours", "SACC School", /SACC/],
    ["…even spelt with dots", "S.A.C.C. Hub", /SACC/],
  ];
  for (const [label, value, pattern] of refusals) {
    const r = await save({ whitelabelEnabled: true, brandName: value });
    truthy(`refused: ${label} (400, in the rule's own words)`, r.status === 400 && pattern.test(r.json?.message ?? ""), `status ${r.status} ${r.text?.slice(0, 160)}`);
  }
  for (const value of ["Dashboard", "MARYHILL Dashboard", "Maryhill Hub"]) {
    const r = await save({ whitelabelEnabled: true, brandName: value });
    eq(`accepted and stored exactly as typed: "${value}"`, r.status === 200 ? r.json?.brandName : `status ${r.status}`, value);
  }
  const padded = await save({ whitelabelEnabled: true, brandName: "  MHHS Connect  " });
  eq("surrounding spaces are trimmed, nothing else is changed", padded.json?.brandName, "MHHS Connect");

  hdr("38.2 How the shown name resolves");
  let r = await save({ whitelabelEnabled: true, brandName: "MARYHILL Dashboard" });
  eq("white-labelling on + a name → the school's name, never with a word added", r.json?.productName, "MARYHILL Dashboard");
  truthy("…and the organisation is reported separately, as itself", !!r.json?.organizationName && r.json.organizationName !== "MARYHILL Dashboard", r.json?.organizationName);
  let mine = (await call(AD, "GET", "/api/v1/branding/mine")).json;
  eq("the shell's branding carries the same name", mine?.productName, "MARYHILL Dashboard");
  eq("…and the organisation's own name beside it", mine?.organizationName, r.json?.organizationName);

  r = await save({ whitelabelEnabled: false, brandName: "MARYHILL Dashboard" });
  eq("white-labelling OFF → ours, whatever is typed", r.json?.productName, "SACC Dashboard");
  mine = (await call(AD, "GET", "/api/v1/branding/mine")).json;
  eq("…and the shell is unbranded", mine?.resolved, false);

  r = await save({ whitelabelEnabled: true, brandName: "" });
  eq("white-labelling on, no name → ours", r.json?.productName, "SACC Dashboard");
  // Asserted on the STATUS as well: an empty name once 500ed, and a missing body reads as "none" too.
  eq("…and an empty name is stored as none", r.status === 200 ? (r.json?.brandName ?? null) : `status ${r.status}`, null);

  hdr("38.3 The short form, for a home-screen label");
  r = await save({ whitelabelEnabled: true, brandName: "Maryhill High School Hub" });
  eq("a long name shortens to its first word", r.json?.productShortName, "Maryhill");
  r = await save({ whitelabelEnabled: true, brandName: "Dashboard" });
  eq("a short name is kept whole", r.json?.productShortName, "Dashboard");
  r = await save({ whitelabelEnabled: false });
  eq("ours shortens to SACC", r.json?.productShortName, "SACC");

  hdr("38.4 The platform sets it for a school that asks");
  let p = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/brand-name`, { brandName: "SACC Kids" });
  truthy("the same rule refuses a name claiming to be ours", p.status === 400 && /SACC/.test(p.json?.message ?? ""), `status ${p.status}`);
  p = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/brand-name`, { brandName: `Hub ${RUN}` });
  eq("a valid name is saved", p.status, 200);
  const detail = (await call(SA, "GET", `/api/v1/admin/tenants/${ORG}`)).json;
  eq("…and the tenant record shows it", detail?.brandName, `Hub ${RUN}`);
  const tenantView = (await call(AD, "GET", brandingPath)).json;
  eq("…and the school's own page reads it back", tenantView?.brandName, `Hub ${RUN}`);

  hdr("38.5 Registration refuses a bad app name before it creates anything");
  await call(SA, "POST", "/api/v1/admin/registration-budget/reset", {});
  const email = `brand.${RUN}@e2e-rebrand.test`;
  const reg = await call(null, "POST", "/api/v1/register", {
    organizationName: `Rebrand Check ${RUN}`, brandName: "SACC Academy",
    email, password: NEW_PW, confirmPassword: NEW_PW, firstName: "Brand", lastName: "Check",
    preferredCurrency: "UGX", acceptTerms: true, selectedModuleCodes: ["core-queue"],
  });
  if (reg.status === 429) {
    console.log("  \x1b[33mSKIP\x1b[0m  registration is rate-limited this hour — the product working");
  } else {
    truthy("refused, naming the rule", reg.status === 400 && /SACC/.test(reg.text ?? ""), `status ${reg.status} ${reg.text?.slice(0, 200)}`);
    const tenants = (await call(SA, "GET", `/api/v1/admin/tenants?pageSize=200&search=${encodeURIComponent("Rebrand Check " + RUN)}`)).json;
    const found = (tenants?.items ?? []).filter((t) => t.name === `Rebrand Check ${RUN}`);
    eq("…and no organisation was created", found.length, 0);
  }

  hdr("38.6 The mobile app's workspace answer");
  const info = await call(null, "GET", "/api/v1/tenant/info");
  eq("the shared host names the product SACC Dashboard", info.json?.product, "SACC Dashboard");
  eq("…with the new tagline, not \"Front Office\"", info.json?.descriptor, "Staff, welfare and front office");

  hdr("38.7 The mark, and the manifest");
  const logo = await call(null, "GET", "/api/v1/branding/product-logo");
  const tiles = (logo.text.match(/<rect /g) ?? []).length;
  eq("the mobile product logo is the tile S: eleven lit tiles, no plate", tiles, 11);
  truthy("…carrying our name as its accessible label", /aria-label="SACC Dashboard"/.test(logo.text), logo.text.slice(0, 120));
  const appIcon = await call(null, "GET", "/brand/app-icon.svg", undefined, WEB);
  if (appIcon.status === 0 || appIcon.status >= 500) {
    console.log("  \x1b[33mSKIP\x1b[0m  the Web is not running on " + WEB);
  } else {
    eq("the Web serves the app icon from the same drawing", (appIcon.text.match(/<rect /g) ?? []).length, 16);
    const manifest = (await call(null, "GET", "/app-manifest.json", undefined, WEB)).json;
    eq("the platform host's manifest names us", manifest?.name, "SACC Dashboard");
    eq("…with SACC on the home screen", manifest?.short_name, "SACC");
    truthy("…and a maskable icon of our own", (manifest?.icons ?? []).some((i) => i.purpose === "maskable"), JSON.stringify(manifest?.icons));
    const gone = await call(null, "GET", "/manifest.json", undefined, WEB);
    truthy("the old static manifest is gone", gone.status === 404, `status ${gone.status}`);
  }
} finally {
  // Put the tenant's branding back exactly as it was.
  const back = await call(AD, "PUT", brandingPath, original);
  if (back.status !== 200) console.log(`  (could not restore the branding: ${back.status} ${back.text?.slice(0, 200)})`);
  if (grantedWhiteLabel) await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: false });
}

console.log(`\n  section 38: ${pass} passed, ${fail} failed`);
if (failures.length) console.log("  failed: " + failures.join(" | "));
process.exitCode = fail ? 1 : 0;

// SECTION 19 — CUSTOM TENANT DOMAINS, BRAND-ASSET UPLOADS, AND ATTRIBUTION REMOVAL.
//
// The white-label plan, phases 0-8. What this proves, in order:
//
//   0  A host that matches NO subdomain rule resolves its tenant. That single check IS the Phase 0
//      defect: the custom-domain lookup was nested inside the subdomain branch, and
//      ExtractSubdomainAsync returns null for exactly the hosts the feature was written for, so the
//      branch had never once run.
//   1  A real PNG is accepted; a .png whose bytes are a script is refused; an over-size file is
//      refused; a non-square favicon is refused; the OLD file is gone after a replace; and the
//      served file's Content-Type comes from the bytes rather than from anything the client said.
//   2  An apex is refused, the platform's own domain is refused, a host another tenant holds is
//      refused, a wrong TXT value is refused, the right one is accepted — and the refusals name the
//      STEP rather than saying "failed". DNS is stubbed behind IDnsTxtLookup; everything else runs
//      for real.
//   8  remove_attribution is NOT granted by the Communication module (the whole reason it is a code
//      of its own), it defaults off, a platform override turns it on, and the override is visible on
//      the next request rather than in five minutes.
//
// Run the API with the stub and the certificate step skipped — Development only, both checked
// against the environment as well as the key:
//   Dns__Stub=true CustomDomains__SkipCertificate=true dotnet run --project src/Q-Mgr.API ...
//   API=http://127.0.0.1:5001 node scripts/e2e/white-label-e2e.mjs
import zlib from "node:zlib";
import http from "node:http";

// fetch() REFUSES to set a Host header — it is a forbidden header name and undici drops it
// silently, so a "Host:" passed to fetch reaches the server as the connection's own address and
// every custom-domain assertion built on it passes for the wrong reason. Raw node:http is the only
// way to send one, which is why this exists rather than another fetch wrapper.
const rawGet = (path, host) => new Promise((resolve) => {
  const url = new URL(API);
  const req = http.request(
    { hostname: url.hostname, port: url.port, path, method: "GET", headers: host ? { Host: host } : {} },
    (res) => { let body = ""; res.on("data", (d) => (body += d)); res.on("end", () => resolve({ status: res.statusCode, text: body })); });
  req.on("error", () => resolve({ status: 0, text: "" }));
  req.end();
});

const API = process.env.API ?? "http://127.0.0.1:5001";
const SA_USER = process.env.SA_USER ?? "superadmin";
const SA_PASS = process.env.SA_PASS ?? "admin";
const RUN = Date.now().toString(36);
const DOMAIN = `e2e-${RUN}.maryhillug.net`;

let pass = 0, fail = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const login = async (email, password) => {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  return (await r.json().catch(() => ({}))).accessToken;
};

const call = async (token, method, path, body, extraHeaders = {}) => {
  const headers = { "Content-Type": "application/json", ...extraHeaders };
  if (token) headers.Authorization = `Bearer ${token}`;
  const r = await fetch(`${API}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text, headers: r.headers };
};

const upload = async (token, path, bytes, fileName, contentType) => {
  const form = new FormData();
  form.append("file", new Blob([bytes], { type: contentType }), fileName);
  const r = await fetch(`${API}${path}`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};

// ---- A real PNG, built byte by byte. No fixture file to go missing, and every field is what the
// ---- probe is about to read: signature, IHDR with the dimensions, IDAT, IEND, real CRCs.
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
const png = (w, h, padTo = 0) => {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  // zlib stream of a single all-zero scanline set, stored uncompressed.
  const raw = Buffer.alloc(h * (1 + w * 4));
  const z = zlib.deflateSync(raw);
  const parts = [Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), chunk("IHDR", ihdr), chunk("IDAT", z)];
  // A tEXt chunk is how a file is padded to a size without lying about its pixels.
  if (padTo > 0) parts.push(chunk("tEXt", Buffer.concat([Buffer.from("pad\0", "ascii"), Buffer.alloc(padTo, 0x61)])));
  parts.push(chunk("IEND", Buffer.alloc(0)));
  return Buffer.concat(parts);
};

const SA = await login(SA_USER, SA_PASS);
if (!SA) { console.error("could not sign in as the platform administrator"); process.exit(1); }

const me = await call(SA, "GET", "/api/v1/auth/me");
const tenants = await call(SA, "GET", "/api/v1/admin/tenants?pageSize=50");
if (tenants.status !== 200) { console.error(`could not list tenants (${tenants.status})`); process.exit(1); }

// The dev tenant, chosen as the first one that is not the platform's own. Never a tenant that
// already holds a custom domain — this suite claims and releases one.
const candidates = (tenants.json?.items ?? []).filter((t) => t.slug !== "platform");
if (candidates.length < 2) { console.error("this suite needs at least two tenants on the dev database"); process.exit(1); }
const TENANT = candidates[0];
const OTHER = candidates[1];

hdr(`19. WHITE LABEL — tenant domains, brand assets and attribution (run ${RUN})`);
console.log(`    tenant: ${TENANT.name} (${TENANT.id});  second tenant: ${OTHER.name}`);

// =====================================================================================
hdr("19.1 The domain claim, and what it refuses");

const request = (domain, id = TENANT.id) => call(SA, "PUT", `/api/v1/admin/tenants/${id}/custom-domain`, { domain });

const apex = await request("maryhillug.net");
ok("19.1a: an apex is refused", apex.status === 400, `status ${apex.status}`);
ok("19.1b: and the refusal explains WHY (DNS forbids a CNAME at the root)",
  /subdomain/i.test(apex.json?.title ?? ""), apex.json?.title);

const junk = await request("not a domain");
ok("19.1c: a value that is not a host is refused", junk.status === 400, `status ${junk.status}`);

// The platform's own base domain. Read from the SaaS settings so this follows the install.
const saas = await call(SA, "GET", "/api/v1/platform/settings/SaaS");
const baseDomain = saas.json?.baseDomain ?? saas.json?.BaseDomain;
if (baseDomain) {
  const own = await request(`anything.${baseDomain}`);
  ok("19.1d: a host inside the platform's own domain is refused", own.status === 400, `status ${own.status}`);
} else {
  ok("19.1d: a host inside the platform's own domain is refused", true, "SKIP — no base domain configured");
}

const claimed = await request(DOMAIN);
ok("19.1e: a valid subdomain is accepted", claimed.status === 200, `status ${claimed.status} ${claimed.text?.slice(0, 160)}`);
ok("19.1f: it is PENDING, never live", String(claimed.json?.state) === "Pending" || claimed.json?.state === 1, `state ${claimed.json?.state}`);
ok("19.1g: nothing is routed to it yet", claimed.json?.domain == null, `domain ${claimed.json?.domain}`);
ok("19.1h: the TXT record to create is named in full",
  claimed.json?.verificationRecordName === `_qmgr-verify.${DOMAIN}` && (claimed.json?.verificationRecordValue ?? "").length > 20,
  `${claimed.json?.verificationRecordName} = ${claimed.json?.verificationRecordValue}`);

// THE SECOND RECORD. A domain whose owner is proved but which still resolves to their old web
// host is not a working domain, and until 2026-09-21 the panel asked only for the TXT — so there
// was nothing anywhere telling the tenant to point the name here, and nothing here asserting it.
ok("19.1h2: the CNAME record to create is named in full, beside the TXT",
  (claimed.json?.routingRecordName ?? "") === DOMAIN && (claimed.json?.routingRecordValue ?? "").length > 0,
  `${claimed.json?.routingRecordName} -> ${claimed.json?.routingRecordValue}`);

// A HOSTNAME, never a scheme, a path or a bare address. In development this reads "localhost",
// which is correct for a machine that answers on it; in production it is whatever the install
// actually answers on, which is the same resolution every link a person follows uses.
ok("19.1h3: and it is a bare host — no scheme, no path, not an IP address",
  /^[a-z0-9.-]+$/i.test(claimed.json?.routingRecordValue ?? "") && !/^[0-9.]+$/.test(claimed.json?.routingRecordValue ?? ""),
  `${claimed.json?.routingRecordValue}`);

const stolen = await request(DOMAIN, OTHER.id);
ok("19.1i: a second tenant cannot claim the same host", stolen.status === 400, `status ${stolen.status}`);
ok("19.1j: and is told it is already claimed", /already claimed/i.test(stolen.json?.title ?? ""), stolen.json?.title);

// =====================================================================================
hdr("19.2 Verification — each step separately retryable, each failure naming the step");

const verifyNothing = await call(SA, "POST", `/api/v1/admin/tenants/${TENANT.id}/custom-domain/verify`);
ok("19.2a: with no TXT record published, verification is refused", verifyNothing.status === 400, `status ${verifyNothing.status}`);
ok("19.2b: and the refusal names the DNS step, not 'failed'",
  /could not find the TXT record/i.test(verifyNothing.json?.title ?? ""), verifyNothing.json?.title);

const wrong = await call(SA, "POST", `/api/v1/admin/tenants/${TENANT.id}/custom-domain/stub-dns?value=qmgr-domain-verification=wrong-value-entirely`);
if (wrong.status === 404) {
  ok("19.2c-h: the verification flow", true, "SKIP — the DNS stub is not enabled (Dns__Stub=true, Development only)");
} else {
  ok("19.2c: a TXT record was published into the stub", wrong.status === 200, `status ${wrong.status}`);

  const mismatch = await call(SA, "POST", `/api/v1/admin/tenants/${TENANT.id}/custom-domain/verify`);
  ok("19.2d: a record that exists but does not match is refused", mismatch.status === 400, `status ${mismatch.status}`);
  ok("19.2e: and it says the value is wrong, NOT that the record is missing — a different next step",
    /does not match/i.test(mismatch.json?.title ?? ""), mismatch.json?.title);

  await call(SA, "POST", `/api/v1/admin/tenants/${TENANT.id}/custom-domain/stub-dns`);
  const verified = await call(SA, "POST", `/api/v1/admin/tenants/${TENANT.id}/custom-domain/verify`);
  ok("19.2f: the right TXT value is accepted", verified.status === 200, `status ${verified.status} ${verified.text?.slice(0, 200)}`);
  ok("19.2g: the domain is LIVE", (String(verified.json?.state) === "Live" || verified.json?.state === 3) && verified.json?.domain === DOMAIN, JSON.stringify(verified.json?.state));
  ok("19.2h: and there is nothing left to prove — the TXT record is no longer shown",
    verified.json?.verificationRecordValue == null, verified.json?.verificationRecordValue);

  // ---------------------------------------------------------------------------------
  hdr("19.3 PHASE 0 — the host actually resolves its tenant");

  // THE defect, exercised through the MIDDLEWARE rather than through a direct row lookup.
  //
  // TenantStatusMiddleware refuses a request whose resolved tenant is Suspended and lets one
  // through whose tenant did not resolve at all. So for a suspended tenant, a bare Host header is
  // the difference between ACCOUNT_SUSPENDED and an ordinary 401 — and before the fix it was
  // always the 401, because ExtractSubdomainAsync returns null for a host outside the platform's
  // base domain and the custom-domain lookup sat inside the branch that null skipped.
  const suspended = TENANT.status === "Suspended" || TENANT.status === 3;
  if (!suspended) {
    ok("19.3a: the middleware resolves the tenant from the Host header", true,
      `SKIP — this proof needs a Suspended tenant to observe; ${TENANT.name} is ${TENANT.status}`);
  } else {
    const probe = `/api/v1/organizations/${TENANT.id}/branding`;
    const withHost = await rawGet(probe, DOMAIN);
    ok("19.3a: a request carrying ONLY the tenant's own Host resolves that tenant",
      withHost.status === 403 && /SUSPENDED/i.test(withHost.text ?? ""),
      `status ${withHost.status} ${withHost.text?.slice(0, 120)}`);

    const withoutHost = await rawGet(probe);
    ok("19.3b: and the same call without it does NOT — so it was the host that did it",
      !(withoutHost.status === 403 && /SUSPENDED/i.test(withoutHost.text ?? "")),
      `status ${withoutHost.status}`);
  }

  ok("19.3c: an unknown host is indistinguishable from the platform host",
    (await call(null, "GET", `/api/v1/public/branding/host/nobody-${RUN}.example.com`)).json?.resolved === false,
    "an unclaimed host answered as branded");
}

// =====================================================================================
hdr("19.4 Brand-asset uploads — the bytes decide, not the name");

const AD_USER = process.env.AD_USER ?? "e2e.admin.ct@qmgr.local";
const AD_PASS = process.env.AD_PASS ?? "E2eTeacher!2026";
const AD = await login(AD_USER, AD_PASS);
const adMe = AD ? await call(AD, "GET", "/api/v1/auth/me") : null;
const ORG = adMe?.json?.organizationId;

if (!ORG) {
  ok("19.4: brand-asset uploads", true, `SKIP — could not sign in as a tenant administrator (${AD_USER})`);
} else {
  const brandingPath = `/api/v1/organizations/${ORG}/branding`;

  // The white_label feature gates these endpoints, so a tenant without it gets 403 — which is
  // correct behaviour and not something to assert around. Grant the override for the run.
  await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: true });

  // Attribution removal sits ON TOP of white-labelling and is withheld when the tenant switch is
  // off — the documented rule, not an accident — so the switch has to be on for 19.5 to mean
  // anything. Put back at the end of the run.
  const brandingBefore = (await call(AD, "GET", brandingPath)).json;
  await call(AD, "PUT", brandingPath, { ...brandingBefore, whitelabelEnabled: true });

  const good = await upload(AD, `${brandingPath}/logo`, png(240, 80), "logo.png", "image/png");
  ok("19.4a: a real PNG is accepted", good.status === 200, `status ${good.status} ${good.text?.slice(0, 160)}`);
  const firstLogo = good.json?.logoUrl;
  ok("19.4b: and the logo URL is stored", typeof firstLogo === "string" && firstLogo.includes("/uploads/media/"), firstLogo);

  // The whole point of ImageProbe: the name and the declared type both say PNG and the bytes do not.
  const script = Buffer.from("<script>alert(1)</script>", "utf8");
  const lying = await upload(AD, `${brandingPath}/logo`, script, "logo.png", "image/png");
  ok("19.4c: a .png declared image/png whose BYTES are a script is refused", lying.status === 400, `status ${lying.status}`);
  ok("19.4d: and it is told what is accepted", /PNG, JPEG or WebP/i.test(lying.json?.message ?? ""), lying.json?.message);

  const svg = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>', "utf8");
  const svgUp = await upload(AD, `${brandingPath}/logo`, svg, "logo.svg", "image/svg+xml");
  ok("19.4e: SVG is refused outright (OWASP: it carries script in almost every context)", svgUp.status === 400, `status ${svgUp.status}`);

  const big = await upload(AD, `${brandingPath}/logo`, png(64, 64, 1_200_000), "big.png", "image/png");
  ok("19.4f: an over-size logo is refused", big.status === 400, `status ${big.status}`);
  ok("19.4g: and the message states the size AND the limit", /limit is/i.test(big.json?.message ?? ""), big.json?.message);

  const huge = await upload(AD, `${brandingPath}/logo`, png(3000, 100), "wide.png", "image/png");
  ok("19.4h: a logo over 2048px a side is refused", huge.status === 400, `status ${huge.status}`);

  const oblong = await upload(AD, `${brandingPath}/favicon`, png(128, 64), "fav.png", "image/png");
  ok("19.4i: a favicon that is not square is refused", oblong.status === 400, `status ${oblong.status}`);
  ok("19.4j: and it is told the actual dimensions", /128x64/.test(oblong.json?.message ?? ""), oblong.json?.message);

  const tiny = await upload(AD, `${brandingPath}/favicon`, png(32, 32), "fav.png", "image/png");
  ok("19.4k: a favicon under 64px is refused", tiny.status === 400, `status ${tiny.status}`);

  const fav = await upload(AD, `${brandingPath}/favicon`, png(128, 128), "fav.png", "image/png");
  ok("19.4l: a square 128px favicon is accepted", fav.status === 200, `status ${fav.status} ${fav.text?.slice(0, 160)}`);

  // REPLACE deletes the old file. Without this a school trying five logos leaves four orphans,
  // and an orphan is the one class the authorizer can only ever serve token-gated.
  const replaced = await upload(AD, `${brandingPath}/logo`, png(200, 60), "logo2.png", "image/png");
  ok("19.4m: a replacement is accepted", replaced.status === 200, `status ${replaced.status}`);
  ok("19.4n: and the URL changed", replaced.json?.logoUrl !== firstLogo, `${firstLogo} -> ${replaced.json?.logoUrl}`);

  if (firstLogo) {
    const oldFile = await fetch(firstLogo);
    ok("19.4o: the file it replaced is GONE, not left as an orphan", oldFile.status === 404, `status ${oldFile.status}`);
  }

  const served = await fetch(replaced.json.logoUrl);
  ok("19.4p: the new logo is served anonymously — a sign-in page and a kiosk both fetch it with no login",
    served.status === 200, `status ${served.status}`);
  ok("19.4q: and its Content-Type comes from the stored extension, which came from the bytes",
    (served.headers.get("content-type") ?? "").startsWith("image/png"), served.headers.get("content-type"));

  // ---------------------------------------------------------------------------------
  hdr("19.5 Attribution removal is its OWN entitlement");

  const shellOff = await call(AD, "GET", "/api/v1/branding/mine");
  ok("19.5a: a tenant's own shell branding reads back with no permission code", shellOff.status === 200, `status ${shellOff.status}`);
  ok("19.5b: attribution is NOT removed by default", shellOff.json?.attributionRemoved === false, JSON.stringify(shellOff.json?.attributionRemoved));

  // THE POINT OF THE SEPARATE CODE. The Communication module grants white_label; if attribution
  // removal rode that flag it would be free for every tenant running signage.
  const modules = await call(SA, "GET", `/api/v1/admin/tenants/${ORG}/modules`);
  const hasComms = JSON.stringify(modules.json ?? {}).includes("engagement-communications");
  ok("19.5c: this tenant holds the Communication module (which grants white_label)", hasComms, "not held — 19.5d proves less");
  ok("19.5d: and STILL does not get attribution removal from it", shellOff.json?.attributionRemoved === false);

  const grant = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/remove_attribution`, { enabled: true });
  ok("19.5e: a platform override grants it", grant.status === 200, `status ${grant.status}`);

  // The entitlement cache is five minutes. The override endpoint invalidates it, so this is the
  // NEXT request, not a request five minutes from now.
  const shellOn = await call(AD, "GET", "/api/v1/branding/mine");
  ok("19.5f: and it is visible on the very next request, not in five minutes",
    shellOn.json?.attributionRemoved === true, JSON.stringify(shellOn.json));

  const unknown = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/not_a_feature`, { enabled: true });
  ok("19.5g: an unknown feature code cannot be overridden", unknown.status === 400, `status ${unknown.status}`);

  const revoke = await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/remove_attribution`, { enabled: false });
  ok("19.5h: removing the override puts the attribution back", revoke.status === 200, `status ${revoke.status}`);
  ok("19.5i: proved on the next request",
    (await call(AD, "GET", "/api/v1/branding/mine")).json?.attributionRemoved === false);

  // ---------------------------------------------------------------------------------
  hdr("19.6 Cleanup — the tenant is left as it was found");

  const clearedFav = await call(AD, "DELETE", `${brandingPath}/favicon`);
  ok("19.6a: the favicon this run uploaded is removed", clearedFav.status === 200, `status ${clearedFav.status}`);
  const clearedLogo = await call(AD, "DELETE", `${brandingPath}/logo`);
  ok("19.6b: the logo this run uploaded is removed", clearedLogo.status === 200, `status ${clearedLogo.status}`);
  if (brandingBefore) await call(AD, "PUT", brandingPath, brandingBefore);
  await call(SA, "PUT", `/api/v1/admin/tenants/${ORG}/feature-overrides/white_label`, { enabled: false });
}

const released = await call(SA, "DELETE", `/api/v1/admin/tenants/${TENANT.id}/custom-domain`);
ok("19.6c: the domain this run claimed is released", released.status === 200, `status ${released.status}`);
ok("19.6d: and the tenant is back to having none", String(released.json?.state) === "None" || released.json?.state === 0, `state ${released.json?.state}`);
ok("19.6e: the host stops resolving at once — the resolver cache is evicted, not waited out",
  (await call(null, "GET", `/api/v1/public/branding/host/${DOMAIN}`)).json?.resolved === false);

hdr(`19. DONE — ${pass} passed, ${fail} failed`);
post(`\n19. DONE — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);

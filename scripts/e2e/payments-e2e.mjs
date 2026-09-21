#!/usr/bin/env node
// =====================================================================================================
// e2e section 16 — payment collection through the sacc.ug gateway (2026-09-19).
//
//   API=http://127.0.0.1:5001 node scripts/e2e/payments-e2e.mjs
//
// It starts a local stand-in for the gateway (sacc-gateway-stub.mjs, the contract read from E:\CRM) and
// points the platform's gateway settings at it — which the API allows for a loopback address in
// Development only — then drives every path a real payment takes: the platform settings and their
// validation, the connection check, the webhook registration, a module purchase settled by a signed
// webhook, a failed, an unanswered and an underpaid one, the reconciliation job settling a payment
// whose webhook never came, paying an invoice by hand, the renewal number, the test prompt, and
// reconciliation. No money moves and no phone rings.
//
// It changes the dev tenant: a module it had to buy is removed again at the end, the renewal number is
// put back, and the gateway settings are switched back OFF. A stored API key cannot be read back (it is
// masked), so if the platform had a real key before, the suite says so rather than pretending it
// restored it.
// =====================================================================================================

import { startStub } from "./sacc-gateway-stub.mjs";

const API = process.env.API || "http://127.0.0.1:5001";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const STUB_PORT = Number(process.env.STUB_PORT || 5099);
const STUB_KEY = "stub-key-for-local-e2e-" + Date.now().toString(36);
const CALLBACK = `${API}/api/v1/payments/sacc/webhook`;
const MASK = "••••••••";
const PHONE = "0772 123 456";          // MTN, the payer
const PHONE_NORMAL = "256772123456";

let pass = 0, fail = 0, skip = 0;
const failures = [];
const ok = (name) => { pass++; console.log(`  \x1b[32mPASS\x1b[0m  ${name}`); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  console.log(`  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 500)}`);
};
const skipped = (name, why) => { skip++; console.log(`  \x1b[33mSKIP\x1b[0m  ${name} — ${why}`); };
const hdr = (t) => console.log(`\n\x1b[1m${t}\x1b[0m`);
const eq = (name, actual, expected) => (actual === expected ? ok(name) : bad(name, expected, actual));
const truthy = (name, cond, detail = "") => (cond ? ok(name) : bad(name, "true", `false ${detail}`));

async function call(token, method, path, body, headers = {}) {
  const h = { ...headers };
  if (token) h.Authorization = `Bearer ${token}`;
  let payload;
  if (body !== undefined) { h["Content-Type"] = h["Content-Type"] ?? "application/json"; payload = typeof body === "string" ? body : JSON.stringify(body); }
  const res = await fetch(API + path, { method, headers: h, body: payload });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json, text };
}
const get = (t, p) => call(t, "GET", p);
const post = (t, p, b) => call(t, "POST", p, b ?? {});
const put = (t, p, b) => call(t, "PUT", p, b ?? {});
const del = (t, p) => call(t, "DELETE", p);
async function login(id, passwords) {
  for (const pw of passwords) {
    const r = await call(null, "POST", "/api/v1/auth/login", { email: id, password: pw });
    if (r.json?.accessToken) return r.json.accessToken;
  }
  return null;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const waitFor = async (fn, ms = 20000) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(500); } return v; };
const trigger = async (id) => (await fetch(`${API}/hangfire/recurring/trigger`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: `jobs%5B%5D=${encodeURIComponent(id)}` })).status;

const stub = await startStub({ port: STUB_PORT, apiKey: STUB_KEY });
const stubCall = async (path, body) => (await fetch(stub.url + path, { method: body ? "POST" : "GET", headers: { "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined })).json();
const settle = (referenceId, state, extra = {}) => stubCall("/__stub/settle", { referenceId, state, ...extra });

const cleanup = { removeModule: null, renewal: undefined, gatewayWasOn: null, gatewayHadKey: false, gatewayUrl: null };
let SA, AD;

try {
  // -------------------------------------------------------------------------------------------------
  hdr("16.0 Sign in, and the gateway as it stands");
  SA = await login(SA_USER, [SA_PASS]);
  AD = await login(TENANT_ADMIN, [PW, NEW_PW]);
  truthy("SuperAdmin signs in", !!SA);
  truthy("tenant admin signs in (section 13 creates the account on a fresh tenant)", !!AD);
  if (!SA || !AD) throw new Error("sign-in failed");

  const before = (await get(SA, "/api/v1/platform/payments/gateway")).json;
  truthy("GET platform/payments/gateway answers", !!before, JSON.stringify(before));
  cleanup.gatewayWasOn = before?.enabled ?? false;
  cleanup.gatewayHadKey = before?.apiKey === MASK;
  cleanup.gatewayUrl = before?.baseUrl ?? null;
  eq("the stored API key is never returned — only the mask or nothing", [MASK, ""].includes(before?.apiKey ?? ""), true);

  // -------------------------------------------------------------------------------------------------
  hdr("16.1 Stripe stays off");
  const providers0 = (await get(AD, "/api/v1/billing/payment-providers")).json;
  eq("payment-providers: stripeEnabled is false", providers0?.stripeEnabled, false);
  truthy("payment-providers: the card provider is never Stripe", providers0?.cardProvider !== "stripe", JSON.stringify(providers0));

  // -------------------------------------------------------------------------------------------------
  hdr("16.2 Gateway settings refuse what would fail a payment later");
  const refuse = async (name, body, field) => {
    const r = await put(SA, "/api/v1/platform/payments/gateway", body);
    truthy(`${name} → 400 naming ${field}`, r.status === 400 && !!r.json?.errors?.[field], `${r.status} ${r.text}`);
  };
  // An empty address is the platform default, the SACC gateway (asserted with the gateway OFF, so it never
  // points at the real sacc.ug with the stub key).
  const defaulted = await put(SA, "/api/v1/platform/payments/gateway", { enabled: false, baseUrl: "", apiKey: "", cardsEnabled: false });
  truthy("an empty gateway address saves as the platform default https://sacc.ug", defaulted.status === 200 && defaulted.json?.baseUrl === "https://sacc.ug", `${defaulted.status} ${defaulted.text}`);
  truthy("…and the callback address is worked out, ending in the webhook route", (defaulted.json?.callbackUrl ?? "").endsWith("/api/v1/payments/sacc/webhook") && (defaulted.json?.callbackUrl ?? ""), defaulted.json?.callbackUrl);
  await refuse("plain http to a public host", { enabled: true, baseUrl: "http://sacc.ug", apiKey: STUB_KEY, cardsEnabled: false }, "baseUrl");
  await refuse("an API path instead of the site", { enabled: true, baseUrl: "https://sacc.ug/api/epay", apiKey: STUB_KEY, cardsEnabled: false }, "baseUrl");
  await refuse("a query string", { enabled: true, baseUrl: "https://sacc.ug?x=1", apiKey: STUB_KEY, cardsEnabled: false }, "baseUrl");
  await refuse("a key that is too short", { enabled: true, baseUrl: stub.url, apiKey: "short", cardsEnabled: false }, "apiKey");
  await refuse("a key with a space in it", { enabled: true, baseUrl: stub.url, apiKey: "abcdefgh ijklmnopqrst", cardsEnabled: false }, "apiKey");
  await refuse("switching on with no key", { enabled: true, baseUrl: stub.url, apiKey: "", cardsEnabled: false }, "apiKey");
  await refuse("cards without the gateway", { enabled: false, baseUrl: stub.url, apiKey: STUB_KEY, cardsEnabled: true }, "cardsEnabled");
  eq("a tenant admin cannot read the gateway settings", (await get(AD, "/api/v1/platform/payments/gateway")).status, 403);
  eq("a tenant admin cannot change them", (await put(AD, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url, apiKey: STUB_KEY, cardsEnabled: false })).status, 403);
  const generic = await put(SA, "/api/v1/platform/settings/MobileMoney", { settingsJson: "{}" });
  truthy("the generic settings editor refuses the gateway (it would drop the webhook secret)", generic.status === 400 && /Payments page/i.test(generic.text), `${generic.status} ${generic.text}`);

  // -------------------------------------------------------------------------------------------------
  hdr("16.3 Point the gateway at the stub");
  const saved = await put(SA, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url + "/", apiKey: STUB_KEY, cardsEnabled: false });
  eq("save → 200", saved.status, 200);
  eq("the trailing slash is trimmed", saved.json?.baseUrl, stub.url);
  eq("the key comes back masked", saved.json?.apiKey, MASK);
  eq("Mobile Money is available", saved.json?.mobileMoneyAvailable, true);
  eq("cards are not", saved.json?.cardsAvailable, false);
  const keep = await put(SA, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url, apiKey: MASK, cardsEnabled: false });
  eq("sending the mask back keeps the stored key", keep.json?.mobileMoneyAvailable, true);
  const redacted = (await get(SA, "/api/v1/platform/settings/MobileMoney")).json;
  truthy("the generic settings read masks the key too", !JSON.stringify(redacted ?? {}).includes(STUB_KEY), JSON.stringify(redacted).slice(0, 300));

  // -------------------------------------------------------------------------------------------------
  hdr("16.4 Check connection");
  const check = (await post(SA, "/api/v1/platform/payments/gateway/check")).json;
  eq("the stub accepts the key", check?.authorised, true);
  await put(SA, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url, apiKey: "a-wrong-key-that-is-long-enough", cardsEnabled: false });
  const wrong = (await post(SA, "/api/v1/platform/payments/gateway/check")).json;
  truthy("a wrong key is reported as not authorised, with the status", wrong?.authorised === false && wrong?.statusCode === 401, JSON.stringify(wrong));
  await put(SA, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url, apiKey: STUB_KEY, cardsEnabled: false });

  // -------------------------------------------------------------------------------------------------
  hdr("16.5 Register the webhook");
  const badCallback = await post(SA, "/api/v1/platform/payments/gateway/webhook", { callbackUrl: "https://example.com/elsewhere" });
  truthy("a callback that is not the webhook route is refused", badCallback.status === 400 && !!badCallback.json?.errors?.callbackUrl, `${badCallback.status} ${badCallback.text}`);
  const reg = (await post(SA, "/api/v1/platform/payments/gateway/webhook", { callbackUrl: CALLBACK })).json;
  eq("registered", reg?.registered, true);
  eq("the stub holds one subscription", stub.hookCount(), 1);
  const reg2 = (await post(SA, "/api/v1/platform/payments/gateway/webhook", { callbackUrl: CALLBACK })).json;
  truthy("registering again replaces it — still one subscription, a new id", reg2?.registered && stub.hookCount() === 1 && reg2.webhookId !== reg.webhookId, JSON.stringify(reg2));
  const afterReg = (await get(SA, "/api/v1/platform/payments/gateway")).json;
  eq("the signing secret is stored and masked", afterReg?.webhookSecret, MASK);
  eq("the webhook id is stored", afterReg?.webhookId, reg2?.webhookId);
  const hook = [...stub.state.hooks.values()][0];
  truthy("the subscription covers every final event and review", ["payment.successful", "payment.failed", "payment.expired", "payment.review"].every((e) => hook.events.includes(e)), hook.events.join(","));

  // -------------------------------------------------------------------------------------------------
  hdr("16.6 What a tenant is offered");
  const providers = (await get(AD, "/api/v1/billing/payment-providers")).json;
  eq("Mobile Money is on", providers?.mobileMoneyEnabled, true);
  eq("cards are off (not switched on)", providers?.cardEnabled, false);

  // -------------------------------------------------------------------------------------------------
  hdr("16.7 A purchase refuses bad input before anything is written");
  const catalog = (await get(null, "/api/v1/modules")).json ?? [];
  const mine = async () => (await get(AD, "/api/v1/modules/mine")).json ?? [];
  const status0 = await mine();
  const trialing = status0.find((m) => m.status === "Trialing");
  const notOwned = catalog.filter((c) => !status0.some((m) => m.moduleCode === c.code && m.purchased));
  const target = trialing ? catalog.find((c) => c.code === trialing.moduleCode) : notOwned[0];
  const secondTarget = notOwned.find((c) => c.code !== target?.code);
  if (!target) throw new Error("the tenant owns every module and none is on trial — nothing to buy");
  if (!trialing) cleanup.removeModule = target.code;
  console.log(`        buying ${target.code}${trialing ? " (on trial)" : ""}; second module: ${secondTarget?.code ?? "none"}`);

  const purchase = (code, body) => post(AD, `/api/v1/modules/${code}/purchase`, { billingCycle: "Monthly", method: "mobile", phoneNumber: PHONE, ...body });
  const invoicesBefore = ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? []).length;
  const collectsBefore = stub.state.collects.size;
  const refusal = async (name, r, status, code) => truthy(`${name} → ${status} ${code}`, r.status === status && r.json?.error === code, `${r.status} ${r.text}`);
  await refusal("a number that is not a Ugandan mobile", await purchase(target.code, { phoneNumber: "0712 12" }), 400, "INVALID_PHONE");
  await refusal("a number on no mobile-money network (073…)", await purchase(target.code, { phoneNumber: "0733 123 456" }), 400, "INVALID_PHONE");
  await refusal("an unknown method", await purchase(target.code, { method: "cash" }), 400, "INVALID_METHOD");
  await refusal("a card while cards are off", await purchase(target.code, { method: "card", payerEmail: "bursar@example.com" }), 400, "UNAVAILABLE");
  await refusal("a billing cycle that does not exist", await purchase(target.code, { billingCycle: "Weekly" }), 400, "INVALID_BILLING_CYCLE");
  eq("an unknown module → 404", (await purchase("no-such-module")).status, 404);
  eq("nothing reached the gateway", stub.state.collects.size, collectsBefore);

  eq("no invoice was written", ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? []).length, invoicesBefore);
  // The CRM refuses a network it has no provider for with state "Pending" and isFinal false — found
  // 2026-09-19 when an Airtel number got "approve the prompt on your phone" and then silence.
  await stubCall("/__stub/refuse", { networks: ["Airtel"] });
  const airtel = await purchase(target.code, { phoneNumber: "0753 404 044" });
  truthy("an Airtel number the gateway cannot route is refused at once, not left waiting", airtel.status === 400 && airtel.json?.error === "PAYMENT_NOT_STARTED", `${airtel.status} ${airtel.text}`);
  truthy("…telling the payer to use MTN", /Airtel Money is not available right now\. Please pay with an MTN number\./.test(airtel.json?.message ?? ""), airtel.json?.message);
  const airtelInvoice = ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? [])[0];
  eq("…and the invoice it opened is voided, so nothing is owed", airtelInvoice?.status, "Void");
  // The CRM after its own fix answers the same refusal as failed and final, over 200.
  await stubCall("/__stub/refuse", { networks: ["Airtel"], final: true });
  const airtelFixed = await purchase(target.code, { phoneNumber: "0753 404 044" });
  truthy("…and the fixed CRM's final refusal reads the same to the payer", airtelFixed.status === 400 && /Please pay with an MTN number/.test(airtelFixed.json?.message ?? ""), `${airtelFixed.status} ${airtelFixed.text}`);
  await stubCall("/__stub/refuse", { networks: [] });

  // -------------------------------------------------------------------------------------------------
  hdr("16.8 A Mobile Money purchase: ledger first, then the gateway");
  const started = await purchase(target.code);
  eq("purchase → 200", started.status, 200);
  eq("the payment is Pending", started.json?.state, "Pending");
  const ref = started.json?.referenceId;
  const collect = stub.collect(ref);
  truthy("the gateway received it under Q-Mgr's reference", !!collect, ref);
  eq("…with the reference as the idempotency key", collect?.idempotencyKey?.toLowerCase(), String(ref).toLowerCase());
  eq("…to the normalized number", collect?.phoneNumber, PHONE_NORMAL);
  eq("…as service Other (the gateway provisions nothing)", collect?.service, "Other");
  eq("…by Mobile Money", collect?.method, "mobile");
  const invoices1 = (await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? [];
  const invoice = invoices1.find((i) => i.total === collect?.amount && i.status === "Open");
  truthy("an Open UGX invoice for the same amount exists before any money moved", !!invoice && invoice.currency === "UGX", JSON.stringify(invoices1.slice(0, 3)));
  const again = await purchase(target.code);
  eq("a second click returns the SAME payment, not a second prompt", again.json?.referenceId, ref);
  eq("the gateway still has one collect for it", stub.state.collects.size, collectsBefore + 1);
  eq("the module is not active yet", (await mine()).find((m) => m.moduleCode === target.code)?.status === "Active", false);
  const pending = (await get(AD, `/api/v1/modules/purchase-status/${ref}`)).json;
  eq("purchase-status reads Pending", pending?.state, "Pending");
  eq("another organization's reference answers 404", (await get(AD, `/api/v1/modules/purchase-status/${crypto.randomUUID()}`)).status, 404);

  // -------------------------------------------------------------------------------------------------
  hdr("16.9 Paying an invoice that already has a prompt out returns that prompt");
  if (invoice) {
    const payAgain = await post(AD, `/api/v1/billing/invoices/${invoice.id}/pay`, { phoneNumber: PHONE });
    eq("invoices/{id}/pay → the open payment, not a new one", payAgain.json?.referenceId, ref);
    eq("still one collect", stub.state.collects.size, collectsBefore + 1);
  }

  // -------------------------------------------------------------------------------------------------
  hdr("16.10 The signed webhook settles it — no browser involved");
  const settled = await settle(ref, "Succeeded");
  eq("the stub delivered one webhook", settled.deliveries?.length, 1);
  eq("Q-Mgr answered 200", settled.deliveries?.[0]?.status, 200);
  const active = await waitFor(async () => (await mine()).find((m) => m.moduleCode === target.code && m.status === "Active"), 15000);
  truthy("the module is Active (read from the DB, no status poll)", !!active);
  const paidInvoice = ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? []).find((i) => i.id === invoice?.id);
  eq("the invoice is Paid", paidInvoice?.status, "Paid");
  const paidStatus = (await get(AD, `/api/v1/billing/payments/${ref}`)).json;
  eq("billing/payments/{ref} reads Succeeded", paidStatus?.state, "Succeeded");
  if (invoice) {
    const payPaid = await post(AD, `/api/v1/billing/invoices/${invoice.id}/pay`, { phoneNumber: PHONE });
    truthy("paying a paid invoice → 409 ALREADY_PAID", payPaid.status === 409 && payPaid.json?.error === "ALREADY_PAID", `${payPaid.status} ${payPaid.text}`);
  }
  // Final is final: a late failure cannot undo it.
  await settle(ref, "Failed");
  await sleep(500);
  eq("a late 'failed' webhook does not undo a success", (await get(AD, `/api/v1/billing/payments/${ref}`)).json?.state, "Succeeded");

  // -------------------------------------------------------------------------------------------------
  hdr("16.11 The webhook endpoint trusts nothing it cannot verify");
  const body = JSON.stringify({ id: "whd_forged", event: "payment.successful", created_at: new Date().toISOString(), data: { reference_id: ref } });
  const wh = (headers, b = body) => call(null, "POST", "/api/v1/payments/sacc/webhook", b, { "Content-Type": "application/json", ...headers });
  eq("no signature → 401", (await wh({})).status, 401);
  eq("a signature made with the wrong secret → 401", (await wh({ "X-Webhook-Signature": (await import("./sacc-gateway-stub.mjs")).sign("whsec_wrong", body).header })).status, 401);
  const secret = stub.hookSecret();
  const { sign } = await import("./sacc-gateway-stub.mjs");
  eq("a signature older than five minutes → 401", (await wh({ "X-Webhook-Signature": sign(secret, body, Math.floor(Date.now() / 1000) - 600).header })).status, 401);
  eq("a body changed after signing → 401", (await wh({ "X-Webhook-Signature": sign(secret, body).header }, body.replace("successful", "failed"))).status, 401);
  const unknown = JSON.stringify({ id: "whd_unknown", event: "payment.successful", created_at: new Date().toISOString(), data: { reference_id: crypto.randomUUID() } });
  eq("a valid signature for a reference Q-Mgr never issued → 200, and nothing happens", (await wh({ "X-Webhook-Signature": sign(secret, unknown).header }, unknown)).status, 200);
  const delivery = stub.state.deliveries.find((d) => d.referenceId === ref && d.event === "payment.successful");
  if (!delivery) {
    bad("a redelivered webhook (same X-Webhook-Id) is acknowledged as a duplicate", "a delivery from 16.10 to replay", "none");
  } else {
    const replay = await stubCall("/__stub/redeliver", { deliveryId: delivery.id });
    truthy("a redelivered webhook (same X-Webhook-Id) is acknowledged as a duplicate", replay.status === 200 && /duplicate/.test(replay.response), JSON.stringify(replay));
  }

  // -------------------------------------------------------------------------------------------------
  hdr("16.12 Failed, unanswered and underpaid purchases");
  if (!secondTarget) {
    skipped("16.12", "the tenant owns every other module — no second module to buy");
  } else {
    cleanup.removeModule = cleanup.removeModule ?? null;
    const failed = (await purchase(secondTarget.code)).json;
    const s1 = await settle(failed.referenceId, "Failed");
    eq("a declined prompt: webhook answered 200", s1.deliveries?.[0]?.status, 200);
    const failedStatus = await waitFor(async () => { const s = (await get(AD, `/api/v1/modules/purchase-status/${failed.referenceId}`)).json; return s?.isFinal ? s : null; });
    eq("…the payment is Failed", failedStatus?.state, "Failed");
    const voided = ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? []).find((i) => i.total === stub.collect(failed.referenceId).amount && i.status === "Void");
    truthy("…its invoice is Void (a purchase that never completed owes nothing)", !!voided);
    eq("…and the module was not added", (await mine()).some((m) => m.moduleCode === secondTarget.code && m.purchased), false);

    const expired = (await purchase(secondTarget.code)).json;
    truthy("a new attempt after a failure is a new payment", !!expired.referenceId && expired.referenceId !== failed.referenceId);
    await settle(expired.referenceId, "Abandoned");
    const expiredStatus = await waitFor(async () => { const s = (await get(AD, `/api/v1/billing/payments/${expired.referenceId}`)).json; return s?.isFinal ? s : null; });
    eq("an unanswered prompt ends Abandoned", expiredStatus?.state, "Abandoned");

    const under = (await purchase(secondTarget.code)).json;
    const amount = stub.collect(under.referenceId).amount;
    await settle(under.referenceId, "Succeeded", { confirmedAmount: amount - 1000 });
    const underStatus = await waitFor(async () => { const s = (await get(AD, `/api/v1/billing/payments/${under.referenceId}`)).json; return s?.state === "Review" ? s : null; });
    eq("less money than was due is held for Review, not provisioned", underStatus?.state, "Review");
    eq("…and the module is still not added", (await mine()).some((m) => m.moduleCode === secondTarget.code && m.status === "Active"), false);
    await settle(under.referenceId, "Succeeded", { confirmedAmount: amount });
    const resolved = await waitFor(async () => (await mine()).find((m) => m.moduleCode === secondTarget.code && m.status === "Active"), 15000);
    truthy("once the gateway confirms the full amount, the module activates", !!resolved);
    if (resolved) await del(AD, `/api/v1/modules/${secondTarget.code}`);
  }

  // -------------------------------------------------------------------------------------------------
  hdr("16.13 A lost webhook: the reconciliation job settles it");
  const jobTarget = secondTarget ?? null;
  if (!jobTarget) {
    skipped("16.13", "no second module to buy");
  } else {
    const lost = (await purchase(jobTarget.code)).json;
    await settle(lost.referenceId, "Succeeded", { webhook: false });
    const triggered = await trigger("reconcile-gateway-payments");
    if (triggered >= 400) {
      skipped("the reconciliation job", `the Hangfire dashboard answered ${triggered} (Development opens it to local callers only)`);
    } else {
      // Poll the DB-only read. purchase-status would ask the gateway itself and prove nothing here.
      let done = null;
      const end = Date.now() + 120_000;
      while (!done && Date.now() < end) {
        done = await waitFor(async () => (await mine()).find((m) => m.moduleCode === jobTarget.code && m.status === "Active"), 20000);
        if (!done) await trigger("reconcile-gateway-payments");
      }
      truthy("the job asked the gateway and activated the module, with no webhook and no browser", !!done);
    }
    await del(AD, `/api/v1/modules/${jobTarget.code}`);
  }

  // -------------------------------------------------------------------------------------------------
  hdr("16.13b A payment held for review is decided by a person");
  if (!secondTarget) {
    skipped("16.13b", "no second module to buy");
  } else {
    const heldBuy = (await purchase(secondTarget.code)).json;
    const heldAmount = stub.collect(heldBuy.referenceId).amount;
    await settle(heldBuy.referenceId, "Succeeded", { confirmedAmount: heldAmount - 500 });
    const held = await waitFor(async () => (await get(AD, `/api/v1/billing/payments/${heldBuy.referenceId}`)).json?.state === "Review");
    truthy("an underpayment is held for review", held);
    const rec1 = (await get(SA, "/api/v1/platform/payments/reconciliation")).json;
    truthy("…and listed under Needs a decision", (rec1?.awaitingReview ?? []).some(p => p.referenceId === heldBuy.referenceId), JSON.stringify(rec1?.awaitingReview?.slice(0, 2)));

    const resolve = (token, ref, body) => post(token, `/api/v1/platform/payments/${ref}/resolve`, body);
    const noOutcome = await resolve(SA, heldBuy.referenceId, { outcome: "", note: "Checked the gateway record" });
    truthy("deciding with no outcome is refused, naming the field", noOutcome.status === 400 && !!noOutcome.json?.errors?.outcome, `${noOutcome.status} ${noOutcome.text}`);
    const shortNote = await resolve(SA, heldBuy.referenceId, { outcome: "received", note: "ok" });
    truthy("…and so is a note under ten characters", shortNote.status === 400 && !!shortNote.json?.errors?.note, `${shortNote.status} ${shortNote.text}`);
    eq("a tenant admin cannot decide it", (await resolve(AD, heldBuy.referenceId, { outcome: "received", note: "Tenant trying to self-approve" })).status, 403);
    eq("an unknown payment → 404", (await resolve(SA, crypto.randomUUID(), { outcome: "received", note: "Checked the gateway record" })).status, 404);

    const decided = await resolve(SA, heldBuy.referenceId, { outcome: "received", note: "CRMPro shows the full amount on the receipt" });
    eq("marking it received → Succeeded", decided.json?.state, "Succeeded");
    const activeAfter = await waitFor(async () => (await mine()).find(m => m.moduleCode === secondTarget.code && m.status === "Active"), 15000);
    truthy("…the module is activated", !!activeAfter);
    const rec2 = (await get(SA, "/api/v1/platform/payments/reconciliation")).json;
    truthy("…it leaves Needs a decision", !(rec2?.awaitingReview ?? []).some(p => p.referenceId === heldBuy.referenceId));
    const row = (rec2?.recent ?? []).find(p => p.referenceId === heldBuy.referenceId);
    truthy("…and the payment records who decided it and why", /Marked received by .+: CRMPro shows the full amount/.test(row?.resolution ?? ""), row?.resolution);
    const again = await resolve(SA, heldBuy.referenceId, { outcome: "not-received", note: "Changing my mind afterwards" });
    truthy("a decided payment cannot be decided again", again.status === 409 && again.json?.error === "NOT_IN_REVIEW", `${again.status} ${again.text}`);
    await settle(heldBuy.referenceId, "Failed");
    await sleep(500);
    eq("a later gateway answer does not undo the decision", (await get(AD, `/api/v1/billing/payments/${heldBuy.referenceId}`)).json?.state, "Succeeded");
    await del(AD, `/api/v1/modules/${secondTarget.code}`);

    const heldBuy2 = (await purchase(secondTarget.code)).json;
    await settle(heldBuy2.referenceId, "Succeeded", { confirmedAmount: stub.collect(heldBuy2.referenceId).amount - 500 });
    await waitFor(async () => (await get(AD, `/api/v1/billing/payments/${heldBuy2.referenceId}`)).json?.state === "Review");
    const rejected = await resolve(SA, heldBuy2.referenceId, { outcome: "not-received", note: "No such receipt in CRMPro for this reference" });
    eq("marking it not received → Failed", rejected.json?.state, "Failed");
    const voidedInv = ((await get(AD, "/api/v1/billing/invoices?page=1&pageSize=1000")).json ?? [])[0];
    eq("…its purchase invoice is voided", voidedInv?.status, "Void");
    eq("…and the module was not added", (await mine()).some(m => m.moduleCode === secondTarget.code && m.status === "Active"), false);
  }

  // -------------------------------------------------------------------------------------------------
  hdr("16.14 The renewal number");
  const renewal0 = (await get(AD, "/api/v1/billing/renewal-number")).json;
  cleanup.renewal = renewal0?.phoneNumber ?? null;
  const badRenewal = await put(AD, "/api/v1/billing/renewal-number", { phoneNumber: "12345" });
  truthy("a number that is not Mobile Money is refused", badRenewal.status === 400 && badRenewal.json?.error === "INVALID_PHONE", `${badRenewal.status} ${badRenewal.text}`);
  const goodRenewal = (await put(AD, "/api/v1/billing/renewal-number", { phoneNumber: "+256 752 000 111" })).json;
  eq("a valid number is stored normalized", goodRenewal?.phoneNumber, "256752000111");
  eq("…and named by its network", goodRenewal?.operator, "Airtel");
  eq("reading it back gives the same number", (await get(AD, "/api/v1/billing/renewal-number")).json?.phoneNumber, "256752000111");

  // -------------------------------------------------------------------------------------------------
  hdr("16.15 The test prompt proves the whole path");
  const testBad = await post(SA, "/api/v1/platform/payments/gateway/test-prompt", { phoneNumber: "0733 123 456" });
  truthy("a number that cannot take Mobile Money is refused", testBad.status === 400 && !!testBad.json?.errors?.phoneNumber, `${testBad.status} ${testBad.text}`);
  const testStart = (await post(SA, "/api/v1/platform/payments/gateway/test-prompt", { phoneNumber: PHONE })).json;
  eq("the test prompt is Pending", testStart?.state, "Pending");
  eq("…for UGX 500", stub.collect(testStart?.referenceId)?.amount, 500);
  eq("a tenant admin cannot send one", (await post(AD, "/api/v1/platform/payments/gateway/test-prompt", { phoneNumber: PHONE })).status, 403);
  await settle(testStart.referenceId, "Succeeded");
  const testDone = await waitFor(async () => { const s = (await get(SA, `/api/v1/platform/payments/gateway/test-prompt/${testStart.referenceId}`)).json; return s?.isFinal && s?.webhookReceived ? s : null; });
  eq("the gateway confirmed it", testDone?.state, "Succeeded");
  eq("and its signed webhook reached this install", testDone?.webhookReceived, true);

  // -------------------------------------------------------------------------------------------------
  hdr("16.16 Reconciliation");
  const today = new Date().toISOString().slice(0, 10);
  const rec = (await get(SA, `/api/v1/platform/payments/reconciliation?from=${today}&to=${today}`)).json;
  truthy("today's figures count what this run sent", (rec?.sent ?? 0) >= (secondTarget ? 5 : 1), JSON.stringify(rec && { sent: rec.sent, confirmed: rec.confirmed }));
  truthy("…with a confirmed amount", (rec?.confirmedAmount ?? 0) > 0);
  if (secondTarget) {
    truthy("…and the failed and the unanswered ones", (rec?.failed ?? 0) >= 1 && (rec?.abandoned ?? 0) >= 1, JSON.stringify(rec && { failed: rec.failed, abandoned: rec.abandoned }));
  }
  truthy("the latest payments list names the tenant", (rec?.recent ?? []).some((p) => !!p.organizationName));
  truthy("a phone number in the list is never shown in full", !(rec?.recent ?? []).some((p) => p.phone === PHONE_NORMAL), JSON.stringify((rec?.recent ?? []).slice(0, 2)));
  eq("a range over a year is refused", (await get(SA, "/api/v1/platform/payments/reconciliation?from=2024-01-01&to=2026-01-01")).status, 400);
  eq("a tenant admin cannot read it", (await get(AD, "/api/v1/platform/payments/reconciliation")).status, 403);
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  // ---------------------------------------------------------------------------------------------------
  hdr("16.17 Put things back");
  try {
    if (AD && cleanup.removeModule) {
      const r = await del(AD, `/api/v1/modules/${cleanup.removeModule}`);
      truthy(`the module this run bought (${cleanup.removeModule}) is removed again`, r.status < 300, `${r.status} ${r.text}`);
    }
    if (AD && cleanup.renewal !== undefined) {
      if (cleanup.renewal) {
        eq("the renewal number is put back", (await put(AD, "/api/v1/billing/renewal-number", { phoneNumber: cleanup.renewal })).json?.phoneNumber, cleanup.renewal);
      } else {
        eq("the renewal number is removed again (there was none before)", (await del(AD, "/api/v1/billing/renewal-number")).status, 200);
      }
    }
    if (SA) {
      const off = await put(SA, "/api/v1/platform/payments/gateway", {
        enabled: false, baseUrl: cleanup.gatewayUrl ?? "https://sacc.ug", apiKey: MASK, cardsEnabled: false,
      });
      eq("the gateway is switched back off", off.json?.enabled, false);
      if (cleanup.gatewayHadKey) console.log("        note: the platform had an API key before this run; it now holds the stub's key. Re-enter the real one on /platform/payments.");
    }
  } catch (e) {
    bad("cleanup", "no exception", e?.stack ?? e);
  }
  await stub.close();
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ""}\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

#!/usr/bin/env node
// =====================================================================================================
// A local stand-in for the sacc.ug payment gateway (CRMPro's /api/epay and /api/webhooks), for the
// payments e2e (section 16). It implements the contract Q-Mgr's SaccGateway was written against — read
// from E:\CRM\CRMApi (EpayController, WebhooksController, WebhookDispatcher) on 2026-09-19 — and nothing
// else:
//
//   POST   /api/epay/collect            202 {referenceId, state, isFinal, statusPollUrl, pollAfterSeconds, redirectUrl?}
//   GET    /api/epay/status/{ref}       200 {referenceId, state, isFinal, confirmedAmount, requestedAmount, ...}
//                                       404 for a reference this key does not own
//   GET    /api/epay/validate/{phone}   200 {valid, network, normalized}
//   POST   /api/webhooks                201 {id, secret, webhook, message}
//   DELETE /api/webhooks/{id}           204
//
// Every call needs X-API-Key. A webhook is signed exactly as the gateway signs it:
//   X-Webhook-Signature: t={unix},v1={hex HMAC-SHA256(secret, "{t}.{body}")}
//
// No money moves and no phone rings. The suite decides each payment's fate through the stub's own
// control routes, which the real gateway does not have:
//
//   POST /__stub/settle     {referenceId, state, confirmedAmount?, webhook?=true}
//   POST /__stub/redeliver  {deliveryId}           the same delivery again, same X-Webhook-Id
//   GET  /__stub/state                              collects, hooks, deliveries
//   POST /__stub/refuse     {networks: ["Airtel"], final?: bool}
//                                                   answer those networks the way the CRM does when no
//                                                   provider is routed for them — final: false is the CRM
//                                                   before its 2026-09-19 fix, final: true after it
//
// Run alone:  STUB_PORT=5099 STUB_KEY=... node scripts/e2e/sacc-gateway-stub.mjs
// =====================================================================================================

import http from "node:http";
import crypto from "node:crypto";
import { pathToFileURL } from "node:url";

const EVENT_FOR = {
  Succeeded: "payment.successful",
  Failed: "payment.failed",
  Abandoned: "payment.expired",
  Review: "payment.review",
  Pending: "payment.pending",
};
const FINAL = new Set(["Succeeded", "Failed", "Abandoned"]);
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The gateway's own phone rule (CRM PhoneNumberHelper): 256 + a 9-digit mobile number. */
export function normalizePhone(value) {
  let digits = String(value ?? "").replace(/\D/g, "");
  if (digits.startsWith("256")) digits = digits.slice(3);
  if (digits.startsWith("0")) digits = digits.slice(1);
  return digits.length === 9 ? "256" + digits : null;
}
export function network(phone) {
  const n = normalizePhone(phone);
  if (!n) return null;
  const prefix = n.slice(3, 5);
  if (["77", "78", "31", "39", "76", "79"].includes(prefix)) return "MTN";
  if (["70", "75", "74", "20"].includes(prefix)) return "Airtel";
  if (prefix === "71") return "UTL";
  if (prefix === "72") return "Lycamobile";
  return null;
}

export function sign(secret, body, timestamp = Math.floor(Date.now() / 1000)) {
  const v1 = crypto.createHmac("sha256", secret).update(`${timestamp}.${body}`).digest("hex");
  return { header: `t=${timestamp},v1=${v1}`, timestamp };
}

export function startStub({ port = 5099, apiKey, host = "127.0.0.1", log = () => {} } = {}) {
  if (!apiKey) throw new Error("startStub needs an apiKey");
  const state = { collects: new Map(), hooks: new Map(), deliveries: [], requests: [], refuse: new Set(), refuseFinal: false };

  const send = (res, status, body) => {
    const text = body === undefined ? "" : JSON.stringify(body);
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(text);
  };
  const problem = (res, status, title, errors) => send(res, status, { title, status, errors });

  const view = (c) => ({
    referenceId: c.referenceId,
    state: c.state,
    isFinal: FINAL.has(c.state),
    confirmedAmount: c.confirmedAmount,
    requestedAmount: c.amount,
    currency: "UGX",
    provider: c.method === "card" ? "Pesapal" : c.network,
    channel: c.method === "card" ? "card" : "mobile",
    message: c.message ?? "",
    errorCode: c.errorCode ?? null,
  });

  async function deliver(collect, eventOverride) {
    const event = eventOverride ?? EVENT_FOR[collect.state];
    const results = [];
    for (const hook of state.hooks.values()) {
      if (!hook.events.includes(event)) continue;
      const id = `whd_${crypto.randomUUID().replace(/-/g, "").slice(0, 16)}`;
      const body = JSON.stringify({
        id,
        event,
        created_at: new Date().toISOString(),
        data: {
          reference_id: collect.referenceId,
          status: collect.state,
          amount: collect.confirmedAmount ?? collect.amount,
          currency: "UGX",
          phone_number: collect.phoneNumber,
          channel: collect.method === "card" ? "card" : "mobile",
          provider: collect.network,
          receipt_number: collect.state === "Succeeded" ? `RCPT${Date.now()}` : null,
          error_code: collect.errorCode ?? null,
          error_message: collect.message ?? null,
        },
      });
      const delivery = { id, hookId: hook.id, event, referenceId: collect.referenceId, body, url: hook.callbackUrl, status: null, attempts: 0 };
      state.deliveries.push(delivery);
      results.push(await post(delivery, hook.secret));
    }
    return results;
  }

  async function post(delivery, secret) {
    const { header, timestamp } = sign(secret, delivery.body);
    delivery.attempts++;
    try {
      const res = await fetch(delivery.url, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Webhook-Id": delivery.id,
          "X-Webhook-Event": delivery.event,
          "X-Webhook-Signature": header,
          "X-Webhook-Timestamp": String(timestamp),
          "X-Webhook-Attempt": String(delivery.attempts),
        },
        body: delivery.body,
      });
      delivery.status = res.status;
      delivery.response = await res.text();
    } catch (e) {
      delivery.status = 0;
      delivery.response = String(e);
    }
    log(`webhook ${delivery.event} ${delivery.referenceId} -> ${delivery.status}`);
    return delivery;
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    let raw = "";
    for await (const chunk of req) raw += chunk;
    let body = {};
    try { body = raw ? JSON.parse(raw) : {}; } catch { return problem(res, 400, "Body is not JSON"); }
    state.requests.push({ method: req.method, path: url.pathname, key: req.headers["x-api-key"] ?? null, body });

    // ---- the stub's own controls (no key) ---------------------------------------------------------
    if (url.pathname === "/__stub/state") {
      return send(res, 200, {
        collects: [...state.collects.values()],
        hooks: [...state.hooks.values()].map(({ secret, ...h }) => h),
        deliveries: state.deliveries.map(({ body, ...d }) => d),
        requests: state.requests.length,
      });
    }
    if (url.pathname === "/__stub/settle" && req.method === "POST") {
      const c = state.collects.get(String(body.referenceId).toLowerCase());
      if (!c) return problem(res, 404, "No such collect");
      c.state = body.state;
      c.confirmedAmount = body.state === "Succeeded" || body.state === "Review" ? (body.confirmedAmount ?? c.amount) : null;
      c.message = body.message ?? (body.state === "Failed" ? "Payer declined the prompt" : body.state === "Abandoned" ? "The prompt expired" : "");
      c.errorCode = body.state === "Failed" ? "PAYER_DECLINED" : null;
      const deliveries = body.webhook === false ? [] : await deliver(c);
      return send(res, 200, { collect: view(c), deliveries: deliveries.map(({ body, ...d }) => d) });
    }
    if (url.pathname === "/__stub/refuse" && req.method === "POST") {
      state.refuse = new Set(body.networks ?? []);
      state.refuseFinal = body.final === true;
      return send(res, 200, { refusing: [...state.refuse], final: state.refuseFinal });
    }
    if (url.pathname === "/__stub/redeliver" && req.method === "POST") {
      const d = state.deliveries.find((x) => x.id === body.deliveryId);
      if (!d) return problem(res, 404, "No such delivery");
      const hook = state.hooks.get(d.hookId);
      const again = { ...d };
      await post(again, hook.secret);
      return send(res, 200, { status: again.status, response: again.response });
    }

    // ---- the gateway's contract (key required) ----------------------------------------------------
    if (req.headers["x-api-key"] !== apiKey) return send(res, 401, { title: "Unauthorized", status: 401 });

    if (url.pathname === "/api/epay/collect" && req.method === "POST") {
      const errors = {};
      if (!UUID.test(String(body.referenceId ?? ""))) errors.referenceId = ["ReferenceId must be a UUID."];
      const amount = Number(body.amount);
      if (!(amount > 0) || amount > 50_000_000) errors.amount = ["Amount must be between 1 and 50,000,000."];
      if (body.narrative && String(body.narrative).length > 500) errors.narrative = ["Narrative is at most 500 characters."];
      if (body.externalId && String(body.externalId).length > 50) errors.externalId = ["ExternalId is at most 50 characters."];
      const method = body.method ?? "mobile";
      if (method === "mobile" && !network(body.phoneNumber)) errors.phoneNumber = ["A valid Ugandan mobile money number is required."];
      if (method === "card" && !/^\S+@\S+\.\S+$/.test(String(body.payerEmail ?? ""))) errors.payerEmail = ["PayerEmail is required for card payments."];
      if (Object.keys(errors).length) return problem(res, 400, "One or more validation errors occurred.", errors);

      // The CRM's answer when no provider is routed for the payer's network (read from CRMApi,
      // PaymentRequest.cs: the early return through PaymentResponse.Failed never runs ApplyState).
      // The reason and status say Failed; state and isFinal still say Pending; no referenceId; 202.
      // Reproduced verbatim here because it is what Q-Mgr has to cope with.
      if (method === "mobile" && state.refuse.has(network(body.phoneNumber)) && state.refuseFinal) {
        // After the CRM's fix: the same refusal, now failed and final, over 200.
        return send(res, 200, {
          success: false, status: "Failed", message: `Payments through ${String(network(body.phoneNumber)).toLowerCase()}-money are not configured.`,
          errorCode: "ServiceUnavailable", referenceId: null, state: "Failed", isFinal: true, pollAfterSeconds: 0,
        });
      }
      if (method === "mobile" && state.refuse.has(network(body.phoneNumber))) {
        return send(res, 202, {
          success: false, status: "Failed", message: `No payment provider is configured for ${network(body.phoneNumber)}.`,
          errorCode: "ServiceUnavailable", referenceId: null, state: "Pending", isFinal: false, pollAfterSeconds: 0,
        });
      }

      const key = String(body.referenceId).toLowerCase();
      const existing = state.collects.get(key);
      if (existing) {
        // Idempotent on the reference: the original payment, never a second prompt. A different
        // amount under the same reference is a conflict.
        if (existing.amount !== amount) return send(res, 409, { title: "Conflict", status: 409 });
        return send(res, 202, { ...view(existing), statusPollUrl: `/api/epay/status/${existing.referenceId}`, pollAfterSeconds: 5 });
      }

      const collect = {
        referenceId: body.referenceId,
        idempotencyKey: body.idempotencyKey ?? null,
        amount,
        narrative: body.narrative ?? null,
        externalId: body.externalId ?? null,
        service: body.service ?? null,
        method,
        phoneNumber: method === "mobile" ? normalizePhone(body.phoneNumber) : (body.phoneNumber ?? null),
        network: method === "mobile" ? network(body.phoneNumber) : null,
        payerEmail: body.payerEmail ?? null,
        payerName: body.payerName ?? null,
        returnUrl: body.returnUrl ?? null,
        state: "Pending",
        confirmedAmount: null,
        createdAt: new Date().toISOString(),
      };
      state.collects.set(key, collect);
      log(`collect ${collect.referenceId} ${method} UGX ${amount} ${collect.phoneNumber ?? collect.payerEmail}`);
      return send(res, 202, {
        ...view(collect),
        statusPollUrl: `/api/epay/status/${collect.referenceId}`,
        pollAfterSeconds: 5,
        redirectUrl: method === "card" ? `http://${host}:${port}/__stub/card/${collect.referenceId}` : undefined,
      });
    }

    const status = url.pathname.match(/^\/api\/epay\/status\/([^/]+)$/);
    if (status && req.method === "GET") {
      const c = state.collects.get(decodeURIComponent(status[1]).toLowerCase());
      return c ? send(res, 200, view(c)) : send(res, 404, { title: "Not Found", status: 404 });
    }

    const validate = url.pathname.match(/^\/api\/epay\/validate\/([^/]+)$/);
    if (validate && req.method === "GET") {
      const phone = decodeURIComponent(validate[1]);
      return send(res, 200, { valid: !!network(phone), network: network(phone), normalized: normalizePhone(phone) });
    }

    if (url.pathname === "/api/webhooks" && req.method === "POST") {
      const errors = {};
      if (!/^https?:\/\//.test(String(body.callbackUrl ?? ""))) errors.callbackUrl = ["CallbackUrl must be an absolute URL."];
      if (!Array.isArray(body.events) || body.events.length === 0) errors.events = ["At least one event is required."];
      if (Object.keys(errors).length) return problem(res, 400, "One or more validation errors occurred.", errors);
      const id = crypto.randomUUID();
      const secret = "whsec_" + crypto.randomBytes(24).toString("hex");
      const hook = { id, name: body.name, callbackUrl: body.callbackUrl, events: body.events, secret };
      state.hooks.set(id, hook);
      return send(res, 201, { id, secret, webhook: { id, name: hook.name, callbackUrl: hook.callbackUrl, events: hook.events }, message: "Store the secret now; it is not shown again." });
    }

    const hookDelete = url.pathname.match(/^\/api\/webhooks\/([^/]+)$/);
    if (hookDelete && req.method === "DELETE") {
      return state.hooks.delete(decodeURIComponent(hookDelete[1])) ? (res.writeHead(204), res.end()) : send(res, 404, { title: "Not Found", status: 404 });
    }

    return send(res, 404, { title: "Not Found", status: 404 });
  });

  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => resolve({
      url: `http://${host}:${port}`,
      state,
      /** Where the suite reads what the stub recorded. */
      collect: (referenceId) => state.collects.get(String(referenceId).toLowerCase()),
      hookCount: () => state.hooks.size,
      hookSecret: () => [...state.hooks.values()].at(-1)?.secret ?? null,
      close: () => new Promise((r) => server.close(() => r())),
    }));
  });
}

// Standalone.
if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const port = Number(process.env.STUB_PORT || 5099);
  const apiKey = process.env.STUB_KEY || "stub-key-for-local-e2e-only-0001";
  const stub = await startStub({ port, apiKey, log: (m) => console.log(m) });
  console.log(`sacc.ug gateway stub on ${stub.url} (key ${apiKey})`);
}

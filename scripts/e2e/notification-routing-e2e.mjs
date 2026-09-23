// SECTION 27 — EVERY NOTIFICATION NAMES ONE PERSON (2026-09-23).
//
// Reported from Maryhill: a teacher onboarded that morning opened the bell and read the school's
// failed payments — "The payment of UGX 120,000 for Communication did not go through." Payment and
// trial notices were written with no recipient. The readers took a recipient-less row as everybody's,
// and the live push sent it through SignalR's Clients.All — every signed-in browser on the platform,
// every tenant. One person reading it cleared it for the whole school.
//
// This suite drives the real path: a Mobile Money purchase through the local gateway stand-in
// (sacc-gateway-stub.mjs — the same one section 16 uses), refused, so the ledger raises "Payment not
// completed". Then it asks who heard:
//
//   27.1  the billing holders do, each with their own copy;                        (the fix)
//   27.2  a teacher does not — not in the list, not in the count, not live;         (the leak)
//   27.3  a signed-in platform account in no tenant receives nothing live;          (the cross-tenant push)
//   27.4  one administrator reading theirs leaves the other's unread;               (the shared read flag)
//   27.5  nobody can read, mark or delete another person's copy by id;
//   27.6  five simultaneous settlements of one payment make ONE notice per person;  (the race)
//   27.7  the admin create endpoint requires a recipient;
//   27.8  the unread-count push carries the moment it was counted.
//
// A FAILED payment activates nothing and voids its own invoice. It BORROWS two of section 1's accounts
// rather than making new ones (the dev tenant sits over its user cap from years of runs): the s4
// teacher as the teacher, and the s2 teacher — moved for the run onto a throwaway role holding
// billing.view — as the second person entitled to the notice. Both are put back, the throwaway role is
// deleted, and the gateway is switched back off.
//
// Run: node scripts/e2e/notification-routing-e2e.mjs   (or through class-teacher-e2e.sh)
import { startStub } from "./sacc-gateway-stub.mjs";

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH || "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const SA_USER = process.env.SA_USER || "superadmin";
const SA_PASS = process.env.SA_PASS || "admin";
const TENANT_ADMIN = process.env.TENANT_ADMIN || "e2e.admin.ct@qmgr.local";
const PW = "E2eTeacher!2026";
const NEW_PW = "Rwenzori#Peaks-2026";
const STUB_PORT = Number(process.env.STUB_PORT || 5098);
const STUB_KEY = "stub-key-for-routing-e2e-" + Date.now().toString(36);
const CALLBACK = `${API}/api/v1/payments/sacc/webhook`;
const MASK = "••••••••";
const PHONE = "0772 123 456";
const RUN = Date.now().toString(36).slice(-6);

let pass = 0, fail = 0, skip = 0;
const failures = [];
const viewer = (line) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: line + "\n" }).catch(() => {});
const ok = (name) => { pass++; const l = `  \x1b[32mPASS\x1b[0m  ${name}`; console.log(l); viewer(l); };
const bad = (name, expected, actual) => {
  fail++; failures.push(name);
  const l = `  \x1b[31mFAIL\x1b[0m  ${name}\n        expected: ${expected}\n        actual:   ${String(actual).slice(0, 500)}`;
  console.log(l); viewer(l);
};
const skipped = (name, why) => { skip++; console.log(`  \x1b[33mSKIP\x1b[0m  ${name} — ${why}`); };
const hdr = (t) => { console.log(`\n\x1b[1m${t}\x1b[0m`); viewer(t); };
const eq = (name, actual, expected) => (actual === expected ? ok(name) : bad(name, expected, actual));
const truthy = (name, cond, detail = "") => (cond ? ok(name) : bad(name, "true", `false ${detail}`));

async function call(token, method, path, body) {
  const h = {};
  if (token) h.Authorization = `Bearer ${token}`;
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });
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
    if (r.json?.accessToken) return { token: r.json.accessToken, user: r.json.user };
  }
  return null;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const waitFor = async (fn, ms = 20000) => { const end = Date.now() + ms; let v; while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(400); } return v; };

// ---- a bare SignalR JSON-protocol client: enough to hear what the hub pushes to one account ----
async function listen(token, label) {
  const neg = await fetch(`${API}/hubs/notifications/negotiate?negotiateVersion=1`, { method: "POST", headers: { Authorization: `Bearer ${token}` } });
  if (!neg.ok) throw new Error(`${label}: negotiate ${neg.status}`);
  const { connectionToken, connectionId } = await neg.json();
  const url = `${API.replace(/^http/, "ws")}/hubs/notifications?id=${encodeURIComponent(connectionToken ?? connectionId)}&access_token=${encodeURIComponent(token)}`;
  const ws = new WebSocket(url);
  const heard = { notifications: [], counts: [] };
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  ws.send(JSON.stringify({ protocol: "json", version: 1 }) + "\x1e");
  ws.onmessage = (m) => {
    for (const frame of String(m.data).split("\x1e").filter(Boolean)) {
      let msg; try { msg = JSON.parse(frame); } catch { continue; }
      if (msg.type !== 1) continue;
      if (msg.target === "ReceiveNotification") heard.notifications.push(msg.arguments[0]);
      if (msg.target === "UnreadCountUpdated") heard.counts.push(msg.arguments);
    }
  };
  await sleep(500);
  return { heard, close: () => ws.close() };
}

const stub = await startStub({ port: STUB_PORT, apiKey: STUB_KEY });
const stubCall = async (path, body) => (await fetch(stub.url + path, { method: body ? "POST" : "GET", headers: { "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined })).json();

const cleanup = { restore: null, tempRoleId: null, gatewayUrl: null, gatewayHadKey: false };
const sockets = [];
let SA, AD;

try {
  // ------------------------------------------------------------------------------------------------
  hdr("27.0 Sign in, borrow a teacher and a second billing reader, point the gateway at the stub");
  const sa = await login(SA_USER, [SA_PASS]);
  const ad = await login(TENANT_ADMIN, [PW, NEW_PW]);
  if (!sa || !ad) throw new Error("sign-in failed");
  SA = sa.token; AD = ad.token;
  const adminId = ad.user.id;
  ok("SuperAdmin and the tenant administrator sign in");

  const t4 = await login("e2e.teacher.s4@qmgr.local", [PW, NEW_PW]);
  const t2 = await login("e2e.teacher.s2@qmgr.local", [PW, NEW_PW]);
  if (!t4 || !t2) throw new Error("section 1's teacher accounts are missing — run class-teacher-e2e.sh once first");
  const teacher = { id: t4.user.id, token: t4.token };
  truthy("the teacher holds no billing permission (else this proves nothing)", !(t4.user.permissions ?? []).some((p) => p.startsWith("billing.")), (t4.user.permissions ?? []).join(","));

  // A throwaway role: billing.view and the dashboard, nothing else. The s2 account holds it for the run.
  const perms = (await get(AD, "/api/v1/roles/permissions")).json ?? [];
  const pid = (code) => perms.flatMap((c) => c.permissions ?? []).find((p) => p.code === code)?.id;
  const role = await post(AD, "/api/v1/roles", { code: `e2e-nr-${RUN}`, name: `E2E Billing Reader ${RUN}`, permissionIds: [pid("dashboard.view"), pid("billing.view")].filter(Boolean) });
  truthy("a throwaway billing-reader role is made", role.status < 300 && !!role.json?.id, `${role.status} ${role.text}`);
  cleanup.tempRoleId = role.json?.id;
  const users = (await get(AD, "/api/v1/users?includeInactive=true")).json ?? [];
  const s2 = (Array.isArray(users) ? users : users.items ?? []).find((u) => u.id === t2.user.id);
  cleanup.restore = s2 && { id: s2.id, firstName: s2.firstName, lastName: s2.lastName, email: s2.email, roleId: s2.roleId, assignedBranchId: s2.assignedBranchId ?? BRANCH, isActive: true };
  const moved = await put(AD, `/api/v1/users/${t2.user.id}`, { ...cleanup.restore, roleId: role.json?.id });
  truthy("the s2 teacher is moved onto it for the run", moved.status < 300, `${moved.status} ${moved.text}`);
  const t2Again = await login("e2e.teacher.s2@qmgr.local", [PW, NEW_PW]);
  const admin2 = { id: t2.user.id, token: t2Again?.token };
  truthy("…and now reads billing", (t2Again?.user.permissions ?? []).includes("billing.view"), (t2Again?.user.permissions ?? []).join(","));
  if (!admin2.token) throw new Error("could not sign the borrowed account back in");

  const before = (await get(SA, "/api/v1/platform/payments/gateway")).json;
  cleanup.gatewayUrl = before?.baseUrl ?? null;
  cleanup.gatewayHadKey = before?.apiKey === MASK;
  const saved = await put(SA, "/api/v1/platform/payments/gateway", { enabled: true, baseUrl: stub.url, apiKey: STUB_KEY, cardsEnabled: false });
  eq("the gateway points at the stub", saved.status, 200);
  const reg = (await post(SA, "/api/v1/platform/payments/gateway/webhook", { callbackUrl: CALLBACK })).json;
  eq("the webhook is registered", reg?.registered, true);

  // Live listeners BEFORE anything is sent. The administrator's is the positive control: it proves
  // the listener hears a push at all, so the teacher's and the platform account's silence means something.
  const liveAdmin = await listen(AD, "admin"); sockets.push(liveAdmin);
  const liveTeacher = await listen(teacher.token, "teacher"); sockets.push(liveTeacher);
  const livePlatform = await listen(SA, "platform"); sockets.push(livePlatform);
  ok("three live listeners connect: the administrator, the teacher, and the platform account");

  const teacherCountBefore = (await get(teacher.token, "/api/v1/notifications/count")).json;

  // ------------------------------------------------------------------------------------------------
  hdr("27.1 A refused payment tells the people who can open Billing — each their own copy");
  const catalog = (await get(null, "/api/v1/modules")).json ?? [];
  const mine = (await get(AD, "/api/v1/modules/mine")).json ?? [];
  const target = catalog.find((c) => !mine.some((m) => m.moduleCode === c.code && (m.purchased || m.status === "Trialing")))
              ?? catalog.find((c) => mine.some((m) => m.moduleCode === c.code && m.status === "Trialing"));
  if (!target) throw new Error("no module the tenant does not already own — nothing to attempt to buy");
  // Server time, not this machine's: the notices are stamped by the API.
  const since = new Date(Date.parse((await fetch(`${API}/api/v1/health`)).headers.get("date")) - 1000);
  const started = await post(AD, `/api/v1/modules/${target.code}/purchase`, { billingCycle: "Monthly", method: "mobile", phoneNumber: PHONE });
  eq("a Mobile Money purchase starts (Pending)", started.json?.state, "Pending");
  const ref = started.json?.referenceId;

  // The stub marks it failed WITHOUT its webhook, and then five status reads race to settle it — the
  // webhook, a status read and the reconciliation job can all arrive together in production.
  await stubCall("/__stub/settle", { referenceId: ref, state: "Failed", webhook: false });
  const racers = await Promise.all(Array.from({ length: 5 }, () => get(AD, `/api/v1/billing/payments/${ref}`)));
  truthy("five simultaneous status reads all answer", racers.every((r) => r.status === 200), racers.map((r) => r.status).join(","));
  await stubCall("/__stub/settle", { referenceId: ref, state: "Failed" });   // and the webhook, late

  const findPaymentNotice = async (token) => ((await get(token, "/api/v1/notifications?eventKey=billing.payment&limit=200")).json ?? [])
    .filter((n) => n.title === "Payment not completed" && new Date(n.createdAt.endsWith("Z") ? n.createdAt : n.createdAt + "Z") >= since);
  const adminNotices = await waitFor(async () => { const l = await findPaymentNotice(AD); return l.length ? l : null; }, 15000) ?? [];
  const admin2Notices = await findPaymentNotice(admin2.token);
  truthy("the administrator is told the payment did not go through", adminNotices.length >= 1, JSON.stringify(adminNotices).slice(0, 200));
  truthy("the second administrator is told too — their own copy", admin2Notices.length >= 1);
  truthy("…and the two are different rows, each addressed to its reader",
    adminNotices[0] && admin2Notices[0] && adminNotices[0].id !== admin2Notices[0].id
      && adminNotices[0].userId === adminId && admin2Notices[0].userId === admin2.id,
    `${adminNotices[0]?.id}/${adminNotices[0]?.userId} ${admin2Notices[0]?.id}/${admin2Notices[0]?.userId}`);

  // ------------------------------------------------------------------------------------------------
  hdr("27.6 One payment, one notice per person — however many settlements raced");
  const forThisPayment = (list) => list.filter((n) => (n.message ?? "").includes(target.name ?? target.code));
  eq("the administrator holds exactly one notice for this payment", forThisPayment(adminNotices).length, 1);
  eq("so does the second administrator", forThisPayment(admin2Notices).length, 1);

  // ------------------------------------------------------------------------------------------------
  hdr("27.2 The teacher hears nothing about the school's money");
  const teacherAll = (await get(teacher.token, "/api/v1/notifications?limit=200")).json ?? [];
  eq("no billing notice in the teacher's list", teacherAll.filter((n) => n.eventKey === "billing.payment" || /^Payment /.test(n.title)).length, 0);
  truthy("every row the teacher is given is addressed to the teacher", teacherAll.every((n) => n.userId === teacher.id),
    JSON.stringify(teacherAll.filter((n) => n.userId !== teacher.id).slice(0, 2)));
  eq("the teacher's unread count did not move", (await get(teacher.token, "/api/v1/notifications/count")).json, teacherCountBefore);
  await sleep(1000);
  eq("nothing was pushed to the teacher live", liveTeacher.heard.notifications.length, 0);

  // ------------------------------------------------------------------------------------------------
  hdr("27.3 Nothing crosses a tenant");
  truthy("the administrator's listener heard their notice live (the control)", liveAdmin.heard.notifications.some((n) => n.title === "Payment not completed"),
    JSON.stringify(liveAdmin.heard.notifications.map((n) => n.title)));
  truthy("…addressed to them", liveAdmin.heard.notifications.every((n) => n.userId === adminId));
  eq("the platform account — in no tenant — heard nothing", livePlatform.heard.notifications.length, 0);

  // ------------------------------------------------------------------------------------------------
  hdr("27.4 Reading your copy does not read anybody else's");
  const mineRow = adminNotices[0], theirs = admin2Notices[0];
  eq("the administrator marks theirs read", (await post(AD, `/api/v1/notifications/${mineRow.id}/read`)).status, 204);
  const theirsAfter = (await findPaymentNotice(admin2.token)).find((n) => n.id === theirs.id);
  eq("the second administrator's copy is still unread", theirsAfter?.isRead, false);
  await post(AD, "/api/v1/notifications/read-all");
  eq("Mark all read on one account leaves the other's unread", (await findPaymentNotice(admin2.token)).find((n) => n.id === theirs.id)?.isRead, false);

  // ------------------------------------------------------------------------------------------------
  hdr("27.5 Nobody reaches another person's copy by its id");
  eq("marking a colleague's notification read → 404", (await post(AD, `/api/v1/notifications/${theirs.id}/read`)).status, 404);
  eq("deleting a colleague's notification → 404", (await del(AD, `/api/v1/notifications/${theirs.id}`)).status, 404);
  eq("a teacher marking the administrator's → 404", (await post(teacher.token, `/api/v1/notifications/${mineRow.id}/read`)).status, 404);
  eq("…and it is still there for its owner", (await findPaymentNotice(admin2.token)).some((n) => n.id === theirs.id), true);

  // ------------------------------------------------------------------------------------------------
  hdr("27.7 The admin create endpoint needs a recipient");
  const noOne = await post(AD, "/api/v1/notifications", { title: `Routing ${RUN}`, message: "To nobody." });
  eq("no userId → 400", noOne.status, 400);
  eq("an unknown userId → 404", (await post(AD, "/api/v1/notifications", { userId: crypto.randomUUID(), title: `Routing ${RUN}`, message: "x" })).status, 404);
  const toTeacher = await post(AD, "/api/v1/notifications", { userId: teacher.id, title: `Routing ${RUN}`, message: "Dummy — safe to ignore." });
  eq("addressed to the teacher → 201", toTeacher.status, 201);
  eq("…and it reaches exactly the teacher", (await get(teacher.token, "/api/v1/notifications?limit=50")).json?.some((n) => n.title === `Routing ${RUN}`), true);
  eq("…and not the administrator who sent it", (await get(AD, "/api/v1/notifications?limit=200")).json?.some((n) => n.title === `Routing ${RUN}`), false);

  // ------------------------------------------------------------------------------------------------
  hdr("27.8 The unread count travels with the moment it was counted");
  const counts = liveAdmin.heard.counts;
  truthy("the administrator received count pushes", counts.length > 0, JSON.stringify(counts));
  truthy("each carries a count and a counted-at stamp", counts.every((a) => a.length === 2 && Number.isInteger(a[0]) && a[1] > 6.3e17), JSON.stringify(counts.slice(0, 3)));
} catch (e) {
  bad("the suite ran to the end", "no exception", e?.stack ?? e);
} finally {
  hdr("27.9 Put things back");
  for (const s of sockets) try { s.close(); } catch { }
  try {
    if (cleanup.restore) {
      const r = await put(AD, `/api/v1/users/${cleanup.restore.id}`, cleanup.restore);
      truthy("the s2 teacher has their own role back", r.status < 300, `${r.status} ${r.text}`);
    }
    if (cleanup.tempRoleId) {
      const r = await del(AD, `/api/v1/roles/${cleanup.tempRoleId}`);
      truthy("the throwaway role is deleted", r.status < 300, `${r.status} ${r.text}`);
    }
    if (SA) {
      const off = await put(SA, "/api/v1/platform/payments/gateway", { enabled: false, baseUrl: cleanup.gatewayUrl ?? "https://sacc.ug", apiKey: MASK, cardsEnabled: false });
      eq("the gateway is switched back off", off.json?.enabled, false);
      if (cleanup.gatewayHadKey) console.log("        note: the platform had an API key before this run; it now holds the stub's key. Re-enter the real one on /platform/payments.");
    }
  } catch (e) { bad("cleanup", "no exception", e?.stack ?? e); }
  await stub.close();
  console.log(`\n\x1b[1m${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ""}\x1b[0m`);
  if (failures.length) console.log("Failed:\n  " + failures.join("\n  "));
  process.exitCode = fail ? 1 : 0;
}

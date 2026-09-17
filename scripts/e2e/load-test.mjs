#!/usr/bin/env node
// =====================================================================================================
// A small, honest load test: N virtual users for D seconds against a running API, each looping through
// the reads a signed-in member of staff makes all day (portal, notifications, own timeline) plus the
// administrator's heavy reads (directory, reports, activity log) and a queue ticket lookup. Reports
// requests per second, p50 / p95 / p99 / max latency per route and every non-2xx, and exits non-zero
// if the error rate or p95 crosses the thresholds.
//
// Node, not k6: no new tool on anyone's machine, and nothing on the server (the standing rule).
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> USERS=50 SECONDS=60 [THINK_MS=2000] node scripts/e2e/load-test.mjs
//
// Read-only on purpose: it can be pointed at a staging copy without leaving rows behind. It signs in
// the e2e accounts the staff-performance suite seeds (run that once first).
// =====================================================================================================

const API = process.env.API || "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH;
const USERS = Number(process.env.USERS || 50);
const SECONDS = Number(process.env.SECONDS || 60);
const MAX_ERROR_RATE = Number(process.env.MAX_ERROR_RATE || 0.01);
const MAX_P95_MS = Number(process.env.MAX_P95_MS || 1500);
// A person pauses between pages. Without a pause one virtual user makes ~300 requests a minute and the
// per-browser rate limit (100/min) correctly refuses it, which measures the limiter, not the server.
const THINK_MS = Number(process.env.THINK_MS ?? 2000);
const PW = "E2eTeacher!2026";
if (!BRANCH) { console.error("set BRANCH"); process.exit(2); }

const login = async (id) => {
  const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: id, password: PW }) });
  const j = await r.json().catch(() => ({}));
  return { token: j.accessToken, userId: j.user?.id ?? j.userId };
};

const teachers = ["e2e.sp.math1", "e2e.sp.math2", "e2e.sp.lang1", "e2e.sp.support", "e2e.sp.hod.math", "e2e.sp.hod.lang"];
const admins = ["e2e.admin.ct@qmgr.local", "e2e.sp.dos"];

const stats = new Map(); // route → { lat: number[], errors: Map<status, count> }
const record = (route, ms, status) => {
  let s = stats.get(route);
  if (!s) stats.set(route, (s = { lat: [], errors: new Map() }));
  s.lat.push(ms);
  if (status < 200 || status >= 300) s.errors.set(status, (s.errors.get(status) ?? 0) + 1);
};

// Each virtual user is a different browser, so it carries its own X-Real-IP — exactly what Q-Mgr.Web
// relays on every call since 2026-09-17. Without it the whole run is one address and measures nothing
// but the rate limiter (the first run: 97% 429s, which is how the shared-bucket bug was found).
async function hit(route, token, path, ip) {
  const t0 = performance.now();
  let status = 0;
  try {
    const r = await fetch(API + path, { headers: { Authorization: `Bearer ${token}`, "X-Real-IP": ip } });
    status = r.status;
    await r.arrayBuffer();
  } catch { status = 599; }
  record(route, performance.now() - t0, status);
}

const pct = (arr, p) => { if (!arr.length) return 0; const s = [...arr].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]; };

(async () => {
  console.log(`Signing in ${teachers.length + admins.length} accounts…`);
  const sessions = [];
  for (const id of teachers) sessions.push({ kind: "staff", id, ...(await login(id.includes("@") ? id : `${id}@qmgr.local`)) });
  for (const id of admins) sessions.push({ kind: "admin", id, ...(await login(id.includes("@") ? id : `${id}@qmgr.local`)) });
  const live = sessions.filter((s) => s.token);
  if (live.length < sessions.length) { console.error(`Only ${live.length}/${sessions.length} signed in — run staff-performance-e2e.mjs once to seed them.`); process.exit(2); }
  // The id for "my timeline" comes from the portal, which every session can read.
  for (const s of live) {
    const r = await fetch(`${API}/api/v1/staff/portal`, { headers: { Authorization: `Bearer ${s.token}` } });
    s.userId = (await r.json().catch(() => ({})))?.me?.userId ?? s.userId;
  }

  console.log(`${USERS} virtual users for ${SECONDS}s against ${API}, ~${THINK_MS}ms think time between page views`);
  const end = Date.now() + SECONDS * 1000;
  let n = 0;
  const vu = async (i) => {
    const s = live[i % live.length];
    const ip = `10.77.${Math.floor(i / 250)}.${(i % 250) + 1}`;
    while (Date.now() < end) {
      n++;
      await hit("GET portal", s.token, "/api/v1/staff/portal", ip);
      await hit("GET notifications", s.token, "/api/v1/notifications?limit=20", ip);
      if (s.userId) await hit("GET own timeline", s.token, `/api/v1/branches/${BRANCH}/staff/members/${s.userId}/timeline`, ip);
      if (s.kind === "admin") {
        await hit("GET directory", s.token, `/api/v1/branches/${BRANCH}/staff/structure/members`, ip);
        await hit("GET reports", s.token, `/api/v1/branches/${BRANCH}/staff/reports`, ip);
        await hit("GET activity", s.token, `/api/v1/branches/${BRANCH}/staff/activity?pageSize=50`, ip);
        await hit("GET records search", s.token, `/api/v1/branches/${BRANCH}/staff/records?pageSize=25`, ip);
      }
      if (THINK_MS > 0) await new Promise((r) => setTimeout(r, THINK_MS * (0.5 + Math.random())));
    }
  };
  const t0 = Date.now();
  await Promise.all(Array.from({ length: USERS }, (_, i) => vu(i)));
  const secs = (Date.now() - t0) / 1000;

  let total = 0, errors = 0, worstP95 = 0;
  console.log(`\n${"route".padEnd(22)} ${"reqs".padStart(7)} ${"rps".padStart(7)} ${"p50".padStart(7)} ${"p95".padStart(7)} ${"p99".padStart(7)} ${"max".padStart(7)}  errors`);
  for (const [route, s] of [...stats.entries()].sort()) {
    const errs = [...s.errors.values()].reduce((a, b) => a + b, 0);
    total += s.lat.length; errors += errs;
    const p95 = pct(s.lat, 95); worstP95 = Math.max(worstP95, p95);
    console.log(`${route.padEnd(22)} ${String(s.lat.length).padStart(7)} ${(s.lat.length / secs).toFixed(1).padStart(7)} ${pct(s.lat, 50).toFixed(0).padStart(7)} ${p95.toFixed(0).padStart(7)} ${pct(s.lat, 99).toFixed(0).padStart(7)} ${Math.max(...s.lat).toFixed(0).padStart(7)}  ${errs ? [...s.errors].map(([k, v]) => `${k}×${v}`).join(" ") : "-"}`);
  }
  const rate = total ? errors / total : 1;
  console.log(`\n${total} requests in ${secs.toFixed(1)}s = ${(total / secs).toFixed(1)} req/s; error rate ${(rate * 100).toFixed(2)}%; worst p95 ${worstP95.toFixed(0)}ms`);
  const failed = rate > MAX_ERROR_RATE || worstP95 > MAX_P95_MS;
  console.log(failed ? `FAIL (thresholds: error rate ≤ ${MAX_ERROR_RATE * 100}%, p95 ≤ ${MAX_P95_MS}ms)` : "PASS");
  process.exit(failed ? 1 : 0);
})();

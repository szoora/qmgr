// SECTION 44 — SEGREGATION OF DUTIES (plan LESSON_PLANS_AND_SCHEMES_OF_WORK §3, built 2026-09-26).
//
// Each of the gaps G1–G7 the audit found is attempted by the person who must NOT be able to do it, and asserted refused
// in words; then done by the right person, so a refusal is never a broken endpoint passing for a rule. NIST SP 800-53
// AC-5: nobody decides on their own work or about themselves, and nobody takes two stages of one item.
//
//   G1  an appraisal: the subject cannot review it; whoever wrote the review cannot moderate or sign it
//   G2  nobody logs a performance record about themselves
//   G3  nobody annuls or re-scores a record about themselves
//   G4  whoever wrote the minutes does not adopt them          (also asserted in section 17)
//   G5  a report's author does not excuse their own report ("no duty that day")
//   G6  the colleague in a swap or cover does not decide it      — needs a published timetable: see the SKIP below
//   G7  nobody appoints themselves to lead a department
//
// Uses the Staff Performance accounts section 14 creates (e2e.sp.*). Everything it makes is labelled with the run id.
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/separation-of-duties-e2e.mjs

const API = process.env.API ?? "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH ?? "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const PWS = ["E2eTeacher!2026", "Rwenzori#Peaks-2026"];
const RUN = Date.now().toString(36);
const B = `/api/v1/branches/${BRANCH}`;

let pass = 0, fail = 0, skipped = 0;
const viewer = (l) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => { cond ? pass++ : fail++; const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond || !detail ? "" : "  — " + detail}`; console.log(l); viewer(l); };
const eq = (name, actual, expected, extra = "") => ok(name, actual === expected, `got ${JSON.stringify(actual)}, wanted ${JSON.stringify(expected)} ${extra}`);
const skip = (name, why) => { skipped++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); viewer(l); };
const hdr = (s) => { console.log(`\n${s}`); viewer(`\n${s}`); };

async function call(token, method, path, body) {
  const r = await fetch(`${API}${path}`, { method, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
}
async function signIn(email) {
  for (const password of PWS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, id: j.user?.id }; }
  }
  return null;
}
const iso = (ms) => new Date(ms).toISOString();

hdr(`44. SEGREGATION OF DUTIES (run ${RUN})`);
const AD = await signIn(process.env.AD_USER ?? "e2e.admin.ct@qmgr.local");
const DOS = await signIn("e2e.sp.dos@qmgr.local");
const HOD = await signIn("e2e.sp.hod.math@qmgr.local");
const M1 = await signIn("e2e.sp.math1@qmgr.local");
const M2 = await signIn("e2e.sp.math2@qmgr.local");
if (!AD || !DOS || !HOD || !M1 || !M2) { console.log("    FAIL  the section 14 accounts are missing — run section 14 first"); process.exit(1); }

const params = (await call(AD.token, "GET", "/api/v1/staff/parameters")).json;
const list = params?.items ?? params ?? [];
const P = (name) => list.find((p) => p.name === name && p.isActive !== false);
const contribution = P("Co-curricular Activity") ?? P("Records & Schemes of Work") ?? list.find((p) => p.kind === "Contribution" && !p.isSystemSource);
const conduct = P("Conduct");

// ---------------------------------------------------------------------------------------------------------------
hdr("44.2 G2 — nobody logs a record about themselves");
{
  const body = (subject) => ({ subjectUserId: subject, parameterId: contribution?.id, points: 2, occurredAt: iso(Date.now() - 3600_000), visibility: "Standard", description: `E2E ${RUN} separation-of-duties check` });
  const self = await call(DOS.token, "POST", `${B}/staff/records`, body(DOS.id));
  eq("44.2a: the Director of Studies cannot log a record about themselves (403)", self.status, 403, self.text.slice(0, 120));
  ok("44.2b: …and is told why, in words", /yourself/i.test(self.json?.detail ?? ""), self.json?.detail);
  const selfHod = await call(HOD.token, "POST", `${B}/staff/records`, body(HOD.id));
  eq("44.2c: a head of department cannot award themselves points (403)", selfHod.status, 403);
  const other = await call(DOS.token, "POST", `${B}/staff/records`, body(M1.id));
  ok("44.2d: the same record about somebody else is accepted (201)", other.status === 201, `${other.status} ${other.text.slice(0, 120)}`);
}

// ---------------------------------------------------------------------------------------------------------------
hdr("44.3 G3 — nobody annuls or re-scores a record about themselves");
{
  const aboutHod = await call(DOS.token, "POST", `${B}/staff/records`, { subjectUserId: HOD.id, parameterId: contribution?.id, points: 1, occurredAt: iso(Date.now() - 3600_000), visibility: "Standard", description: `E2E ${RUN} a record about the head, for G3` });
  ok("44.3a: a record about the head of department is logged by the Director of Studies", aboutHod.status === 201, aboutHod.text.slice(0, 120));
  const id = aboutHod.json?.id;
  if (id) {
    const annul = await call(HOD.token, "POST", `${B}/staff/records/${id}/annul`, { reason: `E2E ${RUN} I do not like this record` });
    eq("44.3b: the head cannot annul a record about themselves (403)", annul.status, 403, annul.text.slice(0, 120));
    const points = await call(HOD.token, "PATCH", `${B}/staff/records/${id}/points`, { points: 5, reason: `E2E ${RUN} raising my own points` });
    eq("44.3c: …nor re-score it (403)", points.status, 403, points.text.slice(0, 120));
    const note = await call(HOD.token, "POST", `${B}/staff/records/${id}/respond`, { body: `E2E ${RUN} my reply to this record` });
    ok("44.3d: …but may reply with a note (the right of reply is not a decision)", note.status === 200 || note.status === 201, `${note.status} ${note.text.slice(0, 120)}`);
    const annulByOther = await call(DOS.token, "POST", `${B}/staff/records/${id}/annul`, { reason: `E2E ${RUN} annulled by its author for the test` });
    ok("44.3e: the author, who is not the subject, may annul it", annulByOther.status === 200, `${annulByOther.status} ${annulByOther.text.slice(0, 120)}`);
  }
}

// ---------------------------------------------------------------------------------------------------------------
hdr("44.1 G1 — an appraisal is never reviewed by its subject nor signed by its reviewer");
{
  const PERIOD = `${new Date().getFullYear()}-T${new Date().getMonth() < 4 ? 1 : new Date().getMonth() < 8 ? 2 : 3}`;
  await call(DOS.token, "POST", `${B}/staff/appraisals/open`, { periodKey: PERIOD, subjectUserIds: [DOS.id, M2.id] });
  const board = (await call(DOS.token, "GET", `${B}/staff/appraisals/board?period=${PERIOD}`)).json;
  const rows = board?.items ?? board?.appraisals ?? board ?? [];
  const find = (uid) => (Array.isArray(rows) ? rows : []).find((a) => a.subjectUserId === uid);
  const own = find(DOS.id);
  if (!own) skip("44.1a–c", "no appraisal for the Director of Studies could be opened this term");
  else if (!["Open", "SelfAssessment", "AppraiserReview"].includes(own.stage)) skip("44.1a", `the Director of Studies' appraisal is already at ${own.stage} from an earlier run`);
  else {
    const selfReview = await call(DOS.token, "POST", `${B}/staff/appraisals/${own.id}/review`, { rating: 5, comments: "E2E reviewing myself" });
    ok("44.1a: the subject cannot review their own appraisal (403, or 409 while it waits for a self-assessment)", [403, 409].includes(selfReview.status), `${selfReview.status} ${selfReview.text.slice(0, 120)}`);
  }
  const m2 = find(M2.id);
  if (!m2) skip("44.1b–d", "no appraisal for the second teacher could be opened");
  else {
    // A signed appraisal from an earlier run is APPEALED, which reopens moderation — so every run exercises the rule.
    if (m2.stage === "Signed") await call(M2.token, "POST", `${B}/staff/appraisals/${m2.id}/appeal`, { note: `E2E ${RUN} reopening moderation for the test` });
    // The administrator (an approver) writes the review, so the administrator must not sign it.
    // Open → targets (the appraiser), then the subject's self-assessment; only then may a review be written.
    if (m2.stage === "Open") await call(HOD.token, "PUT", `${B}/staff/appraisals/${m2.id}/targets`, { targets: [{ text: `E2E ${RUN} a target` }] });
    if (["Open", "SelfAssessment"].includes((await call(M2.token, "GET", `${B}/staff/appraisals/${m2.id}`)).json?.stage))
      await call(M2.token, "POST", `${B}/staff/appraisals/${m2.id}/self`, { selfRating: 3, comments: `E2E ${RUN} self-assessment`, ratings: [] });
    if (!["Moderation", "Appealed", "Signed"].includes(m2.stage)) {
      const rv = await call(AD.token, "POST", `${B}/staff/appraisals/${m2.id}/review`, { rating: 3, comments: `E2E ${RUN} review by the administrator` });
      ok("44.1b: an approver may write the review", rv.status === 200 || rv.status === 409, `${rv.status} ${rv.text.slice(0, 120)}`);
    }
    const after = (await call(AD.token, "GET", `${B}/staff/appraisals/${m2.id}`)).json;
    if (after?.stage === "Moderation" || after?.stage === "Appealed") {
      ok("44.1c0: the reviewer's own view offers no moderation", after?.canIModerate === false, JSON.stringify({ can: after?.canIModerate }));
      const mod = await call(AD.token, "POST", `${B}/staff/appraisals/${m2.id}/moderate`, { finalRating: 3, signNow: true });
      eq("44.1c: whoever wrote the review cannot moderate or sign it (403)", mod.status, 403, mod.text.slice(0, 120));
      const sign = await call(AD.token, "POST", `${B}/staff/appraisals/${m2.id}/sign`);
      // Sign accepts only the Moderation stage; an appealed appraisal is moderated instead, so there 409 is the answer.
      eq("44.1d: …nor sign it directly (403; 409 while it is under appeal)", sign.status, after.stage === "Moderation" ? 403 : 409);
      const other = await call(DOS.token, "POST", `${B}/staff/appraisals/${m2.id}/moderate`, { finalRating: 3, signNow: true });
      eq("44.1e: somebody else signs it (200)", other.status, 200, other.text.slice(0, 160));
      // Left appealed, so the next run has something to moderate.
      await call(M2.token, "POST", `${B}/staff/appraisals/${m2.id}/appeal`, { note: `E2E ${RUN} left open for the next run` });
    } else skip("44.1c–e", `the appraisal is at ${after?.stage}, not waiting for moderation`);
  }
}

// ---------------------------------------------------------------------------------------------------------------
hdr("44.4 G4 — whoever wrote the minutes does not adopt them");
{
  const att = list.find((p) => p.isActive !== false && /attendance/i.test(p.name)) ?? list[0];
  const start = new Date(Date.now() - 2 * 3600_000), end = new Date(Date.now() - 3600_000);
  const meeting = await call(AD.token, "POST", `${B}/staff/duties`, { parameterId: att.id, title: `SOD-E2E ${RUN} meeting`, startsAt: start.toISOString(), endsAt: end.toISOString(), expectedUserIds: [M1.id, M2.id], recorderUserIds: [AD.id], kind: "Session" });
  ok("44.4a: a meeting is set up", meeting.status === 201, meeting.text.slice(0, 120));
  const id = meeting.json?.id;
  if (id) {
    await call(AD.token, "PUT", `${B}/staff/duties/${id}/minutes/content`, { sections: [{ key: "agenda", body: `E2E ${RUN} written by the administrator` }], decisions: [], actions: [] });
    const writer = await call(AD.token, "POST", `${B}/staff/duties/${id}/minutes/approve`, {});
    eq("44.4b: the writer cannot adopt the minutes (403)", writer.status, 403);
    const minutes = (await call(AD.token, "GET", `${B}/staff/duties/${id}/minutes`)).json;
    eq("44.4c: …and is not offered the button", minutes?.canApprove, false);
    const other = await call(DOS.token, "POST", `${B}/staff/duties/${id}/minutes/approve`, {});
    eq("44.4d: somebody else adopts them (200)", other.status, 200, other.text.slice(0, 120));
  }
}

// ---------------------------------------------------------------------------------------------------------------
hdr("44.5 G5 — a report's author does not excuse their own report");
{
  const defaults = (await call(AD.token, "GET", `${B}/staff/rota/defaults`)).json;
  const tod = list.find((p) => p.id === defaults?.parameterId);
  if (!tod) skip("44.5", "the rota's Teacher on Duty parameter is not available");
  else {
    const day = 24 * 3600_000;
    const slot = await call(AD.token, "POST", `${B}/staff/duties`, {
      parameterId: tod.id, title: `SOD-E2E ${RUN} reported duty`, startsAt: iso(Date.now() - 2 * day), endsAt: iso(Date.now() + day), kind: "Rota",
      expectedUserIds: [M1.id], supervisorUserIds: [DOS.id], recorderUserIds: [], reportCadence: "Daily", reportDueLocalTime: "18:00"
    });
    ok("44.5a: a daily-report rota slot is set up, supervised by the Director of Studies", slot.status === 201, slot.text.slice(0, 120));
    if (slot.json?.id) {
      const mine = ((await call(DOS.token, "GET", `${B}/staff/duty-reports/mine`)).json ?? []).filter((r) => r.dutyId === slot.json.id);
      const own = mine[0];
      if (!own) skip("44.5b", "the supervisor has no report row of their own on the slot");
      else {
        const nd = await call(DOS.token, "POST", `${B}/staff/duty-reports/${own.id}/no-duty`, { body: `E2E ${RUN} excusing my own report` });
        eq("44.5b: the supervisor cannot excuse their OWN report, although they supervise the slot (403)", nd.status, 403, nd.text.slice(0, 120));
        const byOther = await call(AD.token, "POST", `${B}/staff/duty-reports/${own.id}/no-duty`, { body: `E2E ${RUN} the school was closed` });
        eq("44.5c: a duty manager excuses it (200)", byOther.status, 200, byOther.text.slice(0, 120));
      }
      await call(AD.token, "DELETE", `${B}/staff/duties/${slot.json.id}`);
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
hdr("44.6 G6 — the colleague in a swap or cover does not decide it");
skip("44.6", "a swap or cover needs a published timetable with lessons for two teachers; the rule is DutySeparation in StaffSelfService.RefuseDecision and CanIDecide, and section 26 drives the flow");

// ---------------------------------------------------------------------------------------------------------------
hdr("44.7 G7 — nobody appoints themselves to lead a department");
{
  const code = `SOD${RUN.slice(-5).toUpperCase()}`;
  const selfHead = await call(DOS.token, "POST", `${B}/staff/structure/departments`, { name: `E2E ${RUN} self-appointed`, code, headUserId: DOS.id, sortOrder: 99 });
  eq("44.7a: the Director of Studies cannot make themselves head of a new department (403)", selfHead.status, 403, selfHead.text.slice(0, 120));
  const selfDeputy = await call(DOS.token, "POST", `${B}/staff/structure/departments`, { name: `E2E ${RUN} self-deputy`, code: code + "D", deputyHeadUserId: DOS.id, sortOrder: 99 });
  eq("44.7b: …nor its deputy (403)", selfDeputy.status, 403);
  const byAdmin = await call(AD.token, "POST", `${B}/staff/structure/departments`, { name: `E2E ${RUN} appointed`, code, headUserId: DOS.id, sortOrder: 99 });
  ok("44.7c: an administrator may appoint them (201)", byAdmin.status === 201, `${byAdmin.status} ${byAdmin.text.slice(0, 120)}`);
  if (byAdmin.json?.id) {
    const rename = await call(DOS.token, "PUT", `${B}/staff/structure/departments/${byAdmin.json.id}`, { name: `E2E ${RUN} renamed`, code, headUserId: DOS.id, sortOrder: 99 });
    ok("44.7d: a head keeps their post through an unrelated edit of their own department", rename.status === 200, `${rename.status} ${rename.text.slice(0, 120)}`);
    await call(AD.token, "PUT", `${B}/staff/structure/departments/${byAdmin.json.id}`, { name: `E2E ${RUN} retired`, code, headUserId: null, sortOrder: 99 });
    await call(AD.token, "PATCH", `${B}/staff/structure/departments/${byAdmin.json.id}/toggle`);
  }
}

console.log(`\n${pass} passed, ${fail} failed${skipped ? `, ${skipped} skipped` : ""}`);
process.exitCode = fail ? 1 : 0;

// SECTION 45 — LESSON PLANS AND SCHEMES OF WORK (plan LESSON_PLANS_AND_SCHEMES_OF_WORK, built 2026-09-26).
//
// The chain end to end, against a live API and real accounts:
//   45.1  starting a plan: pre-filled, once per press, only for a class the teacher teaches; a draft is the author's alone
//   45.2  submitting: required sections, the row version, a submitted plan is frozen, who can read it
//   45.3  one-stage lesson plan: the head of department approves; the author cannot; the teacher is told; a revision
//   45.4  returning: a reason is required, the snapshot is kept, the resubmission starts again; comments reach the author
//   45.5  a scheme of work takes BOTH stages; the forwarder cannot approve; five approvals race and one lands; who is told;
//         records of work
//   45.6  the author is the head of department: stage 1 is skipped and SAID, never handed to the author
//   45.7  files: a clean PDF is kept; a script, an automatic action, a hidden object stream, a disguised name, a non-PDF,
//         too many pages and too many kilobytes are all refused; the file is served only to those who may read the plan
//   45.8  the Word template: downloaded in the school's format, FILLED IN HERE, uploaded back and read into the form
//   45.9  the queue and the reports are gated; the curriculum list is the school's to edit
//
// Uses the section 14 accounts (e2e.sp.*) and the E2EMATH department. It seeds a subject in that department and two
// subject-teacher assignments, and removes the assignments and puts the school's plan settings and curriculum back.
//
//   API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/teaching-plans-e2e.mjs

import zlib from "node:zlib";

const API = process.env.API ?? "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH ?? "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
const PWS = ["E2eTeacher!2026", "Rwenzori#Peaks-2026"];
const RUN = Date.now().toString(36);
const B = `/api/v1/branches/${BRANCH}`;
const TP = `${B}/teaching-plans`;

let pass = 0, fail = 0, skipped = 0;
const viewer = (l) => fetch("http://127.0.0.1:5010/append?key=api", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => { cond ? pass++ : fail++; const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond || !detail ? "" : "  — " + detail}`; console.log(l); viewer(l); };
const eq = (name, actual, expected, extra = "") => ok(name, actual === expected, `got ${JSON.stringify(actual)}, wanted ${JSON.stringify(expected)} ${extra}`);
const skip = (name, why) => { skipped++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); viewer(l); };
const hdr = (s) => { console.log(`\n${s}`); viewer(`\n${s}`); };

async function call(token, method, path, body, raw) {
  const headers = { ...(token ? { Authorization: `Bearer ${token}` } : {}) };
  let payload;
  if (raw) payload = raw; else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const r = await fetch(path.startsWith("http") ? path : `${API}${path}`, { method, headers, body: payload });
  const buf = Buffer.from(await r.arrayBuffer());
  const text = buf.toString("utf8");
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text, buf, type: r.headers.get("content-type") ?? "" };
}
async function signIn(email) {
  for (const password of PWS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password }) });
    if (r.ok) { const j = await r.json(); return { token: j.accessToken, id: j.user?.id, name: j.user?.fullName }; }
  }
  return null;
}
const upload = (token, path, bytes, name, extra = {}) => {
  const fd = new FormData();
  fd.append("file", new Blob([bytes]), name);
  for (const [k, v] of Object.entries(extra)) fd.append(k, String(v));
  return call(token, "POST", path, undefined, fd);
};
const notified = async (who, key, text) => ((await call(who.token, "GET", `/api/v1/notifications?eventKey=${key}&limit=100`)).json ?? [])
  .some((n) => `${n.title} ${n.message}`.includes(text));

// ---- A PDF by hand: the smallest file the gate accepts, and hostile variants ------------------------------------------
function pdf({ pages = 1, catalogExtra = "", extraObjects = [], text = "E2E lesson plan" } = {}) {
  const objs = [];
  const kids = [];
  objs.push(`1 0 obj\n<< /Type /Catalog /Pages 2 0 R ${catalogExtra} >>\nendobj\n`);
  let next = 3;
  const pageObjs = [];
  for (let i = 0; i < pages; i++) {
    const p = next++, c = next++;
    kids.push(`${p} 0 R`);
    const content = `BT /F1 12 Tf 72 720 Td (${text} page ${i + 1}) Tj ET`;
    pageObjs.push(`${p} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents ${c} 0 R /Resources << /Font << /F1 99 0 R >> >> >>\nendobj\n`);
    pageObjs.push(`${c} 0 obj\n<< /Length ${content.length} >>\nstream\n${content}\nendstream\nendobj\n`);
  }
  objs.push(`2 0 obj\n<< /Type /Pages /Kids [${kids.join(" ")}] /Count ${pages} >>\nendobj\n`);
  objs.push(...pageObjs);
  objs.push(`99 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n`);
  const head = Buffer.from("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n", "latin1");
  const parts = [head, Buffer.from(objs.join(""), "latin1"), ...extraObjects.map((o) => Buffer.isBuffer(o) ? o : Buffer.from(o, "latin1"))];
  parts.push(Buffer.from("trailer\n<< /Root 1 0 R >>\n%%EOF\n", "latin1"));
  return Buffer.concat(parts);
}
function flateObject(num, dict, body) {
  const data = zlib.deflateSync(Buffer.from(body, "latin1"));
  return Buffer.concat([Buffer.from(`${num} 0 obj\n<< ${dict} /Filter /FlateDecode /Length ${data.length} >>\nstream\n`, "latin1"), data, Buffer.from("\nendstream\nendobj\n", "latin1")]);
}

// ---- A zip by hand (a .docx is a zip): read with inflateRaw, write stored with crc32 --------------------------------
function unzip(buf) {
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  const count = buf.readUInt16LE(eocd + 10), cdOffset = buf.readUInt32LE(eocd + 16);
  const files = {};
  let p = cdOffset;
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20), nlen = buf.readUInt16LE(p + 28), xlen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32), local = buf.readUInt32LE(p + 42);
    const name = buf.slice(p + 46, p + 46 + nlen).toString("utf8");
    const lnlen = buf.readUInt16LE(local + 26), lxlen = buf.readUInt16LE(local + 28);
    const data = buf.slice(local + 30 + lnlen + lxlen, local + 30 + lnlen + lxlen + csize);
    files[name] = method === 8 ? zlib.inflateRawSync(data) : data;
    p += 46 + nlen + xlen + clen;
  }
  return files;
}
function zip(files) {
  const locals = [], centrals = [];
  let offset = 0;
  for (const [name, data] of Object.entries(files)) {
    const n = Buffer.from(name, "utf8"), crc = zlib.crc32(data) >>> 0;
    const lh = Buffer.alloc(30); lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt32LE(crc, 14); lh.writeUInt32LE(data.length, 18); lh.writeUInt32LE(data.length, 22); lh.writeUInt16LE(n.length, 26);
    const ch = Buffer.alloc(46); ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(20, 4); ch.writeUInt16LE(20, 6); ch.writeUInt32LE(crc, 16); ch.writeUInt32LE(data.length, 20); ch.writeUInt32LE(data.length, 24); ch.writeUInt16LE(n.length, 28); ch.writeUInt32LE(offset, 42);
    locals.push(lh, n, data); centrals.push(ch, n);
    offset += 30 + n.length + data.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10); end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}
// Fills the answer cell after the first cell whose text starts with `label`.
const EMPTY_P = '<w:p><w:pPr><w:spacing w:after="60"/></w:pPr></w:p>';
function fillAfter(xml, label, text, skipCells = 0) {
  const at = xml.indexOf(`>${label}`);
  if (at < 0) return xml;
  let slot = xml.indexOf(EMPTY_P, at);
  for (let i = 0; i < skipCells && slot >= 0; i++) slot = xml.indexOf(EMPTY_P, slot + EMPTY_P.length);
  if (slot < 0) return xml;
  const filled = `<w:p><w:r><w:t xml:space="preserve">${text}</w:t></w:r></w:p>`;
  return xml.slice(0, slot) + filled + xml.slice(slot + EMPTY_P.length);
}

hdr(`45. LESSON PLANS AND SCHEMES OF WORK (run ${RUN})`);
const AD = await signIn(process.env.AD_USER ?? "e2e.admin.ct@qmgr.local");
const DOS = await signIn("e2e.sp.dos@qmgr.local");
const AA = await signIn("e2e.sp.aa@qmgr.local");
const HOD = await signIn("e2e.sp.hod.math@qmgr.local");
const M1 = await signIn("e2e.sp.math1@qmgr.local");
const LANG = await signIn("e2e.sp.lang1@qmgr.local");
if (!AD || !DOS || !AA || !HOD || !M1 || !LANG) { console.log("    FAIL  the section 14 accounts are missing — run section 14 first"); process.exit(1); }

// ---- Fixture ----------------------------------------------------------------------------------------------------------
const depts = (await call(AD.token, "GET", `${B}/staff/structure/departments?includeInactive=true`)).json ?? [];
const MATH = depts.find((d) => d.code === "E2EMATH");
if (!MATH) { console.log("    FAIL  department E2EMATH is missing — run section 14 first"); process.exit(1); }
// The E2EMATH head must be the Maths head with NO deputy, or 45.6's "skipped and said" cannot be told apart.
if (MATH.headUserId !== HOD.id || MATH.deputyHeadUserId) await call(AD.token, "PUT", `${B}/staff/structure/departments/${MATH.id}`, { name: MATH.name, code: MATH.code, headUserId: HOD.id, deputyHeadUserId: null, sortOrder: MATH.sortOrder ?? 1 });
let subjects = (await call(AD.token, "GET", `${B}/staff/subjects?includeInactive=true`)).json ?? [];
let SUBJ = subjects.find((s) => s.code === "E2EPLM");
if (!SUBJ) SUBJ = (await call(AD.token, "POST", `${B}/staff/subjects`, { name: "E2E Plans Mathematics", code: "E2EPLM", departmentId: MATH.id, sortOrder: 90 })).json;
else if (SUBJ.departmentId !== MATH.id) await call(AD.token, "PUT", `${B}/staff/subjects/${SUBJ.id}`, { name: SUBJ.name, code: SUBJ.code, departmentId: MATH.id, sortOrder: 90 });
ok("fixture: a subject in the E2EMATH department", !!SUBJ?.id, JSON.stringify(SUBJ));
const vocab = (await call(AD.token, "GET", `${B}/students/vocabularies`)).json;
const classes = (vocab?.classes ?? []).filter((c) => c.isActive !== false).map((c) => c.name);
if (classes.length < 3) { console.log("    FAIL  the branch needs three configured classes"); process.exit(1); }
const [C0, C1, C2] = classes;
const seeded = [];
const assign = async (userId, className) => {
  const r = await call(AD.token, "POST", `${B}/class-teachers/subject-teachers`, { className, userId, subjectId: SUBJ.id, periodsPerWeek: 4 });
  if (r.json?.id) seeded.push(r.json.id);
  return r.status;
};
ok("fixture: the teacher is assigned two classes and the head one", [await assign(M1.id, C0), await assign(M1.id, C1), await assign(HOD.id, C2)].every((s) => [200, 201, 409].includes(s)));

const policy = (await call(AD.token, "GET", "/api/v1/staff/policy")).json;
const originalPlans = structuredClone(policy.teachingPlans ?? {});
policy.teachingPlans = { ...(policy.teachingPlans ?? {}), lessonPlanStages: 1, uploadsEnabled: true, requirement: "EveryLesson", lessonPlanTemplate: [], schemeColumns: [], procedurePhases: [] };
const setPolicy = await call(AD.token, "PUT", "/api/v1/staff/policy", policy);
ok("fixture: plan settings — one stage for lesson plans, uploads on, default templates", setPolicy.status === 200, setPolicy.text.slice(0, 160));
const originalCurriculum = (await call(AD.token, "GET", `${TP}/curriculum`)).json ?? { topics: [] };

const future = (days) => { const d = new Date(Date.now() + days * 86400_000); return d.toISOString().slice(0, 10); };
const lessonDate = future(200 + Math.floor(Math.random() * 300));
const fill = (plan, extra = {}) => ({
  rowVersion: plan.rowVersion,
  content: {
    ...plan.content,
    answers: { ...plan.content.answers, topic: `E2E ${RUN} Fractions`, outcomes: "By the end, learners add fractions (s).", ...extra },
    procedure: plan.content.procedure.map((p, i) => ({ ...p, teacher: `Step ${i + 1} teacher`, learner: `Step ${i + 1} learners`, minutes: 10 })),
  },
});
const made = [];
const cleanup = [];

try {
  // ------------------------------------------------------------------------------------------------------------
  hdr("45.1 Starting a plan");
  const request = { kind: "LessonPlan", subjectId: SUBJ.id, classNames: [C0], lessonDate, clientRequestId: crypto.randomUUID() };
  const created = await call(M1.token, "POST", TP, request);
  eq("45.1a: a teacher starts a lesson plan for their class (201)", created.status, 201, created.text.slice(0, 160));
  const P1 = created.json;
  made.push(P1?.id);
  eq("45.1b: …as a draft", P1?.status, "Draft");
  ok("45.1c: the header is filled — the teacher's name, the subject, the class", P1?.content?.header?.teacherName && P1?.content?.header?.subjectName === SUBJ.name && P1?.content?.header?.classText === C0, JSON.stringify(P1?.content?.header));
  eq("45.1d: the procedure arrives with the school's four phases", P1?.content?.procedure?.length, 4);
  ok("45.1e: the plan carries the school's template to draw itself", (P1?.sections ?? []).some((s) => s.key === "outcomes") && P1?.fileCapKb === 64);
  const again = await call(M1.token, "POST", TP, request);
  ok("45.1f: the same press again returns the SAME plan (200), never a second", again.status === 200 && again.json?.id === P1?.id, `${again.status} ${again.json?.id}`);
  const notMine = await call(M1.token, "POST", TP, { ...request, classNames: [C2], clientRequestId: crypto.randomUUID() });
  eq("45.1g: a class the teacher does not teach is refused (400)", notMine.status, 400);
  const lang = await call(LANG.token, "POST", TP, { ...request, clientRequestId: crypto.randomUUID() });
  eq("45.1h: a teacher of another department cannot plan this subject (400)", lang.status, 400);
  eq("45.1i: nobody else reads a draft — not the head of department (404)", (await call(HOD.token, "GET", `${TP}/${P1.id}`)).status, 404);
  eq("45.1j: …nor the Director of Studies (404)", (await call(DOS.token, "GET", `${TP}/${P1.id}`)).status, 404);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.2 Submitting");
  const empty = await call(M1.token, "POST", `${TP}/${P1.id}/submit`);
  eq("45.2a: an unfinished plan is refused, naming what is missing (400)", empty.status, 400);
  ok("45.2b: …in words", /Learning outcomes/i.test(empty.json?.detail ?? ""), empty.json?.detail);
  const saved = await call(M1.token, "PUT", `${TP}/${P1.id}`, fill(P1));
  eq("45.2c: the teacher saves the plan (200)", saved.status, 200, saved.text.slice(0, 160));
  const stale = await call(M1.token, "PUT", `${TP}/${P1.id}`, fill(P1));
  eq("45.2d: a save against an old copy is refused, never silently overwritten (409)", stale.status, 409);
  const submitted = await call(M1.token, "POST", `${TP}/${P1.id}/submit`);
  eq("45.2e: submitted (200)", submitted.status, 200, submitted.text.slice(0, 160));
  eq("45.2f: …and it is with the head of department", submitted.json?.status, "Submitted");
  ok("45.2g: …who is named", (submitted.json?.waitingOn ?? "").length > 5, submitted.json?.waitingOn);
  eq("45.2h: a submitted plan is frozen (409)", (await call(M1.token, "PUT", `${TP}/${P1.id}`, fill(submitted.json))).status, 409);
  eq("45.2i: another department's teacher cannot read it (404)", (await call(LANG.token, "GET", `${TP}/${P1.id}`)).status, 404);
  const asHod = (await call(HOD.token, "GET", `${TP}/${P1.id}`)).json;
  ok("45.2j: the head of department reads it and may approve (one stage)", asHod?.canIApprove === true && asHod?.canIForward === false, JSON.stringify({ a: asHod?.canIApprove, f: asHod?.canIForward }));
  const asDos = (await call(DOS.token, "GET", `${TP}/${P1.id}`)).json;
  ok("45.2k: the Director of Studies reads it but has nothing to decide yet", asDos?.canIApprove === false, JSON.stringify({ a: asDos?.canIApprove }));
  const hodQueue = (await call(HOD.token, "GET", `${TP}/queue?awaitingMe=true`)).json;
  ok("45.2l: it is in the head's queue, waiting on them", (hodQueue?.items ?? []).some((i) => i.id === P1.id && i.awaitsMe), JSON.stringify(hodQueue?.items?.map((i) => i.title)));
  eq("45.2m: the head was told", await notified(HOD, "staff.plan-submitted", P1.title), true);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.3 One stage: the head of department approves");
  eq("45.3a: the author cannot approve their own plan (403)", (await call(M1.token, "POST", `${TP}/${P1.id}/approve`, {})).status, 403);
  const approved = await call(HOD.token, "POST", `${TP}/${P1.id}/approve`, { note: "Good sequence." });
  eq("45.3b: the head approves (200)", approved.status, 200, approved.text.slice(0, 160));
  eq("45.3c: …Approved", approved.json?.status, "Approved");
  eq("45.3d: the teacher is told", await notified(M1, "staff.plan-approved", P1.title), true);
  eq("45.3e: a lesson plan does NOT ping the Director of Studies one by one (the Monday digest carries it)", await notified(DOS, "staff.plan-approved", P1.title), false);
  eq("45.3f: an approved plan is never edited (409)", (await call(M1.token, "PUT", `${TP}/${P1.id}`, fill(approved.json))).status, 409);
  const rev = await call(M1.token, "POST", `${TP}/${P1.id}/revise`);
  ok("45.3g: a change is a new version (201)", rev.status === 201 && rev.json?.version === 2 && rev.json?.status === "Draft", `${rev.status} v${rev.json?.version}`);
  made.push(rev.json?.id);
  const old = (await call(M1.token, "GET", `${TP}/${P1.id}`)).json;
  ok("45.3h: …and the approved version stays in use until the new one is approved", old?.isCurrent === true && rev.json?.isCurrent === false);
  const revAgain = await call(M1.token, "POST", `${TP}/${P1.id}/revise`);
  eq("45.3i: asking again returns the same open revision", revAgain.json?.id, rev.json?.id);
  const reflect = await call(M1.token, "POST", `${TP}/${P1.id}/reflection`, { note: "The learners needed more worked examples." });
  ok("45.3j: the self-evaluation is written after approval, and nothing else changes", reflect.status === 200 && reflect.json?.reflection?.includes("worked examples") && reflect.json?.status === "Approved", `${reflect.status}`);
  // Approving a REVISION: the old version stops being current and the new one starts, in one act. This 409ed for
  // everybody until it was caught on a second run of this section — so it runs on every run now.
  await call(M1.token, "PUT", `${TP}/${rev.json.id}`, fill(rev.json, { outcomes: "Revised: learners add and subtract fractions (s)." }));
  await call(M1.token, "POST", `${TP}/${rev.json.id}/submit`);
  const revApproved = await call(HOD.token, "POST", `${TP}/${rev.json.id}/approve`, {});
  eq("45.3k: the revision is approved (200)", revApproved.status, 200, revApproved.text.slice(0, 160));
  ok("45.3l: …it is now the current version, and the old one is not", revApproved.json?.isCurrent === true && (await call(M1.token, "GET", `${TP}/${P1.id}`)).json?.isCurrent === false);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.4 Returning for changes");
  const p2 = (await call(M1.token, "POST", TP, { kind: "LessonPlan", subjectId: SUBJ.id, classNames: [C1], lessonDate: future(700), clientRequestId: crypto.randomUUID() })).json;
  made.push(p2?.id);
  await call(M1.token, "PUT", `${TP}/${p2.id}`, fill(p2));
  await call(M1.token, "POST", `${TP}/${p2.id}/submit`);
  eq("45.4a: a return needs a reason of ten characters (400)", (await call(HOD.token, "POST", `${TP}/${p2.id}/return`, { note: "No." })).status, 400);
  const returned = await call(HOD.token, "POST", `${TP}/${p2.id}/return`, { note: "Add an assessment for the outcomes.", sectionKey: "assessment" });
  eq("45.4b: returned (200)", returned.status, 200, returned.text.slice(0, 160));
  ok("45.4c: …with the reason, and a snapshot of what was returned", returned.json?.returnReason?.includes("assessment") && (returned.json?.trail ?? []).some((t) => t.kind === "Returned" && t.snapshotJson?.includes("Fractions")));
  eq("45.4d: the teacher is told", await notified(M1, "staff.plan-returned", p2.title), true);
  const fixed = await call(M1.token, "PUT", `${TP}/${p2.id}`, fill(returned.json, { assessment: "An exit ticket." }));
  eq("45.4e: the teacher may edit a returned plan (200)", fixed.status, 200);
  const re = await call(M1.token, "POST", `${TP}/${p2.id}/submit`);
  eq("45.4f: …and resubmits it; the chain starts again", re.json?.status, "Submitted");
  await call(HOD.token, "POST", `${TP}/${p2.id}/comment`, { note: "Better. Thank you." });
  eq("45.4g: a reviewer's comment reaches the author", await notified(M1, "staff.plan-comment", p2.title), true);
  const withdrawn = await call(M1.token, "POST", `${TP}/${p2.id}/withdraw`);
  eq("45.4h: the author may withdraw it before a decision — it is a draft again", withdrawn.json?.status, "Draft");
  eq("45.4i: …and discard it", (await call(M1.token, "POST", `${TP}/${p2.id}/discard`)).status, 204);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.5 A scheme of work: both stages");
  let scheme = await call(M1.token, "POST", TP, { kind: "SchemeOfWork", subjectId: SUBJ.id, classNames: [C0, C1], clientRequestId: crypto.randomUUID() });
  ok("45.5a: a scheme is started for two streams (201, or 200 for this term's existing one)", [200, 201].includes(scheme.status), scheme.text.slice(0, 160));
  let S = scheme.json;
  // An earlier run leaves this term's scheme approved, or a revision of it part-way: take it back to a draft either way.
  if (S?.status === "Approved") S = (await call(M1.token, "POST", `${TP}/${S.id}/revise`)).json;
  if (S?.status === "Submitted" || S?.status === "Forwarded") S = (await call(M1.token, "POST", `${TP}/${S.id}/withdraw`)).json;
  made.push(S?.id);
  const rows = [1, 2].map((w) => ({ key: `e2e${RUN}${w}`, week: w, periods: 4, cells: { topic: `E2E ${RUN} Topic ${w}`, outcomes: `Outcome ${w} (k)` } }));
  const sSaved = await call(M1.token, "PUT", `${TP}/${S.id}`, { rowVersion: S.rowVersion, rows });
  eq("45.5b: the weeks are saved", sSaved.status, 200, sSaved.text.slice(0, 160));
  const sSub = await call(M1.token, "POST", `${TP}/${S.id}/submit`);
  ok("45.5c: submitted, and a scheme always takes two stages", sSub.json?.status === "Submitted" && sSub.json?.stages === 2, JSON.stringify({ s: sSub.json?.status, n: sSub.json?.stages }));
  eq("45.5d: the head cannot approve a scheme at stage 1 — they forward it (409)", (await call(HOD.token, "POST", `${TP}/${S.id}/approve`, {})).status, 409);
  const fwd = await call(HOD.token, "POST", `${TP}/${S.id}/forward`, { note: "Sequenced well." });
  eq("45.5e: the head forwards it (200)", fwd.json?.status, "Forwarded", fwd.text.slice(0, 160));
  eq("45.5f: the author is told it moved on", await notified(M1, "staff.plan-forwarded", S.title), true);
  eq("45.5g: an approver is told it waits", await notified(DOS, "staff.plan-forwarded", S.title), true);
  const hodTwice = await call(HOD.token, "POST", `${TP}/${S.id}/approve`, {});
  eq("45.5h: whoever forwarded it cannot also approve it (403)", hodTwice.status, 403);
  const race = await Promise.all([DOS, AA, DOS, AA, DOS].map((u) => call(u.token, "POST", `${TP}/${S.id}/approve`, { note: "Approved." })));
  eq("45.5i: five approvals at once — exactly one lands", race.filter((r) => r.status === 200).length, 1, race.map((r) => r.status).join(","));
  const finalS = (await call(M1.token, "GET", `${TP}/${S.id}`)).json;
  eq("45.5j: …Approved", finalS?.status, "Approved");
  eq("45.5k: the teacher is told", await notified(M1, "staff.plan-approved", S.title), true);
  eq("45.5l: the head who forwarded it is told", await notified(HOD, "staff.plan-approved", S.title), true);
  const other = finalS?.approvedByName === DOS.name ? AA : DOS;
  eq("45.5m: the Director of Studies (the approvers) hear of a scheme at once", await notified(other, "staff.plan-approved", S.title), true);
  const row = await call(M1.token, "GET", `${TP}/${S.id}/record-of-work`);
  ok("45.5n: the record of work is derived from the scheme's weeks", row.status === 200 && row.json?.rows?.length === 2, `${row.status} ${row.text.slice(0, 120)}`);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.6 When the author heads the department");
  const h = (await call(HOD.token, "POST", TP, { kind: "LessonPlan", subjectId: SUBJ.id, classNames: [C2], lessonDate: future(720), clientRequestId: crypto.randomUUID() })).json;
  made.push(h?.id);
  await call(HOD.token, "PUT", `${TP}/${h.id}`, fill(h));
  const hSub = await call(HOD.token, "POST", `${TP}/${h.id}/submit`);
  ok("45.6a: stage 1 is SKIPPED — it goes straight to the approver", hSub.json?.status === "Forwarded", JSON.stringify({ s: hSub.json?.status }));
  ok("45.6b: …and the reason is said", /heads E2E Mathematics|no deputy/i.test(hSub.json?.stageSkippedReason ?? ""), hSub.json?.stageSkippedReason);
  eq("45.6c: the head cannot approve their own plan (403)", (await call(HOD.token, "POST", `${TP}/${h.id}/approve`, {})).status, 403);
  eq("45.6d: the Director of Studies approves it (200)", (await call(DOS.token, "POST", `${TP}/${h.id}/approve`, {})).status, 200);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.7 The PDF fallback: what the server keeps, and what it refuses");
  const f = (await call(M1.token, "POST", TP, { kind: "LessonPlan", subjectId: SUBJ.id, classNames: [C0], lessonDate: future(740), clientRequestId: crypto.randomUUID() })).json;
  made.push(f?.id);
  const good = await upload(M1.token, `${TP}/${f.id}/file`, pdf(), "plan.pdf", { originalSize: 312000 });
  ok("45.7a: a clean PDF is attached (200), with its size and pages", good.status === 200 && good.json?.filePages === 1 && good.json?.fileSizeBytes > 0 && good.json?.originalSizeBytes === 312000, `${good.status} ${good.text.slice(0, 160)}`);
  const refused = async (name, bytes, want = 400, pattern = null) => {
    const r = await upload(M1.token, `${TP}/${f.id}/file`, bytes, "bad.pdf");
    ok(name, r.status === want && (!pattern || pattern.test(r.json?.title ?? r.json?.detail ?? "")), `${r.status} ${(r.json?.title ?? r.text).slice(0, 140)}`);
  };
  await refused("45.7b: a PDF with a script is refused", pdf({ catalogExtra: "/Names << /JavaScript 50 0 R >>", extraObjects: ["50 0 obj\n<< /S /JavaScript /JS (app.alert(1)) >>\nendobj\n"] }), 400, /script/i);
  await refused("45.7c: a PDF that acts when opened is refused", pdf({ catalogExtra: "/OpenAction 51 0 R", extraObjects: ["51 0 obj\n<< /S /URI /URI (http://example.com) >>\nendobj\n"] }), 400, /opened/i);
  await refused("45.7d: a disguised name (/J#61vaScript) is read as what it is", pdf({ extraObjects: ["52 0 obj\n<< /S /J#61vaScript /JS (x) >>\nendobj\n"] }), 400, /script/i);
  await refused("45.7e: a script hidden in a compressed object stream is found", pdf({ extraObjects: [flateObject(53, "/Type /ObjStm /N 1 /First 5", "60 0 << /S /JavaScript /JS (app.alert(2)) >>")] }), 400, /script/i);
  await refused("45.7f: a file carrying other files is refused", pdf({ extraObjects: ["54 0 obj\n<< /Type /EmbeddedFile /Length 0 >>\nstream\n\nendstream\nendobj\n"] }), 400, /other files/i);
  await refused("45.7g: something that is not a PDF is refused", Buffer.from("PK\x03\x04 not a pdf at all, just some bytes padding padding padding padding padding"), 400);
  await refused("45.7h: more pages than the school allows is refused", pdf({ pages: 5 }), 400, /pages/i);
  await refused("45.7i: more than the school's 64 KB is refused before anything is read (413)", Buffer.concat([pdf(), Buffer.alloc(70 * 1024, 0x20)]), 413);
  const still = (await call(M1.token, "GET", `${TP}/${f.id}`)).json;
  ok("45.7j: every refusal left the good file in place", still?.filePages === 1 && still?.fileUrl, JSON.stringify({ p: still?.filePages }));
  const url = still?.fileUrl ?? "";
  const bare = url.split("?")[0];
  eq("45.7k: the file is served on its signed link", (await call(null, "GET", url)).status, 200);
  eq("45.7l: without the link a colleague who may not read the plan gets nothing (404)", (await call(LANG.token, "GET", bare)).status, 404);
  eq("45.7m: …and nobody signed out gets it either (401)", (await call(null, "GET", bare)).status, 401);
  eq("45.7n: the author reads it with their own sign-in", (await call(M1.token, "GET", bare)).status, 200);
  eq("45.7o: the file is removed on request (204)", (await call(M1.token, "DELETE", `${TP}/${f.id}/file`)).status, 204);
  cleanup.push(() => call(M1.token, "POST", `${TP}/${f.id}/discard`));

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.8 The Word template: downloaded, filled, read back — and never stored");
  const docx = await call(M1.token, "GET", `${TP}/templates/lesson-plan.docx`);
  ok("45.8a: the lesson plan template downloads as Word", docx.status === 200 && docx.buf.slice(0, 2).toString() === "PK" && /wordprocessingml/.test(docx.type), `${docx.status} ${docx.type}`);
  const parts = unzip(docx.buf);
  const doc = parts["word/document.xml"]?.toString("utf8") ?? "";
  ok("45.8b: it is in the school's format — its sections, the procedure and the header", doc.includes("LESSON PLAN") && doc.includes("Learning outcomes") && doc.includes("Teacher&apos;s activities"), doc.slice(0, 80));
  ok("45.8c: it carries its section keys, so a renamed section still reads back", (parts["docProps/custom.xml"]?.toString("utf8") ?? "").includes("s.outcomes"));
  let filled = fillAfter(doc, "Learning outcomes", `E2E ${RUN} learners compare fractions (u)`);
  filled = fillAfter(filled, "Topic", `E2E ${RUN} Fractions from Word`);
  // The procedure row is Phase | Minutes | Teacher | Learners: the teacher's cell is the SECOND empty one.
filled = fillAfter(filled, "Introduction", "Recall halves and quarters", 1);
  parts["word/document.xml"] = Buffer.from(filled, "utf8");
  const back = await upload(M1.token, `${TP}/templates/read`, zip(parts), "filled.docx");
  eq("45.8d: a filled template is read (200)", back.status, 200, back.text.slice(0, 160));
  ok("45.8e: …into the sections it was written in", back.json?.content?.answers?.outcomes?.includes("compare fractions") && back.json?.content?.answers?.topic?.includes("from Word"), JSON.stringify(back.json?.content?.answers));
  ok("45.8f: …including the procedure's teacher activity", (back.json?.content?.procedure ?? []).some((p) => /Recall halves/.test(p.teacher ?? "")), JSON.stringify(back.json?.content?.procedure));
  const schemeDoc = await call(M1.token, "GET", `${TP}/templates/scheme-of-work.docx?subjectId=${SUBJ.id}&classNames=${encodeURIComponent(C0)}`);
  ok("45.8g: the scheme template downloads, landscape, with its columns", schemeDoc.status === 200 && (unzip(schemeDoc.buf)["word/document.xml"]?.toString("utf8") ?? "").includes("SCHEME OF WORK"));
  eq("45.8h: an old .doc is refused with the way out (400)", (await upload(M1.token, `${TP}/templates/read`, Buffer.from("not really a doc"), "old.doc")).status, 400);

  // ------------------------------------------------------------------------------------------------------------
  hdr("45.9 Gates: the queue, the reports and the curriculum");
  const langQueue = (await call(LANG.token, "GET", `${TP}/queue`)).json;
  eq("45.9a: a teacher who reviews nothing has an empty queue", (langQueue?.items ?? []).length, 0);
  eq("45.9b: a teacher cannot read the planning reports (404)", (await call(M1.token, "GET", `${TP}/reports`)).status, 404);
  const rep = await call(DOS.token, "GET", `${TP}/reports`);
  ok("45.9c: the Director of Studies reads them", rep.status === 200 && rep.json?.review, rep.text.slice(0, 120));
  const schemeRows = rep.json?.schemes?.rows ?? [];
  const m1Row = schemeRows.find((r) => r.teacherUserId === M1.id && r.subjectId === SUBJ.id);
  ok("45.9c1: the report names each teacher's scheme — the teacher's is Approved", m1Row?.progress === "Approved" && m1Row?.classNames?.length >= 2, JSON.stringify(m1Row));
  const hodRow = schemeRows.find((r) => r.teacherUserId === HOD.id && r.subjectId === SUBJ.id);
  ok("45.9c2: …and a teacher with no scheme is listed as Not started, by name", hodRow?.progress === "NotStarted" && !hodRow?.planId, JSON.stringify(hodRow));
  const s = rep.json?.schemes;
  ok("45.9c3: the counts add up to the teachers expected", s && s.expected === s.notStarted + s.draft + s.withHeadOfDepartment + s.withApprover + s.returned + s.approved, JSON.stringify(s && { e: s.expected, n: s.notStarted, d: s.draft, h: s.withHeadOfDepartment, a: s.withApprover, r: s.returned, ok: s.approved }));
  const template = await call(M1.token, "GET", `${TP}/templates`);
  ok("45.9c4: any teacher reads the school's format for a blank sheet", template.status === 200 && template.json?.sections?.length > 5 && template.json?.weeks >= 1, template.text.slice(0, 120));
  eq("45.9d: a teacher cannot change the curriculum list (403)", (await call(M1.token, "PUT", `${TP}/curriculum`, { topics: [] })).status, 403);
  const merge = await call(DOS.token, "POST", `${TP}/curriculum/merge`, { topics: [{ subjectId: SUBJ.id, classLevel: "S1", term: 3, order: 1, topic: `E2E ${RUN} Number bases`, periods: 15, competency: "Uses number bases", outcomes: "Converts between bases (s)" }] });
  ok("45.9e: the Director of Studies imports a topic", merge.status === 200 && merge.json?.added === 1, merge.text.slice(0, 120));
  const again2 = await call(DOS.token, "POST", `${TP}/curriculum/merge`, { topics: [{ subjectId: SUBJ.id, classLevel: "s1", term: 3, order: 1, topic: `E2E ${RUN}  number bases`, periods: 16 }] });
  ok("45.9f: the same topic again (case and spacing folded) updates, never duplicates", again2.json?.updated === 1 && again2.json?.added === 0, again2.text.slice(0, 120));
  const unknown = await call(DOS.token, "PUT", `${TP}/curriculum`, { topics: [{ subjectId: crypto.randomUUID(), classLevel: "S1", term: 1, topic: "Nothing" }] });
  eq("45.9g: a topic for a subject the school does not have is refused (400)", unknown.status, 400);
} catch (e) {
  ok("the suite ran to the end", false, e?.stack ?? String(e));
} finally {
  for (const c of cleanup) { try { await c(); } catch { } }
  for (const id of seeded) await call(AD.token, "DELETE", `${B}/class-teachers/${id}`);
  const p = (await call(AD.token, "GET", "/api/v1/staff/policy")).json;
  if (p) { p.teachingPlans = originalPlans; await call(AD.token, "PUT", "/api/v1/staff/policy", p); }
  await call(DOS.token, "PUT", `${TP}/curriculum`, originalCurriculum);
  console.log(`\n${pass} passed, ${fail} failed${skipped ? `, ${skipped} skipped` : ""}`);
  process.exitCode = fail ? 1 : 0;
}

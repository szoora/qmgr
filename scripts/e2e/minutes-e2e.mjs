// MINUTES OF A MEETING — section 17. The lifecycle is the feature, so this drives it end to end
// against a real meeting with a real register: write → circulate → adopt → correct, and the two
// things that make it a record rather than a text box (attendance that comes from the register,
// and an adopted document that cannot be edited).
//
// Run: API=http://127.0.0.1:5001 BRANCH=<guid> node scripts/e2e/minutes-e2e.mjs
const API = process.env.API ?? "http://127.0.0.1:5001";
const BRANCH = process.env.BRANCH ?? "a805ba99-ef62-4685-a1ad-b11b2ea7747f";
// A TENANT ADMINISTRATOR, AND SA_USER MUST NOT REACH IT (fixed 2026-09-22).
//
// This read process.env.SA_USER, so it worked standalone — where nothing sets it and the default
// applies — and BROKE the moment class-teacher-e2e.sh ran it, because the runner passes
// SA_USER=superadmin. The platform SuperAdmin's tenant CONTEXT is the platform organization, while
// every branch route resolves the organization from the BRANCH. So the parameters list came back from
// one organization and the duty POST looked the id up in another: "Parameter not found. Choose an
// active parameter for this duty", and section 17 aborted at its first setup step.
//
// EXACTLY the trap already recorded for section 21, which signs in as a tenant administrator for this
// reason. The lesson generalises: a suite that is only ever run standalone is not the suite the runner
// runs, and an env var named for one role must not be able to hijack another.
const AD_USER = process.env.AD_USER ?? "e2e.admin.ct@qmgr.local";
const AD_PASS = process.env.AD_PASS ?? "E2eTeacher!2026";
const RUN = Date.now().toString(36);

let pass = 0, fail = 0;
const post = (l) => fetch("http://127.0.0.1:5010/append?key=ui", { method: "POST", body: l + "\n" }).catch(() => {});
const ok = (name, cond, detail = "") => {
  cond ? pass++ : fail++;
  const l = `    ${cond ? "PASS" : "FAIL"}  ${name}${cond ? "" : "  — " + detail}`;
  console.log(l); post(l);
};
const eq = (name, actual, expected) => ok(name, actual === expected, `got ${JSON.stringify(actual)}, wanted ${JSON.stringify(expected)}`);
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const login = async (email, password) => {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  const j = await r.json().catch(() => ({}));
  return j.accessToken;
};

const call = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};

const AD = await login(AD_USER, AD_PASS);
if (!AD) { console.error("could not sign in"); process.exit(1); }
// Adoption is somebody OTHER than whoever wrote the minutes (DutySeparation G4, 2026-09-26): the administrator writes,
// the Director of Studies — who also manages duties — adopts. Section 14 creates the account.
const DO = (await login(process.env.DOS_USER ?? "e2e.sp.dos@qmgr.local", "E2eTeacher!2026")) ?? (await login(process.env.DOS_USER ?? "e2e.sp.dos@qmgr.local", "Rwenzori#Peaks-2026"));
if (!DO) { console.error("could not sign in as the Director of Studies (run section 14 first)"); process.exit(1); }
const me = JSON.parse(Buffer.from(AD.split(".")[1], "base64").toString()).sub;
const B = `/api/v1/branches/${BRANCH}/staff`;

hdr(`17. MINUTES — the lifecycle, the register as attendance, and the adopted record (run ${RUN})`);

// ---------------------------------------------------------------- setup: a meeting with two people
const params = (await call(AD, "GET", "/api/v1/staff/parameters")).json ?? [];
const attendance = (params.items ?? params).find(p => p.isActive && /attendance/i.test(p.name)) ?? (params.items ?? params)[0];

// Section 17.9 needs somebody who does NOT hold staff.duties.manage, or "their edit is refused"
// passes for the wrong reason — several seeded roles (head of department, academic assistant) hold
// it, and the first name in the directory is as likely as not to be one of them.
const allStaff = ((await call(AD, "GET", `${B}/structure/members?pageSize=500`)).json?.items ?? [])
  .filter(s => s.userId !== me);
const plain = allStaff.filter(s => s.roleCode === "teacher" || s.roleCode === "support-staff" || s.roleCode === "viewer");
const staff = [...plain, ...allStaff.filter(s => !plain.includes(s))];
if (staff.length < 2) { console.error("SKIP: fewer than two other staff on this branch"); process.exit(2); }
const [p1, p2] = staff;
console.log(`meeting: ${p1.fullName} (${p1.roleCode}) and ${p2.fullName} (${p2.roleCode})`);

const start = new Date(); start.setHours(start.getHours() - 2);
const end = new Date(start); end.setHours(end.getHours() + 1);
const created = await call(AD, "POST", `${B}/duties`, {
  parameterId: attendance.id,
  title: `MINUTES-E2E ${RUN} staff meeting`,
  location: "Staff room",
  startsAt: start.toISOString(),
  endsAt: end.toISOString(),
  expectedUserIds: [p1.userId, p2.userId],
  recorderUserIds: [me],
  kind: "Session",
});
eq("SETUP: a meeting was created", created.status, 201);
const dutyId = created.json?.id;
if (!dutyId) { console.error(created.text?.slice(0, 300)); process.exit(1); }

// ---------------------------------------------------------------- 17.1 nothing yet
hdr("17.1 A meeting with no minutes");
const empty = await call(AD, "GET", `${B}/duties/${dutyId}/minutes`);
eq("17.1a: the minutes read back (200)", empty.status, 200);
eq("17.1b: …as None", empty.json?.status, "None");
ok("17.1c: the template comes from the policy, not from nowhere", (empty.json?.template ?? []).length > 0,
  `${(empty.json?.template ?? []).length} sections`);
eq("17.1d: attendance is zero-marked, not invented", empty.json?.attendance?.present, 0);
eq("17.1e: …and says the register is still open", empty.json?.attendance?.registerClosed, false);

// ---------------------------------------------------------------- 17.2 the register IS the attendance
hdr("17.2 Attendance comes from the register and is never retyped");
const submitted = await call(AD, "POST", `${B}/duties/${dutyId}/register`, {
  close: true,
  entries: [
    { userId: p1.userId, outcome: "Present" },
    { userId: p2.userId, outcome: "Excused", note: "Away on a course" },
  ],
});
eq("17.2a: the register was taken and closed", submitted.status, 200);

const afterRegister = (await call(AD, "GET", `${B}/duties/${dutyId}/minutes`)).json;
eq("17.2b: present follows the register", afterRegister?.attendance?.present, 1);
eq("17.2c: Excused reads as apologies, which is what minutes call it", afterRegister?.attendance?.apologies, 1);
eq("17.2d: nobody is absent", afterRegister?.attendance?.absent, 0);
eq("17.2e: the register is now closed, so the figures are final", afterRegister?.attendance?.registerClosed, true);
ok("17.2f: and the names are carried, not just the counts",
  (afterRegister?.attendance?.presentNames ?? []).length === 1 && (afterRegister?.attendance?.apologyNames ?? []).length === 1,
  JSON.stringify(afterRegister?.attendance));
eq("17.2g: quorum is not tracked unless the tenant asked for it", afterRegister?.attendance?.quorumRequired, null);

// ---------------------------------------------------------------- 17.3 writing the draft
hdr("17.3 The draft: sections, motions and actions");
const due = new Date(); due.setDate(due.getDate() + 7);
const saved = await call(AD, "PUT", `${B}/duties/${dutyId}/minutes/content`, {
  sections: [{ key: "agenda", body: `Agenda for run ${RUN}: fees, the timetable, and any other business.` }],
  decisions: [{ text: "That the term's fees structure be adopted as presented.", movedBy: "The Head", secondedBy: "The Bursar", outcome: "Carried", votesFor: 9, votesAgainst: 1, abstentions: 2 }],
  actions: [
    { text: `Circulate the fees structure to parents (${RUN})`, assignedUserId: p1.userId, dueAt: due.toISOString(), status: "Open" },
    { text: "Board of Governors to review the bursary policy", assignedUserId: null, status: "Open" },
  ],
});
eq("17.3a: the draft saved (200)", saved.status, 200);
eq("17.3b: …and is a Draft", saved.json?.status, "Draft");
eq("17.3c: the motion is kept with its mover and its vote", saved.json?.decisions?.[0]?.votesFor, 9);
eq("17.3d: two actions were recorded", (saved.json?.actions ?? []).length, 2);
ok("17.3e: an action with no person is allowed — a meeting can minute one for a body",
  (saved.json?.actions ?? []).some(a => a.assignedUserId == null), JSON.stringify(saved.json?.actions?.map(a => a.assignedName)));

const actionId = (saved.json?.actions ?? []).find(a => a.assignedUserId === p1.userId)?.id;
ok("17.3f: the assigned action has an id of its own (a row, not a line in a blob)", !!actionId, "no id");

const blank = await call(AD, "PUT", `${B}/duties/${dutyId}/minutes/content`, { sections: [], decisions: [], actions: [{ text: "   ", status: "Open" }] });
eq("17.3g: an action with no wording is refused (400)", blank.status, 400);

// ---------------------------------------------------------------- 17.4 it is the assignee's to-do
hdr("17.4 An action is a real to-do for a real person");
const mine = await call(AD, "GET", `${B}/minutes/my-actions`);
eq("17.4a: my-actions answers (200)", mine.status, 200);
ok("17.4b: the recorder has none of these — they were given to somebody else",
  !(mine.json ?? []).some(a => a.id === actionId), JSON.stringify((mine.json ?? []).map(a => a.text)));

// ---------------------------------------------------------------- 17.5 circulate
hdr("17.5 Circulation for correction");
const circulated = await call(AD, "POST", `${B}/duties/${dutyId}/minutes/circulate`);
eq("17.5a: circulated (200)", circulated.status, 200);
eq("17.5b: …and the status says so", circulated.json?.status, "Circulated");
ok("17.5c: it is still editable — circulation is for correction, not adoption", circulated.json?.canWrite === true);

// ---------------------------------------------------------------- 17.6 adoption
hdr("17.6 Adoption is the boundary");
const writerAdopts = await call(AD, "POST", `${B}/duties/${dutyId}/minutes/approve`, {});
eq("17.6a0: whoever wrote the minutes cannot adopt them (403, G4)", writerAdopts.status, 403);

const selfAdopt = await call(DO, "POST", `${B}/duties/${dutyId}/minutes/approve`, { approvedAtDutyId: dutyId });
eq("17.6a: a meeting cannot adopt its own minutes (400)", selfAdopt.status, 400);

const adopted = await call(DO, "POST", `${B}/duties/${dutyId}/minutes/approve`, {});
eq("17.6b: adopted (200)", adopted.status, 200);
eq("17.6c: …and the status is Approved", adopted.json?.status, "Approved");
ok("17.6d: adoption records who and when", !!adopted.json?.approvedAt && !!adopted.json?.approvedByName,
  JSON.stringify({ at: adopted.json?.approvedAt, by: adopted.json?.approvedByName }));
eq("17.6e: an adopted record is no longer writable", adopted.json?.canWrite, false);

const editAfter = await call(AD, "PUT", `${B}/duties/${dutyId}/minutes/content`, { sections: [{ key: "agenda", body: "Rewritten after adoption" }], decisions: [], actions: [] });
eq("17.6f: editing an adopted record is REFUSED (409)", editAfter.status, 409);

const twice = await call(DO, "POST", `${B}/duties/${dutyId}/minutes/approve`, {});
eq("17.6g: adopting twice is refused (409)", twice.status, 409);

const stillThere = (await call(AD, "GET", `${B}/duties/${dutyId}/minutes`)).json;
ok("17.6h: and the refused edit changed nothing", (stillThere?.sections ?? []).some(s => s.body?.includes(RUN)),
  JSON.stringify(stillThere?.sections));

// ---------------------------------------------------------------- 17.7 corrections are append-only
hdr("17.7 A correction is appended, never applied");
const tooShort = await call(AD, "POST", `${B}/duties/${dutyId}/minutes/corrections`, { text: "typo" });
eq("17.7a: a correction needs ten characters (400)", tooShort.status, 400);

const corrected = await call(AD, "POST", `${B}/duties/${dutyId}/minutes/corrections`, {
  text: "Item 2: the vote was 9 for and 2 against, not 9 and 1.",
});
eq("17.7b: the correction was added (200)", corrected.status, 200);
eq("17.7c: …as a correction, one of them", (corrected.json?.corrections ?? []).length, 1);
ok("17.7d: it carries who made it and when",
  !!corrected.json?.corrections?.[0]?.byName && !!corrected.json?.corrections?.[0]?.at,
  JSON.stringify(corrected.json?.corrections?.[0]));
eq("17.7e: the ADOPTED text is untouched — the correction sits beside it", corrected.json?.decisions?.[0]?.votesAgainst, 1);

// ---------------------------------------------------------------- 17.8 closing an action
hdr("17.8 Closing an action");
const done = await call(AD, "POST", `${B}/minutes/actions/${actionId}/complete`, { note: "Sent by SMS and on the noticeboard." });
eq("17.8a: the action closed (200)", done.status, 200);
eq("17.8b: …and is Done", done.json?.status, "Done");
ok("17.8c: with who closed it", !!done.json?.completedByName, JSON.stringify(done.json));

const afterDone = (await call(AD, "GET", `${B}/duties/${dutyId}/minutes`)).json;
ok("17.8d: the adopted minutes still show it — actions outlive adoption",
  (afterDone?.actions ?? []).some(a => a.id === actionId && a.status === "Done"),
  JSON.stringify(afterDone?.actions?.map(a => [a.text, a.status])));

// ---------------------------------------------------------------- 17.9 who may read it
hdr("17.9 Reading is wider than writing, and 404 never 403");
const missing = await call(AD, "GET", `${B}/duties/${crypto.randomUUID()}/minutes`);
eq("17.9a: an unknown meeting is 404", missing.status, 404);

// The people expected at the meeting can read adopted minutes; a stranger to it cannot even see it exists.
for (const pw of ["E2eTeacher!2026", "Rwenzori#Peaks-2026"]) {
  const t = await login(p1.email, pw);
  if (!t) continue;
  const asAttendee = await call(t, "GET", `${B}/duties/${dutyId}/minutes`);
  eq("17.9b: somebody who was at the meeting reads the adopted minutes (200)", asAttendee.status, 200);
  eq("17.9c: …but cannot write them", asAttendee.json?.canWrite, false);
  eq("17.9d: …and cannot adopt them", asAttendee.json?.canApprove, false);
  const theirEdit = await call(t, "PUT", `${B}/duties/${dutyId}/minutes/content`, { sections: [], decisions: [], actions: [] });
  if (p1.roleCode === "teacher" || p1.roleCode === "support-staff" || p1.roleCode === "viewer") {
    ok("17.9e: their edit is refused as 404, never 403 — a 403 would confirm what they cannot read",
      theirEdit.status === 404, `got ${theirEdit.status}`);
  } else {
    // They hold staff.duties.manage, so the refusal is the ADOPTION rule, not the permission one.
    // Asserting 404 here would be asserting the wrong rule and would pass or fail by accident.
    ok("17.9e: a duty manager is refused by the adoption rule instead (409)", theirEdit.status === 409, `got ${theirEdit.status}`);
  }
  break;
}

// ---------------------------------------------------------------- cleanup
// A duty whose register has been CLOSED cannot be cancelled, by design — the records it produced
// stand, exactly as the welfare ledger has no delete. So this run leaves one meeting and its
// adopted minutes behind, titled with the run id so the database owner can find them.
const cancelled = await call(AD, "DELETE", `${B}/duties/${dutyId}`);
const note = cancelled.status === 204
  ? "    NOTE  the meeting was cancelled and its minutes went with it"
  : `    NOTE  the meeting STAYS ("MINUTES-E2E ${RUN} staff meeting") — its register is closed, and a duty whose register has been taken is never cancelled. Safe for the database owner to remove.`;
console.log(note); post(note);

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

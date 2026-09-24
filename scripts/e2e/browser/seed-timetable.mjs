// A PUBLISHED TIMETABLE IN FORCE TODAY, for a browser suite that needs one (2026-09-23).
//
// Every API suite archives what it publishes, so on the dev branch nothing is in force on any given day, and a
// suite that assumed a live timetable measured the branch's state instead of the product. This is the one home
// for making one: copy the newest version that has `teacherId` and a colleague in it into a one-week draft
// starting today, give each copied lesson's teacher the subject-teacher assignment it needs (without it
// TimetableChecker raises TeacherNotAssigned, a hard clash, and publishing refuses — section 26's lesson), and
// publish. `cleanup()` archives the version (a published one is never deleted), deletes a draft that never
// published, and REMOVES every assignment it added: they grant Teaching-tier access to a class of children.
//
// A live version that already qualifies is used as it is, and nothing is created or cleaned up.
//
//   const tt = await seedLiveTimetable({ API, BRANCH, token, teacherId, label: 'print' });
//   if (!tt.target) → skip, saying tt.reason
//   ...; await tt.cleanup();

export async function seedLiveTimetable({ API, BRANCH, token, teacherId, label = 'seed', days = 6 }) {
  const TT = `${API}/api/v1/branches/${BRANCH}/timetable`;
  const auth = { Authorization: `Bearer ${token}` };
  const json = { ...auth, 'Content-Type': 'application/json' };
  const detail = (id) => fetch(`${TT}/timetables/${id}`, { headers: auth }).then(r => r.json());
  const qualifies = (d) => {
    const ts = new Set((d?.lessons ?? []).map(l => l.teacherUserId));
    return ts.size > 1 && ts.has(teacherId);
  };

  const created = { versionIds: [], published: new Set(), assignments: [] };
  const cleanup = async () => {
    for (const id of created.versionIds) {
      const r = created.published.has(id)
        ? await fetch(`${TT}/timetables/${id}/archive`, { method: 'POST', headers: json, body: '{}' })
        : await fetch(`${TT}/timetables/${id}`, { method: 'DELETE', headers: auth });
      console.log(`    cleanup: ${created.published.has(id) ? 'archived' : 'deleted'} seeded timetable ${id.slice(0, 8)} (${r.status})`);
    }
    for (const id of created.assignments)
      await fetch(`${API}/api/v1/branches/${BRANCH}/class-teachers/${id}`, { method: 'DELETE', headers: auth });
    if (created.assignments.length) console.log(`    cleanup: removed ${created.assignments.length} seeded subject-teacher assignment(s)`);
  };

  // A copy of `sourceId` as a new draft over the given dates, its missing assignments seeded. Exposed so a suite
  // can make a second, overlapping draft (the replace-a-live-version confirmation needs one).
  const copyDraft = async (sourceId, name, from, to) => {
    const r = await fetch(`${TT}/timetables`, {
      method: 'POST', headers: json,
      body: JSON.stringify({ name, copyFromTimetableId: sourceId, effectiveFrom: from, effectiveTo: to }),
    });
    const text = await r.text();
    const draft = r.ok ? JSON.parse(text) : null;
    const id = draft?.timetable?.id;
    if (!id) return { error: `could not copy: ${r.status} ${text.slice(0, 200)}` };
    created.versionIds.push(id);
    const byLesson = new Map((draft.lessons ?? []).map(l => [l.id, l]));
    const wanted = new Map();
    for (const issue of (draft.diagnosis?.issues ?? []).filter(i => i.kind === 'TeacherNotAssigned'))
      for (const lid of issue.lessonIds ?? []) {
        const l = byLesson.get(lid);
        if (l) wanted.set(`${l.teacherUserId}|${l.className}|${l.subjectId}`, l);
      }
    for (const l of wanted.values()) {
      const a = await fetch(`${API}/api/v1/branches/${BRANCH}/class-teachers/subject-teachers`, {
        method: 'POST', headers: json,
        body: JSON.stringify({ className: l.className, userId: l.teacherUserId, subjectId: l.subjectId, periodsPerWeek: 4 }),
      });
      const j = await a.json().catch(() => null);
      if (j?.id) created.assignments.push(j.id);
    }
    return { id };
  };
  const publish = async (id, replace = false) => {
    const r = await fetch(`${TT}/timetables/${id}/publish`, {
      method: 'POST', headers: json, body: JSON.stringify({ acknowledgeSoftClashes: true, note: `e2e ${label}`, replace }),
    });
    if (r.ok) created.published.add(id);
    return r;
  };

  const iso = (d) => d.toISOString().slice(0, 10);
  const from = new Date(), to = new Date(); to.setDate(to.getDate() + days);

  // The live one, when it already qualifies.
  const current = await fetch(`${TT}/current`, { headers: auth });
  if (current.status === 200) {
    const c = await current.json();
    const d = await detail(c.timetable?.id ?? c.id);
    if (qualifies(d))
      return { target: { id: d.timetable.id, name: d.timetable.name }, lessons: d.lessons, seeded: false, from: iso(from), to: iso(to), copyDraft, publish, cleanup };
  }

  // Otherwise a copy of the newest version that has this teacher and a colleague.
  const versions = await fetch(`${TT}/timetables`, { headers: auth }).then(r => r.json());
  let source = null;
  for (const v of versions.filter(v => (v.lessonCount ?? 0) > 1)
                          .sort((a, b) => String(b.createdAt ?? b.effectiveFrom).localeCompare(String(a.createdAt ?? a.effectiveFrom)))) {
    if (qualifies(await detail(v.id))) { source = v; break; }
  }
  if (!source) return { target: null, reason: 'no version on this branch has this teacher and a colleague in it', copyDraft, publish, cleanup };

  const copy = await copyDraft(source.id, `E2E ${label} ${Date.now().toString(36)}`, iso(from), iso(to));
  if (!copy.id) return { target: null, reason: copy.error, copyDraft, publish, cleanup };
  const pub = await publish(copy.id);
  if (!pub.ok) return { target: null, reason: `could not publish the copy of "${source.name}": ${pub.status} ${(await pub.text()).slice(0, 240)}`, copyDraft, publish, cleanup };

  const d = await detail(copy.id);
  return { target: { id: d.timetable.id, name: d.timetable.name }, lessons: d.lessons, seeded: true, source, from: iso(from), to: iso(to), copyDraft, publish, cleanup };
}

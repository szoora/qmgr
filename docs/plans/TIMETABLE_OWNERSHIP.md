# Who owns a timetable

**Status: BUILT 2026-09-22, all seven phases, verified live.** Raised, planned and built the same day.
The plan below is kept as written; what changed while building it is recorded in §7 at the end, and the
three §5 decisions were answered by the user — all three as recommended, plus a fourth on swaps.

**Verified**: e2e section 26 (`scripts/e2e/timetable-ownership-e2e.mjs`, 53 checks, 0 failed, wired into
`class-teacher-e2e.sh`), plus a live check that cover reaches the materialised lesson duty and reverts on
withdrawal (9 checks — not in the suite, because it has to publish a version covering TODAY and that is
not something to do to a tenant with a live timetable). Sections 15 and 21 re-run for regressions.

> *"is it not dangerous if many users have access to the time table? … one of the teachers is
> appointed as time table master … another staff member can be appointed by admin to write the exam
> supervision time table. so a term can have about 3 time tables … if the time table is opened to all
> administrators with a role, there is a possibility to abuse the feature and make unnecessary
> alterations."*

The concern is correct. Three things were checked in the code before anything below was written, and
one of them is a bigger problem than the one asked about.

---

## 0. What the code actually does today

### 0.1 `timetable.manage` is global, and per-version ownership does not exist

Fifteen endpoints on `TimetableController` gate on `Permissions.TimetableManage` and nothing else.
`Timetable` carries no owner column. `CreatedBy` is written at creation (line 343) and **never read
for authorisation anywhere**.

So any holder of `timetable.manage` may edit, publish or archive **any** version on **any** branch of
the tenant. Among the seeded roles that is Director of Studies and Academic Assistant, plus Tenant
Admin through `AllPermissions`. The school's actual model — *one appointed timetable master* — has no
representation at all.

### 0.2 PUBLISHING A SECOND TIMETABLE OVER THE SAME DATES SILENTLY ARCHIVES THE FIRST

This is the finding that changes the shape of the request. `PublishTimetable`:

```csharp
var overlapping = await Db.Timetables
    .Where(t => t.BranchId == branchId && t.Id != id && t.Status == TimetableStatus.Published
             && t.EffectiveFrom <= timetable.EffectiveTo && t.EffectiveTo >= timetable.EffectiveFrom)
    .ToListAsync();
foreach (var old in overlapping) old.Status = TimetableStatus.Archived;
```

backed by the partial unique index `ux_timetables_branch_from_published`. The entity's own header
states the rule: *"At most one Published version covers any date of a branch."*

**So "about 3 timetables in a term" cannot be three `Timetable` rows.** Publishing the exam-supervision
one would archive the teaching one, and because every lesson query filters
`Status == Published && EffectiveFrom <= day && EffectiveTo >= day`, **every lesson would stop
materialising** — no registers, no teaching figures, no portal timetable card. The archive is
reported in the response, but nobody reading "published" expects the other one to have stopped.

### 0.3 The pattern being asked for already exists in this codebase — on duties, not timetables

`StaffDuty` carries `RecorderUserIds`, `SupervisorUserIds` and `ExpectedUserIds`, and CLAUDE.md
already states the rule:

> **Delegation is not scope.** Taking a register on a `StaffDuty` is authorised by
> `caller ∈ RecorderUserIds` (or `staff.duties.manage`).

A teacher named recorder for one meeting can mark that meeting's register and nothing else. **The
timetable is the outlier**, not the thing needing a new invention.

### 0.4 Expiry works; the status label does not

Every read filters on `EffectiveTo >= today` (`StaffLessons`, `TeachingReportBuilder`,
`TimetableChecker`, both controllers), so a timetable past its end date correctly stops producing
lessons. But `Status` still reads **Published**, so the list shows an expired version exactly as it
shows the live one. The behaviour is right and the reporting is not.

---

## 1. The decision the rest of the plan rests on: which of the three is a `Timetable`?

The school says "timetable" for both. The system should not.

| What the school calls it | What it actually is here | Why |
|---|---|---|
| General teaching timetable | **`Timetable`** | A repeating cycle of teacher × class × subject × period |
| Beginning/mid/end-of-term exam supervision | **A named series of `StaffDuty` rows** | Dated occurrences with invigilators, rooms and times. No cycle, no periods-a-week, no class-subject triple |

`DutyKind.Session` is already documented as *"A meeting, **invigilation** or prep slot"*, and the
seeded **Exam Supervision** parameter is already `Kind = ParameterKind.Duty`. Exam supervision has a
home in this system and it is not the timetable.

**Recommendation: do NOT model exam supervision as a second `Timetable`.** Three reasons:

- It trips §0.2 and stops teaching.
- The timetable's shape is wrong for it — a cycle and a periods-per-week load describe nothing about
  invigilation.
- Duties already carry named people per row, which is most of the ownership being asked for.

What exam supervision genuinely **lacks** is a name to group the forty slots under, and an owner for
that group. That is a much smaller build than a second timetable, and it is Phase 3 below.

---

## 2. What comparable systems do, and what the standards say

**Mainstream school MIS uses role + per-person override, not per-object ownership.** Arbor manages
access through *Business Roles* holding pre-defined permissions, with **ad-hoc permissions** added to
an individual's profile on top; SIMS roles cannot be mapped across directly because the two products
model access differently. So the established sector answer to "only Mr Okello builds the timetable"
is *give Mr Okello the timetable role and nobody else* — which works until a second person legitimately
needs it for a different timetable, which is exactly this school's case.

**The versioning answer is well established and is not about permissions.** Untis's *Multi-week
timetable* module exists to hold irregular and period-limited lessons inside an otherwise regular
timetable, and lets a timetable be divided into time ranges, precisely so that a school does not
publish a second competing timetable when the shape of the week changes for a fortnight. aSc and
Prime validate during placement rather than at the end. Neither product treats "a second timetable"
as the way to express "a two-week exam period".

**The standards point the other way from a single global permission.** NIST SP 800-53 **AC-6 least
privilege** requires users hold no more privilege than the job needs; **AC-5 separation of duties**
exists specifically to address *abuse of authorised privileges* by ensuring no individual has sole
control over a critical activity. And on the RBAC/ABAC question, the practical reading is that the
two compose rather than compete: **an access-control entry can name a role, so per-object control and
centrally-managed membership coexist** — which is precisely the design below.

**Where this project should land.** Per-object ownership is what version control (CODEOWNERS) and
document systems do, and it is the right shape for a small number of long-lived objects each with a
named owner. That describes a school's timetables exactly: three or four a term, each one somebody's
named job. It is also already this codebase's idiom for duties. The sector's role-plus-ad-hoc answer
is weaker here because it cannot say *which* timetable.

---

## 3. The design

### 3.1 Three tiers, and the middle one is new

| Tier | Who | May |
|---|---|---|
| **Read** | anybody in the branch | Read a Published version. **Unchanged** — this carries no permission today and must not start to |
| **Own** | `Timetable.ManagerUserIds` — **new** | Edit, publish and archive **that version**. Needs no `timetable.manage` |
| **Administer** | `timetable.manage` | Create a version, appoint its managers, and override any version — **visibly** (§3.3) |

**The write rule is one line and gets one home**, exactly as `RecorderUserIds` has:

```
caller ∈ timetable.ManagerUserIds  ||  caller holds timetable.manage
```

A teacher appointed timetable master therefore needs **no new role** — which matters, because giving
them `timetable.manage` today would hand them every other timetable in the school as well.

### 3.2 Appointing is an administrator's act, not a master's

`ManagerUserIds` is set by a holder of `timetable.manage` at creation and on a dedicated endpoint.
**A named manager cannot add or remove managers**, including themselves — otherwise the appointment
is self-serve and the control is decoration. Same asymmetry as `RoleAssignmentGuard`.

### 3.3 The administrator override stays, and is never silent

The tempting stronger rule is to stop `timetable.manage` holders editing a version they do not own.
**Do not.** A school whose timetable master leaves mid-term, is ill, or leaves the draft locked cannot
be shut out of its own timetable; a system that can be bricked by one person's absence will be worked
around with a shared login, which is worse than the problem.

So the override remains, and the *abuse* concern is answered by visibility rather than refusal:

- The write is recorded as an **override** in `ActivityEvent` — the actor, the version, and that they
  were not a named manager.
- **Every named manager is notified**: *"Grace Nakato changed the Term 3 timetable."* Nothing silent.

This is the maker–checker intent of AC-5 without a two-person approval workflow that a school of
forty staff will not run. It also matches this codebase's standing call that a side effect on a
committed write reports rather than refuses.

### 3.4 Publishing over a live timetable REFUSES rather than archives

§0.2 is a real trap independent of ownership, and it is worth fixing on its own.

- Publish **refuses** when another published version overlaps, naming it: *"Term 3 Teaching is
  published for 8 Sep – 5 Dec. Archive it first, or change these dates."*
- The current archive-and-replace behaviour stays available as an explicit, confirmed act
  (`?replace=true`, or a second button reading **Replace the published timetable**), which is the
  genuine case of re-publishing a corrected version.

A silent archive that stops every register in the school should never be the default path.

### 3.5 Expiry is a state, not an inference

Add `TimetableStatus.Expired`, derived on read from `EffectiveTo < today` rather than stored — the
same call as `StaffEmploymentStatus`, which the codebase already derives from dates *"so the two can
never disagree"*. Nothing about lesson materialisation changes; only the label stops lying.

The reminder ladder already exists (`IReminderLadderService`), so *"Term 3 Teaching expires in 14
days and no version follows it"* is a new `ReminderSubject` and its default stage list, **not a new
job** — the standing rule for that engine.

---

## 4. Phases

**Phase 1 — ownership.** `Timetable.ManagerUserIds` (`Guid[]`, additive migration). The write rule in
one helper, called by all fifteen endpoints. `PUT …/timetables/{id}/managers`, gated on
`timetable.manage`. The editor shows who owns the version and, for a manager, says so plainly.

**Phase 2 — the override is visible.** `ActivityEvent` for a non-owner write; notification to every
named manager. One `NotificationEventKeys` entry.

**Phase 3 — the exam supervision series.** A name and an owner over a set of duties. The smallest
shape that works: `StaffDuty.SeriesName` plus `SeriesManagerUserIds` — or, if the enhance-before-add
rule is applied strictly, reuse `RecorderUserIds` and add only the series name. **This needs the
decision in §5 before it is built**, because it may be a bigger or much smaller piece than it looks.

**Phase 4 — publish refuses to archive silently**, with the explicit replace path.

**Phase 5 — `Expired` as a derived status** and the expiry reminder.

Phases 1 and 4 are independent and either can ship first. Phase 4 is the one that prevents a school
losing its registers, so it is arguably first.

---

## 5. Decisions needed before Phase 3

1. **Is exam supervision a duty series (recommended) or a second `Timetable`?** If the latter, §0.2
   has to be solved properly — concurrent published timetables of different *kinds*, which means a
   `TimetableKind` column, a scoped uniqueness rule, and deciding what a lesson query means when two
   are live. That is a substantially larger build and would be its own plan.
2. **Does the administrator override stay?** §3.3 recommends yes, made visible. The alternative is
   that only named managers may write, with a break-glass path.
3. **May a named manager appoint a co-manager?** §3.2 recommends no.

---

## 6. Verification

- **API suite** — a new section: a named manager writes their own version and is refused on another;
  a `timetable.manage` holder writes both and the second is recorded as an override; a manager cannot
  appoint; a non-manager without the permission reads a published version and cannot write it.
- **Concurrency** — two simultaneous publishes of overlapping versions; exactly one succeeds and the
  other is refused, not silently archived. Node, per the standing rule for any "exactly once" claim.
- **Browser** — the editor as a named manager (controls present, no `timetable.manage`), as an
  administrator (override banner), and as a teacher (read only). A suite must **refuse to run**
  against a branch with one timetable, for the same reason `timetable-views.mjs` refuses a
  single-teacher version: every assertion would pass while proving nothing.

---

## Sources

- Arbor Help Centre — [Managing Business Roles, Permissions and access](https://support.arbor-education.com/hc/en-us/articles/212663965-Managing-Business-Roles-Permissions-and-access-on-MAT-MIS),
  [Introduction glossary for staff access and permissions](https://support.arbor-education.com/hc/en-us/articles/115004446965-Introduction-glossary-for-staff-access-and-permissions),
  [How do we migrate Business Roles from SIMS?](https://support.arbor-education.com/hc/en-us/articles/31857654937885-How-do-we-migrate-Business-Roles-from-SIMS)
- [Untis — Timetable scheduling / Multi-week timetable](https://www.untis.at/en/products/untis-timetable-scheduling)
- [aSc TimeTables — Why to test the timetable?](https://help.asctimetables.com/text.php?id=128&lang=en)
- NIST SP 800-53 — [AC-5 Separation of Duties and AC-6 Least Privilege](https://csrc.nist.gov/files/pubs/sp/800/53/r5/ipd/docs/sp800-53r5-draft-controls-markup.pdf);
  NIST CSF [PR.AA-05](https://csf.tools/reference/nist-cybersecurity-framework/v2-0/pr/pr-aa/pr-aa-05/)
- [RBAC: roles, the NIST model, role explosion](https://www.back4app.com/glossary/role-based-access-control-rbac/) — on ACL entries naming a role so per-object and central membership coexist

---

## 7. What was actually built (2026-09-22)

### The decisions, as answered

1. **Exam supervision is a duty series, not a second `Timetable`.** Taken as recommended. Nothing was
   built for it beyond the publish refusal that makes the alternative impossible to do by accident —
   grouping a set of invigilation duties under a name and an owner is a smaller, separate piece.
2. **The administrator override stays, made visible.** Taken as recommended.
3. **A named manager may not appoint a co-manager.** Taken as recommended.
4. **A FOURTH decision was needed and was not in this plan.** Put to the user after the plan was
   written, when investigating how two teachers agree a swap between themselves:
   *"two teachers have agreed, and both already teach the classes involved — who applies it?"* →
   **still a decider, on the unified rule**; and *"which shapes of swap should this pass build?"* →
   **permanent AND one-off cover**. That is §7.3 and §7.4 below.

### 7.1 Ownership — `Timetable.ManagerUserIds`

`TimetableAccess` is the one home for the write rule; every write endpoint dropped its
`[RequirePermission(TimetableManage)]` attribute and calls `GuardWriteAsync` instead, because an
attribute refuses before the handler runs and so cannot let an appointed master through.
`PUT …/timetables/{id}/managers` appoints, on the permission alone.

**Two things were found while building that the plan did not anticipate**, and both would have made
the feature unreachable rather than merely incomplete:

- **THE STAFF-SCOPE REFUSAL FIRED FIRST.** Every timetable write already refused a caller whose
  `StaffScope` is narrower than the organization — and the seeded `teacher` role is
  `StaffScope.SelfOnly`, which is exactly who a school appoints as its timetable master. So the first
  cut 403'd the appointed master before ownership was consulted at all.
  `TimetableAccess.NeedsUnscopedStaffView` is the fix: **the appointment IS the unscoping**, an
  explicit per-object grant made by somebody holding the permission and an unscoped view. The same
  call this codebase already makes for `StaffDuty.RecorderUserIds` — delegation is not scope.
- **`SetManagers` GAVE THE WRONG REFUSAL.** It checked the scope before the appointment, so a teacher
  trying to appoint a colleague was told *"the timetable is built by an unscoped timetable master"* —
  true of somebody else's situation and silent about theirs. Found by e2e 26.1h. The appointment check
  runs first now.

**Existing versions are backfilled to their `CreatedBy`**, which has been written since the timetable
shipped and never read for authorisation. It grants nobody anything new — creating a version needed
the permission, which already opened every version — and it makes the feature useful on an existing
install instead of needing every version re-appointed by hand.

### 7.2 The override is visible — and publishing no longer archives silently

A non-owner write writes `ActivityActions.TimetableOverridden` and notifies every named manager
(`NotificationEventKeys.StaffTimetableOverride`). The editor says so **before** the write, not after.

`PublishTimetableRequest.Replace` defaults false, and publishing over a live overlapping version now
answers **409 `WOULD_REPLACE_PUBLISHED`** naming what it would take out of service. This was the
plan's §0.2 and is the most consequential change in the file: archiving a live version stops every
lesson materialising school-wide, and it used to happen with a line in the response nobody reads.

### 7.3 A one-day departure — `TimetableLessonException`

`Cover` and `Cancelled`, one per lesson per date (unique index), with a check constraint tying
`CoverUserId` to `Cover`. **There is deliberately no `Moved`**: a one-off swap is TWO COVERS, each
teacher taking the other's lesson at its own time, which is simpler and more honest — a one-off does
not change WHEN a class is taught, only who teaches it, so no class, room or cohort is disturbed.

**The trap, and the reason this needed a live check rather than an API assertion.**
`StaffLessons.MaterialiseAsync` reconciles on `(TimetableLessonId, StartsAt)` — and a cover changes
**neither**. Without an explicit reassignment pass an existing duty is found, left alone, and the
cover silently does nothing for any lesson inside the 14-day window, which is most of them. The pass
keeps the duty's row (its reminder stage, its place in My Day) and changes hands; a duty somebody has
already flagged is never reassigned, because the register was taken.

### 7.4 The swap — and the bug it uncovered

**`ApplyApprovalAsync` for a swap called `CurrentDraftAsync`.** So a mid-term swap — which is *when*
two teachers actually have this conversation, because before term starts the master is still building
the draft — was accepted, agreed by the colleague, approved by a decider, and then refused with
*"there is no draft timetable to change any more"*. Three people acted and the system refused last.
The feature only worked when nobody needed it, and **nothing in the app ever posted `MyLessonId`**, so
the whole journey was reachable only by curl.

- **`ITimetableRepublishService`** applies a permanent swap on a published version: copy → trade →
  `TimetableChecker` → publish over. The date range is KEPT, not split at the swap date, because cycle
  day 1 is anchored on `EffectiveFrom` and moving it would shift every cycle day of an A/B timetable.
- **`PreviewSwapAsync` fills `StaffConfigRequestDto.DecisionWarnings`**, so the decider reads what the
  swap would do to the CLASSES involved before pressing approve. That is where a decider earns their
  place in a swap: not to second-guess the two teachers, but to see what the two teachers cannot.
  Only issues the swap ITSELF introduces are listed — a published timetable routinely carries
  acknowledged soft issues, and listing those back would make every swap look as though it broke
  something.
- **`ConfigRequestKind.LessonCover`** is new: one lesson, one date, a named colleague, nothing in
  return. It needs the colleague's agreement exactly as a swap does — nobody is volunteered to stand
  in front of a class by somebody else.
- **`Components/Admin/Staff/LessonSwapCard.razor`** is the half that did not exist: a teacher's own
  lessons on the live timetable, with *Ask for cover* and *Ask to swap*.

**The decider rule is unified**: `caller ∈ ManagerUserIds || caller holds timetable.manage`, still AND
never the asker. The two rules disagreeing was a hole in both directions — an appointed master could
not decide a swap on their own timetable, and a permission holder could decide swaps on one they had
nothing to do with. A `ClassAssignment` carries no timetable and stays on the permission alone: it
grants Teaching-tier access to a class of children, which is not a timetable master's decision.

### 7.5 `Expired`, and the expiry ladder

`TimetableStatus.Expired` is **derived on read** from `EffectiveTo` (`Timetable.StatusOn`), never
stored — the same call as `StaffEmploymentStatus`. `ReminderSubject.TimetableExpiring` is a ladder on
the existing engine (14 days, 3 days, the day itself), claimed with a conditional update on
`Timetable.ReminderStage`, and it **skips a version that already has a successor published** — a
ladder that fires at a school which has already built next term's timetable is one people learn to
ignore. `ReminderAudience.TimetableManagers` is new and distinct from `TimetableMasters`: the point of
appointing somebody is that the chase goes to them.

### 7.6 What was NOT built, stated rather than implied

- **The exam-supervision duty series** (§1's recommendation). Nothing groups a set of invigilation
  duties under a name and an owner yet; the publish refusal only stops a school reaching for a second
  `Timetable` instead.
- **A dated split of a timetable's date range.** A permanent swap replaces the whole version rather
  than starting a new arrangement from a date. A school that genuinely needs "this from the 10th" is
  asking for a split, which shifts cycle-day anchoring and is its own decision.
- **Future cover does not survive a permanent swap.** A re-publish makes new lesson ids and the
  exceptions cascade with the version they were against. Carrying them across by matching teacher,
  slot and class would be a guess, and a wrongly carried cover puts the wrong person in front of a
  class. The count is logged; the people affected were told when it was withdrawn.
- **No browser suite.** The e2e is API-level plus the live cover check. The editor's appoint dialog,
  the override banner, the replace confirmation and `LessonSwapCard` were built and compile clean but
  have not been driven in a browser.

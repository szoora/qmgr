# Duty rota, duty reports, subject teachers and the timetable — plan

**Status:** BUILDING — Phase 89 in `docs/TASK_TRACKER.md`, started 2026-09-17; all fourteen §15 decisions
taken as proposed. **Written:** 2026-09-17.
**Progress (2026-09-17, late): ALL PHASES BUILT AND VERIFIED** — 0A and 0–6, one migration per phase that needed one (`AddStaffOnboarding`, `AddSubjectsAndTieredAssignments`, `AddDutyRota`, `AddDutyReports`, `AddTimetable`, `AddLessonDutyColumns`); final e2e sections 0–14 471 passed, 0 failed, section 15 252 passed, 0 failed (242 before the rooms block below); browser checks for every phase in the user's Chrome. Details are Phase 89 in the tracker.
**Artifact:** https://claude.ai/artifact/S6dLKnBX4o6SscUUd8WgpY
**Module:** Welfare & Performance (`student-welfare`) — the staff side it extends already lives there.

Requested as: a **duty rota** (a staff member on duty for a variable period — a day, a week, a month),
with **duty reports** written daily / weekly / monthly and read and **commented on** by administrators;
**reminders before a duty starts that grow more urgent as it approaches**, and reminders to write a
report that has not been written; an **administrator on duty** who supervises the teacher on duty and
writes reports the same way. Then the **class teacher vs subject teacher** hierarchy over classes split
into **streams** (S2A, S2B, S2C), student visibility gated to the teacher's own classes, discipline and
welfare visible to the class teacher but not the subject teacher; **subjects taught** per teacher per
class; **lesson scheduling** with reminders and a **daily task list**; a **timetable** built by a
timetable master with **clash and duplication detection** that notifies them; lessons **flagged taught,
missed, compensated**; and **teaching-load and lessons-taught reports**. Everything built on the existing
infrastructure and shared component library, and information dissemination **tightly secured, with no
leakage**. Added during planning: **onboarding staff** onto the system — an Excel import with a default
password changed at first sign-in, and self-registration approved by the organization's administrator,
who assigns the role (§12).

**The one-paragraph version.** Almost every part of this already has a home in the codebase, and the plan
is mostly about *widening* those homes rather than building new ones. A rota slot, a lesson and a meeting
are all "something a named member of staff is expected at, with an outcome" — that is `StaffDuty`, which
already has expected people, recorders, a register, reminders, a chase, the portal's *Coming up*, the
activity log and the scoring behind it. A duty report is the one genuinely new record, and it follows the
append-only record-plus-notes shape that welfare and staff records already use. The subject teacher is a
second kind of `ClassTeacherAssignment`, and the security work is making the student scope service tell
the two kinds apart *so that nothing can forget to*. The timetable is two small tables whose lessons
materialise into `StaffDuty` rows, which gives lesson reminders, the day's task list, taught / missed /
recovered flags, recovery offsets and every existing report for free. The genuinely new infrastructure is
three pieces: an **escalating reminder ladder** (one engine for duties, reports and lessons), a
**week-grid component**, and a **clash checker**. Onboarding extends the existing staff import rather than
replacing it — with one change to the request: **no shared default password**; each person gets their own
one-time temporary password (or the existing invitation link), forced to change at first sign-in — and adds
a join link whose applicants wait, unable to sign in, until an administrator approves them and assigns a role.

---

## 1. What the sources say the tool should be

Full references are in §16. The rules below shape the data model and are stated first.

1. **A duty rota is a required, displayed school record in Uganda, and the teacher on duty is in charge
   of the day's routine.** MoES's *Basic Requirements and Minimum Standards* (BRMS) list a "Staff Duty
   Roster book", a displayed duty roster and a "Log Book for major events" among the records every school
   keeps, and put the teacher on duty in charge of assembly. The rota and a log that management reads are
   therefore not extras; they digitise records a school is already required to keep.
2. **Supervision is a chain: record → analyse weekly → head acts.** MoES's *Performance Management
   Guidelines* (2020) send the weekly lesson register to the deputy, who analyses it and submits it to the
   head, who acts and files. The administrator on duty supervising the teacher on duty, and a head reading
   both, is the same chain.
3. **Duties are workload, and workload has limits.** The DfE's *School Teachers' Pay and Conditions
   Document 2025* caps directed time (1,265 hours), protects a midday break and says cover should be rare
   and unforeseeable. Timetabling products count duties toward load and set a maximum duty load per teacher
   (Timetabling Solutions; aSc min/max supervisions). The rota must show fairness — who has done how many
   duties this term — the way Arbor publishes cover statistics per member of staff.
4. **Lesson attendance has an established Ugandan vocabulary.** The MoES register and its weekly summary
   use *lessons taught*, *lessons not taught*, *absent with / without permission*, and a *Lesson Recovery
   Schedule* where "lessons recovered should not be considered as lessons missed". Secondary teachers
   teach 18–24 periods of 40 minutes (World Bank / MoES CURASSE report); MoES expects 20–24 lessons a week
   (reported by *The Independent*, 2025, with 54% of payroll teachers found under-loaded). TELA already
   monitors teacher attendance and timetable implementation nationally, so the record must be compatible
   with it, not a rival to it.
5. **Being at school is not being in class.** The World Bank's Service Delivery Indicators found only 40%
   of public primary classrooms had a teacher present and teaching, and much absence was sanctioned. So a
   lesson flag is separate from staff attendance, and a missed lesson records *why* and *who approved it*.
6. **Timetabling is a constraint problem with named hard and soft constraints.** The International
   Timetabling Competition's XHSTT format names *Avoid Clashes*, *Avoid Unavailable Times*, *Limit Idle
   Times*, *Limit Workload* and *Spread Events*, each hard or soft with a weight, and ranks solutions on hard
   breaches first. Untis re-diagnoses a timetable after every manual edit; FET's known weakness is that it
   does not say which constraints conflict. **So: check on every edit, report each breach by constraint and
   by resource, never let a hard clash publish.**
7. **Scope is permission × relationship, and a subject teacher is not a pastoral lead.** KCSIE 2026 limits
   safeguarding files to "those who need to see it"; the Information Sharing guidance says share no more
   widely than necessary and record what was shared, why and with whom. Arbor gates safeguarding alerts to
   "my students" by a separate permission; CPOMS lets any staff member *log* a concern but not *search*
   for one. Uganda's Data Protection and Privacy Act 2019 requires data that is "adequate, relevant and not
   excessive" (s.3) and treats health data as special (s.9). **So: a subject teacher sees the students they
   teach at a teaching tier, can report a concern, and cannot read welfare or discipline history.**
8. **Reminders escalate, stop on acknowledgement, and do not fatigue.** Alert-fatigue research (AHRQ
   PSNet) says only severe alerts should interrupt; Google's SRE book says every page must be actionable;
   NN/g says combine bursts; a randomised trial (Fitz, Kushlev et al., 2019) found batching notifications
   three times a day lowered stress while no notifications raised anxiety. PagerDuty escalates on a timeout
   and stops when someone acknowledges. Apple's guidelines reserve *time-sensitive* for the next hour.
   Ireland's right-to-disconnect code says hold non-urgent messages to the next working start. **So: a
   ladder of stages that grows more urgent, stops the moment the task is done, collapses missed stages, and
   respects quiet hours except for the final stage.**
9. **Content stays behind the login.** NHS England guidance on messaging warns about content shown on
   locked screens and prefers "your results are ready" with a link. OWASP Top 10 A01 and ASVS 5.0 require
   deny-by-default, record ownership checks against IDOR, field-level access control, enforcement on the
   server, and authorisation changes applied immediately. **So: email, SMS and push carry a prompt and a
   link, never a child's name or a report's content.**

---

## 2. What already exists, and what is reused

Checked against the working tree on 2026-09-17.

| Need | Exists as | Reuse |
|---|---|---|
| Something a named person is expected at, with an outcome | `StaffDuty` (expected + recorders, register, `ReminderSentAt`, `RegisterChaseSentAt`, minutes in the Library, duplicate) | **Widen**: a `Kind` (Session / Rota / Lesson) and a few nullable columns. Rota slots and lessons become duties. |
| Outcome of being expected | `StaffPerformanceRecord` via the register, `DutyOutcome` (Present, Late, Absent, Excused, Recovered, Completed, NotCompleted) | Lesson flags and rota close-out write the same records. |
| Missed-then-recovered | `PerformanceParameter.OffsetsParameterId`, Lesson Recovery → Lesson Attendance, scoring offset | A recovery lesson is a Lesson duty that offsets a missed one. |
| Append-only record + notes + evidence | `StaffPerformanceRecord` + `StaffPerformanceNote` + `StaffPerformanceAttachment`; `WelfareRecord` + `WelfareNote` | Same shape for the duty report and its comments. |
| Acknowledgement without a table | `StaffNotice.Acknowledgements jsonb`, atomic `||` update guarded by `jsonb_exists` | "I have seen my duty" on `StaffDuty`. |
| Class-to-teacher link with history, rename-safe | `ClassTeacherAssignment` (`Role` ClassTeacher / Assistant, ended-not-deleted), renames moved in `UpdateVocabularies` | **Widen**: a `SubjectTeacher` role plus `SubjectId`. |
| "Which students may this caller see" | `IStudentScopeService` (fails closed, 404, per-request memo) | Becomes **tiered**: pastoral classes vs teaching classes. |
| "Which staff may this caller see" | `IStaffScopeService` (Organization / AssignedDepartments / DirectReports / SelfOnly) | Unchanged; gates duty reports and lesson flags about other staff. |
| Class vocabulary | `Branch.Settings` → `BranchVocabulariesDto.Classes` (`VocabularyItemDto`) | **Widen**: an optional `Level` ("S2") on the item; a `Rooms` list. |
| Departments, heads, line managers | `Department`, `User.DepartmentIds`, `User.LineManagerUserId` | Subjects belong to departments, so heads see their subjects' teaching load. |
| Tenant policy with one reader and a lock | `Organization.Settings["StaffPerformance"]` via `IStaffPerformancePolicyService`, `WithPolicyLockAsync` | Reminder ladders, quiet hours, report cadence and template, load norms. |
| Notifications | `CreateInAppNotificationAsync`, `NotificationEventKeys`, preferences, Hangfire dispatch, `NotificationLog`, SignalR `user-{id}` | New event keys only. |
| Timestamp-gated sweeps, branch-local time | `StaffPerformanceJobs`, `AppointmentJobs`, `WelfareReminderJob` | One new ladder sweep plus timetable integrity and lesson generation. |
| Scheduled digest | the weekly staff digest (`LastStaffDigestSentAt`) | Becomes the daily "My Day" digest carrier. |
| Evidence upload gated per file | `UploadOwnerKind`, `UploadAuthorizer`, `UploadLinks.Sign/Strip`, `QFileUpload` | One appended kind for report attachments. |
| Activity log with rung | `ActivityEvent.Visibility`, `IActivityLogger`, subject-trail filter | Every new write and every report read. |
| Print, export, publish | `QPrintSheet`, `QDataExport` (+`ExportResult.Format`), `reportPublish.js`, `POST …/activity/exports` | Rota sheet, report pack, timetables, load reports. |
| Reports with scoped banner | `StaffReportBuilder`, `ScopedToDepartments` | Teaching load and lessons taught/missed/recovered panels. |
| Components | `QTimeline`, `QStatTile/Row`, `QBarList`, `QTabs`, `QChip`, `QEmptyState`, `QActivityLog`, `QFilterBar`, `QAvatar`, `QPager`, `QDatePicker` (+`.q-datetime-pair`), `QDateRangePicker`, `QLibraryPicker`, `QMultiSelect`, `QFileUpload`, `QModal` | All of them. |

**What does not exist, confirmed:** a rota or recurrence (a duty is capped at 14 days); a supervisor on a
duty; a duty report; a comment thread on anything but records; a reminder with more than one stage; a
Subject; a stream or year level on a class; a subject-teacher link; a timetable, bell schedule, period or
room; clash detection for a person, class or room; a week or calendar grid component (the only one is
page-local markup in `Content/Schedules.razor`).

**Four findings to act on regardless:**

- **The duty reminder never reaches a recorder who is not also expected** (`StaffPerformanceJobs`), and
  **nothing stops a recorder marking themselves Present** on a register. Both matter more once lessons are
  duties; both are fixed in Phase 0.
- **`IStudentScopeService.GetClassNamesAsync` and `WelfareAlertService` read every live assignment
  regardless of its `Role`.** Adding a subject-teacher role without changing them would silently give
  subject teachers welfare access and welfare alerts. Phase 0 removes the untyped method so the compiler
  finds every caller.
- **The `teacher` role's `DataScope` is `Organization`.** It holds no student permission today, so nothing
  leaks; granting it `students.view` as it stands would expose the whole school. It moves to
  `AssignedClasses` before any student permission is added.
- **`Branch.Settings` is read-modify-written without a lock** (noted in `AppointmentsController`). The
  bell schedule and rooms will live there, so their writer takes the advisory-lock pattern the staff
  policy already uses.

---

## 3. The model

```
Subject ───────────────┐                          Branch.Settings: Classes (+Level), Rooms, BellSchedule
                       │
ClassTeacherAssignment (ClassTeacher | Assistant | SubjectTeacher + SubjectId + PeriodsPerWeek)
                       │
Timetable (version: Draft → Published → Archived, for a period)
   └─ TimetableLesson (cycle day, period, class, subject, teacher(s), room)
          │  materialised, rolling 14 days
          ▼
StaffDuty ──Kind: Session (meetings, invigilation — today) | Rota (on duty for a span) | Lesson
   │   expected, recorders, supervisors, acknowledgements, reminder stage, series, recovery link
   ├─▶ register ──▶ StaffPerformanceRecord (taught / missed / excused / recovered; rota completed / not)
   └─▶ StaffDutyReport (one per author per report period; Draft → Submitted → Reviewed)
          ├─ StaffDutyReportNote     (comment, response, review, return for changes, reopen)
          └─ StaffDutyReportAttachment (evidence, gated per file)

Reminder ladder (one engine): duty start · report due · lesson start · timetable clash
```

### 3.1 Where each thing lives, and why a new table was not the first choice

Applying the standing rule (nullable column → enum value → array/JSON → table):

| Need | Decision | Why not leaner |
|---|---|---|
| Rota slot, lesson occurrence | **Widen `StaffDuty`**: `Kind` (enum, default Session), `SeriesId uuid?`, `SupervisorUserIds uuid[]?`, `ReportCadence` (enum?), `ReportDueLocalTime` (time?), `Acknowledgements jsonb`, `ReminderStage int`, `TimetableLessonId uuid?`, `ClassName`(100)?, `SubjectId uuid?`, `Room`(60)?, `RecoversDutyId uuid?` | All are "a person expected somewhere, with an outcome"; a separate table would duplicate the register, reminders, portal, scoring and reports. The 14-day cap becomes a per-kind cap (Rota ≤ 92 days, Lesson ≤ 1 day). |
| A rota pattern (rotate a list weekly) | **No table.** The generator writes duties sharing a `SeriesId`; the pattern is not stored | A pattern is an action, not a thing. "Extend the rota" re-runs the generator from the last slot. |
| Duty report | **New table `StaffDutyReport`** | Its own lifecycle (draft, submitted, reviewed, returned), several per duty (one per author per report period), reviewed on its own; no existing row can carry it. |
| Comments on a report | **New table `StaffDutyReportNote`** (append-only, `Kind`) | Per-comment author, time and notification; the notes pattern, not a jsonb list, because a comment is read, notified and audited on its own. |
| Report evidence | **New table `StaffDutyReportAttachment`** + `UploadOwnerKind.DutyReportAttachment` | Kept apart from `StaffPerformanceAttachment` for the same reason welfare evidence is kept apart: `UploadAuthorizer` classifies by "which table points at this file". |
| Subject | **New table `Subject`** (OrganizationId, Name, Code, DepartmentId?, Color, SortOrder, IsActive) | Referenced by id from assignments, lessons and reports, reported on directly — the vocabulary DTO's own rule for "a real table". |
| Subject teacher | **Widen `ClassTeacherAssignment`**: `Role` gains `SubjectTeacher = 2` (appended), `SubjectId uuid?`, `PeriodsPerWeek int?` | Same class-name matching, rename handling, history and coverage. A second table would be a second copy of the class-name rules — this codebase's most repeated bug. |
| Stream / year level | **Widen `VocabularyItemDto`**: `Level` (string?) | "S2A" stays the class; `Level = "S2"` groups streams for reports and timetabling. The Web editor round-trips the field, so the overwrite hazard does not apply. |
| Rooms | **`BranchVocabulariesDto.Rooms`** (JSON, name-matched) | Nothing needs a room by id beyond clash checking, which matches normalised names. |
| Bell schedule, cycle | **`Branch.Settings["Timetable"]`** via one reader (`ITimetableSettingsService`), advisory-locked writes | Per branch, edited as a whole, read as a whole. |
| Timetable version | **New table `Timetable`** (BranchId, Name, PeriodKey, Status, PublishedAt/By, CycleDays) | Draft and published sets must coexist while the master edits; a status column on lessons cannot express "the published one". |
| Timetable lesson | **New table `TimetableLesson`** (TimetableId, CycleDay, PeriodKey, ClassName, SubjectId, TeacherUserId, Room, GroupId?) | The template a term repeats; one row per teacher so a unique index can enforce *teacher* clashes in the database. |
| Lesson flag source | **`RecordSource.SelfReport`** appended | A teacher's own "taught" is marked as such until confirmed. |
| Policies | **`StaffPerformancePolicyDto`** gains `ReminderLadders`, `QuietHours`, `DutyReportTemplate`, `DutyReportDefaults`, `TeachingLoadNorms`, `LessonReminderMinutes` | One reader, one lock, already there. |

**New tables: six** (`StaffDutyReport`, `StaffDutyReportNote`, `StaffDutyReportAttachment`, `Subject`,
`Timetable`, `TimetableLesson`). **New columns:** twelve on `StaffDuty`, two on
`ClassTeacherAssignment`, plus JSON fields and appended enum values. Nothing dropped; every enum value
appended; every new table has an `IEntityTypeConfiguration`.

### 3.2 Field lists

**`StaffDuty` (widened)** — `Kind` (`DutyKind { Session, Rota, Lesson }`); `SeriesId`; `SupervisorUserIds`
(the administrator on duty; they read the on-duty reports and write their own); `ReportCadence`
(`ReportCadence { None, Daily, Weekly, Monthly, EndOfDuty }`); `ReportDueLocalTime` (e.g. 18:00 branch
local); `Acknowledgements` (userId → timestamp); `ReminderStage` (highest pre-start stage sent);
`TimetableLessonId`, `ClassName`, `SubjectId`, `Room` (lessons); `RecoversDutyId` (a recovery lesson
names the missed one).

**`StaffDutyReport`** (`: BaseAuditableEntity`) — `OrganizationId`, `BranchId`, `DutyId`, `AuthorUserId`,
`AuthorRole` (`OnDuty | Supervisor`), `PeriodStart`, `PeriodEnd` (the day / week / month it covers),
`DueAt`, `Status` (`Draft | Submitted | Reviewed | Returned`), `SectionsJson` (answers to the tenant's
template: e.g. arrival and assembly, attendance, meals, cleanliness, boarding, incidents, recommendations),
`Summary` (text), `LinkedWelfareRecordIds uuid[]` (incidents are **links**, never copied child data),
`Visibility` (`WelfareVisibility`, default Standard; Confidential when the author marks it),
`SubmittedAt`, `ReviewedAt`, `ReviewedByUserId`, `ReminderStage`, `LastReminderAt`. Unique
`(DutyId, AuthorUserId, PeriodStart)`.

**`StaffDutyReportNote`** — `ReportId`, `AuthorUserId`, `Kind` (`Comment | Response | Review | Return |
Reopen`), `Body`, `CreatedAt`. A `Response` only from the report's author; `Review`/`Return` only from a
reviewer.

**`StaffDutyReportAttachment`** — `WelfareAttachment`'s columns against `ReportId`.

**`Subject`** — `OrganizationId`, `Name`, `Code`, `DepartmentId?`, `Color`, `SortOrder`, `IsActive`;
retired, never deleted; `Restrict` on delete.

**`ClassTeacherAssignment` (widened)** — `Role` gains `SubjectTeacher`; `SubjectId` (required when
SubjectTeacher, null otherwise); `PeriodsPerWeek` (planned load). The existing unique "one class teacher
per class" index is `Role = 0` only and is untouched; a new partial unique index prevents the same teacher
holding the same subject in the same class twice.

**`Timetable`** — `OrganizationId`, `BranchId`, `Name`, `PeriodKey` (the term), `CycleDays` (5 for a week,
10 for a two-week A/B cycle), `Status` (`Draft | Published | Archived`), `PublishedAt`, `PublishedByUserId`,
`EffectiveFrom`, `EffectiveTo`. One Published per branch per overlapping date range (partial unique index +
lock).

**`TimetableLesson`** — `TimetableId`, `CycleDay`, `PeriodKey` (from the bell schedule), `ClassName`,
`ClassNameNormalized`, `SubjectId`, `TeacherUserId`, `Room`, `RoomNormalized`, `GroupId?` (a joint lesson
across streams, e.g. an S5 elective, shares a group and is not a class clash with itself). Unique partial
index `(TimetableId, CycleDay, PeriodKey, TeacherUserId)`.

---

## 4. The duty rota

### 4.1 Assigning people to duty

- **Kinds of duty, one page.** `/admin/staff/duties` gains a **Rota** view beside the existing week list:
  a `QWeekGrid` (§9) by day or a month list, each slot showing who is on duty, who supervises, and the
  report status. Nav label stays short: **Duty Rota**.
- **A rota slot** is a `StaffDuty` of kind Rota: title ("Teacher on duty"), span (a day, a week, a month,
  or any custom span up to 92 days), the people on duty (`ExpectedUserIds`), the administrator(s) on duty
  (`SupervisorUserIds`, optional), the report cadence and due time, and the parameter it scores against
  (a Duty-kind parameter, "Teacher on Duty", seeded).
- **Generate a rota** instead of typing it: pick the span length (day / week / month), a start date, a
  number of slots, and an ordered list of staff to rotate through (and optionally supervisors to rotate).
  The generator writes one duty per slot with a shared `SeriesId`, skipping school holidays taken from the
  policy's periods. "Extend" continues the rotation from the last slot. Undo is cancel-the-series (open
  slots only; a slot with a report stays).
- **Clash and fairness checks on assignment**, warnings not refusals: the person is already on an
  overlapping rota slot; on an overlapping Session duty (e.g. invigilation); inactive or on leave; over
  the tenant's maximum rota slots per term; and a fairness strip showing each person's duty count this
  term (Arbor's cover statistics; Timetabling Solutions' maximum duty load).
- **Swaps.** Two people may swap slots with the duty manager's approval; the swap is two expected-list
  edits logged as one activity event and notifies both and the supervisor.

### 4.2 Reminders before the duty — the ladder

One engine (§8) drives every escalating reminder. For a rota slot the default ladder, tenant-editable:

| Stage | When (before start, branch-local) | Channel | Tone |
|---|---|---|---|
| 1 | 7 days | in the weekly/daily digest only | "You are on duty next week" |
| 2 | 3 days | bell | "On duty from Monday" |
| 3 | 1 day, at the policy's morning hour | bell + email | "On duty tomorrow" |
| 4 | 2 hours | bell + email (+ SMS if the tenant and person allow) | time-sensitive: "On duty from 07:00 today" |

- **Acknowledge stops the ladder** for that person ("Seen — I'm on duty"), written atomically into
  `Acknowledgements`. An unacknowledged person at stage 4 also puts one line on the **supervisor's** to-do
  ("Sarah Auma has not acknowledged today's duty"), the PagerDuty-style escalation.
- **Missed stages collapse.** A slot created two days ahead sends stage 3 once, not stages 1–3 in a burst.
- **Quiet hours** (policy, default 20:00–06:00) hold stages 1–3 to the next working start; stage 4 may
  interrupt because it is, by definition, the next hour or two.
- **Rescheduling resets** `ReminderStage` and acknowledgements (today's `ReminderSentAt` reset, widened).

### 4.3 The duty report

- **Who writes:** every person on duty writes their own report for each report period of the slot
  (daily, weekly, monthly or once at the end). The administrator on duty writes a report too
  (`AuthorRole = Supervisor`), covering their supervision of the teacher(s) on duty.
- **What it contains:** the tenant's template (policy), each section a short text or a choice, plus a
  summary. Incidents are **not typed in as free text about children**: the form offers *Log a welfare or
  discipline record* (the existing welfare dialog, subject to the author's own welfare permissions) and
  links the record's id. A reader sees a linked incident only if their own welfare access and class scope
  allow it; everyone else sees "1 linked record you do not have access to". Evidence photos attach through
  `QFileUpload` into the gated store.
- **Draft, submit, review.** Drafts autosave (the `@bind` rule applies). Submit locks the text; a reviewer
  may **Return for changes** with a comment (the author edits and resubmits; the returned version stays in
  the notes) or **Mark reviewed**. A submitted report is append-only thereafter: corrections are
  `Response` notes, as on staff records.
- **Comments:** reviewers comment; the author responds; each comment notifies the other party with a
  content-free message and a link. The thread is `QTimeline`.
- **Where it is read:**
  - the author, always (their own);
  - the supervisor(s) of that duty slot — the teacher-on-duty report;
  - holders of `staff.dutyreports.view` within their staff scope — heads, deputy, director of studies;
  - **not** other teachers on the same duty (each report is its author's) and **not** the teacher on duty
    reading the supervisor's report about them, unless the tenant switches that on;
  - a Confidential report only by holders of `staff.confidential.view` in scope, its author and the
    slot's supervisors.
- **Review queue:** `/admin/staff/duty-reports` — submitted awaiting review, returned, overdue, filter by
  period, slot, author; `QStatRow` of on-time %, overdue, awaiting review.

### 4.4 Reminders to write the report

The same engine, counting from `DueAt` (period end at the due time):

| Stage | When | Who | Channel |
|---|---|---|---|
| 1 | at `DueAt` if not submitted | author | bell |
| 2 | +12 hours | author | bell + email |
| 3 | +24 hours | author, and one to-do line to the slot's supervisor | bell + email |
| 4 | +48 hours | author, supervisor, and one line in the head / DoS daily digest | bell + email |

Stops the moment the report is submitted. A report marked "no duty that day" (a closure or cancellation,
supervisor-approved) stops it too. At the end of the slot, the supervisor **closes the duty** through the
existing register: *Completed* or *Not completed* with a reason, per person — the formative rule from the
staff plan: the outcome is a person's judgement, not an automatic score. Reports on time and reviewed are
shown as evidence beside it.

---

## 5. Class teacher, subject teacher and streams

### 5.1 Streams

`Classes` entries stay the unit students belong to ("S2A"). The class editor gains an optional **Level**
per class ("S2"), which groups streams for reports ("All of S2"), timetable views and bulk assignment. An
auto-fill suggests the level from the leading letters and digits of the name. Renames already move
assignments; they now move `TimetableLesson.ClassName` in the same save, and a class used by a published
timetable counts as in-use for the "cannot be removed — retire it" check.

### 5.2 Subjects and who teaches them

- **`/admin/subjects`** (nav under *Staff Performance → Structure*, label **Subjects**): name, code,
  department, colour. Seeded on first read with the Uganda lower-secondary set (tenant-editable), because
  a school should see a working catalogue before configuring anything.
- **Assigning a subject teacher:** on the class-teachers page, a class now shows its **class teacher**
  (one, plus assistants) and its **subject teachers** (subject → teacher, planned periods a week). A
  teacher's profile and portal show "Mathematics — S2A, S2B (12 periods); Physics — S3A (4 periods)".
- **Coverage** (the existing endpoint, widened): classes with no class teacher (today), **subjects in a
  class's curriculum with no subject teacher**, **subject teachers with no timetabled lessons**, and
  **planned periods ≠ timetabled periods**.

### 5.3 Visibility: the tiered student scope — the security core of this section

`IStudentScopeService` answers per student with a **tier**, not a yes/no:

| Tier | Comes from | May read |
|---|---|---|
| **Pastoral** | a live ClassTeacher or Assistant assignment for the student's class | everything the role's permissions allow for students: roster, guardians, pastoral tier, welfare and discipline at the rungs the role holds |
| **Teaching** | a live SubjectTeacher assignment for the student's class (and no pastoral one) | name, photo, class, subjects the teacher teaches them, their own lesson registers, learning-support notes the tenant marks "shareable with teaching staff" — **no guardians, no welfare, no discipline, no pastoral tier** |
| **None** | neither | nothing (404) |

- **Unscoped roles** (`DataScope = Organization`: admin, DoS, deputy) keep today's behaviour.
- **One user, both tiers:** a class teacher of S2A who also teaches Physics in S3B is Pastoral for S2A
  students and Teaching for S3B students. Tiers are per assignment, never per role.
- **The `teacher` role moves to `AssignedClasses`** and gains `students.view` only; welfare permissions
  stay off it. The `class-teacher` role keeps its welfare permissions, which now apply to its **pastoral**
  classes only.
- **Reporting a concern is not reading one** (CPOMS's model): a subject teacher holding `welfare.create`
  (tenant option, default on) can log a concern about a student they teach; the record forces at least
  Confidential if it is a welfare case (existing rule); they keep read access to what they wrote (the
  author rule), and nothing else.
- **Server-side, field-level:** `StudentsController.MapToDto` gains the Teaching tier and blanks fields
  before serialising; `WelfareController` rejects a Teaching-tier caller with 404 on every
  `{studentId}` route; list endpoints filter by tier.
- **Removing the foot-gun:** `GetClassNamesAsync` (tier-blind) is **deleted** and replaced by
  `GetPastoralClassNamesAsync` and `GetTeachingClassNamesAsync`, so every existing caller becomes a
  compile error that must choose. `WelfareAlertService` notifies pastoral assignments only.
- **Welfare reports** stay pastoral: `ScopedToClasses` lists pastoral classes; a Teaching-only caller has
  no welfare reports at all.

---

## 6. The timetable

### 6.1 Setting up

- **Bell schedule** (per branch, `/admin/timetable/settings`): day types (e.g. Monday–Friday, Saturday
  morning), periods per day type with start and end (default 40 minutes, the Ugandan norm, editable),
  breaks and assembly as non-teaching periods, and the cycle (5-day week or 10-day A/B).
- **Rooms** (vocabulary): name, optional capacity and type (lab, general); name-matched like classes.
- **Load norms** (policy): lessons a week per teacher, default 20–24, minimum 18 for a teacher on both
  A- and O-Level, editable; maximum periods a day; maximum consecutive periods.

### 6.2 Building it — the timetable master

- **Who:** holders of `timetable.manage` (granted to Director of Studies and Academic Assistant by default;
  a school may create a "Timetable Master" custom role in the role editor). **Unscoped by design**, and
  refused to a scoped caller at the controller, because publishing materialises lessons for everyone in a
  background job (the standing rule for bulk work a job carries out).
- **Where:** `/admin/timetable` (nav **Timetable**): a Draft per term; views by class, teacher and room on
  `QWeekGrid` (cycle days × periods); a side list of **unplaced load** (each class-subject-teacher's planned
  periods minus placed ones).
- **Placing a lesson:** pick a cell, pick class-subject-teacher from the unplaced list, pick a room. Each
  save runs the **clash checker** and returns the draft's full diagnosis; the grid colours conflicting
  cells and the side panel lists each breach **by constraint and by resource** (the XHSTT vocabulary):
  - *Hard* — teacher double-booked; class double-booked (unless a joint group); room double-booked;
    teacher unavailable (declared availability, or on an overlapping Session duty); lesson outside the bell
    schedule; teacher inactive or no longer assigned the subject.
  - *Soft* — teacher over the weekly or daily maximum, or under the minimum; more than N consecutive
    periods; the same subject twice in a day for a class; idle gaps; planned ≠ placed periods.
- **Concurrency:** every write takes `pg_advisory_xact_lock` on the timetable; the teacher clash is also a
  unique index, so two masters editing at once cannot double-book in the database even if both checks
  passed.
- **Publish:** refused while any hard clash remains (no override); soft clashes shown and acknowledged with
  a note. Publishing archives the previous version for overlapping dates, notifies every teacher with
  lessons ("Your Term 3 timetable is published", link only), and enqueues lesson materialisation.
- **No automatic solver in v1.** A solver is a large dependency and a separate project (the FET and aSc
  class of software); the manual builder with live diagnosis is what schools that own FET or aSc also do
  after import. An import from a CSV export (e.g. from aSc or FET) is Phase 6, through the existing
  roster-import job machinery.

### 6.3 Keeping it right — the integrity sweep

A daily sweep (`TimetableIntegrityJob`) re-diagnoses every published timetable against the world as it now
is — a teacher deactivated, an assignment ended, a class renamed or retired, a Session duty placed over a
lesson, a room retired — and notifies the timetable masters once per new breach ("2 new timetable clashes
in Main Branch", link only), gated by a stored hash of the last reported set so an unchanged clash is not
re-announced.

---

## 7. Lessons in the day

### 7.1 Materialising lessons

A job (`LessonGenerationJob`, nightly and on publish) writes the next 14 days of `StaffDuty` rows of kind
Lesson from the published timetable: expected = the teacher, recorders = the teacher and lesson supervisors
(holders of `timetable.lessons.flag` in scope), parameter = Lesson Attendance, dates from the cycle and the
bell schedule, skipping holidays and closures. Idempotent on a unique `(TimetableLessonId, date)`. A
cancelled lesson (a school event) is a cancelled duty with a reason.

### 7.2 Reminders and the daily task list

- **My Day** (portal, top; nav **My Day** for teaching staff): today's lessons in order (period, time, class,
  subject, room, status), duties, rota slot, reports due, recovery lessons owed, welfare actions owed. A
  compact phone-first list, with **Taught**/**Not taught** buttons on each past lesson.
- **Morning digest** at the policy hour (default 06:30, branch-local): one bell item and optional email,
  "Today: 6 lessons, on duty, 1 report due" — one message, not six (NN/g's batching rule).
- **Before each lesson:** one in-app, time-sensitive reminder at the policy's minutes (default 10), per-user
  switch, **off by default for SMS and email** (alert fatigue: the digest already carries the day).
- **Changes:** a lesson moved or cancelled after the digest sends one bell item to the teacher ("Period 3
  S2A Mathematics moved to Lab 2").

### 7.3 Flagging lessons: taught, missed, compensated

Using MoES's own vocabulary and the existing outcomes:

| Flag | Outcome | Who | Notes |
|---|---|---|---|
| Taught | Present (or Late) | the teacher (self-report), or a lesson supervisor | a teacher's own mark is `Source = SelfReport`; a supervisor's confirmation or override supersedes it (annul-and-rewrite, as registers do) |
| Missed — with permission | Excused | supervisor | reason required; outside the attendance denominator (MoES: organisational factors are not a gap) |
| Missed — without permission | Absent | supervisor | reason recorded |
| Compensated (recovered) | Recovered on the recovery lesson | the teacher schedules; a supervisor confirms | a recovery is a Lesson duty with `RecoversDutyId`; when marked taught it offsets the missed lesson (existing offset) |
| Not recovered | — | derived | missed with no taught recovery by the policy's recovery deadline |
| Not recorded | — | derived | no flag by end of day: shown as *unrecorded*, never silently as taught or missed |

- **Integrity:** the register already refuses marks for people not expected; Phase 0 adds that a recorder
  cannot mark **themselves** except on a Lesson duty, where it is recorded as a self-report. Supervisors
  are scoped (a head of department flags their department's lessons).
- **Chase:** unrecorded lessons at the end of day go into the teacher's next digest, not a bell per
  lesson; lessons still unrecorded after the policy window go to the supervisor's weekly analysis
  (MoES's deputy-analyses-weekly step).

---

## 8. Reminders, notifications and the escalation engine

### 8.1 One engine

`IReminderLadderService` + `ReminderLadderJob` (every 15 minutes): for each subject type (duty start,
report due, lesson start, timetable clash), the policy defines stages `{ offset, channels, audience,
interruptive }`. A row stores the highest stage sent (`ReminderStage`). The sweep computes the stage that
*should* have been sent by now, sends only that one if it is higher (**collapse**), applies quiet hours to
non-interruptive stages, and writes the stage — under a conditional `UPDATE … WHERE ReminderStage < @stage`
so two workers can never send the same stage twice (the notice fan-out pattern). **Done or acknowledged
stops it**; rescheduling resets it. Existing single-shot reminders (duty, register chase) move onto it.

### 8.2 Event keys (wire format, added to `NotificationEventKeys`)

`staff.rota-assigned`, `staff.rota-reminder`, `staff.rota-unacknowledged` (supervisor),
`staff.duty-report-due`, `staff.duty-report-overdue` (escalated), `staff.duty-report-submitted`
(reviewers), `staff.duty-report-comment`, `staff.duty-report-returned`, `staff.lesson-reminder`,
`staff.my-day`, `staff.lesson-changed`, `staff.lesson-unrecorded`, `staff.timetable-published`,
`staff.timetable-clash` (masters). Each with a name, category and email default (digest and escalations
on; lesson reminders bell only).

### 8.3 What a message may say

Every notification about a report, a comment, a lesson flag or a clash **names the thing and links to it;
it never carries content**: "A comment was added to your duty report for Mon 22 Sep" — not the comment;
"Your report for 22 Sep is overdue" — not the incidents; "2 new timetable clashes" — not the names. SMS is
used only for stage 4 duty reminders and carries no student names ever. Messages are sent per person
(never a group address), and delivery is in the existing `NotificationLog`.

---

## 9. Shared components: consolidate, then build

| Component | Status | Used by |
|---|---|---|
| **`QWeekGrid`** — rows (periods or hours) × columns (cycle days or dates), cell templates, conflict and highlight states, click and keyboard selection, sticky headers, a phone mode that becomes a per-day list | **new** | timetable (class / teacher / room), rota by day, My Day on desktop; **also migrate** `Content/Schedules.razor`'s page-local week overview onto it |
| **`QMonthList`** — dates grouped by week with slot chips (a month rota on a phone) | **new, small** | rota month view |
| `QTimeline` | reuse | report comments, duty history |
| `QStatTile` / `QStatRow`, `QBarList`, `QTabs`, `QChip`, `QEmptyState`, `QFilterBar`, `QAvatar`, `QPager` | reuse | review queue, load reports, rota fairness strip |
| `QFileUpload`, `QLibraryPicker` | reuse | report evidence, minutes |
| `QPrintSheet`, `QDataExport`, `reportPublish.js` | reuse | printed rota, report pack, timetables (landscape), load report |
| `QDatePicker` + `.q-datetime-pair`, `QDateRangePicker` | reuse | spans, due times, ranges |
| `QMultiSelect`, `QSelect` | reuse | rotating staff list, supervisors |

Nothing scrolls sideways (the grid scrolls inside its card and becomes a list below 640px); every date
through `QDateFormat`; every input `@bind`; a scoped caller never sees a button that opens an empty modal.

---

## 10. Screens (nav labels kept short)

**Staff Performance** group: *My Portal* · **My Day** · *Duties* → gains **Duty Rota** · **Duty Reports** ·
*Records* · *Appraisals* · *Reports* · *Structure* (→ **Subjects**) · **Timetable**.

- **My Day** — today's lessons, duties, rota, reports due, recovery owed; Taught / Not taught; next lesson
  countdown.
- **Portal** — *On duty* card (acknowledge, write today's report, comments), *Reports to write*, *My
  timetable* (week grid), *My teaching* (subjects × classes × periods, taught % this term).
- **Duty Rota** — week grid / month list; generate, extend, swap; fairness strip; clash warnings.
- **Duty Reports** — review queue; a report page with sections, linked records, evidence, comments,
  Return / Mark reviewed; print and publish a period's pack.
- **Timetable** — draft editor with diagnosis, publish; class / teacher / room / master views; settings
  (bell schedule, cycle, rooms, availability).
- **Lessons** (inside Timetable) — a day or week of lessons by department with flags, unrecorded, recovery
  owed; bulk confirm self-reports (scoped).
- **Reports** — new panels (§11).

---

## 11. Reports and analysis

All through `StaffReportBuilder`'s shape, scoped with the banner, exportable, printable, publishable:

- **Teaching load** — per teacher: periods a week planned vs timetabled vs the norm band (under / within /
  over), by subject and class; per department; per subject; per level. Flags the MoES 20–24 band and the
  18 minimum for mixed-level teachers.
- **Lessons taught** — per teacher, department, subject, class, level, week: scheduled, taught, missed with
  / without permission, recovered, not recovered, unrecorded, taught % (MoES Annex 4 summary and Annex 5
  recovery schedule, printable in their layout).
- **Weekly lesson analysis** — the deputy's weekly sheet, emailed Monday to holders of
  `timetable.lessons.flag` / `staff.reports.view` in scope (the existing monthly-summary machinery).
- **Duty rota** — slots per person this term (fairness), acknowledgements on time, reports on time,
  overdue, reviewed, returned; supervisors' review turnaround.
- **Timetable health** — hard and soft breaches over time, unplaced load, rooms utilisation.

---

## 12. Onboarding staff: import and self-registration

Asked for mid-plan: bring a school's staff onto the system by importing an Excel sheet of their emails
with a default password (suggested: `staff`) that they change on first sign-in, and let staff register
themselves, pending an administrator's approval and role assignment — each within their own organization
and its own administration. Every feature above depends on people being on the system, so this is Phase 0A.

### 12.1 What exists

- **Staff import** (`StaffImportController`, `RosterImportKind.Staff`, `RosterImportProcessorJob`):
  rows of names, email, username, phone, employee number, job title, role code, department codes and line
  manager email; preview, background processing with live progress, per-row outcomes, refused for a scoped
  caller. Each new account gets a **random unusable password** and, when invitations are on, a **single-use
  reset link valid for 7 days** worded as an invitation (`/reset-password` handles it). Verified in Chrome on
  2026-09-17.
- **Excel parsing in the browser** — SheetJS is already loaded app-wide for roster uploads (no server
  dependency).
- **Password policy** (`PasswordValidationService`), failed-attempt **lockout** (`User.FailedLoginAttempts`,
  `LockoutEnd`), login by email or username, IP rate limiting (per viewer since 2026-09-17).
- **Verification and review machinery** from organization sign-up: email and phone verification codes
  (`RegistrationController`), duplicate detection with one normalisation home (`RegistrationIdentity`),
  and a platform review queue (`RegistrationReviewController`) — the shape a tenant's join-request queue
  follows.
- **The user cap** (402 at `MaxUsersPerBranch`, 250 on Welfare & Performance), the role editor, and
  `IStaffProfileChangeNotifier`.

**What does not exist:** a "must change password at next sign-in" state; a temporary password that
expires; a join link or join code for a tenant; a pending-approval account state; an approval queue for
staff; an onboarding checklist.

### 12.2 The recommendation on a shared default password

**Do not use one default password (such as `staff`) for everyone. Use a unique, random, one-time
temporary password per person — or the existing invitation link — with a forced change at first
sign-in.** The reasons, from the standards:

- **A shared default is the attack.** Everyone in the school knows it, so any colleague — or a student who
  overhears it — can sign in as any teacher who has not yet changed it, and a teacher's account reaches
  class rosters and, for a class teacher, welfare and discipline records. CISA's *Secure by Design* alert
  (2023) urges software makers to eliminate default passwords because they are actively exploited; OWASP
  ASVS 4.0.3 V2.5.4 requires that "shared or default accounts are not present".
- **Initial secrets should be random and short-lived.** ASVS 4.0.3 V2.3.1: system-generated initial
  passwords "SHOULD be securely randomly generated … and expire after a short period of time".
- **A guessable password is refused by rule.** NIST SP 800-63B-4 §3.1.1.2: verifiers "SHALL compare the
  prospective secret against a blocklist" including "context-specific words, such as the name of the
  service, the username". `staff`, the school's name and the username belong on that list.
- **The industry's own bulk tools work this way.** Google Workspace's bulk upload takes a per-user password
  and a *Change Password at Next Sign-In* column; Microsoft Entra's bulk create takes an *Initial password*
  per row and leaves delivery to the administrator.

The user's goal — get a whole staff list signed in quickly, even where not everyone reads email — is kept;
only the secret changes. Three delivery modes, chosen per import:

| Mode | How the person gets in | When to use it |
|---|---|---|
| **Invitation email** (exists) | single-use link, 7 days, sets their own password | staff with working email |
| **Temporary password slips** (new, recommended for schools) | the import generates a **unique readable temporary password per person** (10 characters, no look-alike characters), shows the batch **once** as a printable A4 sheet of cut-out slips (`QPrintSheet`: name, username, temporary password, sign-in address, "expires in 72 hours") for the administrator to hand out; the passwords are never stored in readable form and cannot be shown again (re-issue instead) | staff without reliable email; a staff-meeting onboarding session |
| **Temporary password by SMS** (new, optional) | the same temporary password sent to the person's own phone, one message each, no school or student data in it | where the tenant has SMS enabled |

If a school still wants to type its own temporary password for a batch, the import accepts one **only**
if it passes the password policy and the blocklist (so never `staff`, the school name, or the username),
applies it per batch rather than organization-wide, and still expires it in 72 hours with a forced
change. That option is off by default and the page says why.

### 12.3 First sign-in: the forced change

- **New columns on `User`:** `MustChangePassword` (bool) and `TemporaryPasswordExpiresAt` (datetime?).
  Set by a temporary-password import or an administrator's password reset; cleared by the change.
- **Sign-in with a temporary password** returns a **restricted token** carrying only a
  `password-change` scope, no refresh token. Every endpoint except "change my password" and "sign out"
  refuses it (one authorization rule, not a check per controller). The Web sends the person straight to
  **Set your password**; nothing else loads.
- **Expired temporary password** → sign-in refused with "Your temporary password has expired — ask your
  administrator for a new one", and a line on the administrators' onboarding page. Lockout rules apply as
  normal.
- **The new password** must pass the policy and the NIST blocklist (common and breached passwords, the
  service name, the school name, the username, the temporary password itself).
- **After the change:** an activity event, all other sessions revoked, and the **onboarding checklist**
  on the portal: confirm phone, notification preferences, profile photo, and the tenant's
  *acceptable-use and data-protection notice* — a `StaffNotice` requiring acknowledgement, which already
  records who acknowledged and when.

### 12.4 Self-registration with approval

- **The join link.** An administrator switches on *Staff sign-up* for the organization and gets a join
  link carrying a long random code (`/join/{code}`), with a QR code for the staff-room noticeboard.
  Settings in `Organization.Settings["StaffOnboarding"]` via one reader: the **hash** of the code (the
  code is shown once, like a share link's slug), an expiry, optional **allowed email domains**, the branch
  applicants join, and whether approval is required (always on in v1). **Rotate** invalidates the old link
  at once. The code decides the organization — an applicant can never choose one by id.
- **The application** (`/join/{code}`, public page on `PublicLayout`, no `AuthorizeRouteView`): first and
  last name, email, phone, employee number (optional), job title, the branch if the organization has more
  than one, and their own password (policy + blocklist). **Email is verified by a one-time code** (the
  existing verification machinery) before the request reaches anyone; phone optionally.
- **The pending account.** Created with `IsActive = false` and a new `User.PendingApprovalAt`, the
  organization's lowest role (`viewer`) as a placeholder, no branch-scoped access. Sign-in answers
  "Your request is waiting for your administrator's approval" and issues no token.
- **The approval queue** (`/admin/users/requests`, nav under *Administration → Users*, label
  **Join requests**; permission `users.approve`, new, held by Tenant Admin and Manager): each request with
  its details and **duplicate signals** from `RegistrationIdentity` (same phone, same name, an existing
  account with the same employee number). **Approve** asks for the role, branch, departments, line
  manager — and, once §5 exists, class-teacher and subject assignments — in one form; **Reject** takes an
  optional reason.
- **Rules that keep it safe:**
  - The approver can assign **only roles at or below their own rank**, and never Tenant Admin unless they
    hold the role-assignment permission — the privilege-escalation rule e2e section 13 already guards.
  - Approval re-checks: email verified, request not expired (policy, default 14 days), seat cap (402 with
    the cap named), and the email not taken meanwhile.
  - **Anti-enumeration:** the join page answers the same way whether or not the email already has an
    account in this organization ("If this email can be used, we have sent a code"); a duplicate is
    surfaced to the approver, not the applicant.
  - **Rate limited** per viewer and per join code; many failed codes from one address are logged.
  - **Rejected and expired requests** are deleted after 30 days — the applicant's data is not kept without
    purpose (Uganda DPPA s.3, s.12); the activity event (without the applicant's details) stays.
  - Notifications to approvers are content-free: "2 join requests are waiting" with a link.
- **On approval:** `IsActive = true`, `PendingApprovalAt` cleared, the applicant is emailed "Your account is
  approved — sign in" (and an SMS if enabled), `IStaffProfileChangeNotifier` runs for the assigned role, and
  the onboarding checklist appears on first sign-in.

### 12.5 The import, extended

- **Excel as well as CSV**, parsed in the browser with the SheetJS already loaded; a downloadable template
  (`QDataExport` writes the headers) with the columns in order; per-row validation in the preview.
- **Columns added:** `DeliveryMode` override (optional), `ClassTeacherOf` (e.g. `S2A`), `Teaches`
  (e.g. `MATH:S2A,S2B; PHY:S3A`) — applied when §5 is built, validated against the class vocabulary and
  the subject catalogue.
- **Onboarding status page** (Users → *Onboarding*): who has not signed in yet, whose temporary password
  expired, who has not acknowledged the acceptable-use notice; **Re-issue** (new temporary password or new
  invitation, the old one invalidated) singly or in bulk.

### 12.6 A constraint to know

**`Users.Email` and `Users.Username` are unique across all organizations**, not per tenant. A teacher who
works at two schools on the platform cannot hold both accounts with one email, and a common username
collides across tenants. The join page and the import report this as "already in use"; a proper
multi-organization identity is a separate project (decision 12).

---

## 13. Security: how nothing leaks

This is the checklist every phase is built and tested against. It reuses the rules the codebase already
enforces and adds the new ones.

**Access decisions**
1. **Deny by default, decide on the server, 404 never 403** for anything out of scope (OWASP A01, ASVS
   8.3.1). Every new controller carries `[Authorize]`, `[RequireModule(ModuleCodes.StudentWelfare)]` and a
   per-action permission; every `{studentId}` route uses `VerifyStudentAccess`; every `{userId}` route uses
   `IStaffScopeService`.
2. **Permission × relationship × rung** (NIST RBAC for *what*, attributes for *which*): the role grants the
   action, the assignment tier or staff scope decides the rows, `WelfareVisibility` decides the rung.
   Nothing is granted by rank.
3. **The tiered student scope** (§5.3) replaces the tier-blind method, so a forgotten caller is a compile
   error, not a leak. The Teaching tier is blanked field by field in the mapper, not hidden in markup.
4. **Duty reports**: author; the slot's supervisors; `staff.dutyreports.view` within staff scope;
   Confidential rung as staff records. Not other people on the same duty. Linked welfare records render
   through the welfare access check, so a report can never become a side door to a child's file.
5. **Lesson flags about a teacher** are readable by the teacher and by holders of the flag or reports
   permission whose staff scope covers them — never by peers. Timetables themselves (who teaches what,
   where, when) are operational and readable by all staff of the branch; a student's timetable is not
   exposed to anyone outside the school.
6. **Scope changes apply on the next request** (ASVS 8.3.2): no caching of assignments or tiers beyond the
   request; ending an assignment removes access immediately.

**Writes and jobs**
7. **Scoped callers cannot start bulk work a background job carries out**: rota generation over staff
   outside scope, timetable publish and lesson materialisation, CSV import. Refused at the controller.
8. **Integrity rules**: a recorder cannot mark themselves except a Lesson self-report; a supervisor cannot
   review their own report; a report's author cannot mark it reviewed; a submitted report is append-only.
9. **Races closed at the database**, each with a concurrent assertion in the e2e: report uniqueness per
   author per period (unique index), acknowledgements (atomic jsonb), reminder stages (conditional
   update), timetable edits (advisory lock + teacher-clash unique index), publish (lock + partial unique
   index on Published), rota generation (lock per branch), lesson materialisation (unique per lesson per
   date).
10. **Tenant isolation**: every query filters by organization (and branch), SuperAdmin through
    `VerifyBranchOwnership`; background jobs join the organization explicitly.

**Dissemination**
11. **Content-free notifications** (§8.3), per-person sends, no student names in SMS, lock-screen safe.
12. **Uploads**: `UploadOwnerKind.DutyReportAttachment` classified in `UploadAuthorizer.LookUpAsync`
    before media, rung and report access applied, signed links on read, `UploadLinks.Strip` on write.
13. **Exports and prints** carry only what the caller could read, run the same checks as the page, and are
    logged through `POST …/activity/exports`. Printed rotas show names and slots, not reports.
14. **Activity log**: every report read by someone other than its author, every comment, return, review,
    lesson flag, override, timetable publish, rota generation and swap — with `Visibility` set, subject
    trails filtered, summaries never carrying report text, comments or ratings.
15. **Retention**: reports and flags are kept like staff records; attribution blanked after the policy
    window (existing purge).

**Onboarding (§12)**
16. **No shared or default password, ever** (ASVS V2.5.4, CISA): temporary passwords are random per
    person, stored hashed only, shown once, expire in 72 hours, and a sign-in with one yields a
    password-change-only token. An administrator-typed batch password must pass the policy and blocklist.
17. **Nobody chooses their own organization or privilege.** The join code decides the tenant; the pending
    account holds no role that reaches a record; the approver assigns at or below their own rank; the
    import refuses a role above the importer's.
18. **Nothing about existing accounts leaks from a public page**: identical answers for known and unknown
    emails, rate limits per viewer and per code, codes stored hashed and rotatable, rejected applicants'
    details purged after 30 days.

**Verification (extend `scripts/e2e/class-teacher-e2e.sh`, Node section 14 or a new section 15)** — the
assertions that matter are the negative ones:
- a subject teacher gets roster basics for students they teach, **404 on those students' welfare**, no
  guardians or pastoral fields, and 404 on students in classes they do not teach;
- a class teacher of S2A who teaches Physics in S3B sees welfare for S2A and **not** S3B;
- the welfare alert reaches the class teacher and **not** the subject teacher;
- a teacher on duty cannot read another teacher's report on the same slot, nor the supervisor's report;
- a notification or email about a report or comment contains no report text;
- report evidence: 404 to a peer, 200 to author and supervisor;
- a scoped caller is refused timetable publish and rota generation;
- two simultaneous placements of the same teacher in the same period: one succeeds, never a 500, never two
  rows; two simultaneous submits of the same report: one row;
- the ladder sends each stage once across two sweeps, collapses missed stages, stops on acknowledge and
  on submit, holds non-final stages in quiet hours;
- a hard clash blocks publish; the integrity sweep reports a new clash once;
- a recorder cannot mark themselves Present on a Session duty;
- a temporary-password token gets 401 on every endpoint except change-password and sign-out; an expired
  temporary password cannot sign in; `staff`, the school name and the username are refused as passwords;
- a pending applicant cannot sign in; a join request with a rotated or expired code is refused; the join
  page answers identically for a known and an unknown email; a Manager cannot approve someone as Tenant
  Admin; approval past the seat cap is 402; two simultaneous approvals of one request create one account.

---

## 14. Phases

Each phase ships on its own and is verified against the dev tenant (API e2e plus Chrome at desktop and
390px) before the next begins.

**Phase 0A — Onboarding.** `User.MustChangePassword`, `TemporaryPasswordExpiresAt`, `PendingApprovalAt`;
the password-change-only token and its authorization rule; the blocklist in `PasswordValidationService`;
temporary-password delivery (slips, SMS) on the staff import, Excel parsing and template; the join link,
`/join/{code}`, email verification, the pending account, Join requests queue with approval form, rotation,
purge of rejected requests; Onboarding status page and re-issue; first-sign-in checklist with the
acceptable-use notice; permission `users.approve` in all three catalogues. Migration: `AddStaffOnboarding`.
Independent of everything else, so it can ship first.

**Phase 0 — Foundations and the security core.** Tiered `IStudentScopeService` (delete the tier-blind
method), `SubjectTeacher` assignment role, `teacher` role to `AssignedClasses` + `students.view`, Teaching
tier in the student mapper, welfare alerts pastoral-only; `Subject` table and page; `Level` on classes;
`IReminderLadderService` + job + quiet hours, the existing duty reminder and register chase moved onto it
(and reaching recorders); recorder self-mark rule; `QWeekGrid`; permissions in all three catalogues
(`staff.dutyreports.view`, `staff.dutyreports.review`, `timetable.manage`, `timetable.lessons.flag`),
event keys. Migration: `AddSubjectsAndTieredAssignments`.

**Phase 1 — Duty rota.** `StaffDuty.Kind` and rota columns; generator, extend, swap; Duty Rota views;
fairness and clash warnings; acknowledgements; pre-duty ladder; portal *On duty*. Migration:
`AddDutyRota`.

**Phase 2 — Duty reports.** Report, note, attachment tables; template in policy; form, submit, return,
review, comments; overdue ladder with supervisor and head escalation; duty close-out through the register;
review queue; print and publish; activity log; upload kind. Migration: `AddDutyReports`.

**Phase 3 — Timetable builder.** Bell schedule and rooms settings (locked writes); `Timetable` and
`TimetableLesson`; editor on `QWeekGrid` with live diagnosis; publish; class / teacher / room / master views
and prints; integrity sweep. Migration: `AddTimetable`.

**Phase 4 — Lessons live.** Lesson materialisation into `StaffDuty`; My Day; morning digest; lesson
reminders; Taught / Not taught self-report; supervisor flags and overrides; recovery lessons; unrecorded
chase. Migration: `AddLessonDutyColumns`.

**Phase 5 — Reports and analysis.** Teaching load, lessons taught, weekly lesson analysis email (MoES
layout), rota and report compliance, timetable health; dashboard tiles.

**Phase 6 — Import and polish.** Timetable CSV import (from aSc / FET exports) through the import-job
machinery, refused for scoped callers; migrate `Content/Schedules.razor` onto `QWeekGrid`.

---

## 15. Decisions needed before building

1. **Report cadence default and template.** Proposed: daily reports for day-long slots, weekly for
   week-long, monthly for month-long; template sections *Arrival & assembly, Attendance, Meals, Cleanliness,
   Boarding (if any), Incidents (linked records), Recommendations*, tenant-editable.
2. **Who reads the supervisor's report.** Proposed: reviewers only, not the teacher on duty it describes.
3. **Rota close-out scoring.** Proposed: the supervisor's Completed / Not completed writes a Duty record
   against "Teacher on Duty" (weight 1); reports on time are evidence, not points.
4. **Lesson flags.** Proposed: teacher self-reports *Taught*; lesson supervisors (HoD for their department,
   DoS / deputy for all) confirm or override; unrecorded is shown as unrecorded.
5. **What a subject teacher sees.** Proposed: name, photo, class, their subject, their registers, and
   learning-support notes the tenant marks shareable; no guardians, welfare, discipline or pastoral tier;
   may *log* a concern (default on).
6. **Timetable cycle.** Proposed: support both a 5-day week and a 10-day A/B cycle; bell schedule per day
   type; 40-minute default period.
7. **Load norms.** Proposed defaults: 20–24 lessons a week, 18 minimum for mixed A/O-Level teachers, 8
   periods a day maximum, 4 consecutive maximum — all editable.
8. **Reminder defaults.** Proposed ladders as §4.2 and §4.4; lesson reminder 10 minutes, in-app only;
   quiet hours 20:00–06:00; SMS only for the final pre-duty stage and only where the tenant enables SMS.
9. **Automatic timetable generation.** Proposed: not in scope (no solver dependency); manual build with
   live diagnosis, plus CSV import in Phase 6.
10. **Timetable master role.** Proposed: a permission (`timetable.manage`) held by DoS and Academic
    Assistant, not a new system role; a school can make a custom role in the role editor.
11. **The shared default password.** Proposed: not offered. Imports use invitation links or per-person
    temporary password slips (default: slips where no email is given, invitation otherwise); an
    administrator-typed batch password is allowed only if it passes the policy and blocklist.
12. **One email across two schools.** `Users.Email` is unique platform-wide today, so a teacher working at
    two tenants needs a second email. Proposed: accept that for now and report it clearly; a
    multi-organization identity is a separate project.
13. **Who approves join requests, and the default role.** Proposed: Tenant Admin and Manager
    (`users.approve`); the approver must pick a role — there is no silent default — with *Teacher*
    preselected when the applicant said they teach.
14. **Join link lifetime.** Proposed: 30 days, rotatable any time, optional allowed email domains; requests
    expire after 14 days unanswered and are purged 30 days after rejection or expiry.

---

## 16. Sources

Every source below was opened and checked on 2026-09-17 unless marked otherwise.

**Uganda — ministry, legislation and national systems**
- MoES, *Basic Requirements and Minimum Standards Indicators for Education Institutions* (revised 2009) —
  staff duty roster book, displayed duty roster, log book for major events, teacher on duty in charge of
  assembly, lesson attendance register, one class teacher per class.
  https://www.education.go.ug/utsep/wp-content/uploads/2020/03/Final-Copy-BRMS.pdf
- MoES, *Performance Management Guidelines for Tertiary Institutions and Schools* (May 2020) — lesson
  attendance register, weekly deputy analysis to the head, lessons taught / not taught, lesson recovery
  schedule, "lessons recovered should not be considered as lessons missed".
  https://education.go.ug/wp-content/uploads/2020/04/Performance-Management-Guidelines-May-2020.pdf
- MoES, *Guidelines for the Senior Women and Senior Men Teachers in Uganda* (2020) — case records shared
  only with relevant actors; head teacher access.
  https://www.ungei.org/sites/default/files/2021-02/Guidelines-Implementation-Roles-Responsibilities-Senior-Teachers-Uganda-2020-eng.pdf
- World Bank with MoES, *Uganda Secondary Education & Training Curriculum, Assessment & Examination:
  Roadmap for Reform* — 18–24 periods of 40 minutes; teacher absenteeism around 20%.
  https://documents1.worldbank.org/curated/en/351411468316470400/pdf/703900ESW0P1030nd0Assessment0Report.pdf
- World Bank, *Uganda Service Delivery Indicators* — 40% of classrooms with a teacher present and teaching;
  absence often sanctioned.
  https://www.worldbank.org/en/country/uganda/publication/uganda-service-delivery-indicators-health-education-services
- *The Independent* (Uganda), "Schools underutilizing teachers on payroll" (5 March 2025) — MoES 20–24
  lessons a week, 18 minimum for mixed levels, under- and over-loading figures. (Newspaper report; the MoES
  circular itself was not found.)
  https://www.independent.co.ug/schools-underutilizing-teachers-on-payroll-hire-more-pta-staff/
- TELA: *The Independent* (2023) and Kagadi District Education Department — attendance and timetable
  implementation monitoring.
  https://www.independent.co.ug/govt-rolls-out-new-inspection-tool-to-curb-teacher-absenteeism/ ;
  https://education.kagadi.go.ug/tela-information-disseminated-to-kagadi-district-proprietors-and-headteachers-of-private-education-institutions/
- *Data Protection and Privacy Act, 2019* (Act 9 of 2019) — s.3, s.8, s.9, s.12, s.14, s.20.
  https://media.ulii.org/files/legislation/akn-ug-act-2019-9-eng-2019-05-03.pdf

**United Kingdom — statutory guidance**
- DfE, *School Teachers' Pay and Conditions Document 2025* — directed time, midday supervision, cover
  rarely, cover administration not a teacher task.
  https://assets.publishing.service.gov.uk/media/687a6260312ee8a5f0806bb5/School_teachers__pay_and_conditions_document_2025_and_guidance_on_school_teachers__pay_and_conditions.pdf
- DfE, *Keeping children safe in education 2026* — need-to-know, separate child protection files, access
  only by those who need it.
  https://assets.publishing.service.gov.uk/media/6a9081309a177a1decf97b00/Keeping_children_safe_in_education_2026.pdf
- DfE, *Information Sharing Duty* (September 2026) — share no more widely than necessary; record what,
  why, with whom.
  https://assets.publishing.service.gov.uk/media/66320b06c084007696fca731/Info_sharing_advice_content_May_2024.pdf
- DfE, *Data protection in schools: policies and procedures* (July 2026) — who has access and how it is
  controlled.
  https://www.gov.uk/guidance/data-protection-in-schools/data-protection-policies-and-procedures

**School systems (vendor documentation)**
- Arbor — Staff Absence and Cover; cover notifications; cover statistics; safeguarding alerts for "my
  students"; staff access glossary.
  https://support.arbor-education.com/hc/en-us/sections/201466591-Staff-Absence-and-Cover ;
  https://support.arbor-education.com/hc/en-us/articles/4403063582737-How-do-cover-emails-and-notifications-work ;
  https://support.arbor-education.com/hc/en-us/articles/207403169-How-can-I-manage-cover-and-view-cover-statistics ;
  https://support.arbor-education.com/hc/en-us/articles/360017730037-Alerts-for-safeguarding-notes-on-My-Homepage ;
  https://support.arbor-education.com/hc/en-us/articles/115004446965-Introduction-glossary-for-staff-access-and-permissions
- Bromcom — duty codes on staff timetables; access levels and staff scopes.
  https://docs.bromcom.com/knowledge-base/how-to-manage-non-contact-duty-codes/ ;
  https://docs.bromcom.com/knowledge-base/how-to-manage-access-levels-and-permissions/
- Tes Timetable Daily (formerly Edval) — cover of classes and duties; day sheets.
  https://www.tes.com/for-schools/timetable/daily
- Timetabling Solutions, *Lesson 15: Allocate Yard Duties* — maximum duty load, availability, allocation
  checks. https://timetablingmainsite.blob.core.windows.net/pdflessons/V10.1/TT/2026/Lesson%2015%20-%20Allocate%20Yard%20Duties.pdf
- aSc Timetables (EduPage help) — supervisions, min/max per teacher, two supervisors.
  https://help.edupage.org/?p=u1/u3&lang=en
- Untis — substitution planning; diagnosis after manual edits.
  https://www.untis.at/en/products/untis-timetable-scheduling
- FET — constraint weights; no conflict explanation.
  https://lalescu.ro/liviu/fet/doc/en/faq.html
- CPOMS FAQ — log a concern without search access; restriction by category or year group.
  https://www.cpoms.co.uk/faq/

**Research on timetabling**
- Schaerf, "A Survey of Automated Timetabling", *Artificial Intelligence Review* 13 (1999).
  https://ir.cwi.nl/pub/4935
- Pillay, "A survey of school timetabling research", *Annals of Operations Research* 218 (2014),
  doi:10.1007/s10479-013-1321-8 (metadata verified; publisher page not accessible to automated fetch).
- Post, Di Gaspero, Kingston, McCollum, Schaerf, "The Third International Timetabling Competition",
  *Annals of Operations Research* 239 (2016) — XHSTT constraint types, hard/soft, infeasibility value.
  https://www2.cs.sfu.ca/~mitchell/cmpt-827/2015-Fall/Projects/TT-ITC-2011-Report.pdf

**Notifications and behaviour**
- AHRQ PSNet, *Alert Fatigue* (reviewed 2024). https://psnet.ahrq.gov/primer/alert-fatigue
- Google, *Site Reliability Engineering*, "Monitoring Distributed Systems".
  https://sre.google/sre-book/monitoring-distributed-systems/
- Nielsen Norman Group, "Five Mistakes in Designing Mobile Push Notifications" (2018).
  https://www.nngroup.com/articles/push-notification/
- Fitz, Kushlev et al., "Batching smartphone notifications can improve well-being", *Computers in Human
  Behavior* 101 (2019). https://www.kushlev.com/s/2019-Fitz-Batching.pdf
- Apple, *Human Interface Guidelines — Managing notifications*.
  https://developer.apple.com/design/human-interface-guidelines/managing-notifications
- Altmann, Traxler, Weinschenk, "Deadlines and Memory Limitations", *Management Science* (2022),
  doi:10.1287/mnsc.2021.4227 (abstract).
- Behavioural Insights Team, *EAST* (2014). https://www.bi.team/wp-content/uploads/2014/04/BIT-EAST-handbook.pdf
- PagerDuty, *Escalation Policy Basics*. https://support.pagerduty.com/main/docs/escalation-policies
- Workplace Relations Commission (Ireland), *Code of Practice on the Right to Disconnect* (2021).
  https://www.workplacerelations.ie/en/what_you_should_know/codes_practice/code-of-practice-for-employers-and-employees-on-the-right-to-disconnect.pdf

**Security**
- OWASP Top 10:2025, A01 Broken Access Control. https://owasp.org/Top10/2025/
- OWASP ASVS 5.0, V8 Authorization and V14 Data Protection.
  https://github.com/OWASP/ASVS/blob/master/5.0/en/0x17-V8-Authorization.md
- NIST SP 800-162, *Guide to Attribute Based Access Control*. https://csrc.nist.gov/pubs/sp/800/162/upd2/final
- NIST, *Role Based Access Control* project (ANSI INCITS 359). https://csrc.nist.gov/projects/role-based-access-control
- OWASP, *Logging Cheat Sheet*. https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- NHS England, *Texting, emailing and messaging patients and service users* (read via the Web Archive,
  3 February 2026). https://transform.england.nhs.uk/information-governance/guidance/texting-emailing-and-messaging-patients-and-service-users/
- ICO, bulk email guidance.
  https://ico.org.uk/for-organisations/advice-for-small-organisations/news-blogs-and-events/news/ico-publishes-new-guidance-on-sending-bulk-communications-by-email/

**Onboarding and credentials**
- OWASP ASVS 4.0.3, V2.3.1 (initial passwords random, short-lived) and V2.5.4 (no shared or default
  accounts). https://github.com/OWASP/ASVS/tree/v4.0.3/4.0/en
- NIST SP 800-63B-4, *Digital Identity Guidelines: Authentication and Authenticator Management* — §3.1.1.1
  and §3.1.1.2: blocklist of common, expected and context-specific passwords; no periodic changes; change
  on compromise. https://pages.nist.gov/800-63-4/sp800-63b.html
- CISA, *Secure by Design Alert: How Manufacturers Can Protect Customers by Eliminating Default Passwords*
  (December 2023). https://www.cisa.gov/resources-tools/resources/secure-design-alert-how-manufacturers-can-protect-customers-eliminating-default-passwords
- Microsoft Learn, *Bulk create users in Microsoft Entra ID* — per-row initial password, no invitation
  sent. https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-add
- Google Workspace Admin Help, *Add or update multiple users from a CSV file* — per-user password,
  "Change Password at Next Sign-In". https://knowledge.workspace.google.com/admin/users/add-or-update-multiple-users-from-a-csv-file
- Slack Help Center, *Security tips to protect your workspace* — sign-up by approved email domain; require admin approval
  for all invitations. https://slack.com/help/articles/115004155306

**Not verified:** Kenya TSC lesson attendance and duty guidance (site unreachable); a primary MoES circular
for the 20–24 lesson norm; public duty-rota documentation for iSAMS, Veracross, ManageBac, Schoolbase,
Teachmint, Zeraki and ShulePro; an authoritative UK definition of the form tutor role.

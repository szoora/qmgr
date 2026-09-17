# Staff Performance Monitor — implementation plan

**Status:** BUILT (Phases 85–86, 2026-09-16 and 2026-09-17), with the amendments below.
**Written:** 2026-09-16. Tracker: Phase 85 (build) and Phase 86 (audit against this plan, gaps closed).

> **Amendments, 2026-09-17 — where the build and this plan now deliberately differ:**
>
> 1. **Not a module of its own.** Staff performance is part of the Student Welfare module (code
>    `student-welfare`, renamed **"Welfare & Performance"**); every staff route and page is gated on
>    it. §2's separate `ModuleCodes.StaffPerformance`, §12 Phase 0's separate seed and §13 decision
>    7's separate price no longer apply: the welfare module's price and a user cap of 250 a branch
>    cover both. The sidebar keeps its two short groups, "Student Welfare" and "Staff Performance".
> 2. **A Confidential record: the subject reads it in full; the notification about it is
>    title-only.** §8's table describes the ALERT and stands. §13 decision 3 is settled that way:
>    the right of reply (§6) and s.24 subject access (§1.7) both need the content, and the portal is
>    the subject-access view. §14's "visible to them as title only" now reads "readable by them in
>    full; their notification carries only that it exists".
> 3. **A register records its marks on every save, not only on close.** §6.2 said closing writes the
>    records. Built: each save writes one `Final` record per marked person and notifies them;
>    closing declares the register complete. A register left open still counts as not taken (portal
>    to-do, register chase). Reason: a register taken on a phone must not lose its taps to a dropped
>    connection or a closed tab.
**Designed artifact:** https://claude.ai/artifact/NL5aZ9uCKUAy2w7kb94wAo

Requested as: a performance monitor for staff against variable parameters (lesson attendance, exam
supervision, meeting attendance, prep supervision, co-curricular activities, and so on), each
scorable so that a summative rating follows; a hierarchy of Administrator, Director of Studies,
Academic Assistant, Head of Department, Class Teacher, Teacher and Support Staff, any of whom may
log a record about a staff member according to their permissions; a portal for every staff member
as a hub for reminders, notifications, notices and a dashboard of ratings and scores; summary and
detailed reports to administrators; delegated recording (a teacher assigned to take minutes may
take the register); a comprehensive activity log; evidence uploads; and all of it blended with the
existing Student Welfare ledger, built from the shared component library, consolidating that
library where it falls short.

**The one-paragraph version.** Staff performance is a second *subject* for machinery Student
Welfare already has: an append-only ledger with notes and evidence, an admin-owned taxonomy with
default points, three visibility rungs, a row-scope service, alert suppression in one place, and a
timeline. What is new is the subject (a member of staff instead of a child), the hierarchy it needs
(departments, heads, line managers), the events that generate most records (meetings, exam and prep
supervision, lessons, with a register taken by a named recorder), the scoring that turns records
into a rating, the termly appraisal that closes a period, and the portal that puts all of it in
front of the person it is about. Everything below is organised around reusing the first list and
building only the second.

---

## 1. What the research says the tool should be

Full sources are in §15. The design rules that follow from them, stated first because they shape
the data model:

1. **Model the cycle Uganda already prescribes.** The Ministry of Education and Sports'
   *Performance Management Guidelines for Tertiary Institutions and Schools* (May 2020) is the
   directly applicable primary source, and it already names this tool's parameters: a **Teacher
   Lesson Attendance Register** analysed weekly by the Deputy Head, a **Lesson Recovery Schedule**
   (a recovered lesson "should not be considered as lessons missed"), a **Lesson Observation Form**
   (at least one observed lesson per term, with a preparatory meeting before and a feedback
   session after; "not… finding fault but helping the teacher to improve"), a **termly review
   meeting** producing a support and development plan, and an **annual appraisal by 31 December**
   on a 1–5 scale (Excellent 5 … Poor 1) that the termly appraisals "cumulatively constitute". Kenya's
   TPAD runs the same termly self-appraisal → appraiser → agreement cycle online with audit trails.
   The plan adopts that cycle rather than inventing one.
2. **Formative first, summative second.** OECD TALIS finds nearly half of teachers experience
   appraisal as "simply to fulfil administrative requirements" and that the effect on practice
   depends on what happens *after* the rating. Tanzania's OPRAS failed for exactly that reason.
   **The computed score never becomes the rating on its own**; the appraiser sets the rating with
   the score, the self-assessment and the evidence in view, records strengths and a development
   plan, and a second line moderates.
3. **Multiple measures, capped weights, four-level anchored rubrics.** The MET Project: "Teaching
   is too complex for any single measure"; composites where no measure exceeds about half the
   weight were most stable; a second observer adds more reliability than a second lesson; scores
   compress toward the middle and administrators rate their own staff a tenth of a point higher
   than outsiders do. So parameters carry a weight with a per-parameter cap, observations use a
   four-level scale with written descriptors (Danielson's Unsatisfactory / Basic / Proficient /
   Distinguished; MoES Annex 6's Very Good / Good / Fair / Poor), the portal shows the breakdown
   before the total, and the reports show observer-level dispersion so a lenient or harsh rater is
   visible.
4. **Recognition is frequent, specific and mostly non-monetary; contingent rewards have a cost.**
   Gallup's "recognised in the last seven days" item moves productivity 10–20%; the peer-recognition
   products (Bonusly, Kudos, Workvivo) share one shape, a feed of small named timely
   acknowledgements with a monthly giving allowance tagged to a value. Deci, Koestner and Ryan's
   meta-analysis of 128 studies finds expected, performance-contingent tangible rewards reduce
   intrinsic motivation (d ≈ −0.3) while informational positive feedback increases it. So
   recognition is a record any staff member can give within a monthly budget, notifies the receiver
   at once, and is the default reward; prizes are the exception, small and unexpected.
5. **Leaderboards behave like imposed stretch goals and demotivate both ends.** Landers et al.
   and Na & Han: top-ranked users coast, bottom-ranked users burn out, neither becomes intrinsically
   motivated; the recommendations are relative or team-level boards, regular resets, and
   self-competition. So **individual ranks are private by default**: a person sees their own
   position and trend, the school sees departments, and a public top-N board is a tenant switch
   that is off unless turned on.
6. **Wellbeing is monitored, never scored.** The Teacher Wellbeing Index (78% of staff stressed;
   "do not feel appreciated" and "poor line management" among the top negative-culture drivers),
   the DfE Wellbeing Charter and the WHO's workplace guidance all treat wellbeing as something the
   employer owes the employee. The Ugandan Well-being ASSETS instrument is validated for
   distribution-level monitoring, not individual comparison. So a welfare-of-staff record (a
   bereavement, a workload concern, a health matter) is unscored, Confidential by default, and
   excluded from the composite by construction, as `WelfareCaseType.Welfare` is for students.
7. **The subject has rights over the record.** Uganda's Data Protection and Privacy Act 2019 (s.3
   principles, s.24 access within thirty days, s.25 objection, s.6 a data protection officer) and
   the ICO's employer guidance (an informal "can I see my appraisal notes" is a valid access
   request; attendance collected for cover must not silently become a pay input; biometric or
   pay-linked monitoring needs an impact assessment) shape the access model. So the portal *is* the
   subject-access view, every record carries the purpose it was collected for, the subject can
   append a response, the whole file exports, and biometric attendance and salary links are out of
   scope by design.

---

## 2. What already exists, and what is reused rather than built

The map below was checked against the working tree on 2026-09-16, not against older notes.

| Need | Exists as | Reuse |
|---|---|---|
| Append-only ledger with one mutable field | `WelfareRecord` + `WelfareNote` (`Visibility` mutated only via an endpoint that writes a note) | Same shape for `StaffPerformanceRecord` + `StaffPerformanceNote` |
| Admin taxonomy with default points, colour, sort, retire-not-delete, `Restrict` on delete | `WelfareCategory` (org-scoped) | Same shape for `PerformanceParameter` |
| Three visibility rungs as separate permissions, compared as a set, server wins on create | `WelfareVisibility` + `MaxVisibilityAsync` / `VisibleLevelsAsync` / `CanSeeAsync` in `WelfareController` | **Reuse the enum type itself**; mirror the three helpers |
| Row scope as a single service, per-request memoised, fails closed, 404 never 403 | `IStudentScopeService` | Mirror as `IStaffScopeService` (never fork the student one) |
| Alert suppression in one early return | `WelfareAlertService` | Mirror as `StaffAlertService` |
| Evidence upload gated per file | `WelfareAttachment` → `UploadOwnerKind.WelfareAttachment` → `UploadAuthorizer` → `UploadLinks.Sign/Strip` | New `UploadOwnerKind.StaffEvidence`, same pipeline, same `QFileUpload` |
| Staff↔unit assignment with history, coverage report | `ClassTeacherAssignment` + `class-teachers/coverage` | Model for department headship; a "structure coverage" report |
| Reminder sweep gated by a timestamp, lead time from settings | `WelfareReminderJob` (overdue) + `AppointmentJobs` (ahead of time, per-branch lead) | `StaffPerformanceJobs`, one class, several sweeps |
| Scheduled digest in branch-local time, idempotent via `LastSentAt` | `VisitorReportSubscriptionJob` + `VisitorReportEmail` (inline-styled KPI tables) | Weekly staff digest and monthly administrator summary |
| Preference-aware notification, bell synchronous, SMS/email via Hangfire, delivery log | `CreateInAppNotificationAsync` + `EventKey` + `NotificationDispatchJob` + `NotificationLog` | New `NotificationEventKeys` only |
| Live push to a user | SignalR `user-{id}` group, `ReceiveNotification`, `UnreadCountUpdated` | One new event, `StaffScoreUpdated` |
| Points on a record, defaulted from its category | `WelfareRecord.Points` / `WelfareCategory.DefaultPoints` | Same two columns, same sign rules |
| Per-staff distribution ("who logs what") | `WelfareSummaryDto.ByStaff` and the *Consistency Check* panel | Feeds the auto-award source and the fairness audit |
| Per-staff service signals | `Feedback.ServedByUserId`, `TokenHistory.UserId`, `UserSession.TokensServed`, `Counter.AssignedUserId` | Optional system-source parameters for front-office staff |
| Print route + Publish to Library + revocable share | `WelfareTimelineReport` + `reportPublish.js` + `DocumentShare` | Staff report and appraisal PDF take the same road |
| Export | `QDataExport` | Every list |
| Dates and ranges | `QDateFormat`, `QDatePicker`, `QDateRangePicker` | Everywhere |
| Batch preview-then-commit, scoped-caller refusal, undo | `BatchController` + `RosterImportJob(Kind)` | Staff bulk import as `RosterImportKind.Staff` |
| Self-service endpoints authorised by JWT only | `ProfileController` | Portal endpoints follow it |
| Invitation without a new column | `User.PasswordResetToken` + `/reset-password` | Invite = create + reset link |

**What does not exist, confirmed:** no `Department`, no line manager, no staff-vs-user
distinction beyond `JobTitle` / `EmployeeNumber` / `OfficeLocation`, no audit or activity log of any
kind, no notice board (Broadcasts target the public with a mandatory unsubscribe footer; Docs are
platform-wide and anonymous), no calendar, no notification centre page (the bell dropdown of
twenty items is the whole surface), no `Category` or persisted `EventKey` on `Notification`, no
role editor beyond permission checkboxes (`POST /api/v1/roles` has no caller in the Web project),
no `RoleDataScope` a tenant can set despite the entity's doc comment saying there is, no scheduled
reminder that fires *before* a due time for staff, and no timeline, stat tile, tab strip, chip,
empty state, bar list, avatar, rating or table component in the shared library. `QPager` exists
with zero call sites. `Notification.MetaData` exists and the DTO never carries it.

**Three findings to act on regardless of this feature:**

- **`DbSeeder.SeedModulesAsync` runs only in Development**, so on a production box a new module
  row never appears, and `ModuleCatalogController` reports `NOT_SEEDED` with advice ("restart the
  API") that is only true on a dev machine. The seed must move to a startup defaults class that
  runs in every environment, `PlatformEmailDefaults`-style, before any module can be sold.
- **`Users.Username` and `Users.Email` are globally unique across tenants** while the controller
  validates per organization. "Every staff member gets a login" across many schools will collide
  on common usernames. Not this feature's to fix, but it will surface here first.
- **`POST /api/v1/users` returns 402 at the plan's user cap.** Onboarding a whole staff list hits
  this before anything else does; the module's seat model has to answer it (§13, decision 7).

---

## 3. The model

```
PerformanceParameter  (what is measured; org-owned taxonomy; default points, weight, cap, applies-to)
        │
        ├─▶ StaffDuty  (a meeting / exam session / prep slot / lesson: when, where, who is expected,
        │               who may take the register, the minutes in the Library)
        │        │
        │        └─▶ register ──▶ one StaffPerformanceRecord per expected person
        │
        └─▶ StaffPerformanceRecord  (append-only; subject, logger, outcome, points, rating, visibility, source)
                 ├─▶ StaffPerformanceNote        (follow-up, the subject's response, moderation)
                 └─▶ StaffPerformanceAttachment  (evidence, gated per file)

Records ──(policy: periods, weights, caps, bands)──▶ computed score per person per period
                                                          │
                                                          └─▶ StaffAppraisal  (targets → self → appraiser → moderation → signed;
                                                                              the score is evidence, the rating is a decision;
                                                                              three termly rows roll up to the annual one)
ActivityEvent   (who did what, to which record, from where: every write in the module, plus sign-in)
StaffNotice     (to a branch / departments / roles; read state via Notification rows; acknowledgements on the row)
```

Recognition ("kudos") is **not** a table: it is a `StaffPerformanceRecord` whose parameter is of
kind Recognition, logged by a peer within a monthly budget. An award ("Teacher of the Term") is
the same row with a different parameter and a Library certificate attached. Points are **not** a
ledger table: they are summed from records per period and snapshotted onto the appraisal when it
is signed, so a later correction cannot silently change a signed rating.

### 3.1 Where each thing lives, and why a new table was not the first choice

Applying the standing rule (nullable column → enum value → array/JSON → table, in that order):

| Need | Decision | Why not the leaner option |
|---|---|---|
| Departments | **New table `Department`** (OrganizationId, BranchId?, Name, Code, HeadUserId?, DeputyHeadUserId?, SortOrder, IsActive) | It is FK'd by users and reported on directly, which is the vocabulary DTO's own rule for "a real table" (`RosterDto.cs:496-509`). A JSON vocabulary entry cannot be a head's assignment target. |
| A person's departments | **`User.DepartmentIds uuid[]`** array column | A teacher of Maths and Physics is in two departments; nothing is stored per membership beyond the id. A membership-history table is the upgrade path if "who was in which department when" is ever needed; the appraisal snapshot records the appraiser anyway. |
| Line manager | **`User.LineManagerUserId`** nullable column | One scalar; a change is logged in `ActivityEvent`, which carries the history. |
| Staff scope on a role | **`Role.StaffScope`** new column (enum `StaffDataScope { Organization, AssignedDepartments, DirectReports, SelfOnly }`) | `Role.DataScope` is already taken by the student axis; one enum cannot say "all students, my department's staff". Two columns, one per subject. |
| The measured things | **New table `PerformanceParameter`** | Widening `WelfareCategory` with a Subject discriminator was considered: it would put a child-behaviour taxonomy and a staff-duty taxonomy in one list, one editor and one FK target, and `StudentFlag` also reads that table. Same *shape*, separate table. |
| The ledger | **New table `StaffPerformanceRecord`** | Not a `WelfareRecord`: that table's `StudentId` is non-nullable and its guards, scope and alerting are all about a child. Same shape, separate subject. |
| Notes, attachments | **New tables**, mirroring `WelfareNote` / `WelfareAttachment` | A nullable second FK on `WelfareAttachment` would make the one table that gates *photographs of injured children* polymorphic, with a discriminator in the security path. `UploadAuthorizer` classifies by "which table points at this file"; keep that true. |
| Meetings, exam sessions, prep, lessons | **New table `StaffDuty`** | Own lifecycle (scheduled, register open, closed), FK'd by records, reminders and reports hang off it. Expected attendees and named recorders are `uuid[]` columns (`ExpectedUserIds`, `RecorderUserIds`), not join tables. Minutes are a `MediaContentId` into the Library, not a file column. |
| The termly appraisal | **New table `StaffAppraisal`** | A workflow with stages, two authors, sign-off timestamps and a frozen score snapshot; no existing row can carry it. |
| Activity log | **New table `ActivityEvent`** | Nothing like it exists. `DocumentShareEvent` is the template, generalised with `EntityType`/`EntityId` and an actor. |
| Notices | **New table `StaffNotice`** | No tenant-scoped rich-body content with a staff audience exists. Read state is **not** a new table: publishing fans out one `Notification` per recipient, whose `ReadAt` already answers "who has seen it". Acknowledgements are a `jsonb Acknowledgements` map (userId → timestamp) on the notice, adequate for a branch of a few hundred staff; a table only if acknowledgement reporting across notices is ever needed. |
| Scoring policy, periods, bands, weight caps, leaderboard switch, budgets, lead times, retention, DPO contact | **`Organization.Settings["StaffPerformance"]`** JSON, one reader | Same as `"DocumentSharing"`. Never a second JSON parser. |
| Points, scores, leaderboards | **Computed** | Summed from records; snapshotted onto `StaffAppraisal` at sign-off. |
| Bulk staff import | **`RosterImportKind.Staff = 3`** appended to the existing enum, rows in `roster_import_jobs` | Same discriminator trick batches already use. |
| Persisted notification category | **`Notification.EventKey`** nullable column | Unlocks grouping the bell and the notification centre by kind without inferring from a queue-centric enum. |
| Role hierarchy | **Five new system roles**, no new mechanism | `RoleCodes.All` gains `director-of-studies`, `academic-assistant`, `head-of-department`, `teacher`, `support-staff`, all placed **below `manager`** so none acquires `IsManagerOrAbove` by accident (§5). |

New tables: nine (`Department`, `PerformanceParameter`, `StaffDuty`, `StaffPerformanceRecord`,
`StaffPerformanceNote`, `StaffPerformanceAttachment`, `StaffAppraisal`, `ActivityEvent`,
`StaffNotice`). New columns on existing tables: four (`User.DepartmentIds`,
`User.LineManagerUserId`, `Role.StaffScope`, `Notification.EventKey`), plus one appended enum value
(`RosterImportKind.Staff`). Every enum value is **appended, never inserted**; every new table is
PascalCase like the welfare five and gets an `IEntityTypeConfiguration` (the welfare tables have
none, and the plan should not copy that omission).

### 3.2 Field lists

**`PerformanceParameter`** (`: BaseAuditableEntity`) — `OrganizationId`, `Name`, `Description?`,
`Kind` (`ParameterKind { Attendance, Duty, Observation, Contribution, Recognition, Conduct,
Wellbeing }`), `AppliesTo` (`StaffGroup { AllStaff, TeachingStaff, SupportStaff }`),
`DefaultPoints int?` (signed; **null for Wellbeing, which is never scored**), `MaxPointsPerEntry
int`, `MaxPointsPerPeriod int?` (the cap that stops one parameter dominating), `Weight decimal`
(share of the composite, 0 = evidence only), `RatingScale int?` (4 for an observation rubric,
null otherwise), `RubricJson?` (descriptor per level), `DefaultVisibility`, `Purpose` (a sentence
shown on every record: "collected for cover planning and appraisal evidence"), `Color?`,
`SortOrder`. Org-scoped like `WelfareCategory`, `Restrict` on delete, retired with `IsActive`.
Seeded per tenant with the MoES set: Lesson Attendance, Lesson Recovery (positive, offsets a
missed lesson), Lesson Observation (scale 4), Exam Supervision, Prep Supervision, Meeting
Attendance, Co-curricular Activity, Records & Schemes of Work, Recognition, Professional
Development, Conduct, Wellbeing.

**`StaffDuty`** (`: BaseAuditableEntity`) — `OrganizationId`, `BranchId`, `ParameterId`, `Title`,
`Description?`, `Location?`, `StartsAt`, `EndsAt`, `ExpectedUserIds uuid[]?` (null = every
active staff member in the branch), `RecorderUserIds uuid[]` (who may take the register — **this
is the minutes-taker delegation**), `RegisterOpenedAt?`, `RegisterClosedAt?`,
`RegisterClosedByUserId?`, `MinutesMediaContentId?`, `ReminderSentAt?`, `RegisterChaseSentAt?`,
`CreatedByUserId`. No recurrence rule in v1; a "duplicate to next week" action covers the real
use, and an RRULE column is the appended upgrade if a school asks.

**`StaffPerformanceRecord`** (`: BaseAuditableEntity`, **append-only**) — `OrganizationId`,
`BranchId`, `SubjectUserId`, `ParameterId`, `DutyId?`, `Outcome` (`DutyOutcome { NotApplicable,
Present, Late, Absent, Excused, Recovered, Completed, NotCompleted }`; `Excused` is MoES's
"organisational factors… do not constitute a performance gap", `Recovered` is the Lesson Recovery
Schedule), `Points int?` (signed; sign enforced against the parameter's kind, magnitude capped by
`MaxPointsPerEntry`), `Rating int?` (1..scale, observations only), `Description`, `OccurredAt`,
`Source` (`RecordSource { Manual, Register, Observation, Recognition, System, Import }`), `Status`
(`Draft, Final, Annulled`; annulled is the "void" that keeps the row, with a note saying why),
`Visibility` (**`WelfareVisibility`, reused**), `LoggedByUserId`, `AcknowledgedAt?` (the subject
has seen it). Sign rules as welfare: Recognition and Contribution positive, Conduct negative,
Attendance and Duty by outcome, Wellbeing null.

**`StaffPerformanceNote`** — `RecordId`, `Body`, `AuthorUserId`, `Kind` (`Note, Response,
VisibilityChange, Annulment, Moderation`). A `Response` may only be written by the subject.

**`StaffPerformanceAttachment`** — exactly `WelfareAttachment`'s columns against `RecordId`.

**`StaffAppraisal`** (`: BaseAuditableEntity`) — `OrganizationId`, `BranchId`, `SubjectUserId`,
`PeriodKey` (e.g. `2026-T3`, or `2026` for the annual roll-up), `PeriodStart`, `PeriodEnd`,
`AppraiserUserId`, `ModeratorUserId?`, `Stage` (`AppraisalStage { Open, SelfAssessment,
AppraiserReview, Moderation, Signed, Appealed }`), `TargetsJson?` (the agreed targets for this
period, set at Open: MoES's performance plan), `ComputedScore decimal?` + `ComputedBreakdownJson`
(frozen at Signed), `SelfRating int?`, `SelfComments?`, `AppraiserRating int?`,
`AppraiserComments?`, `FinalRating int?` (1–5), `Strengths?`, `DevelopmentAreas?`,
`SupportPlanJson?` (MoES Annex 12: gap → support option → by when), `NextTargetsJson?`,
`SelfSubmittedAt?`, `AppraiserSubmittedAt?`, `ModeratedAt?`, `SignedAt?`, `SignedByUserId?`,
`AppealNote?`, `ReminderSentAt?`, `ReportMediaContentId?` (the signed PDF, published to the
Library). Appraisals are `Confidential` by nature: visible to subject, appraiser, moderator and
`staff.confidential.view` holders.

**`ActivityEvent`** (append-only) — `OrganizationId`, `BranchId?`, `ActorUserId?`, `SubjectUserId?`,
`Action` (string constant, e.g. `staff.record.created`), `EntityType`, `EntityId?`, `Summary`
(one line, already redacted to the actor's visibility), `DetailJson?` (changed fields, **never a
full before/after dump of a record**), `IpAddress?` (truncated /24, /48), `UserAgent?` (coarse),
`OccurredAt`. Attribution columns are blanked after the policy's retention window, the row stays,
exactly `DocumentShareEvent`'s two-tier rule.

**`StaffNotice`** (`: BaseAuditableEntity`) — `OrganizationId`, `BranchId?`, `Title`, `BodyHtml`
(sanitised as `DocArticle.BodyHtml` is), `AudienceDepartmentIds uuid[]?`, `AudienceRoleCodes
text[]?`, `AudienceStaffGroup?`, `PublishAt`, `ExpiresAt?`, `IsPinned`,
`RequiresAcknowledgement`, `AttachmentMediaContentIds uuid[]?` (Library documents),
`Acknowledgements jsonb`, `PublishedByUserId`, `NotificationsSentAt?`.

**`Department`** — as in §3.1.

---

## 4. Blending with Student Welfare

The user asked for this explicitly, and it is where the reuse becomes visible rather than
internal.

- **One timeline component for both subjects.** `StudentWelfareTimeline`'s 1,876-line page-local
  timeline is lifted into `QTimeline` / `QTimelineItem` (§9) and the student page is moved onto it
  in the same change, so the staff timeline is not a second copy of the first.
- **A teacher's pastoral work counts.** A parameter of kind `Contribution` with `Source = System`
  ("Welfare record filed") is awarded automatically when a teacher finalises a `WelfareRecord`, fed
  by the same `ReportedByUserId` grouping that already drives the *Consistency Check* panel. Low
  default points, capped per period, off by default in policy (§13, decision 4). The same hook can
  credit front-office staff from `Feedback.ServedByUserId` and `TokenHistory.UserId`.
- **Welfare actions on the portal.** `WelfareOpenActions` already answers "what do I still owe";
  the portal embeds that list beside the staff reminders so one page holds the teacher's whole
  to-do.
- **One visibility vocabulary.** `WelfareVisibility` is reused as the type; the three
  `staff.*.view` rungs mirror the welfare ones; `UploadAuthorizer` gets a `StaffEvidence` branch
  with the identical non-ladder switch. The name is historical, the semantics are not.
- **The class-teacher role gains the portal.** Every seeded role receives the self-scoped portal;
  the class teacher additionally keeps `AssignedClasses` on the student axis while getting
  `SelfOnly` on the staff axis.
- **Coverage, twice.** `class-teachers/coverage` surfaces the silent failures of the student side;
  `staff/structure/coverage` does the same for the staff side: departments with no head, staff with
  no department, staff with no line manager, staff with no appraiser this period, parameters with
  no records this term, staff with no observed lesson this term (MoES's one-per-term minimum).
- **Same road out.** A staff report or a signed appraisal reaches paper through the print route,
  the Library and, if it must leave the building, a revocable `DocumentShare`. Nothing is shared in
  place.

---

## 5. Roles, permissions and scope

### 5.1 The hierarchy as seeded roles

`RoleCodes.All` is ordered most-privileged-first and `Rank()` indexes into it, so where a role goes
is a security decision. Proposed order, new codes in bold:

```
super-admin · admin · manager · **director-of-studies** · **academic-assistant** · **head-of-department** ·
staff · class-teacher · **teacher** · **support-staff** · viewer
```

All five new roles sit **below `manager`**, so none of them satisfies `IsManagerOrAbove` (which
today also unlocks the visiting-day repeat check-in bypass and similar manager-tier overrides in
the visitor module). `Administrator` in the request maps to the existing **Tenant Admin** (`admin`),
which holds every visible permission including the Restricted rung. A tenant's own custom roles
rank below Viewer today (`Rank()` returns `All.Length` for an unknown code); that is unchanged and
acceptable because nothing in this module keys off rank.

| Role | Staff scope | Holds |
|---|---|---|
| Tenant Admin | Organization | everything `staff.*`, both rungs |
| Director of Studies | Organization | view, create, edit, duties, parameters, appraisals.conduct + approve, reports, notices, structure, confidential |
| Academic Assistant | Organization | view, create, duties, reports, notices; **no** appraisal sign-off, **no** confidential |
| Head of Department | AssignedDepartments | view, create, edit, duties, appraisals.conduct, reports (scoped), recognition |
| Class Teacher | SelfOnly (staff axis); AssignedClasses (student axis, unchanged) | portal, recognition; records only via named-recorder delegation |
| Teacher | SelfOnly | portal, recognition; records only via delegation |
| Support Staff | SelfOnly | portal, recognition |
| Manager, Staff, Viewer (existing) | SelfOnly | portal only, so a front-office employee can be appraised too |

Where appraisals are concerned, MoES's chain (deputy head appraises teachers, head teacher
countersigns) maps to Head of Department or Director of Studies as appraiser and Director of
Studies or Tenant Admin as moderator; the appraiser is whoever `User.LineManagerUserId` names,
falling back to the department head.

### 5.2 Permission codes — three catalogues, always

Add to `RbacSeeder.AllPermissions`, `Permissions.All` and the Web `Permissions` class together:

`staff.records.view` · `staff.records.create` · `staff.records.edit` (notes, annulment, points
correction; records are never rewritten) · `staff.confidential.view` · `staff.restricted.view` ·
`staff.duties.manage` · `staff.parameters.manage` · `staff.appraisals.conduct` ·
`staff.appraisals.approve` · `staff.reports.view` · `staff.notices.manage` ·
`staff.structure.manage` · `staff.recognition.give`.

The portal's own endpoints carry **no permission code**, only `[Authorize]` plus the module gate,
following `ProfileController`: they are the caller's own data. `SeedRolePermissionsAsync` is
add-only, so the grants reach every existing tenant's system roles on the next restart, and a code
later removed from a role array is not revoked; both facts go in the handover.

### 5.3 `IStaffScopeService`: the single home for "which staff may this caller see"

Mirrors `IStudentScopeService` exactly, including the things that are easy to break:

- `IsUnscopedAsync`, `GetVisibleUserIdsAsync(branchId)` (null = unscoped, **empty = nobody**),
  `ApplyAsync(IQueryable<StaffPerformanceRecord>)`, `VerifyStaffAccessAsync` (404, never 403),
  `CanSeeStaffAsync`. Scoped lifetime, per-request memoised, **never** the five-minute permission
  cache: a head removed from a department loses their staff on the next request.
- **Self is always visible and is not scope.** Every caller can read their own Standard and
  Confidential records through the portal endpoints regardless of role; the scope service governs
  reading *other people*.
- `AssignedDepartments` resolves `Department.HeadUserId == me || DeputyHeadUserId == me` then
  `User.DepartmentIds && those`. `DirectReports` resolves `User.LineManagerUserId == me`. A head
  with no department, like a class teacher with no class, sees nobody.
- **Writes are scoped from day one.** `POST records` checks `CanSeeStaffAsync(subject)` for a
  scoped caller; Phase 77's lesson was that a scope covering only reads is half a scope. The
  refusal reuses the *unknown staff member* wording so it does not confirm the person exists.
- **Delegation is not scope.** Taking a register on a duty is authorised by `me ∈
  duty.RecorderUserIds` (or `staff.duties.manage`), and the records it writes are for
  `duty.ExpectedUserIds` only. A teacher named as recorder for Tuesday's staff meeting can mark the
  Director of Studies absent from that meeting and nothing else. The register is written
  synchronously in the controller, bounded by the expected list, so the rule about Hangfire workers
  does not arise.
- **Bulk operations by a scoped caller are refused at the controller**, the `BatchController` rule,
  because `BatchOperationProcessorJob` has no HTTP context to scope from.

### 5.4 The role editor the codebase promised and never built

`Role.DataScope`'s own doc comment says it is "settable on a tenant's own custom roles through the
role editor"; there is no such editor. `UsersSetup`'s Roles tab is a permission-checkbox modal.
Phase 0 adds Create / Rename / Colour / Icon / `DataScope` / `StaffScope` to it and wires the two
`RolesController` endpoints that have no caller. Without this, the hierarchy above is only what the
seeder ships; with it, a school can add "Deputy Head Teacher" or "House Parent" itself.

---

## 6. Recording: five ways a record is born

1. **Manual.** *Log a record* on a staff member's timeline or from the records page: parameter,
   outcome / points / rating, date and time, description, visibility, evidence. The same "late
   entry" warning as welfare (a record dated more than 14 days back says so on its face). Available
   to `staff.records.create` within scope.
2. **Register.** A duty's recorder opens the register: one row per expected person, tap Present /
   Late / Absent / Excused, optional note, close. Each save writes one `Final` record per marked
   person with `Source = Register`, points from the parameter by outcome, and notifies each subject
   (§8); closing declares the register complete (amendment 3).
   Reopening appends `Annulment` notes and new rows rather than editing. A lesson marked Absent can
   later be matched by a `Recovered` record, which offsets it in scoring (MoES Annex 5). The
   register works on a phone with no more than a tap per person, because the class monitor's paper
   register is what it replaces.
3. **Observation.** A manual record on a parameter with a `RatingScale`: the form shows the scale
   with the rubric descriptor per level, asks for the pre-observation meeting date and schedules
   the feedback session, and requires a description of at least the welfare minimum. Default
   visibility `Confidential` (observations are developmental). A second observer on the same lesson
   is a second record linked by `DutyId`; the reports show the pair.
4. **Recognition.** Any holder of `staff.recognition.give` picks a colleague, a Recognition
   parameter ("Went the extra mile", "Covered a lesson", "Mentored a new teacher") and writes why.
   Budget per month from policy; self-recognition refused; the receiver is notified at once; visible
   on both feeds.
5. **System.** Off by default. When on, finalising a welfare record, serving a token, or hosting a
   visitor credits the corresponding System parameter, capped per period, labelled *automatic* on
   the timeline so nobody mistakes it for a colleague's judgement.

Every record can carry evidence: `POST records/{id}/attachments`, 25 MB, the welfare MIME prefixes,
stored through `IMediaStorageService`, classified as `StaffEvidence`, signed on read and stripped on
write. A duty's minutes are a Library document (`MinutesMediaContentId`), which is what lets them
also go on a playlist or a share link with no new code.

---

## 7. Scoring and the summative assessment

**Per parameter, per period, per person:**

- *Attendance / Duty*: `(present + 0.5·late + recovered) / expected` over duties the person was
  expected at → 0–100. `Excused` is removed from the denominator.
- *Contribution / Recognition / Conduct*: signed points summed, clamped to
  `[−MaxPointsPerPeriod, +MaxPointsPerPeriod]`, mapped to 0–100 around 50.
- *Observation*: mean rating over the scale → 0–100 (MoES Annex 6: points scored ÷ points
  expected × 100).
- *Wellbeing*: none. Never.

**Composite** = Σ(parameter score × weight) / Σ(weights of parameters that had any evidence).
Parameters with no evidence in the period drop out of the denominator rather than scoring zero, so
a term with no exams does not punish everyone for exam supervision. The policy refuses a single
weight above 50%, the MET ceiling. **Bands** map the composite onto MoES's five-point scale by
default (Excellent ≥ 90, Very Good ≥ 75, Good ≥ 60, Fair ≥ 45, Poor below), names tenant-editable,
count fixed so a stored rating means the same thing across periods. Observations keep their own
four-level scale; the composite never re-labels them.

**The summative appraisal** is a workflow, not a formula:

```
Open ──▶ targets agreed for the period (the performance plan)
     ──▶ Self-assessment (subject rates each parameter 1–5, writes reflection)
     ──▶ Appraiser review (sees score + breakdown + self-assessment + evidence; sets rating,
         strengths, development areas, a support plan, next period's targets)
     ──▶ Moderation (Director of Studies / Admin; may adjust with a recorded reason; sees the
         appraiser's distribution against the school's, the "rate everyone average" check)
     ──▶ Signed (score and breakdown frozen; PDF published to the Library)
     ──▶ Appealed (subject; a note; reopens to Moderation)
```

Reminders at each stage; a period cannot be closed while any appraisal is before `Signed` unless
an approver overrides with a reason. Three termly appraisals roll up into an annual one whose
rating is their average, exactly as the MoES guidelines say the termly reports "cumulatively
constitute the annual performance appraisal report". The appraisal PDF is the existing print-route
pattern with Publish to Library; sharing it outward is the Library's problem, not this module's.

---

## 8. Notifications, reminders, notices

**Event keys** (added to `NotificationEventKeys`; persisted in preferences, so treated as wire
format): `staff.record-logged` (about me), `staff.points-earned`, `staff.recognition-received`,
`staff.duty-reminder`, `staff.register-due` (to recorders whose duty has ended with no register),
`staff.appraisal-stage`, `staff.notice-published`, `staff.profile-changed` (department, line
manager, role), `staff.weekly-digest`. Each with a name, category "Staff Performance", and sensible
email defaults (digest and appraisal stages on; points-earned off, bell only). Also appended:
`NotificationType.StaffPerformance`, `NotificationDto.EventKey`.

**Who is told about a record, by visibility**, in one early return in `StaffAlertService`:

| Visibility | Subject | Line manager / head | Anyone else |
|---|---|---|---|
| Standard | full record | yes (unless they logged it) | no |
| Confidential | **existence and title only** | no | no |
| Restricted | no | no | no |

The Confidential row is where this deliberately departs from the welfare rule that a non-Standard
record alerts nobody: a child cannot be a recipient of their own record, an employee can and under
s.24 of the Data Protection and Privacy Act arguably should be. Decision 3 in §13 puts this to the
user rather than assuming it.

**Reminders** are one job class, `StaffPerformanceJobs`, several sweeps, the welfare pattern:

| Sweep | Cron | Gate |
|---|---|---|
| Duty reminder ahead of `StartsAt` | hourly | `ReminderSentAt == null`, lead time from policy (default 24 h), branch-local |
| Register not taken after `EndsAt` | hourly | `RegisterClosedAt == null && RegisterChaseSentAt == null`, to recorders |
| Appraisal stage due / overdue | daily 07:00 | `ReminderSentAt` with the 24 h re-notify window |
| Weekly digest to each staff member | hourly, sends at the policy hour on the policy weekday, branch-local | `LastDigestSentAt` per user in the preferences JSON |
| Monthly administrator summary | hourly, first business day | `LastSummarySentAt` in the org policy |
| Activity-log attribution purge | 03:30 daily | policy retention |

One reminder per row, gated by a timestamp: the codebase's documented rule ("no new reminder
table"). "Remind me 7 days, 1 day and 1 hour before" is out of scope on purpose; the portal's
*Coming up* list covers the near term without a schedule.

**Digest content** reuses `VisitorReportEmail`'s inline-styled table and KPI block helpers, which
are `private` today and are promoted into `EmailTemplates` in Phase 3. Rows go in the body, never as
an attachment, for the reasons that file records. The weekly digest to a person is: recognition
received, points and band movement, duties coming up, anything awaiting their response; the
monthly administrator summary is: band distribution, attendance by department, registers not
taken, appraisals by stage, observer dispersion, and the coverage list.

**Notices**: `staff.notices.manage` publishes to a branch, a set of departments, a set of roles, or
a staff group, optionally pinned, optionally requiring acknowledgement, with Library documents
attached. Publishing fans out one `Notification` per recipient (`EventKey =
staff.notice-published`), so the bell, email preferences, delivery log and per-person read state all
come free. The portal shows unread and pinned notices first, an *Acknowledge* button where
required, and the publisher sees "acknowledged by 38 of 52". The `Notification` row and the notice
row together are the read receipt; there is no third table.

**Live**: one new hub event, `StaffScoreUpdated(userId, periodKey, composite, points)`, pushed to
`user-{id}` after any record is finalised about them, consumed by the portal's score tile through
the same `On<T>` + unsubscribe-before-subscribe pattern `MainLayout` uses.

---

## 9. The shared component library: consolidate, then build

The library today has form controls, buttons, cards, modals, spinners, badges, export, upload,
date controls and a batch dialog. It has **no** timeline, stat tile, tab strip, chip, empty state,
bar list, avatar, rating, activity log, filter bar or table; each exists as page-local markup,
several times over (four rival stat-tile vocabularies, nine chip vocabularies, three tab strips).
The user asked for consolidation instead of reinvention, so Phase 0 lifts these, and every later
phase builds only from `Q*` components:

| New shared component | Lifted from | Also migrated onto it |
|---|---|---|
| `QTimeline` / `QTimelineItem` (marker colour+icon, connector, card slot, date, meta, actions, `--draft` / `--annulled`, day headers, load-older) | `StudentWelfareTimeline.razor` `.timeline-*` | `StudentWelfareTimeline` |
| `QStatTile` / `QStatRow` (uses the existing `--qm-statcard-*` tokens; optional `Href` drill-down; variants) | `WelfareReports.razor` `.stat-card*` | `Dashboard` tiles, `WelfareReports`, `DocumentLibrary` tiles, `WelfareTimelineReport` tiles |
| `QBarList` (label / track / count, colour per row, percent or absolute) | `WelfareReports.razor` `.bar-*` (including the `RenderTreeBuilder` copy) | `DocumentLibrary` page-dwell bars |
| `QTabs` (scrolls, never wraps) | `WelfareReports.razor` `.tab-strip` | `FeedbackManagement`, `VisitorReport`; delete the orphan `.tabs-container` CSS |
| `QChip` (status / removable filter / attachment link) | `.status-chip--*`, `.due-view-chip`, `.attachment-chip` | `WelfareOpenActions`, `WelfareReports`, `StudentWelfareTimeline`, `Appointments` |
| `QEmptyState` (icon, title, text, action) | `app.css` `.empty-state` + the per-page markup | new pages first; old pages opportunistically |
| `QActivityLog` (KPI strip, event table, CSV export, retention note) | `DocumentLibrary.razor` activity modal | the document activity modal itself |
| `QRating` (interactive and read-only; also the 4-level rubric picker) | `FeedbackPage.razor` `.star-rating` | `KioskMode`'s duplicate |
| `QPager` — **adopt**, add a "Showing x–y of z" caption | already exists, zero call sites | `Invoices`, `Tenants` hand-rolled pagers |
| `QFilterBar` (the class `layout.css` already targets for mobile wrapping) | `WelfareReports.razor` `.filter-bar` | new pages |
| `QAvatar` (initials, colour from name, optional photo) | three CSS-only classes | header, users grid, portal |
| `QPrintSheet` (A4 sheet, toolbar, Print, Publish to Library) | `WelfareTimelineReport.razor` `.wr-*` | `WelfareTimelineReport` |

Not built: a general `QTable`. Three approaches coexist and unifying them is a project of its own.
New list pages in this module use a plain `<table class="data-table">` inside a `QCard` with
`QPager` and `QEmptyState`, the smallest consistent choice; `RadzenDataGrid` stays where it is.
Charts stay Radzen (`RadzenChart` in a `QCard.chart-card`, fixed height, a real sentence in
`.chart-empty`); `QBarList` covers the proportional bars the reports mostly need.

`ViewerRequestInfo` is generalised so **authenticated** Web→API calls also carry `X-Viewer-Ip` /
`X-Viewer-Agent`; without that every `ActivityEvent` names the Web server. The relay is only wired
for the public share endpoints today.

---

## 10. Screens

**Portal** — `/portal` ("My Portal"), the landing page for `teacher`, `support-staff` and
`class-teacher`, and a nav item for everyone else when the module is active. Mobile-first: the
research on TELA's reception and the fact that most Ugandan staff will open this on a phone both
say the register and the portal must work on a small screen with a poor connection.

```
┌ Greeting · period · band pill · composite ring ─────────────────────────────────────────────┐
│ QStatRow: points this period · duties attended / expected · recognition received · open items │
├ Coming up (duties I am expected at; "Take register" if I am the recorder) ───────────────────┤
├ Reminders & to-do (staff items + WelfareOpenActions rows)  │ Notices (unread/pinned first; Acknowledge) │
├ My breakdown (QBarList per parameter, weight shown)        │ Recognition feed (received · given · Give)  │
├ My timeline (QTimeline of my records; Respond on each)     │ Appraisal card (stage · what I owe · open)  │
└ Notification centre · Preferences · Export my file ─────────────────────────────────────────┘
```

**Notification centre** — `/notifications`: full list grouped by `EventKey`, filters, mark-all-read
per group, paged with `QPager`. The bell dropdown stays the twenty-item preview.

**Administration** (nav group *Staff Performance*, gated `permission && HasModule`):
`/admin/staff` (directory: department, role, line manager, period band, last record; `QDataExport`),
`/admin/staff/{userId}/timeline` (+ `/report` print route), `/admin/staff/records` (search, log,
annul), `/admin/staff/duties` (roster by week; create; recorders; register), `/admin/staff/appraisals`
(period board by stage; open one), `/admin/staff/reports` (by department, by parameter, band
distribution, trend by week, observer dispersion, *who logs what* consistency check, coverage; every
panel a `QStatTile`, `QBarList` or `RadzenChart`; scoped-caller banner exactly as `WelfareReports`),
`/admin/staff/notices`, `/admin/staff/parameters`, `/admin/staff/structure` (departments, heads, line
managers; coverage), `/admin/staff/activity` (`QActivityLog`), `/admin/staff/policy` (periods, bands,
weights, caps, leaderboard switch, budgets, lead times, retention, DPO contact).

Every date through `QDateFormat`; every range through `QDateRangePicker`; every text input `@bind`;
nothing scrolls sideways; a scoped caller never sees a button that opens an empty modal.

---

## 11. Activity log

`IActivityLogger.RecordAsync(action, entityType, entityId, subjectUserId, summary, detail)` is
called explicitly from every write in the module (records, notes, annulments, visibility changes,
duty and register changes, appraisal stage changes, notice publish and acknowledge, structure
changes, parameter edits, exports and PDF publishes), plus sign-in and sign-out from
`AuthController`. Explicit calls, not an EF interceptor: an interceptor captures before/after state
indiscriminately, which for this subject means copying confidential text into a second table by
default.

- **Summary is written at the actor's visibility**, so a Restricted record's creation appears in the
  log as "Restricted record created for J. Okello" to a reader without the rung, never with its
  content.
- **A viewer sees the log scoped the same way as the records**: a head sees actions on their
  department's people; the subject sees actions on their own file (this is the subject-access
  trail: who viewed, who exported); administrators see the branch.
- **Retention** as `DocumentShareEvent`: events for ever, attribution blanked after the policy
  window (default 12 months; decision 6).
- **It is the staff timeline's second layer.** The timeline shows records; a toggle shows the
  actions between them (who changed visibility, who acknowledged, who exported).

---

## 12. Phases

Each phase ships on its own and is verified against the dev tenant before the next begins.

**Phase 0 — Groundwork and consolidation.** No new feature visible to a tenant yet.
- `ModuleCodes.StaffPerformance = "staff-performance"`, `ModuleCodes.All`, `ModuleRouteMap` (web
  `/portal`, `/admin/staff`; API `api/v1/branches/{branchId}/staff`), and **a production-safe
  module seed** (`ModuleCatalogDefaults`, insert-if-missing, all environments).
- Thirteen `staff.*` codes in all three catalogues; five roles in `RoleCodes` (API and Web mirror),
  `RbacSeeder.SystemRoles` **and** `Permissions.DefaultRoles`; `Role.StaffScope`; the role editor.
- `Department`, `User.DepartmentIds`, `User.LineManagerUserId`; `IStaffScopeService`; structure page
  and coverage endpoint.
- `Notification.EventKey`; `ActivityEvent` + `IActivityLogger`; `ViewerRequestInfo` on
  authenticated calls.
- Component lifts from §9, with the student pages moved onto them in the same change.
- Migration: `AddStaffPerformanceGroundwork`.

**Phase 1 — The ledger.** `PerformanceParameter` (seeded with the MoES set per tenant),
`StaffPerformanceRecord`, `Note`, `Attachment`; `UploadOwnerKind.StaffEvidence`;
`StaffAlertService`; parameters page; log-a-record and observation form; staff timeline and its
print route; the subject's *Respond*; portal v1 (timeline + my records + notification preferences).
Migration: `AddStaffPerformanceLedger`. e2e section 14a–c.

**Phase 2 — Duties and registers.** `StaffDuty`; roster page; recorder delegation; the register;
lesson recovery; minutes to the Library; duty reminders and register chase in
`StaffPerformanceJobs`; *Coming up* on the portal. Migration: `AddStaffDuties`. e2e 14d.

**Phase 3 — The hub.** Notices; recognition with budget; notification centre; digest email (promote
the `VisitorReportEmail` helpers into `EmailTemplates`); `StaffScoreUpdated` push; portal v2 as
drawn in §10. Migration: `AddStaffNotices`. e2e 14e.

**Phase 4 — Scoring and reports.** Policy JSON with one reader; per-parameter and composite
scores; bands; reports page with scoped banner and observer dispersion; leaderboard behind the
policy switch; monthly administrator email; Publish to Library on the reports. No migration. e2e 14f.

**Phase 5 — Summative appraisal.** `StaffAppraisal` and its stages; targets at Open; appraisal
board; the appraisal form with the support plan; frozen snapshot; signed PDF to the Library;
appeals as notes; stage reminders; termly roll-up to annual. Migration: `AddStaffAppraisals`. e2e 14g.

**Phase 6 — Blend, onboarding, hygiene.** System-source awards from welfare, tokens and visitors
(policy-gated); `RosterImportKind.Staff` bulk import with invite-by-reset-link; *Export my file*;
activity log page and retention purge; structure coverage on the dashboard. Migration:
`AddStaffImportKind`. e2e 14h.

---

## 13. Decisions needed before building

1. **Role positions.** All five new roles below `manager` (proposed), so none inherits the
   manager-tier overrides in the visitor module. Alternative: Director of Studies at manager rank.
2. **Leaderboard default.** Proposed: individual ranks private, department comparison visible to
   `staff.reports.view`, a public top-N board **off** unless the tenant switches it on. §1.5 is the
   reason; a school that wants the board can have it.
3. **What a Confidential record tells its subject.** Proposed: existence and title, no content, no
   third party. Alternative A: full content to the subject. Alternative B: nothing, matching the
   welfare rule exactly. Restricted stays silent under all three. **Settled 2026-09-17: the
   notification is title-only; the subject reads the full record on their portal (amendment 2).**
4. **Automatic credit from system activity** (welfare records filed, tokens served, visitors
   hosted). Proposed: built in Phase 6, **off by default**, low points, per-period caps, labelled
   automatic.
5. **Support Staff and points.** Proposed: they hold the portal and recognition and are appraised by
   their line manager; scored parameters apply only where `AppliesTo` says so.
6. **Retention.** Proposed: activity-log attribution 12 months; appraisal rows kept for the
   employment plus 6 years (a common HR retention period; the school's own policy under the Act's
   retention principle governs) and never purged automatically in v1.
7. **Seats and price.** The module's list price, and whether purchasing it lifts the plan's user cap
   or the cap becomes a per-seat charge. `POST /api/v1/users` returns 402 at the cap today.
   **Superseded 2026-09-17: part of the Student Welfare module, whose cap is 250 a branch
   (amendment 1).**
8. **Periods.** Proposed: three terms per year with an annual roll-up, dates entered in the policy
   page per year, the current period derived from today's date. Alternative: months.
9. **Rewards beyond recognition.** Proposed for v1: certificates (Library PDF), notices and the
   private rank. No redemption catalogue, no monetary value and no salary link; the research says
   each of those is a decision with a cost, and the Ministry has itself declined to link TELA
   attendance to pay.
10. **Wellbeing pulse.** Not requested. If wanted, a fortnightly one-question check-in reported only
    in aggregate is the shape the sources support; it would be a later phase and never a parameter.

---

## 14. Verification

There is no test project and that is the decision. Extend `scripts/e2e/class-teacher-e2e.sh` with a
section 14 that first grants `staff-performance` to the tenant as SuperAdmin via
`PUT /api/v1/admin/tenants/{org}/modules/staff-performance` (the trial rule refuses the tenant
purchase route), resolves its own ids, fails loudly on missing setup, and cleans up what the API
allows (records are append-only and stay, labelled dummy). The assertions that matter are the
negative ones:

- a head of department with no department sees **nobody** (fail closed), and gets 404 not 403 on a
  colleague outside their department;
- a teacher cannot `POST` a record about anyone, but **can** take the register on a duty they are
  named recorder for, and only for that duty's expected list;
- a Confidential record about a teacher is invisible to their head, readable by them in full with a
  title-only notification (amendment 2), and its evidence file returns 404 to the head and 200 to
  the subject;
- a Restricted record is invisible to everyone but `staff.restricted.view` holders, including its
  subject, and the activity log names it without content;
- a scoped caller's report summary carries `ScopedToDepartments` and the page shows the banner;
- a bulk import by a scoped caller is refused at the controller;
- a signed appraisal's `ComputedScore` does not change when a later record is annulled;
- a recovered lesson offsets a missed one in the attendance score;
- the weekly digest and the register chase each send once and not twice across two sweeps;
- a recognition beyond the monthly budget is refused with the budget in the message;
- the module seed creates the catalog row on a clean database with `IsDevelopment() == false`.

Verify the portal in Chrome on the dev tenant with real data seeded through the API, the mobile
layout by measuring `scrollWidth`, and the print route by reading the resolved `@page` rules. Say
which was done.

---

## 15. Sources

Official frameworks
- Uganda Ministry of Education and Sports — [Performance Management Guidelines for Tertiary Institutions and Schools (May 2020)](https://education.go.ug/wp-content/uploads/2020/04/Performance-Management-Guidelines-May-2020.pdf): lesson attendance register, lesson recovery schedule, one observed lesson per term, termly review and support plan, 1–5 rating scale, annual appraisal by 31 December, rewards via the Teacher Incentive Framework
- Uganda MoES — [National Teacher Policy (2019)](https://www.education.go.ug/wp-content/uploads/2022/04/National-Teachers-policy.pdf): a motivation framework, head teachers to "regularly appraise staff", recognition of exemplary teachers by department and school
- Uganda — TELA teacher attendance monitoring: [The Independent, 10 May 2026](https://www.independent.co.ug/ministry-teachers-salaries-wont-depend-on-tela-attendance-yet/), [Uganda Radio Network](https://ugandaradionetwork.net/story/ministry-struggles-with-inspections-new-system-to-combat-teacher-absenteeism-ignored): 32% uptake, pay link declined as premature
- Kenya TSC — TPAD: [Institution Today](https://institutiontoday.com/tsc-tpad-appraisal-benefits-objectives-process/), [Education News](https://educationnews.co.ke/tpad-the-ruthless-tsc-scorecard-where/), [Newsblaze on the reduction to five standards](https://newsblaze.co.ke/tsc-customizes-the-tpad-online-system-reduces-number-of-tpad-teaching-standards/): termly self-appraisal → observation → rating meeting → countersign, online with audit trails (the exact original seven standards and per-standard scale were not verified against a TSC primary document)
- England — [The Education (School Teachers' Appraisal) (England) Regulations 2012](https://www.legislation.gov.uk/uksi/2012/115/contents/made) and [Teachers' Standards](https://www.gov.uk/government/publications/teachers-standards): objectives, a written appraisal report, development needs
- OECD — [Teaching in Focus No. 6: Unlocking the potential of teacher feedback (TALIS 2013)](https://www.oecd.org/content/dam/oecd/en/publications/reports/2014/09/unlocking-the-potential-of-teacher-feedback_g17a253f/5jxwvhjc0ghj-en.pdf) and [Results from TALIS 2024](https://www.oecd.org/en/publications/results-from-talis-2024_90df6235-en/full-report/the-demands-of-teaching_0e941e2f.html): appraisal seen as administrative by nearly half; what happens afterwards is what varies
- Rwanda STARS — [IGC blog](https://www.theigc.org/blogs/selection-and-incentive-effects-teacher-performance-contracts-rwanda): the "5 Ps" composite, 3% bonus, 0.16 SD learning gain
- Tanzania OPRAS — [Matete (2016), ERIC EJ1149370](https://eric.ed.gov/?id=EJ1149370): a form without a feedback loop is ineffective
- Uganda — [Mbabazi et al. (2025), F1000Research](https://pmc.ncbi.nlm.nih.gov/articles/PMC12824474/): top-down, superficial discussions, results rarely tied to development

Observation and measurement
- [Danielson Framework for Teaching (2022)](https://danielsongroup.org/the-framework-for-teaching/) and [Wisconsin DPI transition overview](https://dpi.wi.gov/sites/default/files/imce/ee/pdf/wi-2022-danielson-teacher-overview.pdf): 4 domains, 22 components, four levels; a rating "does not define the educator"
- MET Project — [Ensuring Fair and Reliable Measures of Effective Teaching (2013)](https://files.eric.ed.gov/fulltext/ED540958.pdf) and [Ho & Kane, The Reliability of Classroom Observations by School Personnel (2013)](https://files.eric.ed.gov/fulltext/ED540957.pdf): multiple measures, weight caps, second observers, score compression, the "home field" tenth of a point

Motivation, recognition, rewards
- Deci, Koestner & Ryan (1999), [A Meta-Analytic Review of Experiments Examining the Effects of Extrinsic Rewards on Intrinsic Motivation](https://home.ubalt.edu/ntygmitc/642/Articles%20syllabus/Deci%20Koestner%20Ryan%20meta%20IM%20psy%20bull%2099.pdf): contingent tangible rewards d ≈ −0.3; positive feedback d ≈ +0.3
- Gallup — [In Praise of Praising Your Employees](https://www.gallup.com/workplace/236951/praise-praising-employees.aspx) and the [Q12](https://www.gallup.com/q12-employee-engagement-survey/): "recognition in the last seven days", 10–20% productivity variance
- SHRM/Globoforce — [Employee Recognition Survey 2015](https://www.businesswire.com/news/home/20150622005430/en/): values-based recognition, 90% report engagement impact
- Kohn — [Punished by Rewards](https://www.alfiekohn.org/punished-rewards/) and [The Folly of Merit Pay](https://www.edweek.org/teaching-learning/opinion-the-folly-of-merit-pay/2003/09)
- Landers, Bauer & Callan (2017), [Gamification of task performance with leaderboards](https://dl.acm.org/doi/abs/10.1016/j.chb.2015.08.008): a leaderboard is an imposed stretch goal
- Na & Han (2023), [How leaderboard positions shape our motivation](https://www.emerald.com/intr/article/33/7/1/178330/): top coast, bottom burn out; relative and team-level boards, regular resets
- Bonusly — [rewards](https://bonusly.com/features/rewards), [redeeming](https://help.bonus.ly/en/articles/357138-redeeming-rewards): monthly giving allowance, values tags, feed

Modern performance systems and staff portals
- Buckingham & Goodall, [Reinventing Performance Management (HBR, 2015)](https://hbr.org/2015/04/reinventing-performance-management): the 62% rater effect, the four-question snapshot, weekly check-ins
- Google re:Work — [Project Oxygen](https://rework.withgoogle.com/intl/en/guides/following-the-data-the-research-behind-great-managers): ten manager behaviours as an upward-feedback template
- [Lattice](https://lattice.com/platform/performance), [15Five](https://www.15five.com/products/perform/check-ins), [Culture Amp](https://www.cultureamp.com/platform/perform): continuous check-ins, calibration, goals feeding reviews
- School MIS staff modules — [Arbor staff absence and Bradford factor](https://support.arbor-education.com/hc/en-us/articles/360016197477), [Bromcom cover and absence thresholds](https://bromcom.com/all-through-schoolmis), [ESS SIMS Staff Performance](https://ess-sims.co.uk/products-and-services/sims-staff-performance): observations and objectives against professional standards, CPD evidence linked to objectives
- Employee-experience hubs — [Microsoft Viva](https://learn.microsoft.com/en-us/viva/microsoft-viva-overview), [Workvivo recognition](https://www.workvivo.com/blog/employee-recognition-feature-spotlight/): feed, praise, pulse, mobile-first

Staff wellbeing
- Education Support — [Teacher Wellbeing Index 2024](https://www.educationsupport.org.uk/media/ftwl04cs/twix-2024.pdf): 78% stressed; appreciation and line management among the culture drivers
- DfE — [Education Staff Wellbeing Charter](https://www.gov.uk/guidance/education-staff-wellbeing-charter): eleven employer commitments
- WHO — [Mental health at work](https://www.who.int/news-room/fact-sheets/detail/mental-health-at-work): psychosocial risks, organisational interventions first
- D'Sa, Ariapa, Nsubuga et al. (2025), [Well-being ASSETS, Uganda](https://www.tandfonline.com/doi/full/10.1080/03057925.2025.2571466): a Ugandan teacher-wellbeing measure valid for distribution-level monitoring, not individual comparison

Data protection
- Uganda — [Data Protection and Privacy Act, 2019](https://ulii.org/akn/ug/act/2019/9/eng@2019-05-03/source): s.3 principles, s.6 data protection officer, s.24 access within thirty days, s.25 objection, s.29 registration
- UK ICO — [Monitoring workers](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/employment/monitoring-workers/data-protection-and-monitoring-workers/), [Subject access requests: Q&As for employers](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/employment/subject-access-request-q-and-as-for-employers/), [Purpose limitation](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/data-protection-principles/a-guide-to-the-data-protection-principles/purpose-limitation/)

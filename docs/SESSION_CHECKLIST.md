| # | Reported | Status | Finding / fix |
|---|---|---|---|
| H1 | "I am not expecting platform admin role to appear in the tenant management panel" — `super-admin` shown on the tenant Users & Roles page | `[x]` | fixed and verified |
| H2 | "scan codebase for similar leakages and properly gate the features based on platform / tenant" | `[x]` | fixed and verified |
| H3 | "some roles belonging to student and staff welfare should only be visible to subscribers who have enabled the module… it makes no sense showing a business the default roles of a school" | `[x]` | fixed and verified |
| H4 | "that system badge looks distorted" — the SYSTEM chip renders as a tall block behind the text | `[x]` | fixed and verified |
| H5 | "for all permissions, when i click edit i get error" — *An unhandled error has occurred* | `[x]` | fixed and verified |
| H6 | Observed in the same screenshots, not yet reported: every role reads PERMISSIONS (0) | `[~]` | investigating — Phase 85 recorded fixing exactly this; need to establish whether prod predates that build |

**Reported against `qmgr.cashbook.ug` (production).** First question for every one of these: does the deployed build predate the fix? The branch holding the Phase 85 Roles-tab fixes has never been merged or deployed.# Session checklist — 2026-09-18

Live working log for this session. **One home for every item**: everything the audit found, every
decision taken, and every bug the user reports from their own testing goes here, so nothing is lost
to context compaction. Update status inline as work progresses.

Status: `[ ]` queued · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision · `[-]` won't do (with reason)

**Branch** `master` — the only branch since 2026-09-18; started from `3b0544d`

---

## A. Decisions taken this session

| # | Question | Answer |
|---|---|---|
| A1 | Shape of the branch-change fix | **Shared `BranchAwareComponentBase`** — one home, ~70 files |
| A2 | Where the welfare export/publish audit row lives | **Reuse `ActivityEvent`** + a nullable `SubjectStudentId` |

Both were the blocking decisions in the handover's §1. Neither needs re-litigating.

---

## B. Tier 1 — self-contained fixes (no decision needed)

- [x] **B1. Radius token drift** — `EvacuationReport.razor:212` `.evac-badge` used a raw
      `border-radius: 4px`; now `var(--qm-radius-md)`. The only genuine drift outside the
      deliberately excluded kiosk/public/print set (`999px` pills are geometry, correctly left).
- [x] **B2. Dead CQRS message** — `GetCounterTokensQuery` (`GetQueueStatusQuery.cs:38`) had no
      handler and no sender, and warned `MSG0005` on every build. Deleted. Its two siblings in the
      same file both have handlers and senders and were left alone.
      **Removing it immediately surfaced a SECOND dead message the first had been masking** —
      `CreateTokenBulkCommand` (`CreateTokenCommand.cs:22`), also with no handler and no sender.
      Also deleted. This is the argument for the fix in one line: an expected warning hides real ones.
- [x] **B3. Dead field** — `Dashboard.branchStateInitialized` assigned, never read (`CS0414`).
      Removed; the branch subscription itself was already correct and guards `HasBranches` and
      `Guid.Empty`.
- [x] **B4. Fire-and-forget async** — `Tenants.razor:270` called `ShowModulesDialog(...)` without
      awaiting inside a click lambda (`CS4014`), so an exception in it went unobserved. Now
      `async () => { ...; await ... }`.
- [x] **B5. Four `CS0108` shadowing warnings**, each resolved on its own merits rather than
      blanket-silenced:
  - `Student.IsActive`, `StudentGuardian.IsActive` — byte-identical redeclarations of
    `BaseEntity.IsActive`. Deleted; they now inherit it. Same column, same type, same default, so
    no model or migration change.
  - `StudentFlag.IsActive` — **hiding is intentional and correct**: it is a computed
    `EndedAt == null`, explicitly `builder.Ignore(f => f.IsActive)`d in
    `StudentFlagConfiguration` with a comment saying mapping one would let the two disagree, and
    every query filters `EndedAt == null` in SQL. Marked `new` to say so.
  - `StaffLessonsController.Truncate` — a silent hard cut hiding the base's
    `StaffPerformanceControllerBase.Truncate`, which appends an ellipsis. Deleted the copy so the
    three call sites inherit the base: a 2000-char cancellation note cut with no indication reads
    as if the text simply ended.

---

## C. Tier 2 — `BranchAwareComponentBase` (decision A1) — DONE

- [x] **C1.** `Components/Shared/BranchAwareComponentBase.cs` — injects `IBranchStateService`,
      `TrackBranch()` subscribes and records the branch, `OnBranchChangedAsync(Guid)` is the one
      thing a page implements, `Dispose` unsubscribes. The base owns the guard (unchanged / empty),
      the `InvokeAsync` marshalling, the disposed check and the `StateHasChanged` afterwards.
      `OnBranchChangeFailed` exists because the handler runs from an `async void` event: an
      unhandled exception there tears the circuit down, so a failed reload must not throw the user
      out of the app.
- [x] **C2.** All **33** pages that read the branch and never re-read it are on the base.
- [x] **C3.** All **26** pages that hand-rolled the triple are on it too, so there is one home
      rather than two. **This was not only tidying:** the common hand-rolled shape was
      `_ = InvokeAsync(async () => { await LoadAsync(); StateHasChanged(); });` — fire-and-forget,
      with no guard on an unchanged or empty branch and no disposed check. The base fixes all three.
- [x] **C4.** `@using QMgr.Web.Components.Shared` added to `_Imports.razor`. It was missing, which is
      the same out-of-tag-scope trap that left `RedirectToLogin` inert for weeks. Checked for name
      collisions with `Shared.UI` first: none.

**Verified structurally:** 59 pages inherit the base; **0** still reference
`BranchState.OnBranchChanged`; 0 inherit without `TrackBranch()`; 0 inherit without the handler;
0 shadow the base's injection with their own `@inject`.

**The 13 that still do not react are exactly the ones that should not:** `MainLayout` (it is the
raiser) and the five public routes whose branch comes from the URL — `CustomerDisplay`,
`SignageDisplay`, `KioskMode`, `DisplayLayout`, `KioskLayout` — plus the seven one-shot print routes.

**Two pages were wrongly on the candidate list and are not in scope:** `CustomerLinks` and
`JoinRequests` have their own `OnBranchChanged` *dropdown* handlers, unrelated to the branch service.

**Per-page care that a script could not have given:** `VisitorDisplayBoard` and `QueueBoard` and
`CounterTerminal` each move a **SignalR group / connection** with the branch — without that the board
keeps receiving the previous branch's activity and none of the new one's. `StudentRoster`,
`StudentPicture`, `StudentWelfareTimeline`, `StaffRota`, `StaffDuties`, `StaffNotices` and
`Appointments` each clear branch-scoped lookup caches whose `.Any()` guards would otherwise offer the
previous organization's data to a form the API then refuses.

## D. Tier 3 — welfare export/publish logging (decision A2) — DONE

- [x] **D1.** `ActivityEvent.SubjectStudentId` (nullable) + migration
      `20260918161245_AddActivityEventStudentSubject` — **one column, additive only**, so every
      existing row is untouched and behaviour is unchanged until something writes it.
      `IActivityLogger.RecordAsync` gained an optional `subjectStudentId`, so no existing caller changed.
- [x] **D2.** `WelfareController` now has an `IActivityLogger` (it had none at all) and two endpoints:
      `POST branches/{b}/welfare/activity/exports` and `GET branches/{b}/welfare/activity`.
      New wire constants `WelfareActivityActions` (`welfare.report.published`, `welfare.list.exported`,
      `welfare.timeline.exported`) and `WelfareExportKinds`, in `Q-Mgr.Shared` beside the staff ones.
- [x] **D3.** Both endpoints gated on `welfare.reports.view` **or** `welfare.reports.own`, and the
      write calls `VerifyStudentAccess` for a named student — 404, never 403. The read applies
      `IStudentScopeService`: a scoped caller sees events about their own students, plus their own
      subject-less exports, so a class teacher cannot read the school's export history. Visibility is
      set at the record's rung — a student carrying restricted notes makes the event Confidential.
- [x] **D4.** Web wired through one helper, `Services/WelfareActivityReporting.cs`:
      `WelfareTimelineReport` reports the Library publish (replacing the comment that admitted the
      gap), `WelfareReports` reports CSV/XLSX via `QDataExport`'s `OnExported`. The helper **never
      throws and never toasts** — an audit row must not fail an export whose file the reader already
      has, the same reasoning as `TryIssueVisitToken`.
- **Not** routed through `POST …/staff/activity/exports`: that resolves every kind to a `staff.*`
  permission and writes an event about a member of staff. The subject here is a child.

## E. Open, but deliberately not being done

- [!] **E1. `MediaLibrary` cold-navigation guard.** The carried note says it shares the Document
      Library's race. The guard is where the note says (`MediaLibrary.razor:1269-1281`), but the
      **stated mechanism does not survive a code read**: `MainLayout` renders `@Body` only inside
      `@if (!authChecked)`'s else branch and `authChecked` is set only after
      `AppInit.InitializeAsync()` completes, so a page under it cannot run against an uninitialised
      token store. And 76 pages redirect to `/unauthorized` while exactly one calls `AppInit` — if
      the mechanism were real this would be a 75-page outage, not a latent edge case.
      **Reproduce the original Document Library bounce before sweeping anything**; something real
      happened and the recorded cause does not explain it.
- [-] **E2. Two person-facing `yyyy-MM-dd` strings** (`RosterImportProcessorJob.cs:498`,
      `BatchController.cs:217`) — deliberate: ISO cannot show the Sept/Sep ambiguity the culture
      rule exists to fix.
- [-] **E3. Two reminder-ladder observations** — `DueStage` already collapses to the highest due
      stage (changing it would invent reminder policy the e2e asserts), and
      `TimetableIntegrityJob`'s NULL-array claim is unreachable on a non-nullable property.
- [-] **E4. `S3MediaStorageService` unexercised** — user said leave it (2026-09-18). Serving is the
      blocker, not storing: `UploadsController.cs:126` returns `PhysicalFile` from the local store.
- [-] **E5. `AdvancedAnalytics` / `WebhookIntegration` feature codes** — no endpoint exists to
      attach them to. A missing-feature gap, not a wiring gap; out of scope.

---

## F. Stale — verified closed in code, to strike from the tracker

- [x] F1. "Should a class teacher have Welfare Reports at all?" — decided; `welfare.reports.own`
      exists with `RequirePermissionAny` on three endpoints.
- [x] F2. Notification preferences endpoints with no UI — `Pages/NotificationPreferences.razor`.
- [x] F3. Hardcoded default branch id on kiosk/display — none remains; `PublicDisplayRoute` resolves.
- [x] F4. `TransferTokenCommand` a no-op 500 — no orphan command; endpoint live at
      `CountersController.cs:138`.
- [x] F5. Integration adapters "unregistered dead code in the API" — they live in their own
      `Q-Mgr.IntegrationSdk` project. A partner conversation, not engineering.
- [x] F6. `ExportReports` feature code unwired — wired at `ReportsController.cs:103`.
- [x] F7. Bare `{x:dd MMM}` API dates — swept; both remaining `dd MMM` sites pass `InvariantCulture`.

---

## G. Contract checks that came back clean (so nobody re-runs them)

| Check | Result |
|---|---|
| Three permission catalogues in sync | **91 / 91 / 91**, zero drift either way |
| `@oninput=` under `Components/` | 0 |
| Native `<select>` | 0 |
| Raw SQL schema-qualified | 0 unqualified across 20 call sites |
| `IQueueApiService` callers ignoring the return | 0 |
| `RZ10012` (unresolved component tag) | 0 |
| `TODO` / `FIXME` / `NotImplementedException` | 0 |
| Build | 0 errors, 30 warnings (the 4 above + nullability noise) |

---

## H. Bugs reported by the user from their own testing

Reported against **`qmgr.cashbook.ug` (production)**, which runs **`master`** — a build that predates
this branch. That matters: for H4, H5 and H6 "already fixed here" and "broken in production" are both
true, confirmed by diffing `master`'s own copy of the page. H1 and H3 are real in **current** code.

| # | Reported | Status | Finding / fix |
|---|---|---|---|
| H1 | Platform Admin listed on the tenant Users & Roles page | `[x]` | **Real in current code.** `RolesController.GetRoles` admitted every `OrganizationId == null` row and every seeded role has that. Fixed via a new one-home rule, `RoleCodes.IsVisibleToTenant`, applied in `GetRoles` **and** `GetRole` — the by-id route leaked the same role's full permission list, and now answers 404, never 403 |
| H2 | "scan codebase for similar leakages" | `[x]` | Swept every `OrganizationId == null` site. Found **H8 below**, which is materially worse than what was reported |
| H3 | School roles shown to a business that has not enabled the module | `[x]` | **Real in current code.** `class-teacher` plus the five Staff Performance roles now map to `ModuleCodes.StudentWelfare` through `RoleCodes.ModuleFor` and are hidden unless that module is active. Visibility only: a tenant that cancels the module keeps anyone already holding the role — revoking a module must not silently strip access |
| H4 | SYSTEM badge distorted | `[x]` | **Production only.** On `master` the badge is a direct child of `.role-header`, a flex row with **no `align-items`**, so `align-items: stretch` + its own `margin-left: auto` stretched it to the full header height — the tall pink block. This branch already wraps it in `.role-badges` (a column, `align-items: flex-end`). Hardened further: `.role-header` now sets `align-items: flex-start` so the shape cannot come back |
| H5 | "Edit Permissions" throws *An unhandled error has occurred* | `[x]` | **Production only.** `master` shows "Edit Permissions" on system roles and posts into an editor whose grouped-permissions response it reads as flat. Phase 85 fixed all three Roles-tab bugs; this branch shows **View Permissions** on a system role and refuses the write server-side |
| H6 | Every role reads PERMISSIONS (0) | `[x]` | **Production only.** `master` renders `role.Permissions.Count` against a list DTO that carries no permissions. This branch fetches each role's detail and maps the real list |
| H8 | *Found while scanning for H2 — not reported* | `[x]` | **Privilege escalation.** `UsersController.CreateUser` and `UpdateUser` took any `RoleId`, resolved it with a bare `FindAsync` and assigned it, with **no rank check at all**. A tenant Admin holding `users.create`/`users.edit` could mint or promote a **Platform Administrator** — a role that bypasses every permission check and reaches every organization. `RoleAssignmentGuard` (which refuses `super-admin` outright) existed and was called by the join-request and staff-import paths, but not by either of these. Both now call it |

**H7 (process, raised by the user):** *"some of these i expect you to identify them already when doing
your end to end tests."* Fair, and H8 is the sharper version of the point — e2e section 13 covers
privilege escalation and did not catch it, because it tests the *join-request* path, which was
guarded, and never posted a role id to `POST /users`. H1, H3, H4 and H5 are a page's own filtering, a
CSS rule and a client-side exception: none is an API-response shape, so a curl suite cannot see them.
Two gaps to close, logged as I3/I5 below.

## I. The comprehensive e2e — RUN, and green

| Suite | Result |
|---|---|
| `class-teacher-e2e.sh` sections 0–14 | **512 / 0** (was 494; +18 = exactly the new assertions, none skipped) |
| `duty-rota-e2e.mjs` section 15 | **254 / 0**, 15.9 skipping honestly at 19:33 — the documented time-of-day behaviour, 263 with it |
| `browser/roles-and-branch-ui.mjs` (new) | **25 / 0** |

**Directly verified against the live API, not inferred from the count:**

    GET /roles as tenant admin  → academic-assistant, admin, class-teacher, director-of-studies,
                                  head-of-department, manager, staff, support-staff, teacher, viewer
                                  (no super-admin)
    GET /roles/{super-admin id} → 404
    POST /users with that role  → 400 "The platform administrator role cannot be assigned here."

The refusal is `RoleAssignmentGuard`'s own wording, so the guard is doing the work rather than an
incidental validation failure.

**The reload was proven, not assumed.** Rendering without throwing is not reloading, and the dev
tenant had only ONE branch, so a switch could not be exercised at all. Created a second branch,
which made Student Roster observably different (6 students against 0): the roster went
**7 rows → 0 → 7** across two switches, **in place** (same path, no full page navigation), with no
error bar and no console errors. The probe branch was deleted afterwards; the tenant is as it was.

**Two failures in the first browser run were MY test's bugs, not the app's** — worth recording so
nobody re-chases them:
- `#blazor-error-ui` exists in every Blazor app and is hidden by a **stylesheet**, so
  `:not([style*="display: none"])` matched it as visible on every page. It must be read with
  `getComputedStyle(el).display`. Six false failures came from this.
- `/reports/queue-analytics` is not a route; Queue Analytics is `/reports/queue`.

**Notes for the next run**
- The dev tenant does **not** have `core-queue` or `visitor-management`; Counters and Service Types
  are module-gated and a counter cannot be created there. Grant and revoke as SuperAdmin if a test
  needs them, as the handover already says.
- Welfare categories are ORGANIZATION-scoped, not branch-scoped — they cannot be used to observe a
  branch switch. Students can.
- [x] **I1.** Migration applied (`20260918161245_AddActivityEventStudentSubject`).
- [x] **I2 / I3 / I4 / I5.** All run and green, as above.

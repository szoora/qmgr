# Q-Mgr Web

Multi-tenant front-office SaaS — queues, visitors, signage, student welfare, secure documents (the customer-facing descriptor is "Front-Office Platform", decided 2026-09-15; the queue is one module, not the product). ASP.NET Core API (`src/Q-Mgr.API`) + Blazor Server web app
(`src/Q-Mgr.Web`), Postgres via EF Core, CQRS via the `Mediator` source-generator library (not
MediatR). **Git was adopted 2026-08-25**, and **there is a GitHub remote:
`origin` → `https://github.com/szoora/qmgr.git`** — this line said "local-only, no remote" until
2026-09-06, which was stale and materially misleading: anything that rewrites history here is
published, not private. Before that it said no git repo existed at all. See `git log` for history
from 2026-08-25 forward rather than this file's older prose for anything after that date.


**This repository keeps ONE branch: `master`. Work on it directly** (user instruction, 2026-09-18:
*"we need to maintain single branch please"*). The two long-lived feature branches were consolidated
that day — `phase-82-scope-sweep-and-platform-email` was already fully contained in
`phase-85-staff-performance`, which fast-forwarded into `master` at `57eb5a6` with no merge commit —
and both were deleted locally and on `origin`. Older tracker entries name those branches; they are
history, not somewhere to push. **Do not open a feature branch without asking**, and note this
overrides the general "if on the default branch, branch first" habit.

**History was rewritten on 2026-09-06 to strip Claude attribution trailers** (`Co-Authored-By:
Claude …` and `Claude-Session: …`) from all 108 commits, then force-pushed. Commit SHAs before that
date in older tracker entries therefore no longer resolve — the entries were left as written rather
than rewritten, so treat any pre-2026-09-06 SHA as a historical label, not a lookup key. New commits
carry no attribution: it is turned off in the user's `~/.claude/settings.json`.

## Design system reference

The user shared `D:\QMGR\Webster` as the visual reference to bring the app's UI in line with —
a "Webster" multipurpose HTML5 template (Potenza Global Solutions), 504 static template pages
under `Webster\templates\`. It's a general marketing-site kit (about/blog/portfolio/shop/pricing
pages), **not** a SaaS admin-dashboard kit, so the relevant extraction is the underlying design
language and a handful of "elements" pages that resemble dashboard UI — not full page layouts,
since Webster's marketing-page structure (header/hero/footer) doesn't match Q-Mgr's admin shell
(sidebar + topbar).

**Most relevant Webster pages/assets for this project** (element/component patterns to match,
picked because they're the closest analogues to Q-Mgr's own screens):
- `elements-data-table.html` / `elements-datatables.html` → Q-Mgr's admin list pages (Users,
  Branches, Counters, etc.)
- `elements-form.html` → Q-Mgr's settings/create/edit forms
- `invoice.html` → `src/Q-Mgr.Web/Components/Pages/Billing/Invoices.razor`
- `elements-pricing-tables.html` → `src/Q-Mgr.Web/Components/Pages/Billing/Subscription.razor`
- `widget.html` → dashboard summary cards (`Dashboard.razor`)
- `event-calendar.html` → anything with date/schedule pickers (`Content/Schedules.razor`)
- `css/skins/skin-blue.css` → superseded, see color decision below (Q-Mgr's brand color is no
  longer blue)

**Extracted tokens and how they map to `qm-theme.css`** (`src/Q-Mgr.Web/wwwroot/css/qm-theme.css`,
the single source of truth for all `--qm-*` custom properties):
- **Typography — already applied.** Webster uses Montserrat (headings, 600-800 weight) + Poppins
  (body, 400-700 weight). This is already wired: `--qm-font-display: 'Montserrat'` /
  `--qm-font-primary: 'Poppins'` in `qm-theme.css`, fonts loaded in `App.razor`. Keep using these
  two families for anything new — don't introduce a third.
- **Color — superseded 2026-08-19: brand color changed from blue to wine/burgundy, NOT matched to
  Webster's palette either.** The original blue (`#0058cc`) was deliberately kept distinct from
  Webster's own default/skin-blue (green `#84ba3f` / sky blue `#299be8`) — but the user later
  clarified (2026-08-19) that the *reason* Webster was provided as a reference was specifically to
  get away from the generic-AI-dashboard look, and that the blue-gradient/neon-glow treatment the
  app had (diagonal gradients on every hero card and icon badge, glowing button/avatar shadows,
  gradient login page with floating decorative blobs) **was itself the tell**, not a distinct
  identity. `--qm-primary` is now `#8c2f52` dark-theme / `#7a2847` light-theme (wine/burgundy) —
  a genuinely different hue from both the old blue and Webster's own green/sky-blue, chosen per
  explicit user direction to "pick a genuinely different accent color." All gradients flattened to
  solid fills and neon glow shadows removed at the same time, matching Webster's flat convention.
  **This is now the deliberate color decision — do not revert to blue.** If a further color change
  is ever wanted, that's still a decision to confirm with the user first, not to infer.
  **Update 2026-08-21 — a pattern for "this color is too harsh" reports:** the user flagged
  `Subscription.razor`'s "current plan" banner (a full-bleed solid `var(--qm-gradient-primary)`
  block with white text) as "too harsh... maintain the light appearance." `--qm-gradient-primary`
  is a *shared* token used 18+ places app-wide, so changing it would be an unreviewed global
  rebrand — exactly what the line above says needs the user's sign-off first. Instead, rescoped
  just that one component: light card background (`--qm-bg-card`) with dark text, wine reserved
  for accents only (badge background via `--qm-primary-light`, price text, icons). If a future
  color complaint is about one specific page/component rather than the brand as a whole, prefer
  this pattern — scope the fix to that component, don't touch the shared token without asking.
- **Radius — one family, and it is the flat one (user decision 2026-09-18).** `qm-theme.css` carries
  `--qm-radius-sm/md/lg/xl` = **3/4/6/8px**, Webster's flat convention. Until 2026-09-18 a second,
  softer family lived beside it: `q-components.css` set its own `--q-btn/card/input/modal/toast-border-radius`
  at 8 and 12px, and 122 page-level literals ran from 2px to 24px. The user chose to unify on the
  tokens, so those five component tokens now resolve to `--qm-radius-md`/`-lg`, and every literal was
  mapped by value: **≤3px → sm, 4–10px → md (buttons, inputs, tiles, shell chrome), ≥11px → lg (cards,
  modals, panels)**. Pills (`50%`, `999px`, `9999px`, `50px`) and `0` were left alone — that is
  geometry, not a radius choice. **A new rule uses a token; a raw px radius is the drift coming back.**
  The kiosk, public display, signage, public feedback, booking, ticket status, shared documents and
  print sheets were deliberately excluded, the same set the size scale excludes.
- **Shadow — Webster's convention** is a soft, low-spread card shadow (`0px 3px 10px
  rgba(0,0,0,0.1)`) plus a larger ambient shadow for section depth (`0px 0px 50px rgba(0,0,0,
  0.05)`). Compare against `--qm-shadow-sm/md/lg` before changing anything broadly.

## Theme gaps — all three closed 2026-09-06; read this before reopening any of them

**These were carried as open from 2026-08-17 to 2026-09-06. They are now closed, and items 2 and 3
were already substantially built before that date — the notes had simply gone stale.** The audit
that closed them is Phase 68 in `docs/TASK_TRACKER.md`. Do not re-plan this work from the wording
below; it is kept for history, with each item's real state stated first.

**The display theme is per-organization, and that is settled (user decision, 2026-09-06.)** Item 3
below asked for the scope to be resolved. It is: one `Organization.DisplayTheme` for all of a
tenant's public screens. A per-display override was offered and declined. A branch with a bright
foyer screen and a dim ward screen picks one theme for both. **Do not add a per-display column
without asking again.**

**Neon glows are gone from the kiosk and the admin shell too (user decision, 2026-09-06.)** Phase 41
removed the glow treatment app-wide, but 15 survived: 12 in `KioskMode.razor`, 2 in
`FeedbackPage.razor`, 1 in `PlaylistPlayer.razor`, plus 5 decorative ones in `layout.css` itself
(sidebar brand logo, brand hover, active nav-link). All removed; every affected element kept a
non-glow affordance (a background change, or the `transform: scale` it already had). **Two glow
families were deliberately kept and are not oversights**: the green `token-pulse` /
`.now-serving-number` / `new-call` pulses, because those are functional attention signalling on
large-format signage seen across a room, not dashboard decoration; and `.star-btn.active`'s small
amber glow, which is the conventional "star lights up" affordance on a rating widget.

**A tint that sits under a themed accent must derive from that accent.** The audit's most
substantive find: `.category-chip.selected` and `.feedback-link-display` in `KioskMode.razor`
hardcoded `rgba(140, 47, 82, …)` — the *dark-theme* wine — behind a border and text colour that
already came from `var(--kiosk-accent)`. Wrong twice: it stayed dark wine in light mode, and on a
hospital/bank/pharmacy kiosk it painted a wine tint behind a red/blue/green accent. They now use
`color-mix(in srgb, var(--kiosk-accent) N%, transparent)`, each with a plain `rgba()` fallback
declaration immediately before it, because kiosk hardware can run browsers older than `color-mix`.
The same shape was fixed with `rgba(var(--qm-primary-rgb), α)` in `CustomerDisplay`,
`StudentWelfareTimeline` and `WelfareReports`. **`--qm-primary-rgb` is themed (140,47,82 dark /
122,40,71 light), so tokenizing a tint this way makes it follow the theme with no override needed
— prefer it over writing a new `[data-theme="light"]` rule.**

**What the audit checked and found already correct**, so nobody re-runs it: every `--qm-*` token in
`:root` has a light counterpart except 21 that are theme-invariant by nature (brand colours like
Facebook blue, font families, radii, transitions); the four global stylesheets hold no stray colour
literal; and `KioskMode`/`CustomerDisplay`/`FeedbackEntry` each carry a large, well-reasoned light
block, with `FeedbackPage` needing none because it is token-only throughout. The `color: white`
that remains in `CustomerDisplay`, `FeedbackEntry`, `KioskMode` and `PlaylistPlayer` is correct:
each sits on a fixed dark ground (a media letterbox, a solid green success circle, an industry
accent) rather than a themed surface. Elsewhere it was replaced with `var(--qm-text-on-primary)`.

### The original wording, for history (user-reported, not yet fixed as of 2026-08-17)

1. **Largely addressed 2026-08-19** (see Phase 41 in `docs/TASK_TRACKER.md`) — the generic
   AI-dashboard patterns this item originally referred to (diagonal gradients, neon glow shadows,
   hardcoded blue bypassing the token system, and — the single biggest offender, found via live
   e2e — 198 raw Bootstrap `btn-primary`/`text-primary`/etc. usages across 42 files rendering stock
   Bootstrap blue regardless of any `--qm-*` token work) were swept app-wide and fixed. Not
   exhaustively re-verified page-by-page beyond the pages spot-checked live during that session, so
   treat as "should be close to fully fixed" rather than "guaranteed zero remaining instances."
2. ~~**Dark/light mode has too much overlap and inconsistent coverage** — some features have no
   light-mode styling at all. `qm-theme.css` uses `[data-theme="light"]` overrides on top of a
   dark-first `:root` (see lines ~89+); this pattern needs a systematic audit against every page,
   not just the ones already covered.~~ **CLOSED 2026-09-06.** The systematic audit ran; the token
   layer and the stylesheets were already complete. Real fixes: `AdBanner` had no light counterpart
   at all (white text on a black scrim, unreadable on the light display), plus the tint and glow
   work described above. See Phase 68.
3. ~~**Public display pages (`CustomerDisplay.razor`) are hardcoded dark-only** — no admin control
   over the public-facing display's theme at all. Needs a real admin-configurable
   light/dark choice for these screens (scope — per-organization vs. per-display — still needs to
   be settled with the user).~~ **CLOSED — and it was already built well before 2026-09-06; this
   note was stale for weeks.** `Organization.DisplayTheme` has a column, a validated
   `PUT /organizations/{id}/display-theme`, a Dark/Light picker in `BrandingSettings.razor`, and
   both `DisplayLayout` and `KioskLayout` stamp it onto their wrapper as `data-theme`.
   `CustomerDisplay` and `KioskMode` each carry a full light block. The only genuinely open part
   was the scope question, now answered per-organization (above). Verified live end to end
   2026-09-06: flipping the org to light flipped `data-theme` on both the kiosk and the display.

## RBAC has TWO axes now: permissions gate actions, `Role.DataScope` gates rows (2026-09-09)

The permission table answers "may this user read welfare records". It cannot answer "…for *these*
students", and until Phase 77 nothing could — every welfare and roster query filtered on `BranchId`
and nothing narrower. `Role.DataScope` (`Organization` | `AssignedClasses`) is the second axis, and
**`IStudentScopeService` is its only home.** Do not write a second "which classes does this user
teach" helper next to the code that needs one; that is this codebase's most-repeated bug.

Three rules that are easy to break by accident:

- **It fails closed, and it must keep failing closed.** A scoped caller with no assignments sees
  NOTHING, never the whole branch. The natural bug is an empty allow-list collapsing into a no-op
  `WHERE`, silently handing a brand-new class teacher the entire school roll. `ApplyAsync`
  short-circuits explicitly rather than relying on EF translating `Contains` over an empty list.
- **Every per-student endpoint calls `VerifyStudentAccess`, not `VerifyBranchOwnership`.** The two
  controllers each have that companion helper for exactly this reason. The e2e caught
  `GetStudentPicture` still on the branch-only guard — the widest single leak available, since the
  Student Picture assembles the pastoral tier, flags, guardians and chronology into one response.
  Out of scope returns **404, never 403**: a 403 confirms the student exists.
- **Do not cache the scope alongside permissions.** `PermissionAuthorizationHandler` caches a
  permission set for five minutes; class membership changes far more often, and a teacher removed
  from a class must lose access on the very next request. The scope service is scoped and memoised
  per request only.
- **A row-level scope does not substitute for the permission gate, and neither covers the third
  case (2026-09-10).** Three leaks were found where the scope was correct and something else was
  not. `welfare/cohorts` — a whole-school disproportionality breakdown — was gated on `welfare.view`
  rather than `welfare.reports.view`, so any welfare reader could pull it directly. The welfare
  import log leaked wider than any query: `RosterImportJobEntry` carries `StudentName`,
  `GuardianName` and a message per row of a branch-wide backfill, so those three endpoints now run
  `ApplyImportJobScopeAsync` (a scoped caller sees only the jobs they started — ownership, not class
  membership, because a failed row may have matched no student). And a scoped caller cannot START a
  bulk welfare import at all: **the processor runs in a Hangfire worker where `IStudentScopeService`
  does not exist**, because it reads the caller's HTTP context. That is the general rule —
  **a bulk write that a background job carries out cannot be row-scoped downstream, so refuse it at
  the controller** rather than letting it bypass the filter every read respects.
- **Tell the client when its figures are scoped.** `WelfareSummaryDto.ScopedToClasses` (empty for an
  unscoped caller) is what lets Welfare Reports say *"These figures cover S4 only."* A class
  teacher reading their own class's total as the school's is a wrong conclusion drawn from a
  correct query, and until 2026-09-10 nothing on the page said which it was.

**There are THREE permission catalogues**, and a code added to one and not the others produces a
different result depending on which seeder wins the startup race: `RbacSeeder.AllPermissions`,
`Permissions.All` in `Domain/Constants/Permissions.cs`, and `Web/Services/IPermissionService.cs`'s
own copy for the UI. The seven `welfare.*` codes were missing from the second for weeks. Add to all
three, always. Likewise **`RoleCodes.All` is ordered most-privileged-first and `Rank()` indexes into
it** — `IsManagerOrAbove` is array position, not a stored level, so where a new role is inserted is
a security decision. `class-teacher` sits between `staff` and `viewer`.


## A role has TWO gates: who may SEE it, and who may ASSIGN it (both found 2026-09-18)

Reported as cosmetic — *"I am not expecting platform admin role to appear in the tenant management
panel"* — and scanning for the same shape found a privilege escalation underneath it. Keep both
halves in mind; they are separate rules with separate homes.

**Visibility: `RoleCodes.IsVisibleToTenant` is the one home. Do not re-derive it per endpoint.**
`RolesController` filtered on `r.OrganizationId == null || r.OrganizationId == callerOrgId`, and
**every seeded role has a null organization**, so a school's Users & Roles page listed Platform
Admin and a bank's listed Class Teacher and the five school staff roles.

- **`RoleCodes.PlatformOnly`** (`super-admin`) is never shown to a tenant, at all.
- **`RoleCodes.ModuleFor`** maps a seeded role to the module it belongs to — `class-teacher` and the
  five Staff Performance roles all map to `ModuleCodes.StudentWelfare`, because Staff Performance is
  PART of that module ("Welfare & Performance"), not a module of its own. A tenant without the module
  does not see them. A custom role's code is arbitrary and maps to null, so a tenant's own roles are
  never hidden by this.
- **Applied in BOTH `GetRoles` and `GetRole`** — the by-id route leaked the same role's FULL
  permission list, and now answers **404, never 403**, the same rule as `VerifyStudentAccess`.
- **It is a VISIBILITY rule only.** A tenant that cancels a module keeps anyone already holding its
  roles: the role row and the assignment both survive. Revoking a module must not silently strip
  somebody's access on the way past. It stops being offered; it is not retrospectively unassigned.

**Assignment: every write that sets a `RoleId` calls `RoleAssignmentGuard.RefusalAsync`. No
exceptions.** It refuses `super-admin` outright, requires `roles.edit` for Tenant Admin, enforces
rank through `IsAtOrBelow`, and refuses a custom role carrying permissions the caller lacks.

**`UsersController.CreateUser` and `UpdateUser` took any `RoleId`, resolved it with a bare
`FindAsync`, and assigned it with NO rank check of any kind** — so a tenant Admin holding
`users.create` or `users.edit` could mint, or promote themselves into, a **Platform Administrator**:
a role that bypasses every permission check and reaches every organization. The guard already existed
and the join-request and staff-import paths already called it; these two simply never did. **A new
endpoint that assigns a role and does not call the guard is this bug again.**

**Why the e2e did not catch it, which is the more useful lesson:** section 13 covers privilege
escalation and passed throughout, because it tested the *join-request* path — which was guarded — and
never posted a role id to `POST /users`. A suite that asserts the guarded door is locked says nothing
about the door beside it. Section 13c2 now posts to both.

## Welfare visibility is a three-rung enum, and a class teacher never sees above the first

`WelfareVisibility { Standard, Confidential, Restricted }` replaced `WelfareRecord.Confidential`
on 2026-09-09. It is also on `StudentFlag`, and `Student.RestrictedNotes` is the student-level
equivalent.

- **`Confidential` is still forced server-side for `CaseType.Welfare`** and can never be lowered
  below it. `Restricted` is never forced — something reaches it only because a holder of
  `welfare.restricted.view` (Tenant Admin + SuperAdmin only) put it there.
- **A non-Standard record alerts nobody through `WelfareAlertService`** — no bell, no email, no
  timeline row for a class teacher. User decision 2026-09-09, and the KCSIE need-to-know reading.
  The suppression is a single early return so there is one line to read to know it cannot leak.
- **`StudentFlag.Visibility` is a different axis from `StudentFlag.Tier`.** Tier is severity and
  still gates the flag's *notes*; Visibility gates the flag's *existence*. They were conflated
  before — `Tier == High` was read as if it meant confidential.
- **Restricted content is blanked server-side in `MapToDto`, never hidden in markup.** The one
  deliberate exception is `StudentDto.HasRestrictedNotes`, which the pastoral tier does get:
  somebody handling the child needs to know an administrator holds something, or they cannot know
  to ask. What it *is* stays gated.
- Visibility is **the one mutable field** on an append-only record. `PATCH …/visibility` writes a
  `WelfareNote` recording who changed it, from what to what, and why, so the chronology still
  carries it. Lowering demands a reason.

**A migration that converts a security field must add, backfill, then drop — in that order.** EF
scaffolded the `Confidential → Visibility` change as DropColumn-then-AddColumn, which would have
silently reclassified every existing safeguarding record as Standard, readable by everyone with
`welfare.view`, with nothing anywhere to say it had happened. See
`20260909165244_AddClassTeachersAndWelfareVisibility`.


## A welfare export is logged on the WELFARE side, never the staff side (built 2026-09-18)

`WelfareController` had **no `IActivityLogger` at all**, so publishing a named child's full welfare
chronology to the Document Library as a shareable PDF, and every CSV/XLSX export of the records
search, happened with nothing anywhere recording that it had. The `MediaContent` row carries
`PublishedAt`/`PublishedByUserId`, so the *document* was traceable — but nothing on the welfare side
said a child's file had left the ledger.

- **`ActivityEvent.SubjectStudentId` is the student counterpart to `SubjectUserId`** — nullable,
  additive (`20260918161245_AddActivityEventStudentSubject`). Exactly one of the two is set on a
  subject-bearing event. The table was built for Staff Performance, where every subject is a *user*;
  a welfare subject is a *student*, which is the only reason it could not be reused as it stood.
- **Do NOT route a welfare export through `POST …/staff/activity/exports`.** That endpoint resolves
  every `Kind` to a `staff.*` permission and writes an event *about a member of staff*. Using it for a
  child would gate the write on the wrong permission and file the row in the wrong place.
  `POST …/welfare/activity/exports` and `GET …/welfare/activity` are the welfare pair.
- **The read gate is `welfare.reports.view` OR `welfare.reports.own`, AND `IStudentScopeService`.**
  A scoped caller sees events about their own students plus their own subject-less exports — a class
  teacher must not read the whole school's export history. The staff log's gate
  (`staff.records.view` + the staff scope) would be the wrong one entirely.
- **The write checks the student with `VerifyStudentAccess`** — 404, never 403 — so nobody can record
  a line about a child they could not have exported.
- **Visibility is set at the rung of what the event is about**, per the standing rule: a student who
  carries restricted notes makes the event Confidential, so a reader of the log does not learn from it
  what the export contained.
- **`Web/Services/WelfareActivityReporting.cs` is the one home on the client**, and it **never throws
  and never toasts**. The file is produced in the browser and the reader already has it; failing a
  completed export because its audit row did not write is the worst available trade. Same reasoning as
  `VisitorsController.TryIssueVisitToken` — a degraded success, not a failure.

## Classes are not a table, and that constrains anything attached to them

Classes live as a JSON list in `Branch.Settings` (`BranchVocabulariesDto.Classes`), and
`Student.ClassName` is free text matched by name. `StudentsController.UpdateVocabularies` writes
the whole list **over** the stored blob, so anything stored on a vocabulary item is silently
deleted by any client that round-trips the list without knowing about it. That is why
`ClassTeacherAssignment` is its own table rather than a field on the class.

Three consequences to keep in mind:

- **Matching is `Trim().ToLower()` on both sides, everywhere** — `ClassTeachersController.NormalizeClassName`,
  `StudentScopeService`, `WelfareAlertService`. A student invisible to their own class teacher
  because of a stray space is a safeguarding failure, not a cosmetic one.
- **A rename must move the assignments in the same save**, and a class with a live teacher counts as
  in-use for the editor's "cannot be removed — retire them" check. Both are wired.
- **`GET …/class-teachers/coverage` exists to surface the silent failures**: classes with no teacher,
  scoped users with no class, and student class names matching no configured class. That last one
  found a real P7 student on the dev tenant.

## Notifications: dispatch is off-thread, retried, and logged (reworked 2026-09-09)

`CreateInAppNotificationAsync` writes the row and pushes SignalR synchronously — the bell is what
"instant" means — then hands SMS/email to `NotificationDispatchJob` via Hangfire, which is on
PostgreSQL storage and therefore survives a restart. No broker, no new server dependency.

- **`NotificationLog` is finally written to.** It was a `DbSet` nothing in the codebase ever inserted
  a row into, so "was this actually delivered?" had no answer. One row per real attempt, recipient
  masked. **A Skipped outcome writes no row** — logging "email is off for this tenant" as
  `Success = false` puts a red failure against every message a tenant sends, which is the same
  skipped-vs-failed conflation the rework exists to end.
- **`ChannelSendResult`, not `bool`.** `Sent` / `Skipped` / `Failed` with a reason. The old bool
  meant no caller could tell a configuration problem from an outage.
- **Pass an `EventKey` to make a send preference-aware.** Null keeps the old behaviour (deliver on
  exactly what the caller asked for), so no existing caller changed. `NotificationEventKeys` strings
  are persisted in `Users.NotificationPreferences` — treat them as a wire format, not labels.
- Digests and quiet hours are deliberately absent; the requirement was instant.

- **There is no push sender and no SSO (user decisions, 2026-09-17).** The push stub and
  `CreateNotificationRequest.DeviceToken` were removed — no app exists and no device token was ever
  stored; `NotificationChannel.Push` and the Firebase/PushSent columns stay in the schema for the day
  one does. `IdentifyResponse.SsoEnabled` was removed for the same reason. Either would be its own plan.

## SSoT: DTO duplication pattern to watch for

`Q-Mgr.Shared/Application/DTOs/` is this codebase's actual single-source-of-truth location for
DTOs shared between `Q-Mgr.API` and `Q-Mgr.Web` (both projects reference it; `TokenDto`/
`CounterDto` already live there correctly). `OrganizationBrandingDto` was found (2026-08-17)
living in `Q-Mgr.API` only, with `Q-Mgr.Web`'s `IOrganizationApiService.cs` independently
maintaining its own duplicate copy — exactly the kind of drift risk SSoT is meant to prevent
(a field added to one and forgotten on the other, which is what had just happened with a new
`DisplayTheme` field before this was caught). Fixed by moving it into `Q-Mgr.Shared`.

**Update 2026-08-19 (Phase 30): the same shape of duplication was found in three more places and
all three are now fixed** — `ContentDto` (`PlaylistDto`, `DisplayDto`, `MediaContentDto`, etc.),
`NotificationDto`, and `UserInfo` all now live solely in `Q-Mgr.Shared/Application/DTOs/`, with
both `Q-Mgr.API` and `Q-Mgr.Web` referencing the shared copy — no independently-defined duplicates
remain on either side for these three types. (`NotificationDto` specifically wasn't a clean 1:1
copy — the SignalR-push shape was missing `ReadAt` compared to the REST/Web shapes; the
`ReadAt`-inclusive shape was adopted as canonical.)

Before adding a new field to any DTO that crosses the API/Web boundary, grep for whether a
same-shaped type already exists on the other side rather than assuming it's already shared — this
bug class has recurred multiple times in this codebase, so treat it as a standing risk, not a
one-time cleanup.

**Update 2026-08-21: another instance found and fixed, and it explains a real production bug, not
just drift risk.** `Subscription.razor`'s local `SubscriptionPlan` display record was being
deserialized directly from the API's actual response shapes (`PlanDto` from
`GET api/v1/billing/plans`, `SubscriptionDto` from `GET api/v1/billing/subscription`) — but the
field names don't match (`PlanCode`/`PlanName`/`MonthlyPrice` vs. the API's `Code`/`Name`/
`MonthlyPriceUsd`) and `Features` is a JSON-object string on the wire but a `List<string>` on the
Razor side. The type mismatch made deserialization throw outright, silently swallowed by a bare
`catch (Exception ex) { Console.WriteLine(...) }` — so "Available Plans" was always empty and the
current-plan price was always stuck at `$0.00`, with no visible error anywhere except the server's
own stdout. Fixed by adding dedicated `PlanApiDto`/`SubscriptionApiDto` records that mirror the
real API shapes exactly, used purely for deserialization, then mapped into the display record.
**Also relevant: Mapster (the auto-mapping library that was registered in DI) was removed
2026-08-21** — it had zero real call sites anywhere in the codebase (confirmed via grep for
`.Adapt<`/`IMapper`/`ProjectToType` — only its own DI registration referenced it), so every DTO
mapping in this project, including the one that broke here, is hand-written object-initializer
code. There is no auto-mapper safety net — a mismatched field name is a silent runtime bug, not a
compile error, until proven otherwise by exactly this kind of manual review.

## The three leak shapes, and where they were still open (2026-09-13)

Phase 81 predicted that three shapes of leak would recur beyond the ones it fixed. They did, in
five more places, all now closed. **Read this before adding an endpoint that touches a student.**

- **A per-student endpoint on the BRANCH guard.** `GET …/welfare-context` and
  `GET …/escalation-check` both used `VerifyBranchOwnership`, so a class teacher could read any
  child's name, code and class — and their prior-response counts — by id. Both now use
  `VerifyStudentAccess`. The rule from `GetStudentPicture` did not stick because it was written as
  a fact about one endpoint rather than as a rule: **if the route has `{studentId}` in it, the
  guard is `VerifyStudentAccess`, full stop.**
- **A WRITE the row scope never covered.** `POST welfare-records` checked the student existed in
  the branch and nothing else, so a class-scoped caller could file a safeguarding record against
  any child in the school and learn their name from the response. `AdditionalStudentIds` was the
  same door one step along. Both are scoped now — the linked list through `_scope.ApplyAsync`.
  **Phase 77 scoped the reads and stopped; a scope that only covers reads is half a scope.**
  The refusal reuses the *unknown student* wording deliberately: a distinct "not your class"
  message confirms the child is on the roll.
- **A bulk operation a background job carries out.** `BatchController` had no scope check at all,
  and three of its operations are gated on `welfare.edit`, **which the class-teacher role holds**.
  It hands a bare list of ids to `BatchOperationProcessorJob`, a Hangfire worker where
  `IStudentScopeService` does not exist. Refused at the controller for a scoped caller, the same
  rule the welfare bulk import already followed. Note `Undo` runs the scope refusal *before* it
  looks the job up, so a scoped caller cannot tell an existing batch (403) from a missing one (404).

**What was checked and found already correct**, so nobody re-runs it: every other `{studentId}`
route in `StudentsController` and `WelfareController` already calls `VerifyStudentAccess` or
`CanSeeStudentAsync`; `FinalizeRecord` is safe by a different route (it only finds the caller's own
draft); `StudentsController`'s three import-job endpoints were closed before this session, contrary
to the note that said they were left; and `visitors/deleted` is gated on `visitors.manage`, which is
stricter than the view permission, not looser.

**The UI has to follow the refusal, or the fix reads as a bug.** `WelfareReports` withheld its batch
bar and row checkboxes from a scoped caller — otherwise a class teacher selects twenty records and
gets a 403 — and also its **Import History** button, found in the browser: they can never start an
import, so that endpoint scopes to "jobs you began yourself" and the modal is permanently empty.
A button that opens an empty modal reads as broken; an absent button reads as an absent permission.

## Uploads are gated per file, and every DTO that carries one signs it (2026-09-15)

`UploadsController` answers `/uploads/media/{file}` — the same path every stored link already
carries, so nothing had to be rewritten — from a store OUTSIDE `wwwroot`
(`MediaStorage:LocalPath`; production sets it in the API unit to `$UploadsPath/media`, development
defaults to `App_Data/uploads/media`; `UploadStoreRelocation` moved an existing store at startup
and removed the production symlink). The decision per file is `IUploadAuthorizer.ClassifyAsync`:
**which table points at this file name is the only thing that separates a foyer poster from a
photograph of an injured child**, so that lookup lives in one place.

- **Public by intent:** signage media (on a playlist, or not marked shareable), broadcast
  attachments (sent to strangers as links), help-centre images. Served with a day of cache.
- **Gated:** welfare evidence, student and visitor photographs, a share-only Library document, and
  any file no row points at (an orphan — token only). A gated file is served for a valid `?t=`
  token (`IUploadAccessService`, one hour, minted only at the point a DTO reaches a caller who has
  already passed the owning record's checks) or for a bearer token whose user may read the owning
  record by that record's own rules — welfare.view plus the visibility rung plus the class-teacher
  scope for evidence. Otherwise **401 anonymous, 404 signed-in — never 403**.
- **A browser cannot attach the JWT to an `<img>`**, so the signed link is the only way a page can
  show a gated file. `UploadLinks.Sign` is called from the DTO mappers (`WelfareController`,
  `VisitorsController.MapToDto`, `StudentsController.MapToDto`, `ContentController.ToDtosAsync`),
  and **`UploadLinks.Strip` from every write that accepts a link back from a client** (visitor and
  student photos, docs cover and body images) — or a token that dies in an hour gets persisted.
  The static mappers are why it has a static face; `Program.cs` attaches the singleton at startup.
- **Add a new upload surface and you must classify it** in `UploadAuthorizer.LookUpAsync`, or it
  is an orphan: token-only, which breaks the page that shows it. A new *kind* fails closed there.
- **What a file IS comes from the extension it is STORED under, chosen from `UploadFileTypes`'
  allow-list — never from the client's declared type or file name.** The security review of
  2026-09-15 found `x.xml` declared as `image/png` was stored as `.xml` and served as `text/xml`
  (an XHTML-namespaced `<script>` runs on this origin), and the share endpoint echoed the row's
  client-declared `MimeType` as its `Content-Type`. Now: the storage layer refuses a type on no
  list, a `.pdf` must start with `%PDF-`, `UploadsController` renders inline only the raster
  image / video / audio / PDF types on that list and downloads everything else, and a share
  always streams as `application/pdf`. **Media is classified by `FilePath` only** — the column the
  server writes on upload — because matching on the client-writable `FileUrl` let a URL-linked
  media row take a welfare attachment public; `CreateMediaContent` refuses a `FileUrl` inside our
  own store, and `MediaFilePathBackfill` filled the column for legacy uploads at startup. The
  gated owners (welfare, student, visitor) are looked up before media, most restrictive first.
- `get:/uploads/*` is whitelisted from IP rate limiting in code (`PostConfigure<IpRateLimitOptions>`),
  because the DB "RateLimiting" row replaces the config section wholesale and an administrator's
  edit must not be able to black out a signage screen.

- **`MediaStorage:Provider` must stay `Local`, and the API says so loudly at startup if it is not
  (2026-09-19).** Flipping it to `S3` wires `S3MediaStorageService` for UPLOADS only —
  `UploadsController` still serves every byte with `PhysicalFile` off the local store, because it
  needs a disk path for the existence check, the ETag and the range processing pdf.js and `<video>`
  depend on. So an S3 install uploads successfully and then 404s every welfare photograph, every
  visitor badge and every signage image, with nothing anywhere saying why. Serving from a bucket is
  real work that has never been run against one, and shipping it unexercised would be worse than
  saying so. `Program.cs` logs a specific error naming the fix; **logged, not fatal, the same call
  as the Data Protection key-ring probe above it.**

## Secure document sharing: publishing is the boundary (built 2026-09-15)

The plan is `docs/plans/SECURE_DOCUMENT_SHARING.md` (**Revision 3, 2026-09-18** carries the post-build
audit: §13 SSoT, §14 findings); the rules that are easy to break:

- **`MediaServing` is the ONE home for "is this file public", and it reads `IsShareable` alone.**
  Playlist membership makes a document playable on signage; it does not make it public. Until
  2026-09-18 the classifier read `!IsShareable || OnSignage`, so adding a share-only document to any
  playlist served it anonymously with a day of cache — the plan promised in §1 that a document could
  be both "without leaking from one into the other", §5 defined only two exclusive categories, and the
  e2e asserted each separately ("on **no** playlist") and never the combination.
  **Both write paths now refuse the combination** (`ContentController.AddPlaylistItem`,
  `DocumentSharesController.UpdatePublishing`), and that refusal is load-bearing rather than tidy:
  gating alone would make a shared document silently stop rendering on a wall, because a public
  display fetches anonymously. Never re-derive this predicate anywhere else.
- **A Library document with share history is RETIRED, never deleted.** `document_shares` cascades from
  `media_content` and `document_share_events` from that, so a hard delete used to erase the whole
  record of who opened a document — on `content.delete`, which Manager holds, by someone who may hold
  neither `documents.share.manage` nor `documents.share.audit`. `DeleteMediaContent` now clears
  `IsActive`/`IsShareable` and keeps the rows when any share exists (NIST SP 800-53 AU-9; the
  controller's own "the history of who could open a document is the audit answer").
- **`ActiveShareCount` comes from `EvaluateState`** (`EvaluateStateOf`, the static entry point), not a
  second SQL predicate. The old copy ignored `LockedUntil`, `NotBefore` and the document's own
  `IsShareable`/`IsActive`/`FilePath`, so the Library card advertised live links on a document whose
  sharing was off while the activity modal on the same page said none.
- **A document carries a classification, and it NARROWS the tenant policy — never widens it.**
  `DocumentClassification { General, Internal, Confidential }` on `MediaContent`, with
  `DocumentSharingPolicyDto.EffectiveFor` as the one reader: the shorter link cap of the two wins, and
  a rule may force download off or email verification on. **The tenant's `AllowDownloadByDefault` /
  `RequireEmailByDefault` are DEFAULTS for a new link, not constraints** — folding them into the
  effective rule made a General document asking for download come back with it withheld, which the
  e2e caught. Raising a classification needs `library.publish`; **lowering needs
  `documents.share.manage` and a reason of ten characters**, kept on the row — without that lock the
  label is decoration, which is exactly what Google's locked DLP labels and Microsoft's recorded
  justification exist to prevent.

- **Sharing only knows the Library (`MediaContent`).** Nothing is shared in place; a report reaches
  the Library through "Publish to Library" on its print route (`reportPublish.js` renders it in the
  browser, no server PDF dependency) and that export runs the report's own permission and scope
  checks. **Nothing in the sharing code touches `IStudentScopeService`, and that is correct.**
- **The link is a row, never a signed token.** `DocumentShare.SlugHash` is the SHA-256 of a
  160-bit random slug; the slug is shown once. Revocation is a column checked on every request —
  never cached, so "revoke" means now. The 60-second content token and the 4-hour session token
  ARE `ITimeLimitedDataProtector` payloads, minted only after every gate has been cleared.
- **The view limit gates opens, not the bytes of a counted session.** A one-time link with download
  allowed counts its view at Open; `Serve` and `Resume` therefore treat `ViewLimitReached` as active
  for a valid grant. Found by the e2e (410 where 200 was expected). Expiry and revocation still bind.
- **Fail closed.** A document taken off sharing, deactivated, or missing its file refuses every link
  (`Unavailable`); the links and their history stay. An unknown slug and a revoked one read the same.
- **Say plainly what view-only is.** The download control is withheld server-side (403 and logged)
  and every page is watermarked from the bitmap up (`drawWatermark`, not an overlay); nothing stops a
  photograph. The UI says "attribution, not prevention" and must keep saying it.
- **Three catalogues, four codes:** `library.publish`, `documents.share.create`,
  `documents.share.manage`, `documents.share.audit`. Audit is narrower than create on purpose (a
  record of named people's reading); Manager holds the first three, Tenant Admin all four.
- **The tenant's policy is in `Organization.Settings["DocumentSharing"]`** (link-lifetime cap,
  attribution retention, defaults) — read through `IDocumentShareService.ReadPolicy`, never a second
  JSON reader. `DocumentShareRetentionJob` blanks email/address/browser on events past the window.
- **The reader's address and browser must be RELAYED, or the log names the Web server.** Every call
  this Blazor Server app makes to the API comes from the Web box's HttpClient, so on the public
  share endpoints the API would see the loopback and no user agent for every viewer. `App.razor`
  captures the host request's address and agent into a cascaded `ViewerRequestInfo`; the Web sends
  them as `X-Viewer-Ip` / `X-Viewer-Agent`; `PublicDocumentSharesController.Viewer()` prefers them.
  A curl suite cannot catch this class — curl *is* the browser there — so it was found in Chrome.
- **Verifying it needs the module:** the Library is in `engagement-communications`. On the dev
  tenant the tenant purchase route refuses while Student Welfare is on trial (`TRIAL_IN_PROGRESS`),
  so the e2e grants it as SuperAdmin via `PUT /api/v1/admin/tenants/{org}/modules/{code}`.

## Email: there IS a platform mailbox now, and every tenant falls back to it (2026-09-13)

**`info@sacc.ug` on `smtp.ionos.com:587` (STARTTLS) is the platform account.** Proven end to end on
2026-09-13: the delivery log recorded its first successful send in the life of the feature.

- **`ISmtpProfileResolver` is the single home** for "which SMTP account does this organization send
  through?". There were three copies before and they disagreed: the tenant send and the tenant
  test-send both required the tenant to have configured its own SMTP and **silently skipped
  otherwise**, which is the state every tenant starts in, while `EmailSender` only ever used the
  platform account. A tenant with email switched on and no SMTP details delivered nothing and said
  nothing. Now: the tenant's own host wins when set, otherwise the platform account.
- **On the fallback the From address is the PLATFORM's, not the tenant's.** IONOS (and Google, and
  Microsoft) reject a From that is not the authenticated mailbox, so carrying the tenant's address
  would turn a working relay into a 5xx on every send. The tenant's *display name* is kept, so the
  recipient still sees the school's name.
- **The password is never a committed default.** `appsettings.json` carries the host, port, SSL,
  username and from-address; `SmtpPassword` is blank there on purpose — this repo has a real GitHub
  remote. It comes from the untracked `appsettings.Development.json` locally and from
  `Environment=Email__SmtpPassword` on the API unit in production, which `build-linux.ps1` fills
  from `-SmtpPassword` or the untracked `scripts/deploy/secrets.local.json`. **A username with no
  password is treated as NOT CONFIGURED** rather than written in: that is the difference between
  "email is off" and "every send fails", and the build warns when it happens.
- **`PlatformEmailDefaults` runs at startup, after the RBAC seeder**, and fills the platform Email
  row from configuration **only when it is blank**. It exists because
  `InitializeDefaultSettingsAsync` returns early the moment *any* `PlatformSettings` row exists, so
  an existing install would never pick the account up. Same shape as `UploadLinkRepair`. It never
  overwrites a host an administrator chose, so a deploy cannot silently repoint a customer's mail.
- **Email__* goes in the systemd unit, not `appsettings.Production.json`** — `install.sh` preserves
  the API's copy of that file on every upgrade, so a key added there never reaches a live server.
  The unit is replaced every install. Same reasoning as `MediaStorage__PublicBaseUrl`.
- **A "Failure sending mail." with no detail used to mean the host was unreachable.** With a real
  relay the delivery log now carries the relay's own words — *"Mailbox unavailable"* for the
  `@qmgr.local` e2e accounts, which is correct and expected, those domains do not exist.
- **Fixed 2026-09-15:** `GET /api/v1/platform/settings/{category}` used to return `SmtpPassword`,
  the Stripe secret and webhook keys and the mobile-money API key in clear to a SuperAdmin, because
  the editor round-tripped them that way. `PlatformSettingsController.RedactSecrets` now returns the
  eight-dot mask (`••••••••`, the same string the editor's summary rows already used) for any
  non-empty secret, and `MergeSecrets` puts the stored value back on save wherever the mask comes
  in. **A secret shown as the mask means "set"; empty means "not set".** Add any new secret
  property to `SecretProperties` in that controller or it will leak the same way.

## Rate limits and usage metering: who a request belongs to (found by the first load test, 2026-09-17)

`scripts/e2e/load-test.mjs` (Node, read-only, N virtual users with think time, per-route p50/p95/p99)
was run for the first time on 2026-09-17 and failed twice before it measured anything. Both failures
were production bugs, not test bugs:

- **Every signed-in person shared ONE rate-limit bucket.** `IpRateLimiting` keys on `X-Real-IP`, and
  every call this Blazor Server app makes reaches the API from the Web server's loopback with no such
  header, so the whole school had 100 requests a minute between them (50 users: 97% 429s). The Web now
  relays the viewer's address as `X-Real-IP` wherever it already relays `X-Viewer-Ip`
  (`AuthenticationMessageHandler`, `AuthService`, `DocumentShareApiService`). **A new Web→API client
  path must relay it too**, or its users share a bucket again. Nginx sets `X-Real-IP` itself on the
  public paths, so a browser cannot choose its own key there.
- **The product's own UI was metered as "API calls".** `UsageLimitMiddleware` counted every `/api/v1`
  request against the modules' monthly `MaxApiCallsPerMonth` (5,000 on every module), so a school's
  own staff exhausted it within a day or two and then got 402 on everything; the load test used a
  tenant's month up in under a minute. **User decision, 2026-09-17: only integration traffic —
  requests authenticated with `X-API-Key` (`auth_method=api_key`) — is checked for API access and
  metered.** People signed in to the product are never "API calls". Section 14.19 asserts both halves.
- With both fixed and a 2-second think time: 50 users, 0 errors, worst p95 112 ms; 200 users, 0 errors,
  worst p95 4.7 s on a Debug build — the portal computes the whole branch's scores for the private
  rank on every load, which is the first thing to cache if a large school finds the portal slow.
- **A usage counter's empty cache entry is not zero.** `UsageTrackingService` keeps month-to-date
  counters in the distributed cache and flushes absolute values to `usage_records`; an entry that
  expired or died with a restart restarted from 0 and overwrote the stored figure on its first flush,
  so every counter undercounted. It now seeds from the stored value.
- **A load test against a real tenant consumes that tenant's allowances.** The first run spent the dev
  tenant's month; its `usage_records.ApiCalls` was reset by hand. Point it at a scratch tenant.

**Restore drill, run 2026-09-17** with the exact `pg_dump --format=custom` / `pg_restore --no-owner
--no-privileges` commands `qmgr-backup-db.sh` and `qmgr-restore-db.sh --drill` use: 72 of 72 tables,
per-table row counts identical except `Notifications`/`NotificationLogs`, which the running API wrote
between the dump and the count. The drill database was dropped. The production drill is still
`sudo bash …/qmgr-restore-db.sh <dump> --drill` on the server.

## Verification: there is no test project, and that is the decision (2026-09-05)

**Do not propose, scaffold, or ask for a test project.** Earlier handovers listed "no automated
test coverage" as a standing gap in this repo; the user closed that question on 2026-09-05 —
there is not going to be one, and it should stop being carried forward as outstanding work.

**There IS now one e2e script**, `scripts/e2e/class-teacher-e2e.sh` — **512 assertions as of 2026-09-18** (470 on 2026-09-17), 297 of them section 14, the Staff Performance Monitor, which the script runs through Node (`staff-performance-e2e.mjs`) and skips with a notice where Node is absent (374 / 201 on 2026-09-16, before the plan audit). Before that, 165 (one more on a tenant that still needs the module granted; section 13, privilege escalation and cross-tenant isolation, added the same evening; 94 until
2026-09-15, 72 until 2026-09-13) over the class-teacher scope, the visibility tiers, the alert, the
reports gate, the notification preferences, the delivery log, the three leak shapes above, real
email delivery, and — since 2026-09-15, section 12 — gated uploads and secure document sharing
(signed links, the passcode / email / view-limit gates, lockout, revocation, the activity log, the
policy cap, and platform-secret masking). Section 12 sends one more real email (a verification code).
It is not a test project and is not run by a build; it is a curl script against a live API and a
real tenant, which is exactly what the rule below asks for, written down so it can be re-run.
Extend it rather than starting a new one.

**And one browser suite, `scripts/e2e/browser/roles-and-branch-ui.mjs` (25 checks, added 2026-09-18).**
It exists because four of the six things reported from production that day were invisible to a curl
suite by construction: a page's own filtering, a CSS rule, a client-side exception, and whether a
branch switch actually reloads. It drives a local headless Chrome over CDP (recipe below) and streams
PASS/FAIL to the viewer on 5010 like the others. **Two rules it cost real time to learn:**
`#blazor-error-ui` exists on every Blazor page and is hidden by a **stylesheet**, so it must be read
with `getComputedStyle(el).display` — matching on `:not([style*="display: none"])` reports an error bar
on every page. And **verifying a branch switch needs TWO branches whose data differs**: the dev tenant
has one, welfare categories are organization-scoped and identical on both, so create a branch and use
**students**, which are branch-scoped. The suite skips that check honestly when only one branch exists.

**Sections 10 and 11 send REAL email** to `info@sacc.ug` (two messages per run) and clear the
tenant's own SMTP host so the platform fallback is what is under test. Set `E2E_MAILBOX` to send
somewhere else. The `@qmgr.local` test accounts are unroutable on purpose, so the welfare-alert
emails in section 6 fail at the relay with "Mailbox unavailable" — that is the correct answer for a
domain that does not exist, not a regression.

    API=http://127.0.0.1:5001 BRANCH=<branch guid> SA_USER=superadmin SA_PASS=admin \
        bash scripts/e2e/class-teacher-e2e.sh

**Only `API` and `BRANCH` are needed** — it resolves the six user/student/category ids itself and
clears leftover assignments before it starts. That was not true until 2026-09-10: those ids were
expected from the environment with no defaults and no check, so a run with the documented arguments
produced a wall of `unbound variable` and **20 false failures that read exactly like product bugs**.
**A suite whose setup can fail silently is worse than no suite**, so it now fails loudly and early
with a message naming what the tenant is missing.

**Login posts `email`, not `identifier`.** The field accepts a username too (see the Auth section),
but `LoginRequest.Email` is the JSON property — posting `identifier` logs "Login failed for
identifier: " with an empty name and returns 401.

### A suite is wired into its runner in the SAME COMMIT that creates it (2026-09-21)

There are three doors and every check lives behind one of them:

    bash scripts/e2e/class-teacher-e2e.sh   # the API suite, 20 sections, 7 Node suites inside it
    bash scripts/e2e/guards.sh              # the 8 static guards — no server, seconds, run by rebuild.sh
    node scripts/e2e/browser/all.mjs        # the 23 browser suites, against a local headless Chrome

**A suite nothing calls reports nothing.** Sections 15, 17 and 18 each existed, each passed
standalone, and were never called by the shell runner — so for weeks a "full run" under-reported by
about ninety assertions and a regression in any of the three would not have shown up. The static
guards and the browser suites had the same problem on a larger scale: **thirty-one scripts, one
runner, and it called seven of them.**

**A SUITE THAT CANNOT FAIL REPORTS NOTHING EITHER**, which is the half that is easy to miss.
`rooms-ui`, `select-verify` and `uniform-check` each counted their failures, printed the tally and
then always exited 0 — a runner trusting exit codes calls them green while they fail. They set
`process.exitCode` now. The genuine exceptions are declared in the runners and say why they are
exempt: `refusal-audit` prints a shortlist for a human, and `density-check`/`furniture-check`
MEASURE rather than assert. Failing a run on one of those would train everybody to ignore the run.

**Verify by running the thing against the dev tenant, seeding whatever data the path needs.**
Creating rows to test with is a normal setup step, not a blocker to report. If a code path has no
data to exercise it, make the data — a queue ticket, a visitor check-in, a welfare record — run the
path end to end, and clean up afterwards where the API allows it. The pattern that does *not*
count: verifying the paths that happened to have data, then listing the rest as "fixed but not
exercised live." That was tried on 2026-09-05 and the user pushed back on it; creating the missing
record took two POSTs and immediately surfaced a second real bug the untested path was hiding.

Two things follow from this that are worth stating plainly:

- **A clean build is not verification.** Most of this codebase's worst bugs compiled fine — the
  unqualified raw SQL below, the silently-swallowed DTO mismatch in `Subscription.razor`, four
  missing `switch` cases that made undo report a conflict against its own writes. Every one needed
  the code actually run against a real row to show itself.

  **The cleanest example, found 2026-09-06 (Phase 69), fixed the same day (Phase 71):**
  `TokenStatus.Serving` was read in nine places and assigned in none, and `Token.ServiceStartedAt`
  was only ever written as `null`. So the dashboard's "Now Serving" tile read 0 however many staff
  were mid-service, and `ServiceDurationMinutes` never got set, which blanked every
  average-service-time figure on `Dashboard`, `CounterPerformance`, `QueueAnalytics` and
  `ReportsOverview`. Nothing about it was visible in the types, the build, or a code read — it took
  issuing a ticket, calling it, completing it, and then looking at what the report actually said.

  **The resolution, and the rule to keep: calling a customer starts their service clock**
  (user decision, 2026-09-06). All three call paths — call-next, call-specific and transfer — now
  set `ServiceStartedAt` alongside `CalledAt`, and transfer *restarts* it rather than clearing it
  so the duration belongs to the counter that actually served the customer. "Now serving" counts
  `Called` **plus** `Serving`, not `Serving` alone. `TokenStatus.Serving` is still never assigned;
  it is kept so the count stays right if a real start-service step is ever added, and such a step
  should move `ServiceStartedAt` later rather than reintroduce a null. The full rationale lives on
  the `ServiceStartedAt` doc comment in `Token.cs` — read it before changing any of this.

  **Rows created before that fix keep a null `ServiceStartedAt` for ever**, so service-time figures
  covering older dates stay blank. That is honest history, not a regression to chase.
- **Some rows cannot be cleaned up, and that is not a reason to skip the test.** The welfare ledger
  has no DELETE endpoint by design — it is append-only. Dummy records created there stay. Label
  them plainly (`Dummy record … Safe to delete`), say so in the handover, and leave removal to the
  database owner rather than reaching for SQL.

### Running it locally — and the browser DOES work, found 2026-09-09

**Chrome drove the app successfully on 2026-09-09, after three sessions of failure.** Two
*separate* causes were behind those failures, found independently by two concurrent sessions the
same day. Both have to be right or the app looks broken in a way that resembles a server problem:

**Cause 1 — the wrong Chrome.** Several Chrome browsers were connected to the account at once and
the session was driving one that was not on screen. `list_connected_browsers` reports
`isLocal: true` for all of them, so **that field cannot tell them apart.** Call **`switch_browser`**,
which prompts every connected Chrome so the user can click Connect in the window actually in front
of them — and name it, so a later session can pick it straight away with `select_browser`. A
`chrome-error://chromewebdata` landing while `curl` gets a clean 200 is the signature of this, not
of a server fault.

**Cause 2 — the Web app was calling an address nothing listened on.** `appsettings*.json` points it
at `https://localhost:5001`, so running the API on plain HTTP left every call going nowhere. That
presents as the app "not loading" rather than as an API error. Pass **`ApiBaseUrl` as an
environment override**:

    dotnet run --project src/Q-Mgr.API/Q-Mgr.API.csproj --urls "http://127.0.0.1:5001" --no-build
    ApiBaseUrl="http://127.0.0.1:5001" dotnet run --project src/Q-Mgr.Web/Q-Mgr.Web.csproj \
        --urls "http://127.0.0.1:5003" --no-build

Both on `127.0.0.1`, both plain HTTP, no dev certificate, no HSTS, no redirect.

**Two tooling limits are real and remain, so plan verification around them:**

- **`resize_window` reports success while the viewport stays at desktop width**, so a mobile layout
  cannot be verified by screenshot. Measure instead: clone the suspect row into a fixed-width probe
  and compare `scrollWidth` against the container with `flex-wrap` on and off. That comparison is
  better evidence than a picture anyway — see the sideways-scrolling section below.
- **Chrome's print dialog cannot be screenshotted**, so a print layout is verified by reading the
  resolved `@page` / `break-inside` rules from `document.styleSheets`, not by looking at a PDF.

`curl`ing the served HTML and CSS remains a good check and still proves the markup and stylesheet
shipped, the `data-theme` wiring resolves, and an endpoint's real response shape — it is just no
longer the only option. **Say which you did; do not call served-HTML checks a visual pass.**

Three automation notes worth keeping:

- **A `wwwroot` change needs a REBUILD, not just a restart.** `@Assets["js/layout.js"]` fingerprints
  at build time, so editing a JS file and only restarting serves the stale asset. This cost real
  time once: a fix appeared to fail with the identical measured value twice and was only caught by
  reading the live function body out of the page. (Static files are otherwise re-read per request —
  it is the fingerprinted `@Assets[]` reference that goes stale, not the file.)

- **Use `form_input` (set the value by element ref), not synthetic `type`.** Typed keystrokes stopped
  reaching Blazor inputs partway through the session while the circuit was demonstrably alive
  (SignalR pushes still arriving); `form_input` kept working. A field that looks focused and ignores
  typing is the automation, not the app.
- **Do not batch a logout with the login that follows it.** `NavigateTo(forceLoad: true)` tears the
  page down mid-batch and the subsequent clicks land on a page that is being replaced, producing
  half-completed logins. One step, one screenshot, then the next.

**When the connected Chrome is on another machine (found 2026-09-17).** `list_connected_browsers`
showed one browser, `switch_browser` found no other, and it answered "127.0.0.1 refused to connect"
for both ports while `curl` got 200s — it was not on this PC. Chrome and Edge ARE installed here, so
drive a local headless one over the DevTools protocol from Node, with nothing to install:

    "/c/Program Files/Google/Chrome/Application/chrome.exe" --headless=new --remote-debugging-port=9333         --user-data-dir="$(cygpath -w "$TEMP/cdp/profile")" --no-first-run about:blank &
    # PUT http://127.0.0.1:9333/json/new?about:blank → webSocketDebuggerUrl; Node 24 has WebSocket built in.

Set a Blazor input's value with the native `HTMLInputElement` value setter plus an `input` event —
`Input.insertText` does not reach `@bind`, the same trap as synthetic `type` above. Record
`Page.frameNavigated` and `Page.navigatedWithinDocument` for the navigation trail; it is what showed
the `/billing/modules` detour that the final URL hid.

When the user reconnects, **two browsers can be listed and the session stays on the old one**: select
the newly connected `deviceId` with `select_browser`. A tab Chrome reports as `visibilityState:
hidden` screenshots as solid black; read state with `javascript_tool` instead (URL, `innerText`,
localStorage), which works normally there.

### The old notes, kept because the constraints behind them still hold

Start the two apps separately; `dotnet run` serves a **snapshot compiled at startup**, so a `.razor`
edit needs the process restarted (or use `dotnet watch run`). Static files under `wwwroot/` are
re-read per request and do not.

    dotnet run --project src/Q-Mgr.API/Q-Mgr.API.csproj --urls "https://localhost:5001"
    dotnet run --project src/Q-Mgr.Web/Q-Mgr.Web.csproj --urls "http://127.0.0.1:5003"

`curl -k` against either works immediately. **Note these two commands use HTTPS on 5001 and NO
`ApiBaseUrl` override — that combination works for `curl` but is not the one to use when driving
the browser.** See the section above for the recipe that does.

The environmental notes below remain true and are still worth avoiding:

- Chrome rejects the ASP.NET dev HTTPS certificate, and the automation tooling **cannot screenshot
  or read an error page**, so the interstitial cannot be clicked through from here. Trusting the
  cert (`dotnet dev-certs https --trust`) modifies the machine's root store — a system security
  setting, so ask before doing it rather than doing it silently.
- Visiting `https://localhost:...` once pins **HSTS on the whole `localhost` host, every port**, so
  a later plain-HTTP run on `localhost` gets force-upgraded and fails. Bind to `127.0.0.1`, which
  carries no HSTS entry.
- Binding both an HTTP and an HTTPS URL re-enables `UseHttpsRedirection` (it learns the HTTPS port
  and 307s). **HTTP-only is what skips the redirect.**
- A `chrome-error://chromewebdata` landing while curl gets a clean 200 is the signature of driving
  the wrong browser, not of a server problem. Check `list_connected_browsers` before assuming
  anything else.

## Every bulk import is `QImportPanel`, and the browser only turns a sheet into CSV (finished 2026-09-19)

Three imports, built months apart, each with its own journey: the student roster parsed in
JavaScript (`rosterImport.parseFile` carried a header-alias map, its own idea of a bad row and its
own typed output), the staff list parsed in C#, and the welfare history parsed in JavaScript under a
SECOND alias map in the same file. The roster and the staff list moved to the shared panel on
2026-09-18; **the welfare history was the last one and moved on 2026-09-19.**

- **`Components/Shared/UI/QImportPanel.razor` owns the whole client-side journey** — choose a sheet,
  read it, validate it, preview it, send it, watch it over SignalR with a slow reconciling poll
  behind, and read the row-by-row result. A caller supplies `Columns`, `BuildRow`, `StartAsync`,
  `LoadJobAsync` and the rest. Uniformity is structural rather than promised: all three callers run
  this component, so they cannot drift again.
- **`rosterImport.js` is down to `sheetToCsv`.** `parseFile` and both alias maps were DELETED once
  the last caller left, rather than left "in case". A second place where a column name can be
  recognised differently is this codebase's most-repeated bug, and it had already produced two
  disagreeing maps in one file. SheetJS still does the reading, in the browser, so the server needs
  no Excel library — that part is the standing no-server-dependencies rule and is unchanged.
- **The aliases a spreadsheet may use are carried across verbatim** whenever an import moves, so no
  sheet that used to import stops importing. The welfare set is in `WelfareReports.razor`.
- **`OnFileChosen` is how the page learns the file name** for `SourceFileName` on the job. The roster
  passed a field nothing ever assigned once its own file picker moved into the panel — CS0649 on
  every build — so every roster import since has recorded a null name in the Import History.

## Text inputs must use `@bind`, never `value="…"` plus `@oninput` (found in production 2026-09-11)

Reported as "when I type fast the input lags and drops characters, on every form". The cause was
`QInput`, which almost every form uses: it rendered `value="@Value"` with a hand-written
`@oninput="HandleInput"`. **Only a `@bind` directive makes the Razor compiler emit
`SetUpdatesAttributeName("value")`**, and ASP.NET Core's `RenderTreeUpdater.UpdateToMatchClientState`
accepts the browser's typed text into the render tree only when that is present. Without it, each
keystroke's re-render diffs against the previous render's value and sends it back, overwriting
whatever was typed since. See dotnet/aspnetcore#14242 (same pattern, same symptom, fixed by `@bind`)
and #40097 (Microsoft's own `InputText` had the same omission).

**It is not a network problem, and was proven not to be.** An A/B test on the same machine, the
same page and the same typing: the old `QInput` made Blazor write stale text into the focused box
10 and 20 times (`A`, `A0`, `A00`… replayed over the full string), the fixed one 0 and 0. On
production the old `QInput` did the same, while a plain `@bind:event="oninput"` input on the same
page made 0 writes. Latency only decides how often a keystroke lands mid-rewind and is lost, which is
why a dev machine never shows it.

**It lost real text in production, not just rewound it.** In production's Create Playlist dialog,
typing `Morning lobby announcements and welcome videos` into Name in three quick bursts produced 29
overwrites and left `Morning announcements and welcome videos`: the word "lobby " was gone. The
old `QInput` textarea (Description) made 0 writes, because it rendered its text as child content
rather than a `value` attribute. The number field (Default Duration) made 2.

- **Use `@bind`, or `@bind:get`/`@bind:set` when a handler must run**, with `@bind:event="oninput"`
  for live updates. `@bind:after` is the place for side effects, as `Register.razor`'s password
  strength meter now does. A separate `@oninput` that writes the bound field reintroduces the bug even
  next to a `@bind`: the client-state flag is attached to `@bind`'s own event, not the extra one.
- **`QInput` keeps its own `currentText`** so a number field can be blank or read `1.` mid-typing
  without snapping back on the next page re-render. Outside changes to `Value` still replace the text.
- **Test it by recording writes, not by eye.** Wrap `HTMLInputElement.prototype`'s `value` setter
  and log writes while the element is focused. Real keystrokes never call the setter, so any logged
  write is Blazor overwriting the box. A correct input logs zero, at any latency.

Seven sites were fixed together: `QInput`, `QSelect`'s search box, `ConfirmDialog`'s reason,
`FeedbackPage`'s free-text answers, and the filters on `StudentRoster`, `WelfareReports` and
`VisitorManagement`. **A grep for `@oninput=` under `Components/` should return nothing**; if it
returns something, check it is not writing a bound value.

## `IQueueApiService` swallows its errors — every caller must check the return (found 2026-09-06)

Almost every Web API service in this project **throws** `InvalidOperationException` carrying the
server's own message when a call fails (`IStudentApiService`, `IAppointmentApiService` and the rest
all do `if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await
ApiErrorService.GetErrorMessageAsync(response))`), so a caller wrapping the call in `try`/`catch`
handles failure correctly and shows the real reason.

**`IQueueApiService` is the exception: ten of its methods catch everything and return `false` or
`null`.** A caller that awaits one without inspecting the result cannot tell success from failure,
and `try`/`catch` around it is dead code because nothing ever throws.

That is not theoretical. `CounterTerminal.razor`'s Complete and No-Show both ignored the result:
they cleared the current token, set the counter Active, incremented the served-today count and
toasted "Service Completed" / "Marked as No-Show" **whether or not the request succeeded** — so a
failed call removed the customer from the operator's screen while they were still `Called` on the
server, and inflated the day's figures. Both were fixed 2026-09-06 (Phase 70) to check the result,
leave the token in place on failure, and say what happened.

**So: when calling `IQueueApiService`, always branch on the return value** — and if you add a
method to it, prefer making it throw like its neighbours over adding an eleventh silent one. A
sweep of the other 13 `await …Api.…Async(…)`-then-success-toast sites app-wide found all of them
correct precisely because their services throw.

## Prerendering is OFF, the render mode lives on `<Routes>`, and none of that is arbitrary

**Do not turn prerendering back on, and do not try to vary the render mode per page or per layout.**
All three were tried on 2026-09-06 and each failure is a hard constraint, not a preference:

- **A page-level `@rendermode` inside an already-interactive subtree is silently ignored.** No
  warning, no error. `Login.razor`, `Register`, `ForgotPassword` and `ResetPassword` each declared
  `prerender: false` and it did nothing for weeks, because `<Routes @rendermode=…>` in `App.razor`
  had already fixed the mode at the root. The lines are still there and still inert; they are kept
  only because they document intent next to the page they belong to.
- **A layout cannot carry a render mode at all.** The framework refuses: *"Cannot pass the
  parameter 'Body' to component 'PublicLayout' with rendermode 'InteractiveServerRenderMode' …
  the parameter is of the delegate type 'RenderFragment', which is arbitrary code and cannot be
  serialized."* A layout takes its page as a `RenderFragment`, and delegates do not cross a
  render-mode boundary. This is the one that kills per-layout granularity outright.
- **Per-page modes with a static `<Routes>` would compile, and would gut the app.** The router then
  renders `MainLayout` statically, so the sidebar, branch switcher, notifications, theme toggle and
  user menu all become dead HTML. Making that work means rebuilding the shell as interactive
  islands inside a static frame — a real refactor, not a flag.

**Why off rather than on.** With prerendering on, Blazor server-renders the login page's `EditForm`
as a genuine native `<form method="post">` with live inputs, before any circuit exists. Type into it
in that window and the input is discarded the moment Blazor takes over and re-renders from an empty
model; press Enter and the browser submits natively — a full page load. That is the long-standing
"the login page reloads while I am typing" report. Measured on `/login`: static POST forms **1 → 0**
and served `<input>` elements **10 → 0** once the flag was turned off.

**The cost, and how it is paid.** Turning prerendering off means nothing a *layout* renders reaches
the first paint — which would return the public display screens to the first-paint flash Phase 62
fixed, since `DisplayLayout`/`KioskLayout` are what set `data-theme`. So `App.razor` stamps
`data-theme` onto `<html>` itself: that file is server-rendered on every request **regardless of
render mode**, making it the one place immune to this. The layouts still set it on their own wrapper
— not redundant, since the host covers the pre-circuit window and the layout everything after.

**`Services/PublicDisplayRoute.cs` is the single home** for "is this a public display screen, and
which branch?". `DisplayLayout` and `KioskLayout` previously each carried a byte-identical copy of
the branch-resolution loop; `App.razor` would have been a third. Note its route allowlist is
load-bearing: `App.razor` runs for every request, and matching on "the path contains a GUID" would
fetch branding for a student id on every `/admin/students/{studentId}/picture` load.

## Every visitor looks anonymous during prerendering — do not redirect on that (found 2026-09-06)

Authentication state here lives in **localStorage**. `AuthService.GetCurrentUserAsync` checks an
in-memory store first, otherwise falls back to a JS-interop read, and its `catch` returns `null`.
**There is no JS interop during prerendering**, so that read throws and null comes back — meaning
`CustomAuthenticationStateProvider` reports **anonymous for everyone during the prerender pass,
signed in or not**. `AuthorizeRouteView` consequently renders its `NotAuthorized` branch for
authenticated users during prerender.

**So any component that redirects the moment it sees "not authenticated" will bounce every signed-in
user to `/login` on every page load.** That is not hypothetical — it is exactly what writing the
missing `RedirectToLogin` component would have shipped if it had redirected on sight (Phase 74).

Two rules for anything that acts on auth state:

- **Decide in `OnAfterRenderAsync`, never `OnInitialized`** — the former does not run during
  prerendering.
- **`await AppInit.InitializeAsync()` before deciding**, then re-check. That call is what loads the
  tokens out of localStorage into the in-memory store; without it even the interactive pass can
  race an uninitialised store and read a signed-in user as anonymous. It is idempotent, so calling
  it costs nothing when `MainLayout` already has. `MainLayout` established this order; follow it
  rather than inventing a second one.

**These two rules are about a COMPONENT THAT ACTS ON AUTH STATE ITSELF — `MainLayout`,
`RedirectToLogin`, a public page. They are NOT a reason to move an ordinary page's permission guard
(checked 2026-09-19).** A note carried since 2026-09-18 claimed the Document Library's guard had to
move out of `OnInitializedAsync` because of this race, and that `MediaLibrary` carried the same
latent bug. **That does not survive a code read.** `MainLayout` renders `@Body` only inside the
`else` of `@if (!authChecked)`, and `authChecked` is set only after `AppInit.InitializeAsync()` on
both of its paths — so no page beneath it can run against an uninitialised token store. Seventy-six
pages guard in `OnInitializedAsync`; a real mechanism would be a seventy-five page outage, not a
latent edge case. Whatever bounced an administrator on 2026-09-18, the recorded cause does not
explain it. **Do not sweep the other pages onto that shape on the strength of that note; reproduce
it first.**

**A public page must never pass through `AuthorizeRouteView` (found 2026-09-15, the share-page
"login bounce").** `AuthorizeRouteView` renders its *Authorizing* and *NotAuthorized* states
inside `DefaultLayout` — `MainLayout` — never inside the page's own `@layout`. Because the auth
state comes from localStorage over JS interop, an anonymous visitor's state is never ready
synchronously, so every public page (`/s/{slug}`, `/login`, the kiosk) spent its first render
inside `MainLayout`, whose own check then sent the visitor to `/login?returnUrl=…` with a full
page load. On a warm server the real page usually won that race; **on the first circuit after a
restart — which is what a deploy does to every open tab — it lost, reproducibly** (every open
share link bounced to the sign-in page the moment the new build came up). `Routes.razor` now sends
a page with no `[Authorize]` through a plain `RouteView`; only a page that carries `[Authorize]`
(none do today — the shell's protection is `MainLayout`'s own redirect) gets the authorize view.
`MainLayout` also refuses to redirect once disposed, since its check spans two awaits. Reproduce
the class by restarting the Web process under an open tab; never by a fresh navigation, which
does not show it.

Related, and the reason this was found: **a redirect to `/login` from `/login` is a full-page
reload of the page the user is already typing into.** `MainLayout` did exactly that until Phase 74.
Guard the already-on-login case explicitly, and prefer soft `NavigateTo` over `forceLoad: true`
unless a fresh circuit is genuinely required — this app has a history of self-reloads presenting to
users as "the login page keeps refreshing".

**Do not ignore RZ10012 build warnings.** `Routes.razor` used `<RedirectToLogin />` for weeks while
the component sat **out of tag scope** — `Components/Shared/RedirectToLogin.razor` existed the whole
time, but `Routes.razor` never imported `QMgr.Web.Components.Shared`, so Razor emitted the tag as
inert literal HTML and the redirect never ran. The compiler said so on every build. **An unresolved
component tag fails silently at runtime** — it renders nothing rather than erroring, so the only
signal is the warning.

Note the shape of that trap: a `.razor` component takes its name from its **filename**, so grepping
file *contents* for the component name will not find it. Check `Components/**` for the file itself
before concluding a component is missing.

## A refused refresh is a sign-out, not a missing permission (fixed 2026-09-17)

The "stuck on Initializing… at /unauthorized" report. A tab from an earlier day made its first calls,
the API refused the refresh token, and `AuthService` cleared the session and told nobody. `MainLayout`
showed the shell anyway, the page's permission check found no user, and `PermissionService` answers
"not signed in" and "not allowed" the same way — so it sent the person to `/unauthorized`, where
`MainLayout` refused to redirect (that path was exempt) and never rendered the page. Reproduced and
verified headless (see "Running it locally").

- **`IAuthService.SessionExpired` is the signal.** Raised only when the refresh endpoint REFUSES
  (400/401/403); `MainLayout` sends the person to `/login?returnUrl=…`. A 429, a 5xx or a network
  failure leaves the session alone — wiping it turned an API restart into a forced sign-in.
- **The expired path clears THIS browser only, never `LogoutAsync`.** `auth/logout` revokes the user's
  one refresh token, which after a sign-in on another device is that device's live session.
- **A refresh token rotated by another tab is adopted**, not treated as a refusal: re-read
  localStorage before giving up. (Written, not exercised — it needs an access token to expire
  mid-circuit.)
- **`MainLayout` re-reads the user after its first API calls** (`SessionStillValidAsync`) before it
  sets `authChecked`, because those calls are where a dead session is discovered.
- **`/unauthorized` is not exempt from the sign-in redirect** — in `MainLayout` or `RedirectToLogin`.
  Only `/login` is. It carries no returnUrl, since the sign-in page refuses to return there.
- **`IModuleApiService.GetMineAsync` throws.** It swallowed errors, so `ModuleState.LoadSucceeded` was
  never false, the documented fail-open never ran, and a dead session (or an API blip) detoured the
  person through `/billing/modules` as if they owned nothing.

## Raw SQL must schema-qualify table names explicitly — found live 2026-08-26, was a severe bug

`QMgrDbContext` sets `modelBuilder.HasDefaultSchema("qmgr")`, so every normal EF LINQ query is
automatically schema-qualified in the generated SQL. **Raw SQL (`FromSqlInterpolated`,
`FromSqlRaw`, `ExecuteSqlInterpolated`, `ExecuteSqlRaw`) does not get this for free** — an
unqualified table name in raw SQL is resolved via Postgres's connection-level `search_path`, which
for this DB is the server default (`"$user", public`), not `qmgr`. `public` has none of this app's
tables, so an unqualified raw-SQL table reference fails outright with `relation "X" does not
exist` — it doesn't silently query the wrong data, it hard-fails every time.

This was found live 2026-08-26 (Phase 57) in `TokenRepository.GetNextWaitingTokenForCounterAsync`,
whose `FOR UPDATE SKIP LOCKED` raw query (added to fix a real double-assignment race) read `FROM
tokens` unqualified — meaning **every single "Call Next" request, the core action of this entire
product, had been 500ing** until this was caught by accident while e2e-testing something unrelated
(an actual live browser session hit the same error independently). Fixed by writing `FROM
qmgr.tokens` explicitly. A repo-wide grep for `FromSql|ExecuteSql` at the time found only this one
bad instance (the other 3 call sites are `pg_advisory_xact_lock(...)` calls with no table
reference, which don't have this problem) — but a grep is a snapshot, not a guarantee about code
written after it.

**Before merging any new raw SQL**: schema-qualify every table name explicitly (`qmgr.tablename`),
and actually execute the query against a live row at least once — this bug class produces a hard
runtime failure with no compiler, EF-migration, or type-check catching it, and nothing else
catches it either until somebody runs that exact code path — which is the whole reason the
verification rule above exists.

## Two production-only failures, both invisible on a dev machine (found 2026-09-09)

Neither could ever reproduce locally, and neither showed up in a build, a type check, or a code
read. Both are worth remembering as *classes*, not just as two fixed bugs.

### 1. `ProtectSystem=strict` made the Data Protection key ring unwritable

Reported as "it says error, but when you check, the visitor actually checked in." Exactly that:
`VisitorsController.CheckIn` commits its transaction, and *then* mints the badge QR token, which is
an `IDataProtector.Protect` call. `Program.cs` persists the key ring to
`AppContext.BaseDirectory/dataprotection-keys` unless `DataProtection:KeyPath` says otherwise —
and the deployed `qmgr-api.service` runs with `ProtectSystem=strict` + `ProtectHome=true`, which
mounts everything outside `ReadWritePaths` read-only. So the key ring could not be created, every
`Protect()` threw, and every single walk-in check-in 500ed *after* the visit was already on the
books. Reproduced locally by pointing `DataProtection:KeyPath` at an unwritable path — the 500 and
the committed row both appear immediately, so use that trick to test this class again.

Fixed in `scripts/deploy/build-linux.ps1`: a `-DataProtectionPath` parameter (default
`/var/lib/qmgr/dataprotection-keys`) that is written into the API's `appsettings.Production.json`,
listed in the unit's `ReadWritePaths`, and created + `chown`ed + `chmod 700`ed by `install.sh`.
**It lives outside `$InstallRoot` deliberately** — a key ring inside the install directory is
replaced on every deploy, which would make previously-encrypted values (the platform Spotify OAuth
tokens) undecryptable after each upgrade.

**Two standing rules from this.** First: **anything a systemd unit needs to write must be in
`ReadWritePaths`, and adding a new write target is a deploy-script change, not just a code
change.** Second, and more general: **a side effect that runs after a transaction has committed
must not be able to fail the request.** `VisitorsController.TryIssueVisitToken` and
`VisitorActivityBroadcaster.BroadcastAsync` both swallow-and-log for this reason — a badge that
cannot be printed, or a live-board push that did not land, is a degraded success, not a failure,
and reporting failure on a committed write is the worst possible answer for a front desk.

**CLOSED 2026-09-10 — `Q-Mgr.Web` now has its own `AddDataProtection`, and both processes share the
API's key ring.** It had none at all, so it fell back to an ephemeral ring and antiforgery tokens
stopped validating after every restart: the same class as the API bug above, failing softer, which
is why it never announced itself.

**The one-line version of this fix would have made production worse, and that is the lesson.**
Adding the call alone points the Web at `AppContext.BaseDirectory` — inside `$InstallRoot`, which
`ProtectSystem=strict` mounts read-only — turning a soft degradation into a hard startup failure.
A key-ring change is **four** changes and they must land together:

1. the `AddDataProtection` call, reading `DataProtection:KeyPath`;
2. that key written into **that process's** `appsettings.Production.json` by `build-linux.ps1`;
3. the path in **that unit's** `ReadWritePaths`;
4. the directory created, `chown`ed and `chmod 700`ed by `install.sh` (already true here — both
   units run as `www-data`, so it was checked, not changed).

**The key path MUST also travel in the systemd unit as `Environment=DataProtection__KeyPath`
(both units), and does since 2026-09-15.** The 2026-09-10 fix wrote it into the generated
`appsettings.Production.json` — the one file `install.sh` deliberately preserves from the server's
own copy on every upgrade — so the live API never received it, kept persisting keys under the
read-only install root, and every `Protect()` call 500ed: found live the evening the sharing
feature was deployed, as `POST /public/shares/{slug}/open responded 500`. **Any setting an
existing server must pick up goes in the unit, never only in appsettings** — this is now the
third instance of the same trap (`MediaStorage__PublicBaseUrl`, `Email__*`, and this). The API
also probes the key ring for writability at startup and logs a loud, specific error naming the fix.

**`SetApplicationName("QMgr")` must match across both processes.** The application name is part of
the key-derivation purpose chain, so two processes sharing a ring but disagreeing on it cannot read
each other's payloads.

**Why an upgrade picks this up at all** is worth knowing before you touch either config:
`install.sh` **preserves the API's** `appsettings.Production.json` (so the real DB password and the
current JWT secret survive) but **always overwrites the Web's fresh from the package**. If it
preserved both, the Web would start with new code and old config — precisely the combination that
hard-fails.

### 2. The notification hub read a config key that exists in no appsettings file

The visitor activity board sat on "Reconnecting..." in production and never updated live.
`NotificationHubService.StartAsync` read `_configuration["ApiSettings:BaseUrl"]` — **a key defined
nowhere in this repository** — so it always fell through to its hardcoded
`https://localhost:5001` default. On a dev box that is coincidentally where the API listens, so it
worked perfectly and had done for as long as the file existed. In production the API is on
`http://127.0.0.1:<ApiPort>` behind nginx, so the hub connected to nothing, `StartAsync` threw, and
`MainLayout.ConnectToNotificationHub`'s bare `catch` swallowed it — taking notifications, the live
visitor board, roster-import progress and permission-change pushes down with it, silently.

The key is `ApiBaseUrl`, which is what `Program.cs` and `ISignalRService` already read. **When you
add a config lookup, grep for the key first: a `?? "default"` fallback turns a typo'd key into
working-on-dev, broken-in-prod, with nothing anywhere to say so.**

Two related fixes went in alongside it. `NotificationClientService` now remembers which branch
groups it has been asked to join and replays them on every successful connect — `JoinBranchAsync`
used to be a bare "invoke if Connected" that silently dropped the membership when a page's
`OnInitializedAsync` beat `MainLayout`'s hub startup, and SignalR groups do not survive a reconnect
either. And `VisitorDisplayBoard` re-reads the on-site list when the connection comes back, since
anything that happened while it was down was pushed to nobody.

### 3. Every uploaded file's link pointed at the API's loopback address (found 2026-09-11)

Reported as signage showing *"Unexpected server response (503) while retrieving PDF
http://127.0.0.1:8586/uploads/media/…pdf"*. Two defects, and fixing either alone still leaves the
file unreachable:

- **The link was built from the request host.** `LocalDiskMediaStorageService.GetUrlAsync` used
  `Request.Scheme://Request.Host`. Uploads reach the API from Q-Mgr.Web's server-side HttpClient
  over `ApiBaseUrl` (`http://127.0.0.1:{ApiPort}`), so that loopback address was saved into every
  upload's link: media, welfare attachments, broadcast attachments, doc covers, visitor photos. On a
  dev box the browser can reach that address too, so it never showed.
- **nginx had no `/uploads/` route.** It fell through to `location /` and Web, which has no such
  files, so even `https://qmgr.cashbook.ug/uploads/…` returned 404.

The fix is three parts that must ship together. **`MediaStorage:PublicBaseUrl`** is the origin for
new links. It is unset in development, where the request host is right. In production it is set as
`Environment=MediaStorage__PublicBaseUrl=https://$HostName` in the **API systemd unit**, not in
appsettings: `install.sh` preserves the server's API `appsettings.Production.json` on every upgrade,
so a key added there never reaches an existing install. The unit is replaced on every install.
**Any new API setting a deployed server must pick up belongs in the unit for the same reason.** A **`location /uploads/`** block proxies to the API. And
**`Infrastructure/Data/UploadLinkRepair`** runs at startup after the RBAC seeder: when the key is
set, it rewrites stored `http(s)://127.0.0.1|localhost(:port)/uploads/` links onto it, across the
seven link columns and inside `doc_articles.BodyHtml` (a user decision over rewriting at read time,
which would have missed links embedded in article HTML). It is idempotent and logs what it changed.

Verified on the dev tenant. With the key unset, an upload saved `http://127.0.0.1:5001/…`,
reproducing production. A restart with the key set logged `Repointed 1 … media_content.FileUrl` and
`Repointed 1 … WelfareAttachments.FileUrl`, and a new upload saved the public base. A further
restart logged `0 row(s) updated`. The nginx block was checked only by parsing the script; there is
no nginx here to run it.

**Welfare evidence uploads had the same fault in the browser, fixed the same day.**
`QFileUpload.razor` handed `qFileUpload.js` an upload target built from `Http.BaseAddress`, which
is `ApiBaseUrl`, the internal loopback in production. The browser's `fetch` could never reach it.
It now uses `ApiPublicUrl`, falling back to `Http.BaseAddress`, the pattern `MainLayout`,
`ApiClientsSetup` and `Support` already followed. In production the page and `ApiPublicUrl` share
an origin, so no CORS applies. It was not tried in production first: the dialog only appears after
creating a welfare record, and the ledger cannot be deleted from.

Verified locally with the two addresses deliberately different: Web ran with
`ApiBaseUrl=http://localhost:5001` and `ApiPublicUrl=http://127.0.0.1:5001`, and a wrapper on
`qFileUpload.init` recorded the target the browser received. It was the `ApiPublicUrl` one. A real
upload through the Attach Evidence dialog then returned 201 and wrote the attachment row. A local
test of this path needs `http://127.0.0.1:5003` in the API's CORS origins (for example
`Cors__AllowedOrigins__4`), because locally Web and API are different origins. The dummy record it
created, "Dummy record - evidence upload test. Safe to delete.", is on Test Student One.


## The staff record has two halves, and they belong to different people (2026-09-19)

Reported as *"we implemented other basic information relating to the staff member, but the feature
does not surface in the ui, there is no way of updating the staff member data"* — and it was worse
than that. Twelve fields had been on the `User` row since 2026-09-18 and the bulk staff import had
been writing them since, but there was **no per-person read endpoint, no write endpoint and no UI
anywhere**. A typo in an imported sheet was permanent, and a school entering its staff by hand could
record none of it.

**The split is enforced by the SHAPE of the request, not by markup**, which is what makes it hold:

- **CONTACT — the person's own.** Phone, second phone, office, emergency contact name and number.
  `PUT api/v1/staff/portal/profile/contact`, no permission code (self is always visible and is not
  scope — the `ProfileController` rule). It takes `UpdateStaffContactRequest`, which **has nowhere to
  put an employment field**, so the endpoint cannot be persuaded to write one however a client asks.
  `JobTitle` is on that record because the class-teacher card writes it, and the portal endpoint
  IGNORES it: what the school calls somebody is the school's decision.
- **EMPLOYMENT — the school's record.** Dates, terms, qualification, registration number, date of
  birth, sex, national ID, employee number. `PUT …/staff/structure/members/{userId}/profile`, gated
  on `staff.structure.manage`. **A person editing their own start date or teaching registration
  number would make the MoES staff return unauditable**, which is the whole reason for the line.
- **Neither** may touch role, permissions or branch. `RoleAssignmentGuard` owns those.

Four rules that are easy to break:

- **`StaffGroup` is DERIVED, never stored.** `IStaffPerformancePolicyService.GroupFor` reads it off
  the person's role, so it is absent from every write request. Changing it means changing the role.
- **The read is `staff.records.view` plus the staff scope, and answers 404 — never 403.** The same
  rule as `VerifyStudentAccess`: a 403 confirms the person exists.
- **The activity summary names the FIELD that moved, never its value.** "national ID updated" is the
  useful half; the values go in `DetailJson`, which no endpoint returns. Recorded at
  `WelfareVisibility.Confidential`.
- **The person is told only about what they would notice** — job title, employment terms — not on
  every save. A corrected national ID is an administrative fix, and a notification on each one
  trains people to ignore the bell. A self-edit notifies nobody at all and is recorded in the log,
  which is where "who changed this number" is answered.

**Adding staff no longer leaves the module.** "Add staff" was `NavigateTo("/admin/users")`, which
dropped the person into Administration with no explanation and no way back — reported as a blackout,
which is the right word. It is a dialog on the directory now, posting to
`POST …/staff/structure/members`: the same user-creation path underneath, **`RoleAssignmentGuard`
included** (a new endpoint that assigns a role and does not call it is the 2026-09-18 privilege
escalation again), the same temporary password, the same onboarding checklist. Deliberately narrower
than Users & Roles — permissions and deactivation stay there and the dialog links to them.

**`RoleListDto` moved to `Q-Mgr.Shared`** on the way: the Web was maintaining two independent copies
of that wire shape and a third was about to be written for the dialog. Same bug class as
`OrganizationBrandingDto`, `ContentDto`, `NotificationDto`, `UserInfo` and `SubscriptionPlan`.

### A hub section may hide its title. It must NEVER hide its actions (found 2026-09-19)

**Nine sections folded into hubs on 2026-09-18 wrapped their whole `page-header` — title AND
buttons — in `@if (!Embedded)`.** The hub has nowhere to render those buttons, so they simply
vanished: inside its hub the **Scoring Policy could not be saved**, the **School Day could not be
saved**, a notice could not be created, a rota slot could not be added, a subject could not be
created and a report could not be printed. All nine pages were read-only and nothing said so.

Guard only the `header-content` / `header-left` block; leave the `page-header` element and its
`header-actions` outside the condition. **`scripts/e2e/section-actions-check.mjs` fails the moment a
section hides an action again**, and `scripts/e2e/suite-route-check.mjs` catches the other half of
this class — an e2e suite still driving a route the hubs retired, which is how three suites came to
measure a 404 page and report it as a product bug.

## A hub owns the route; its sections own nothing (2026-09-18)

Sixteen Staff Performance entries in the left navigation became six. The user's direction was
"related components can be grouped together, same way student welfare combines the links in some hub
kind of thing", then, when the first cut kept the old routes working alongside the new ones, **"why
are we having old links? we need ssot for the links. otherwise we get more confusion"** — and then
"remove the duplication from the left navigation of the menu items in the hubs".

The six: **Staff Directory** (`/admin/staff` — People, Departments, Coverage, Import), **Records**
(`/admin/staff/records` — Records, Reports), **Duties** (`/admin/staff/duties` — Registers, Rota,
Reports), **Timetable** (`/admin/timetable` — Timetable, Lessons, Subjects, School Day, Teaching
Reports), **Appraisals**, and **Setup** (`/admin/staff/parameters` — Parameters, Scoring Policy,
Notices, Activity Log).

- **A section has no `@page`.** Eleven routes were deleted outright, not left as aliases. A hub that
  works while its sections keep their own routes is two sources of truth for one screen, which is
  exactly what the user objected to. A section carries `[Parameter] public bool Embedded` instead;
  the host then owns the page title, the header band and the tab strip.
- **The hub owns `?tab=`, and nothing else may bind that key.** `TeachingReports` had its own
  `?tab=` for its four sub-views and moved to `?view=` — two components binding one query key is the
  same duplication one level down. Records and Setup did not read `?tab=` at all for a day, so every
  deep link landed on the default tab in silence.
- **Retiring a route means finding its callers, and they are not all in the Web project.** Four
  notification `ActionUrl`s (`StaffPortalController`, `StaffRotaController`, `ReminderLadderJob`,
  `StaffPerformanceJobs`), two Dashboard tiles and a print page's Back button still pointed at
  deleted routes — a person tapping the notification would have got a 404 with nothing to say why.
  Grep the API and Shared projects too, and check `ModuleRouteMap` for an entry that no longer
  exists.
- **`StaffHubTabs` decides which tabs a caller sees, and it is the one home for it.** Each folded
  page carried its own permission gate and redirected to `/unauthorized` when it was not met. That
  was right while each had its own route — the nav entry was simply absent for someone who could not
  open it. Inside a hub it breaks twice: **a tab whose section would refuse the caller must not be
  rendered** (otherwise clicking it throws them out of a page they were allowed to be on — the same
  rule as Welfare Reports' Import History button), and **the hub's own gate is the OR of its
  sections, never the first section's permission**. Gating Records on `staff.records.view` alone
  silently took Reports away from a `staff.reports.view` holder who had it the day before. An empty
  visible list is the only thing that redirects.
- **A section's data load is guarded on its own tab being visible**, so a structure-only caller does
  not fetch a staff list they may not read.

Verified by `scripts/e2e/browser/staff-nav-hubs.mjs`, **42 checks in a headed Chrome**: six entries,
eleven absences, five hubs with the right tab counts, all eleven retired routes 404ing rather than
serving a second copy, and every deep link opening its own section.

### The other two groups followed on 2026-09-19 — Communication 10 → 4, Administration 14 → 4

The user's answer when both were put to them was **"both, in this pass"**. Communication is
**Library** (Media · Documents), **Signage** (Playlists · Campaigns · Display Zones · Schedules),
**Broadcasts** and **Feedback** (Responses · Survey questions · Reports). Administration is
**Branches** (Branches · Counters · Service Types), **Users & Roles** (Users · Roles · Join Requests ·
Onboarding), **Appearance** (Branding · Kiosk · Printing · Customer Links) and **Settings** (General ·
Notifications · Industry · Integrations). **Eighteen routes were deleted outright**, not aliased.

**`Components/Shared/HubTabs.cs` is the ONE home for all three groups.** It was `StaffHubTabs` under
`Components/Admin/Staff` and moved rather than being copied the moment a second group needed it — a
gate that decides who sees what is the worst possible place for a second copy that can drift. Note the
five Staff hubs each held a FIELD called `HubTabs`, which shadowed the type the moment it was renamed;
they are `hubTabs` now, matching `hubTab` beside them.

Four rules this pass added, none of which the Staff hubs had to face:

- **A hub can cross module boundaries; its single route cannot.** Branches is base product while
  Counters and Service Types are Core Queue; Users & Roles is base product while Join Requests and
  Onboarding are Welfare & Performance; Settings spans base product, Core Queue and Integrations & API.
  Each of those pages carried its own `ModuleRouteMap` entry, and one route cannot carry several module
  requirements. The requirement moved onto the SECTION —
  **`HubTabs.Section.RequiringModule(...)`** — and onto the sidebar gate.
  **`ModuleAllows(path)` is the trap here: it returns TRUE for an unmapped path** (no requirement
  found), so once a route is deleted from the table every `ShowAdmin*` gate built on it silently
  opened to every tenant. They read `HasModule(ModuleCodes.X)` directly now. A failed module load
  still shows every tab, the same fail-open as every page-level redirect.
- **A same-route `?tab=` navigation does not re-run `OnInitializedAsync`.** A link from one tab of a
  hub to another — Join Requests offering "Join link and onboarding", Schedules offering Playlists —
  changes the URL and nothing else unless the hub follows it. **`HubTabs.FollowQuery` is that one
  home**, called from `OnParametersSet`; it compares against the last query value ACTED ON, never
  against the active tab, or clicking a tab snaps the reader back to whatever the URL still said.
  The five Staff hubs were moved onto it too, and off their hand-rolled `QueryHelpers.ParseQuery`.
- **`MainLayout.IsActive` strips the query.** `ToBaseRelativePath` keeps it, so `/admin/users?tab=roles`
  did not equal `/admin/users` and the sidebar entry lost its highlight on every non-default tab of
  every hub — including the Staff hubs, silently, since 2026-09-18.
- **A section that is not routable cannot use `[SupplyParameterFromQuery]`.** It binds only on a
  routable component, so `DocumentLibrary.highlightId` became a plain `[Parameter]` and the LIBRARY
  HUB reads `?document=` and passes it down. The hub owns `tab`, the section owns `document`.
  A link naming a document with no tab opens Documents, because the notification it came from is
  about a document.

**Two gates were wrong before the merge and the hub is what exposed them**, which is the argument for
running `HubTabs` even where every section shares a permission: the sidebar gated Feedback Reports on
`feedback.view` while the page enforced `reports.view`, so a holder of one and not the other saw a
link that bounced them to `/unauthorized`; and `UsersSetup` demanded `users.view` for a page holding
BOTH the users and the roles list, locking a `roles.view` holder out of the roles. Users and Roles are
separate tabs with separate gates now.

**"Campaign Marketing" is Broadcasts, and the signage section is Campaigns** (user decision, 2026-09-19,
chosen from four options). Two unrelated things were called campaigns and would have sat one line apart
in the sidebar. The route `/admin/marketing` did not move — only what a person reads.

**`scripts/e2e/route-audit.mjs` exists because of this work and should be run after any route change.**
It reads every `@page` in the solution and every `ActionUrl` / `NavigateTo` / `href` / `Href` /
`ConfigureUrl` in Web, API and Shared, and reports links that resolve to nothing. It found two real
bugs on its first run: `NotificationDispatchJob`'s "delivery is failing" alert pointed at
`/admin/notifications`, **a route no page has ever declared** — the one notification whose job is to
report that email or SMS is broken could not be opened — and `ReportsOverview` reached
`/reports/feedback` through `NavigateToReport("feedback")`, an interpolated fragment invisible to a
grep for the literal route. **Do not build a route by interpolating a fragment**; that card now
carries its whole path, precisely so a sweep can see it.
### Names a person reads (user decisions, 2026-09-18)

`My Portal` → **My Workspace**, because `/portal` is the person's own hub — their file, score, to-dos
and timeline — which is what the word means. `My Day` → **My School Day**: that page is date-scoped,
and a name without the day in it loses what the page is organised around ("My Workspace" was
considered for it and rejected for the same reason — it would have collided with the portal).
`Engagement & Communications` → **Communication**: the module holds signage, broadcasts, feedback and
the Library, all of it communication either way, while "Engagement" described only the feedback half
and is the marketing word for it.

**The routes, the module codes and the stored event keys did not move** — `/portal`, `/my-day`,
`engagement-communications`, `NotificationEventKeys.StaffMyDay` and `MyDayLocalTime` are wire
formats. Only labels changed.

**A display name in a seeded catalog needs a migration, not a seeder edit.** `ModuleCatalogDefaults`
is insert-if-missing only (deliberately — prices and names of an existing row belong to the Module
Catalog editor), so an existing install never picks up a new name from the seeder. The migration
renames **only where the row still holds the value it shipped with**, as
`FoldStaffPerformanceIntoWelfareModule` established: once an administrator has edited a row, it is
theirs and a deploy must not silently undo it. The same trap caught the Administrator role rename the
same morning, where `RbacSeeder` was insert-only for names.

## A page that reads the branch once never reads it again — `OnBranchChanged` is the only signal

The old "a SuperAdmin sees an empty Category list until the page is reloaded" note was one instance of
this; it was investigated and closed 2026-09-18. **There is no `OrganizationState` service and no
organization-changed event anywhere in `Q-Mgr.Web`.** A SuperAdmin's chosen organization lives only as
three private fields on `MainLayout`, and picking one raises nothing. `BranchStateService.OnBranchChanged`,
raised by `SetBranchAsync`, is the app's ONLY cross-page change signal — so a page that wants to react
subscribes to the *branch* event, because there is nothing else to subscribe to.

A SuperAdmin arrives with `CurrentBranchId == Guid.Empty`. A page that reads it once in its first render
then fires requests at `…/branches/00000000-0000-0000-0000-000000000000/…`, which the API answers **404**,
and a fetch with no `else` renders an empty list with no explanation. A reload fixes it only because
`SetBranchAsync` has by then written the branch to localStorage.

- **Subscribe, clear, reload, dispose.** `StudentWelfareTimeline` is the reference: subscribe next to the
  branch read in `OnAfterRenderAsync(firstRender)`; the handler returns if the id is unchanged or empty,
  **clears the branch-scoped caches** and re-runs the load; `Dispose` unsubscribes. Clearing matters as much
  as reloading — `StudentRoster`'s quick-log dialog guarded its category fetch with `if (!list.Any())`, so
  after a switch it offered the previous organization's categories and the API refused the post.
- **Guard `Guid.Empty` and say so**, in the shape `WelfareCategoriesSetup` already used: a Warning toast
  reading *"No Branch Selected — Please select a branch from the header first."* The guard is what the page
  shows while nothing is chosen; the subscription is what removes the reload.
- **A lookup fetch with no `else` is the bug underneath the bug.** Three welfare forms swallowed a failed
  category fetch silently. Any future cause — a revoked module, a 500, an org mismatch — reproduces the
  original report exactly, and no amount of subscribing helps.
- **`BranchAwareComponentBase` is that one home, and every page uses it (built 2026-09-18).**
  `Components/Shared/BranchAwareComponentBase.cs`. A page writes `@inherits BranchAwareComponentBase`,
  drops its own `@inject IBranchStateService` (the base injects it), calls **`TrackBranch()`** once from
  `OnAfterRenderAsync(firstRender)` next to the first branch read, and overrides
  **`OnBranchChangedAsync(Guid)`** to clear branch-scoped caches and re-run its load. **The base owns the
  guard on an unchanged or empty branch, the `InvokeAsync` marshalling, the disposed check, the
  `StateHasChanged` afterwards, and the unsubscribe.** Never subscribe to `OnBranchChanged` by hand
  again: a grep for `BranchState.OnBranchChanged` under `Components/` should return **nothing**.
  59 pages inherit it.

- **A page NEVER declares `@implements IDisposable`, `@implements IAsyncDisposable`, or a `Dispose`
  of its own. Teardown goes in an override of `DisposeCore()` or `DisposeCoreAsync()`
  (found 2026-09-19, and it had been live since the base class shipped).** Thirteen pages declared
  one of those interfaces beside `@inherits BranchAwareComponentBase`, and on every one of them the
  base's `Dispose` **never ran**: the `OnBranchChanged` handler was never removed and `_disposed`
  never became true, so a stale instance re-ran its whole load on every later branch switch for the
  life of the circuit — `IBranchStateService` is `AddScoped`, meaning one instance for the whole
  browser session, so the handlers piled up one per visit.

  Two separate mechanisms, and only one of them warned:
  - **Eight pages** wrote `public void Dispose()` beside `@implements IDisposable`. That second line
    re-declares the interface, which **re-maps `IDisposable.Dispose` to the page's own method** (C#
    interface re-implementation, §18.6.4) and leaves the base's unreachable. The compiler reported
    it as **CS0114 on every build** and nothing acted on it. Five of the eight had an *empty* body,
    so those pages did nothing at all on teardown.
  - **Five pages** wrote `DisposeAsync` beside `@implements IAsyncDisposable`. Blazor's renderer
    disposes a component with `IAsyncDisposable` **if it has one and `IDisposable` only otherwise**
    — an `else if`, not both. Nothing reported this one at all: they are different interfaces, so
    there is no hiding and no warning of any kind.

  The base now implements **both** interfaces, its `Dispose` and `DisposeAsync` are **not virtual**,
  and each unsubscribes before calling the page's hook. Trying to `override Dispose` is a compile
  ERROR now rather than a silent leak, which is the point of sealing it off. A grep under
  `Components/` for `@implements IDisposable` or `@implements IAsyncDisposable` on a page that
  inherits the base should return **nothing**.
- **Converting the 26 hand-rolled copies was a FIX, not tidying.** The common shape was
  `_ = InvokeAsync(async () => { await LoadAsync(); StateHasChanged(); });` — fire-and-forget, with no
  guard on an unchanged or empty branch and no disposed check, so a switch reloaded twice, reloaded
  against `Guid.Empty`, or ran against a disposed component. The base fixes all three at once.
- **Thirteen components deliberately do NOT react and must stay that way**: `MainLayout` (it is the
  *raiser*), the five public routes whose branch comes from the URL rather than the switcher
  (`CustomerDisplay`, `SignageDisplay`, `KioskMode`, `DisplayLayout`, `KioskLayout`), and the seven
  one-shot print routes, where a mid-print branch switch is not a real flow.
- **A page that moves a SignalR group or connection must move it in the override.**
  `VisitorDisplayBoard` leaves the old branch group and joins the new; `QueueBoard` and
  `CounterTerminal` reconnect. Without that the board keeps receiving the previous branch's activity
  and none of the new one's — which looks like "live updates stopped", not like a branch bug.
- **Verifying this needs TWO branches whose data differs, and the dev tenant has ONE.** Rendering
  without throwing is not reloading. Create a second branch, use **students** (branch-scoped) rather
  than welfare categories (**organization**-scoped, identical on every branch, so useless here), switch,
  and assert the row count follows. Proven 2026-09-18: Student Roster went 7 rows → 0 → 7 across two
  switches, in place, no error bar. `scripts/e2e/browser/roles-and-branch-ui.mjs` does this and skips
  honestly when only one branch exists.

**CLOSED 2026-09-15 — uploads are served by a controller with a per-file decision; see the
"Uploads are gated per file" section below.** Until then they lived under the API's own `wwwroot`
and `UseStaticFiles()` served every welfare attachment and visitor photograph to anyone holding the
URL, 28 lines before `UseAuthentication()` ran. The finding, its reproduction and the fix are now
normal engineering history in `docs/TASK_TRACKER.md` (Phase 84); the untracked
`SECURITY-UPLOADS.local.md` that held them while the host was exposed has been deleted.

The general rule, extending the notification-hub one above: **`ApiBaseUrl` is for Web's own
server-side calls only. Anything a browser will fetch or post to (a saved link, an upload target,
a docs link) must use a public origin, never the request host of a Web-to-API call.**

### CORRECTION (2026-09-09): Chrome CAN drive the local app — the earlier note was wrong

An earlier version of this file said Chrome could not load `http://127.0.0.1:5003` and that
served-HTML assertions were the only fallback. **That was a misdiagnosis.** The full correction —
both causes, the working recipe, and the two tooling limits that genuinely remain — now lives in
one place: **"Running it locally — and the browser DOES work"** above. This heading is kept only so
anyone who remembers the old wording lands somewhere that says it was wrong.

## Dates have one home, and it is not the browser (decided 2026-09-09)

**Every date shown to a person goes through `Q-Mgr.Web/Services/QDateFormat.cs`.** An audit found
**seventeen** different hand-typed format strings across the Razor files — `MMM d, yyyy`,
`MMM dd, yyyy`, `d MMM yyyy`, `MMMM dd, yyyy`, `dddd, MMMM dd, yyyy` and twelve more — so the
same timestamp read differently on two adjacent pages.

The canonical form is **day-first, month abbreviated, four-digit year**: `09 Sep 2026`. Not because
it is prettier, but because `03/09/2026` is 3 September to a Ugandan reader and 9 March to an
American one, and on a safeguarding record that ambiguity is a real problem. Everything is
`InvariantCulture` on purpose, so the screen, the CSV and the printed sheet cannot disagree — a
report whose dates changed language between the preview and the download would be worse than one
that is only ever English.

**`QDatePicker` is the project's own calendar, not `<input type="date">`.** It was a thin wrapper
until 2026-09-09, which meant its display format came from the **browser's locale** and this
application could not change it. It now renders its own Monday-first popover, honours `Min`/`Max`
by disabling out-of-range days, and keeps every parameter the wrapper had so its 23 call sites
across 10 files needed no edit. `ShowTime` still uses the native time input — a time field has no
ambiguity to fix and no calendar to draw.

**`QDateRangePicker` is the only date-range control.** Twelve pages filter by a range and, before
this, three different preset sets existed. It composes two `QDatePicker`s plus one shared preset
list (`DateRangePresets`) and emits a single `RangeChanged`, so a page reloads once per edit
instead of twice, and cannot briefly query a nonsense From/To pair in between. `DateRange`'s
constructor normalizes a reversed pair rather than returning an empty result nobody can explain.

**The migration is FINISHED as of 2026-09-10 — this note said it was incomplete and is corrected.**
It described 62 remaining hand-typed formats and eight files still on loose pickers. All 88
occurrences (28 distinct spellings) across 39 Razor files are gone, including every interpolated
`{x:MMM d, yyyy}` specifier and the ones inside the raw-string print templates; every raw
`<input type="date">` is gone; and `Invoices` and `Campaigns` moved to `QDateRangePicker`. See
Phase 81 in `docs/TASK_TRACKER.md`. **Anything new goes through `QDateFormat` — there is now a
member for every shape the app actually uses, so reaching for a literal means one is missing.**

Two things were deliberately NOT migrated, so nobody "finishes" them again:

- **A single date on a form is correctly a loose `QDatePicker`**, not a range picker — Student
  Roster's date of birth and admission date, a schedule's own start/end, the reset time, a flag's
  review date, and the welfare create/edit forms. `QDateRangePicker` is for a *filter* over a
  period. The older note conflated "uses a loose picker" with "should use the range picker".
- **Time-only formats stay hand-typed on live clocks.** `HH:mm` and `HH:mm:ss` were the only two
  spellings in the app and neither can be misread, so time was never the ambiguity this class
  exists to fix. `QDateFormat.Time` / `TimeWithSeconds` exist for new work; the clocks that sit
  beside a migrated date were moved anyway so those lines read from one place.

## The page must never scroll sideways (decided 2026-09-09)

Reported as "overlapping items" on a phone. Nothing was overlapping: `.timeline-card-actions` was
a `display:flex` row with **no `flex-wrap`**, so four buttons were wider than the phone. Measured
at a simulated 390px, that row was **555px — 165px of overflow.**

The reason the symptom was *page-wide* rather than confined to one card is the part worth
remembering: **a flex or grid child defaults to `min-width:auto` and refuses to shrink below its
content**, so one over-wide row widened the document itself, the browser scrolled right, and every
other element moved with it — heading clipped, chips clipped, a button sitting outside its card.

Three layers now, in `layout.css`: `min-width:0` plus `overflow-x:clip` on `.qm-main` and on page
and card children (the actual root cause); `flex-wrap` at source on real action rows; and a mobile
block covering the recurring `*-actions` / `*-footer` / `.filter-row` families. `clip` rather than
`hidden` because `hidden` creates a scroll container and would break `position:sticky` inside it.
**Tabs scroll, they do not wrap** — a wrapped tab strip reads as broken in a way a wrapped button
row does not. Wide tables keep their own `overflow-x:auto` wrapper: the goal is that the PAGE never
scrolls sideways, not that content is clipped.

**Verifying this needs measurement, not a screenshot.** `resize_window` reports success and the
viewport stays at desktop width (recorded in Phase 22 and confirmed again 2026-09-09 over five
attempts). Clone the suspect row into a fixed-width probe and compare `scrollWidth` against the
container with `flex-wrap` on and off — that comparison *is* the bug, and is better evidence than
a picture.

## The PDF flip-book: a vendored stylesheet, and pages that must be `<img>` (rebuilt 2026-09-14)

The viewer (`Components/Shared/PdfFlipbook.razor`, `wwwroot/js/pdfFlipbook.js`,
`wwwroot/css/pdf-flipbook.css`) had **never once rendered** between Phase 43 and Phase 83, and the
reason is worth keeping: `App.razor` linked `page-flip@2.0.7/dist/css/page-flip.browser.css`, a path
**that package does not publish** — a 404 for as long as it existed. `.stf__block` is the element
page-flip measures (`Render.getBlockHeight()` → `distElement.offsetHeight`), so without
`position:absolute; height:100%` it collapsed to 0px and the stretch branch clamped the whole book
to zero. Flips, events and the page counter all keep working at zero size, so it presented as "it is
not a flip book", never as an error.

- **The page-flip stylesheet is vendored into `pdf-flipbook.css`. Do not re-point it at a CDN** —
  the only stylesheet in the package is `src/Style/stPageFlip.css`. The vendored copy also fixes the
  package's own `.sft__wrapper` typo, which is what lets the wrapper carry a height.
- **A page element is an `<img>`, never a `<canvas>`.** page-flip animates a turn by
  `cloneNode(true)` on the page (`Page.newTemporaryCopy`), and canvas pixels do not survive a clone
  — a canvas-backed page goes blank for the whole animation.
- **Never hide the viewer's body while it loads.** page-flip measures its container on construction
  and a container inside a `display:none` subtree measures 0×0. The status panel is an absolute
  overlay for exactly this reason.
- **`autoSize` is off and must stay off.** It derives the book's height from its width alone, so in
  a landscape container the book overflows vertically and shows a magnified corner of page one. The
  size comes from an explicit box set on the surface element, refreshed by a `ResizeObserver`.
- **pdf.js and page-flip load on demand, not from `App.razor`.** They were blocking `<script>` tags
  on every page of the app (~340KB for pdf.js alone) for the handful of routes that show a PDF.
- **Signage turns its own pages.** `PlaylistPlayer` no longer runs a fixed timer for a PDF
  (`GetDefaultDuration(Pdf)` is 0, `Pdf` is out of `UsesTimerBasedAdvance`); the item's own
  `DurationSeconds` is the **per-page** dwell, and the flip-book reports its end like a video does.
  A PDF that fails to open must therefore ALSO raise ended, or signage stops dead on it —
  `MediaPlayer.HandlePdfFailure` does both.
- **Idle drives two things off one concept: the chrome fades AND auto-advance pauses.** A kiosk that
  keeps turning pages while somebody reads makes search and zoom pointless — you find page 9 and
  three seconds later the document has moved on. Any real interaction stops the timer; the same idle
  timeout that fades the chrome back out resumes it. `bindIdle`'s initial call passes `fromUser:
  false` so an untouched screen starts flipping at once instead of waiting out the first window.
- **On a narrow screen the viewer is not a flip-book at all (2026-09-15).** A whole A4 page
  letterboxed into a phone-height stage puts body text at ~6px — seen in the field on the first
  shared document. Below 900px (and never on signage, which is built on page turns)
  `pdfFlipbook.js` picks `mode: 'scroll'`: the same page elements stacked vertically at
  fit-to-width, current page from an `IntersectionObserver`, render at up to 3× device pixels,
  pinch and double-tap zoom handled by the viewer itself (the app's viewport meta forbids browser
  zoom for the kiosk's sake), a compact one-row toolbar with 44px targets, thumbnails off by
  default, and a lighter, sparser watermark below ~900px of render. **A PDF cannot reflow**;
  fit-width plus zoom is the ceiling for a self-hosted viewer, and the page should not pretend
  otherwise. **The reader can switch: a Book / Scroll toggle in the toolbar** (`setViewMode(id,
  'book'|'scroll')`), remembered in `localStorage['qmgr-pdf-view']` and honoured at init on any
  screen; absent a choice, the width decides. A narrow screen's book is single-page, since a spread
  there is two half-width pages. Signage never offers it. Switching to the column must reset the
  page elements page-flip styled — its frame loop can write inline styles once more after
  `destroy()`, so the column's CSS carries `!important` on position/size/transform, on purpose.
  The thumbnail rail is rebuilt from the bitmaps already made (`refreshThumbs`) whenever Blazor
  re-creates the rail element; before 2026-09-15 hiding and showing the rail emptied it.
- **`pageFlip.flip()` animates correctly ONLY to an adjacent spread — anything further needs
  `turnToPage()`.** `flipToPage` primes `currentSpreadIndex` to one-before-target and then animates
  from whatever is actually *rendered*, so a multi-spread jump moves exactly one spread and stops.
  It wraps that in its own `try/catch`, so it never throws and a `catch`-based fallback around it is
  dead code. Measured before the fix: stepping search matches from pages 9,10 went 7,8 → 5,6 → 3,4,
  ignoring the target every time. `goToPageIndex` now picks per distance — this is the one path
  thumbnails, the page-number box and search hits all share.
- **Finish any in-flight animation before reading the current page.** An animation owns an
  `onAnimateEnd` that calls `showNext()`/`showPrev()` when it lands; jump over the top of it and
  that handler fires afterwards, leaving the book one spread off. On signage this is a live race,
  not a theoretical one — the auto-advance flip takes 700ms and a visitor tapping a thumbnail during
  it got the wrong page. `goToPageIndex` calls `getRender().finishAnimation()` first.
- **A percentage height does not resolve through the media library's preview-modal chain**
  (`.preview-body` is `flex:1` with only a `min-height`), and a row flex does not rescue it either.
  `.pdf-container` is a **column** flex so `flex: 1` acts on the height. Measured, not assumed.

**Superseded 2026-09-15:** this note used to say that `/uploads/*` never carried CORS headers
because `UseStaticFiles()` served them before `UseCors()`. Uploads are a controller now
(`UploadsController`, see the gated-uploads section), so `UseCors()` applies to them and a local
cross-origin fetch works once `http://127.0.0.1:5003` is in `Cors__AllowedOrigins`. The middleware
order itself was not changed.

**A tooling trap that generalizes beyond this feature: Chrome runs NO `requestAnimationFrame` in a
minimised or backgrounded window**, and page-flip's entire render loop is rAF-driven. With the window
minimised, `flipNext()`, `flip()` and `turnToPage()` all looked completely dead — they change
internal state, but the DOM is only touched in `drawFrame`. That cost about 40 minutes of hunting a
bug that did not exist. **Check `document.visibilityState` and count rAF frames before diagnosing
anything animation-driven as broken.** This sits alongside the two known tooling limits already
recorded above (`resize_window`, the print dialog).

## Printing is the browser's job (decided 2026-09-09)

**No PDF rendering library, on the server or in the page.** The A4 welfare report
(`Components/Admin/WelfareTimelineReport.razor`, route
`/admin/students/{id}/welfare/report`) is HTML plus `@media print`, and the browser's own
"Save as PDF" produces the file. This is the same standing no-third-party-server-dependencies rule
that keeps PPTX rendering out of this project.

Two conventions that came out of building it. **A printable document gets its own route on
`MinimalLayout`**, not a hidden block on an already-large page: the page then *is* the document,
its period travels in the query string so the link is shareable, and there is no app chrome to
hide. And **severity on paper is carried by a border and a weight, never a background tint** — a
tinted chip prints as an indistinguishable grey on the mono printer in a school office.

Note the global print stylesheet in `app.css` was, until 2026-09-09, hiding Radzen's own
`.rz-sidebar`/`.rz-layout-header` — classes **this app never renders**. Its shell is
`.qm-sidebar` / `.qm-header` / `.qm-footer`, so every printed page carried the whole navigation
down the left-hand side. If you add a print surface, check it against the real shell.

## Auth: login identifier and SuperAdmin credentials (decided 2026-08-21)

Login (`AuthController.IdentifyUser`/`Login`, and `Login.razor`'s single input field) accepts
**either an email or a username** as the identifier — this was explicitly requested and did not
already work: both endpoints only ever queried by `Email`, and the client-side form had a strict
`[EmailAddress]` validation attribute that rejected a bare username before the request even left
the browser. Both are now fixed (`Email == identifier || Username == identifier` server-side;
label changed to "Email or Username", `[EmailAddress]` removed client-side). Keep this in mind for
any future auth-related work — a bare-username identifier is a supported, intentional input shape
here, not an edge case to reject.

The platform SuperAdmin account is `support@getsacc.com` / `admin` (username `superadmin`) — a
user-requested change from the old `superadmin@qmgr.platform` default. **This account is seeded in
three independent places**, discovered while making this change: `DbSeeder.SeedRbacDataAsync`,
a separate `RbacSeeder.SeedPlatformAdminUserAsync`, and the existing dev DB row (updated directly
via SQL, since seeding is idempotent-skip once a user exists). `RbacSeeder`'s seeder runs *first*
in `Program.cs`'s startup order, so it's the one that actually wins the race on a fresh install —
if these credentials are ever changed again, all three must be updated together or a fresh install
will silently end up with stale ones. `make-superadmin.sql` and `create-demo-users.sql` (repo
root) also reference these credentials and were fixed to match — they had their own unrelated bugs
too (wrong schema/table-name casing, since this DB's tables live under `qmgr.*` in lowercase
snake_case while columns are PascalCase-quoted; `create-demo-users.sql` also referenced a
nonexistent `'agent'` role code instead of the real `'staff'`, and used string literals for the
integer-enum `Status`/`Tier` columns).

Also removed 2026-08-21: the "Quick Demo Access" one-click login buttons on the login page
(Super Admin/Admin/Staff), per explicit user request — "prepare for prod... if user needs demo,
they register and get trial." Don't re-add this pattern without checking with the user first.

## Standing constraint: no third-party dependencies on the server

Decided 2026-08-19 when PowerPoint-as-slideshow was on the table: genuine slide-by-slide PPTX
rendering has no dependency-free path (there is no PPTX equivalent of PDF.js; the realistic options
are all a real added dependency — a locally-installed converter like LibreOffice headless, a
commercial rendering SDK, or an external paid conversion API). The user was explicit: **do not
install third-party dependencies on the server, in the Docker image, or anywhere in the deploy
target — if a feature's only path requires one, leave the feature out rather than add the
dependency.** This is why PPT content still renders via the Office Online iframe embed (a URL
pointing at Microsoft's own hosted viewer, zero install cost, same mechanism as the YouTube/Vimeo
embeds) instead of a real converted slideshow — see `docs/TASK_TRACKER.md` Phase 43.

This constraint is general, not PPT-specific — check with the user before adding any new
OS-level package, binary, or paid SDK to the API/Web Dockerfiles or runtime, even if it would
solve a real problem cleanly. Pure client-side libraries loaded via CDN (Bootstrap, PDF.js,
page-flip, etc.) are not what this rule is about — those ship to the browser, not the server, and
have already been added freely throughout this project.

## Standing constraint: prefer enhancing an existing table/field over adding a new one

Decided 2026-09-01 while planning the Student Welfare Ledger's post-MVP phases (see the
`Student Welfare Ledger` artifact's §05 "Data model: enhance before you add"). The user's own
framing: "limit creation of unnecessary tables and fields, prioritise enhancement and improvement.
however if absolutely necessary, new tables or fields can be created." A new nullable column on a
table that already exists is cheap; a new table is not just a migration — it's a new thing every
future query, permission check, join, and report has to remember exists.

**Before proposing or building a new table, check whether the actual need fits as:**
- A new nullable column (or a few) on an existing table — the default choice for a new
  attribute of something that already has a row (e.g., action-taken/assigned-to/due-date on a
  `WelfareRecord` rather than a separate actions table).
- A new enum value on a field that already exists (e.g., a `Draft` status added to an existing
  status enum, rather than a parallel "is this a draft" table).
- A native Postgres array/JSON column, when the real need is "also applies to these other rows"
  without per-row metadata (e.g., linking a few additional students to one record) — cheaper than
  a join table when nothing beyond the ID list needs to be queried or stored per link.

**Only reach for an actual new table when the shape genuinely can't fit any of the above** — a
true many-to-many relationship that needs its own attributes per pairing (not just an ID list), or
a sub-resource with an independent lifecycle an existing row can't represent no matter how many
columns it grows (e.g., `WelfareAttachment` — a file upload is genuinely its own resource, not an
attribute of the record it's attached to).

This is a general project convention going forward, not specific to the Welfare Ledger — apply it
to any new feature's data-model planning, and say so explicitly in the plan (which existing
table/field is being widened, and why a new table wasn't the first choice) rather than silently
defaulting to "just add a table" the way a fresh design tends to.

## Identity normalization has one home, and no Postgres extensions (decided 2026-09-03)

Duplicate-registration detection turns emails, phone numbers and organization names into canonical
forms. Every one of those forms is produced by `Q-Mgr.Shared/Domain/Identity/RegistrationIdentity.cs`
and nowhere else. If you need a normalized value — in a new endpoint, a background job, an import,
a report — call that class. Do not write a second lower-case-and-trim helper next to the code that
needs one; this codebase's recurring SSoT failure (see the DTO section above) is exactly a second
copy of a rule that then drifts from the first.

**No Postgres extensions, by explicit user instruction: no `pg_trgm`, and by the same reasoning no
`citext` and no `fuzzystrmatch`.** This is why name similarity is scored in process, over a handful
of rows narrowed by an indexed blocking key, rather than by a fuzzy-match index in SQL. It is also
why `Users.NormalizedEmail` is a real stored column with a unique index rather than a case-
insensitive type. Treat this as part of the standing "no third-party server dependencies" rule
above — a database extension is a server dependency.

**Two related rules that are easy to break by accident:**

- **A schema change that adds a column feeding a unique index must backfill in the same
  migration.** `20260903191859_AddRegistrationDuplicateDetection` does this: existing rows all hold
  the column default, and Postgres will not build a unique index over a column where every row
  reads the same empty string. Its backfill SQL mirrors the C# normalization step for step, and
  sorts organization-name words with `COLLATE "C"` because that is byte-wise ordering, matching
  `StringComparer.Ordinal` on the C# side. A locale-aware sort silently produces keys that never
  match the ones written at sign-up.
- **Refusing a sign-up needs near-proof; anything softer flags for review.** Only a matching
  canonical email, or a phone number an existing account has *verified*, blocks. Name similarity,
  a shared unverified phone, a shared contact name, a shared network and throwaway-mailbox domains
  all add to a score that flags. A wrongly refused sign-up is a lost customer who cannot appeal, so
  if you are tempted to promote a heuristic to a hard block, that is a decision to put to the user,
  not to infer. The flags are reviewed at `/platform/registration-review`.

## Prices are grandfathered: never charge from a catalog row (decided 2026-09-04)

**Amended 2026-09-05:** this section was written the morning of 2026-09-04, before the tier
retirement later the same day dropped `Subscription.PlanId`, `Subscription.AgreedUnitPrice` and
`AgreedCurrency`. The four `Subscription.*` helpers it originally named no longer exist — the
surviving list is below. The rule itself is unchanged; only tiers are gone.

What a customer pays is recorded on their own row, not looked up when the invoice is raised.
`OrganizationModule.AgreedPriceUgx`/`AgreedPriceUsd` hold it. **Any new code that needs a price to
charge, quote, display or report must read the agreed price with a fallback to list —
`OrganizationModule.GetEffectivePriceUgx`/`GetEffectivePriceUsd`, or
`IModuleAccessService.GetChargeableUgxPriceAsync` — never `plan.MonthlyPriceUgx` and friends
directly.** Reading the catalog row is how an administrator's price edit used to silently reprice
every existing customer's next invoice, which is the bug this exists to prevent. Null means "track
the list price", so the helpers are always safe to call.

Three rules that are easy to break by accident:

- **An agreed price is only valid in the currency it was agreed in.** The helpers fall back to the
  list price on a currency mismatch rather than converting: this system holds no exchange rate, and
  charging a UGX figure as USD is a thousand-fold error. This is why USD MRR does not sum a
  UGX-denominated agreed price, and why the tenant billing overview shows the USD list price to a
  customer whose agreed price is in shillings.
- **A renewal is not a new agreement.** `ModuleAccessService.IsNewAgreement` recaptures the price
  only on a first activation, a re-purchase after cancellation, or a switch of billing cycle.
  Recapturing on a renewal or a `PastDue` recovery would quietly undo the grandfathering at the new
  list price. `ModulesController`'s purchase quote and `ActivateAsync`'s capture share that one
  predicate on purpose: a customer paying one number while the row records another is exactly the
  failure this is guarding against.
- **Passing a price change on to existing customers is a deliberate act.** The Module Catalog
  editor's "Apply to existing subscribers" is off by default and is the only thing that rewrites
  agreed prices in bulk. Note the asymmetry it creates: a locked price shields a customer from an
  increase and also withholds a decrease from them, so a price cut only reaches existing customers
  when somebody ticks that box. The Module Catalog editor is now the **only** price editor there
  needs to be: every row in `subscription_plans` is a module since the tiers were retired, so the
  "tier plans have no editor" gap earlier handovers carried forward has no subject left. `GET
  api/v1/admin/plans` is still read-only and still has no PUT, but it returns the module catalog,
  and the platform dashboard card fed by it now links through to the editor rather than presenting
  prices nobody could change. If a second pricing concept is ever introduced, it needs its own
  "apply to existing subscribers" option or its repricing will be silent where module repricing
  is not.

## Payments go through the sacc.ug gateway, and only the ledger moves a payment (built 2026-09-19)

Reported as *"card shows though I have not enabled it… mobile money says gateway not found… why is this
project forcing me to use stripe?"*, then *"I need to see a debit message on my phone. we are using the
sacc.ug gateway (our crm) for collections"*, *"default currency should be UGX not USD"* and *"keep stripe
disabled"*. The plan is the payments artifact; the gateway's contract was read from **E:\CRM\CRMApi** (and the
live `/api/epay/status` and `/validate` answered 401 without a key, as they should). **Never guess that
contract** — `SaccGateway`'s header comment and `scripts/e2e/sacc-gateway-stub.mjs` both state it.

What was wrong, so nobody reintroduces it: Stripe's `Enabled` defaulted to true and payment-providers read
that bare switch, so tenants were offered a card on installs with no Stripe key; `MobileMoneyService` called a
route and body the gateway does not have (the URL and `X-API-Key` were right, everything after was imagined);
a renewal marked the invoice **PAID the moment the prompt was sent**; a module purchase activated only while
the browser kept polling, from a note kept 30 minutes in a cache; a Mobile Money purchase wrote no invoice
and no payment at all; every MTN payment was recorded as Airtel; the default currency was USD.

- **The ledger rule: a row exists before any money moves.** `IPaymentLedger` writes the invoice and a
  `Pending` `Payment` first; **`Payment.Id` IS the gateway's ReferenceId AND its IdempotencyKey**, so the same
  payment can never prompt twice. Only `SettleAsync` moves a payment on — reached from the signed webhook, a
  status read and the reconciliation job, all three the same code, under `pg_advisory_xact_lock` on the
  reference. **Final is final** (a late "failed" cannot undo a success). **Less money than was due is
  `Review`, never provisioned.** A purchase that fails or is abandoned voids its invoice; a renewal's stays open.
- **The webhook body is a doorbell, not a verdict.** `POST api/v1/payments/sacc/webhook` verifies
  `X-Webhook-Signature` (`t=…,v1=HMAC-SHA256(secret, "{t}.{body}")`, five-minute window, fixed-time compare),
  dedupes `X-Webhook-Id`, then asks the gateway for the status with Q-Mgr's own key. Nothing in the body is
  applied. No stored secret → 401, so the gateway keeps retrying until the webhook is registered.
- **`PaymentReconciliationJob` runs every five minutes** (`reconcile-gateway-payments`) and settles what a lost
  webhook left open; unknown to the gateway after 30 minutes → Abandoned; open past 72 hours → Abandoned.
  `generate-monthly-invoices` was replaced by `generate-due-invoices` (daily), because annual cycles exist.
- **A prompt awaiting approval is not a failure.** The renewal job treats `BillingService.AwaitingConfirmation`
  as neither success nor PastDue — telling someone their payment failed while their phone is asking them to
  approve it is the worst available answer.
- **`GET billing/payment-providers` is the ONE availability rule**, returning `PaymentProvidersDto`: Mobile Money
  = the gateway switched on with an address and key; card = the gateway's card channel (Pesapal) when switched
  on, else Stripe only if configured. **Stripe is off unless an administrator configures it**
  (`StripeSettings.Enabled` defaults false; migration `20260919160000_UgxDefaultAndStripeOff` switched off every
  row with no secret key). Stripe's code is kept, not removed — user instruction "keep stripe disabled".
- **Phone numbers have one rule: Shared `UgandaPhone`**, a mirror of CRM's `PhoneNumberHelper` — MTN, Airtel,
  UTL, Lycamobile. The API, the ledger and `MobileMoneyNumberField` (the Web's one phone field) all use it, so
  a number the form accepts is never refused later for its shape. Logs and reconciliation show it masked.
- **The CRM labels a refusal as PENDING (found in production 2026-09-19, an Airtel number).** When no provider is
  routed for the payer's network, `epay/collect` returns early through `PaymentResponse.Failed(...)`, which never runs
  `ApplyState`: HTTP 202, `status: "Failed"`, an `errorCode`, the reason — but `state: "Pending"`, `isFinal: false` and
  **no `referenceId`**. Believing `state` told the payer to approve a prompt that was never sent, then polled a status
  the gateway 404s (`transaction.notfound`). `SaccGateway` treats a non-final answer with no echoed reference and a
  Failed status or an error code as a refusal, and `PayerRefusal` words it by network ("Airtel Money is not available
  right now. Please pay with an MTN number."). The stub reproduces it (`/__stub/refuse`); e2e 16.7 asserts it. The
  real fix belongs in the CRM (call `ApplyState(Failed)` on that early return) — until then Q-Mgr copes.
- **`Payment.Metadata` is jsonb. Never `Contains` on it** — it translates to `LIKE`, which Postgres refuses on
  jsonb (every purchase 500ed until e2e 16.8 found it), and jsonb rewrites the stored text anyway. Read it
  through `PurposeOf` in memory over a narrowed set.
- **The gateway's settings have their own page and endpoint**: `/platform/payments` (Gateway · Reconciliation),
  `api/v1/platform/payments/*`. The generic Platform Settings editor **refuses** the `MobileMoney` category — it
  knew three of six fields and would have dropped the webhook secret. `ApiKey` and `WebhookSecret` are in
  `SecretProperties` (masked). The address must be https — **http is allowed only for a loopback address in
  Development**, which is what lets the e2e point it at the stub. Moving to a different gateway host drops the
  webhook registration, because its secret belongs to the old one.
- **The API key Q-Mgr holds needs `payments:collect`, `payments:status` and `webhooks:manage` — nothing that
  moves money out.** The collect sends `service: "Other"`: `Subscription`/`License` make CRM provision SACC products.
- **The callback and the card return use `ISaccGateway.PublicBaseUrlAsync`** — `MediaStorage:PublicBaseUrl` (set in
  the production unit), then the SaaS base URL. Never the request host (the loopback in production).
- **Development with no gateway configured simulates success through the real `SettleAsync`**; never elsewhere.
- **The renewal number is always visible on the Payment tab** (`#renewal-number`), whether or not Mobile Money is
  switched on today, and can be set, changed or REMOVED (`DELETE billing/renewal-number`, then renewals wait on the
  Invoices tab). The Overview shows it as a tile linking there. Its "invoices waiting" banner counts `Open` — it
  counted `Pending`/`Overdue`, statuses no invoice is ever given.
- **The legal pages were rewritten 2026-09-19** (effective date moved to 19 Sep 2026): the modules as they are,
  UGX per-module pricing, the gateway as processor, and — in Privacy — the student, guardian and staff data schools
  keep. Terms now says removing a module ends access at once, because that is what the code does.
- **Default currency is UGX** everywhere a new row is made. The migration moved existing organizations from USD
  to UGX only where no USD agreed price and no USD invoice exists — four dev organizations kept USD for that reason.
- **The Web**: every write in `IPaymentApiService` throws `ApiFieldException` carrying the API's per-field
  errors, so a form marks the field; status reads return null so one dropped poll does not end a wait.
  `PaymentWait` only REPORTS — the server settles whether or not the dialog is open, and it says so.
  Mandatory fields carry the asterisk and the dialog opens with "* Required" (`.form-required-note`); a refused
  submit says "Complete the fields marked in red" once (`.form-error`).
- **A payment held for REVIEW is decided by a person, on the Reconciliation tab** ("Needs a decision", every
  held payment whatever the period). `POST api/v1/platform/payments/{ref}/resolve` with `received` or
  `not-received` and a note of ten characters or more; platform-settings edit only. It goes through the ledger's
  own `SettleAsync` ("received" confirms the amount due, or it would be held again), so it is FINAL like every
  settlement, and the decision, who made it and the note live in the payment's existing `Metadata` JSON
  (`PaymentResolution`) — no new column. The list shows "Marked received by … : note".
- **The gateway's defaults are the platform's, in ONE place: Shared `SaccGatewayDefaults`** — `BaseUrl`
  `https://sacc.ug` and the callback path `/api/v1/payments/sacc/webhook` (user direction 2026-09-19: "by default
  use sacc crm configurations … platform default configurations, not for tenants"). The stored setting, the seed,
  the client's fallback and the page all read it; an EMPTY address saves as the default; the callback is always
  this install's public address plus that path, never typed. Nothing about the gateway is ever shown to a tenant.
- **A flex row with inline text in it splits the sentence into columns.** `display: flex` makes every text run and
  every `<strong>`/`<a>` its own item — the Decide dialog's warning, the Modules "Pay for **X**" line and the
  Overview banner all rendered as ragged columns. Icon + ONE `<span>` holding the sentence.
- **Verified by two suites**: `scripts/e2e/payments-e2e.mjs` (section 16, **121 checks**, wired into
  `class-teacher-e2e.sh`) and `scripts/e2e/browser/payments-ui.mjs` (**57 checks**, including removing the renewal
  number, the Overview tile and deciding a held payment). The stub imitates the CRM both BEFORE and AFTER its
  2026-09-19 fix (`/__stub/refuse {final}`). Both host the gateway stub
  in-process and switch the gateway back OFF afterwards. **They have not been run against the real sacc.ug** —
  no money has moved yet; the first real prompt is the platform's own "Send UGX 500 prompt" test on
  `/platform/payments`, after pasting the real key and registering the webhook.

## Staff Performance Monitor: a second subject for the welfare machinery (built 2026-09-16)

The plan is `docs/plans/STAFF_PERFORMANCE_MONITOR.md` (artifact linked at its top). The user asked
for it to be built "fully, word for word" in one pass, with tests afterwards, so **the ten §13
decisions were taken as proposed**: the five new roles rank below `manager`; individual ranks are
private and a public board is a tenant switch that is off; a Confidential record tells its subject
that it exists and what it is called (its NOTIFICATION — the subject reads the record itself; settled 2026-09-17); system-source awards exist but are off by
default; support staff hold the portal and recognition and are appraised by their line manager;
activity attribution is blanked after 12 months and appraisals are never purged; the module shipped
at a placeholder price with `MaxUsersPerBranch = 250` (superseded 2026-09-17, below); periods default to three Ugandan terms
(T1 Jan–Apr, T2 May–Aug, T3 Sep–Dec) with an annual roll-up; rewards are certificates, notices and
the private rank; no wellbeing pulse. **Verified live the same day** by e2e section 14
(`scripts/e2e/staff-performance-e2e.mjs`, 201 checks, called from `class-teacher-e2e.sh`) and in
Chrome at desktop and a real 390px frame; see "What the e2e found" below.

**Staff Performance is NOT a module of its own (user decision, 2026-09-17).** It is part of the Student
Welfare module: code `student-welfare` (a stored wire format, unchanged), display name **"Welfare &
Performance"** — kept short on purpose; the user asked that names stay precise. Every staff controller
carries `[RequireModule(ModuleCodes.StudentWelfare)]`, the route map sends `/portal`, `/admin/staff` and
`api/v1/staff…` to it, and the jobs and `IStaffProfileChangeNotifier` check it. The module row's cap is
250 users a branch (every staff member gets a login). `20260917075614_FoldStaffPerformanceIntoWelfareModule`
renamed the row and raised its limits only where each field still held its shipped value, and removed
the never-deployed `staff-performance` row and its dev grants. `ModuleCodes.RetiredStaffPerformance`
exists only for that migration; nothing gates on it. **The sidebar keeps two short groups, "Student
Welfare" and "Staff Performance"** — menu labels name the feature, not the product bundle.

**A register records its marks on every save (user decision, 2026-09-17).** Each submit writes one
`Final` record per marked person and notifies them; closing only declares the register complete. An
open register still counts as not taken: the recorder's portal to-do says how many are "marked and
recorded", and the register chase still fires. The register page says so above its buttons.

- **`Role` now carries TWO scope columns.** `DataScope` (students, `RoleDataScope`) and
  `StaffScope` (other staff, `StaffDataScope { Organization, AssignedDepartments, DirectReports,
  SelfOnly }`). One enum could not say "all students, my department's staff". `IStaffScopeService`
  (`Infrastructure/Services/StaffScopeService.cs`) is the ONLY reader of `StaffScope`, mirrors
  `IStudentScopeService` shape for shape, fails closed (a head with no department sees nobody),
  answers 404 never 403, and is per-request memoised, never cached. **Self is always visible and is
  not scope**: every caller reads their own file through `api/v1/staff/portal`, which carries no
  permission code (the `ProfileController` rule). The role editor in Users & Roles now sets both
  scopes on a custom role; system roles are repaired by `RbacSeeder` on every start.
- **Delegation is not scope.** Taking a register on a `StaffDuty` is authorised by
  `caller ∈ RecorderUserIds` (or `staff.duties.manage`) and writes records only for that duty's
  expected list, synchronously in the controller, never in a job. A teacher named recorder for one
  meeting can mark the Director of Studies absent from that meeting and nothing else.
- **The staff record is `WelfareRecord`'s shape on a different subject, and `WelfareVisibility` is
  reused as the type.** Append-only; an edit is a `StaffPerformanceNote`; the subject's right of
  reply is a note of kind `Response`; a void is `Status = Annulled` plus a note, and the row drops
  out of scoring. Who is told lives in one early return in `StaffAlertService`: Standard → subject
  in full plus their line manager / heads (unless they logged it); Confidential → subject,
  existence and title only; Restricted → nobody. **The author keeps read access to a Confidential
  record they wrote** (the seeded head-of-department role can file a Confidential lesson
  observation it could not otherwise read); Restricted has no author exception.
- **The score is evidence, the rating is a decision.** `IStaffScoringService` computes per person
  per period on read and nothing stores a score except `StaffAppraisal.ComputedScore` /
  `ComputedBreakdownJson`, frozen at Signed. `FinalRating` (1–5, MoES's scale) is set by a person.
  A parameter with no evidence drops out of the denominator; Wellbeing is never scored; the policy
  refuses a single weight above `MaxParameterWeightPercent` (50, the MET ceiling).
- **`Organization.Settings["StaffPerformance"]` has one reader**, `IStaffPerformancePolicyService`
  (`ReadPolicy` / `WritePolicy`, periods, bands, `BandFor`, `GroupFor`, `DefaultParameters`).
- **`ActivityEvent` is written by explicit `IActivityLogger.RecordAsync` calls, never an EF
  interceptor**, with the summary at the actor's visibility. Its address and agent come from
  `X-Viewer-Ip` / `X-Viewer-Agent`, which the Web now relays on EVERY authenticated call
  (`ViewerRequestContext` set by `Routes.razor`, headers added in `AuthenticationMessageHandler`);
  the API stores them through `DocumentShareService.TruncateIp` / `CoarseUserAgent`, the one rule
  for what an address in a log looks like. Attribution is blanked by `StaffPerformanceJobs` after
  the policy window; the row stays.
- **`Notification.EventKey` is persisted now**, and `CreateNotificationRequest.EmailHtmlBody`
  sends an HTML email verbatim through `NotificationDispatchJob.DispatchHtmlEmailAsync` (the plain
  path HTML-encodes). `GET api/v1/notifications` takes `eventKey` and `offset`; `POST read-all`
  takes `eventKey` and returns the remaining unread count. `/notifications` is the full centre.
- **Mapping and labels each have one home**: `Application/Services/StaffPerformanceMapping.cs`
  (every entity → DTO, signs every upload link) and `Web/Components/Admin/Staff/StaffDisplay.cs`
  (every enum's label, colour and chip variant). `StaffCoverageBuilder`, `StaffReportBuilder`,
  `StaffNoticeFanOut` and `StaffLookups` are the shared computations both controllers and jobs use.
- **`UploadOwnerKind.StaffEvidence`** is classified before media, carries the record's rung and
  its subject (in the classification's `StudentId` slot, "the person this file is about"), and the
  subject may read their own Standard/Confidential evidence.
- **The module catalog is now seeded in every environment** by `ModuleCatalogDefaults` (Program.cs,
  after the RBAC seeder). `DbSeeder.SeedModulesAsync` delegates to it. Until this, a module added
  to the code never appeared in a production catalog.
- **One migration carried the whole schema**, `20260916173550_AddStaffPerformanceGroundwork`,
  rather than the six the plan's phases name: the entities were all in the model before the first
  `migrations add`, and splitting them would have meant removing DbSets to fake history. Nine
  tables, four columns, one appended enum value (`RosterImportKind.Staff`), nothing dropped.
- **The shared component library was consolidated first** (plan §9): `QTimeline`, `QStatTile` /
  `QStatRow`, `QBarList`, `QTabs`, `QChip`, `QEmptyState`, `QActivityLog`, `QRating`, `QFilterBar`,
  `QAvatar`, `QPrintSheet`, and `QPager` adopted. The student timeline, welfare reports, dashboard
  tiles, document activity modal, welfare print sheet, feedback and kiosk star ratings, the two
  hand-rolled pagers and three tab strips were moved onto them. Two visible consequences: Dashboard
  tiles lost their left colour stripe and hover glow (in line with the flat decision), and the
  feedback page's stars now render at the 36px the dead rule intended. `WelfareStatusColor()` has one home
  now, `Components/Admin/WelfareDisplay.cs` (checked 2026-09-19 — the note saying it lived in two pages was stale).
- **Three pre-existing bugs fixed on the way**, all in the Roles tab: permission counts always read
  0 (the list DTO has no permission list), the grouped permissions response was read as flat, and
  Save Permissions posted codes where the API wanted ids and would have wiped the role's
  permissions. Also: `RolesController.UpdateRolePermissions` refuses system roles, so the UI now
  shows "View Permissions" there.
- **Staff import job reads are `api/v1/branches/{b}/staff/import-jobs…`**, gated on
  `staff.structure.manage`, not the student ones (which are gated on the student scope).

### What the e2e found (2026-09-16) — rules to keep

- **Section 14 is Node, because its point is concurrency.** It fires the same write several times
  at once and checks the invariant. Four real races fell out, each fixed with the house pattern:
  the recognition budget (count and insert under `pg_advisory_xact_lock` on giver), the register
  double-submit (the whole read-annul-insert under a lock on the duty, duty re-read under the
  lock), lost notice acknowledgements (one atomic jsonb `||` UPDATE guarded by `jsonb_exists`, never
  read-modify-write of the map), and duplicate notice fan-out (a conditional UPDATE claims
  `NotificationsSentAt` before anything is sent). **Any new "at most N" or "exactly once" rule in
  this module needs one of these, and a concurrent assertion in the suite.**
- **RESTRICTED NEVER SCORES.** `StaffScoringService` (both queries), `StaffReportBuilder` and
  `StaffCoverageBuilder` counted Restricted records, so a teacher's own breakdown showed "Conduct: 4
  records, −8" while they could see none: the score disclosed an investigation the record hides.
  Every aggregate over staff records must exclude Restricted. Lower a record to Confidential to
  let it count.
- **Unseen records make ONE to-do line**, linking to `/portal#my-timeline`, where
  `POST api/v1/staff/portal/records/acknowledge-all` marks them in one statement (Restricted and
  self-logged excluded). Eleven identical "Recognition logged about you" rows buried the appraisal.
- **Mobile:** `table.q-stack` turns a table into labelled cards under 640px (`qStackTable.js` stamps
  `data-label` from the header, so markup needs only the class). Every Staff table carries it.
  `QBarList Stacked` puts label and figure on one line with the bar beneath, for a half-width card.
- **The page gutter was 12px on every desktop page, app-wide.** `layout.css`'s safe-area block
  sat outside any media query with `max(12px, env(...))`, overriding the 24px gutter, so
  `.page-header`'s fixed −24px bleed hung 12px past the edge (clipped, so it looked "slightly off"
  rather than broken). The gutter is now `--qm-page-pad` per breakpoint (24/20/12/8), the safe-area
  rule takes `max(var(--qm-page-pad), env(...))`, and the header bleeds by the same variable.
  **Never pair a hard-coded negative margin with a padding set somewhere else.**
- **Running section 12 locally needs `Cors__AllowedOrigins__4=http://127.0.0.1:5003` on the API**,
  or the share-link origin check refuses and 34 checks cascade-fail. That is correct behaviour for
  a misconfigured origin, not a regression.

### The plan audit (2026-09-17) — what "fully built" was missing, and the rules it left

An audit of the code against the plan, item by item, found two leaks and a dozen gaps; all are
closed and asserted in section 14 (now 288 checks). The rules to keep:

- **`ActivityEvent.Visibility` gates the SUBJECT's own trail.** The subject's portal activity, the
  activity layer of their own timeline and "Export my file" all read the activity log, and none
  filtered Restricted — so a teacher could read "Restricted record viewed for <me>". The column is
  set by `IActivityLogger.RecordAsync(..., visibility:)` (a record's rung; the higher rung on a
  visibility change; Confidential for anything about an appraisal) and backfilled by
  `20260917063804_AddActivityVisibilityAndParameterOffsets`. The administrator's log still shows the
  redacted Restricted summary, as the plan says. **Any new event about a record passes its rung.**
- **An activity summary never states a rating or a score.** Three appraisal events did ("signed:
  rating 4 (Very Good), score 72") to anyone holding `staff.records.view`. The figures live in
  `DetailJson`, which no endpoint returns.
- **The Confidential question is settled: the subject reads the full record on their portal; their
  NOTIFICATION says only that one exists.** Right of reply and s.24 subject access need the content;
  the plan's "title only" (decision 3, §14) describes the alert. `StaffRecordDialog`'s help text says so.
- **Closing a period** is `POST api/v1/staff/policy/periods/{key}/close` (approver; a reason of ten
  characters or more when any appraisal is unsigned, kept on the closure) and `…/reopen` (a reason).
  Closures live in `StaffPerformancePolicyDto.ClosedPeriods`, read through `ClosureFor` / `ClosureOf`
  only, and **every write to the policy blob runs under `pg_advisory_xact_lock` on the organization**
  (`WithPolicyLockAsync`) — the editor's save re-reads closures inside the lock, so it can never drop
  one. A closed period refuses new scored records, finalising, annulment, point corrections, registers,
  recognition, automatic credit and opening appraisals, with one 409 wording
  (`ClosedPeriodProblem`). **Wellbeing records and visibility changes are never blocked** — support
  and protecting a record must not wait for a reopen.
- **`PerformanceParameter.OffsetsParameterId`**: a record on a parameter that offsets an Attendance or
  Duty parameter counts as one recovered occasion there. Seeded Lesson Recovery → Lesson Attendance.
  Before this the seeded parameter's purpose promised the offset and scoring ignored it.
- **The four automatic-credit parameters are seeded** (`StaffParameterDefaults`, on first catalogue
  read and when the policy switches automatic credit on), refused as manual entries, hidden from
  breakdowns and coverage while automatic credit is off. "Customer served" credits the user who
  COMPLETED the ticket (then the history row, then the counter); "Positive feedback" credits a 4–5
  rating to `Feedback.ServedByUserId` — **a column nothing had ever written until 2026-09-17**.
- **Late entry is enforced by the API at the policy's threshold** (409 `LATE_ENTRY`, resubmit with
  `acknowledgeLateEntry=true`, the welfare shape); the Web surfaces it as
  `LateEntryConfirmationRequiredException`. It used to be a browser prompt hard-coded to 14 days.
- **The weight cap cannot exceed 50%** (the policy validates 10–50 and the reader clamps old blobs), and
  it is re-checked when a parameter is reinstated or retiring one would push another over.
- **An annual appraisal is not signed while that year's termly appraisal for the person is unsigned**;
  its DTO carries `TermlyRollup` / `TermlyAverage` live. A moderator's view carries this appraiser's
  rating distribution against the branch's.
- **Exports and Publish to Library are logged** through `POST …/staff/activity/exports` (the file is
  built in the browser, so the page reports it; the caller needs the list's own permission and, for a
  timeline, the person in scope). `ExportResult.Format` carries what was produced.
- **`IStaffProfileChangeNotifier` is the one home for "somebody's role changed"**: cache drop, live
  push, activity event, `staff.profile-changed`. The bulk role change and its undo now use it — **they
  had never cleared the permission cache**, so a person demoted in a batch kept their old permissions
  for up to five minutes. Deputy heads and a department's staff are told of head changes.
- **The sweeps check the module per organization** (four of seven did not), and the duty-reminder
  horizon is the editor's 336-hour maximum.
- **The leaderboard mode reaches the portal**: Department → department averages (departments of three
  or more); Public → those plus the top-N names. Private shows neither.
- **Section 14 triggers the sweeps through the Hangfire dashboard** (`POST /hangfire/recurring/trigger`),
  which Development opens to local callers; against any other API it prints SKIP rather than failing.
- **An observation can link a duty** (the dialog's "linked lesson or duty"), a future feedback session
  becomes a duty on the roster, and the reports show observer pairs to the confidential rung only.
- **A scoped activity log shows an event about somebody only when that somebody is in scope**; an
  in-scope actor admits only subject-less events. The actor rule used to be enough on its own, and once
  registers wrote a line per marked person a head of Maths could read about a Languages teacher.
- **`QDatePicker ShowTime` is a time-of-day control and nothing else.** A field that needs a moment
  pairs a date picker and a time picker bound to the same value inside `.q-datetime-pair`. Five Staff
  fields used `ShowTime` alone from the first build, so a duty could only be scheduled for today and a
  record could never be backdated — which also made the late-entry path unreachable from the page.

- **The branch score set is cached, and freshness is by fingerprint (2026-09-17).**
  `StaffScoringService.ComputeBranchCoreAsync` keys an in-process cache on the policy, the active
  parameters with their last edit, the branch's staff and roles, and the period's records as count +
  latest `UpdatedAt ?? CreatedAt`. No invalidation to remember, so nothing serves a figure older than
  the data — **except a raw `ExecuteUpdate`/SQL that changes a scored column without stamping
  `UpdatedAt`. Never write one.** A miss computes once for all waiting requests, in its own DI scope.
  Measured on 199 staff / 12,496 records: 50 users, portal p95 1,959 → 369 ms. The person's OWN score
  is still computed live on every load.

## Dropdowns: never close on a blur timer alone, and test them with real input (2026-09-17)

Reported as "dropdowns do not fire select". `QSelect` and `QMultiSelect` closed 200ms after blur, and pressing an option blurs the
trigger, so any press held past 200ms selected nothing. **The open list prevents mousedown by default** (no blur happens; the
search box opts out with `@onmousedown:stopPropagation`), and **a focus event on the component cancels a pending close**
(`closeGeneration`). **Every dropdown is `QSelect` or `QMultiSelect`; there is no native `<select>` left, so keep it that way.**
Never put a `QSelect` inside a `<label>`: a click in a label re-clicks its first button and toggles the list shut.

**Verify a dropdown with CDP `Input.dispatchMouseEvent` (press, hold, release), never `element.click()`.** A scripted click fires
no mousedown and moves no focus, and it passed for weeks over this bug. Before blaming a missed press, check
`document.elementFromPoint`: `ConnectionOverlay` covers the page whenever the health check fails, and this app scrolls smoothly,
so read positions after `scrollIntoView({ behavior: "instant" })`.

- **A 429 from `api/v1/health` means reachable**, and that route is whitelisted from IP rate limiting in code. Treating it as lost
  put a click-swallowing overlay over a working app.
- **A page-level module redirect checks `ModuleState.LoadSucceeded` first** (all 19 do). Otherwise an API restart sends every open
  module page to `/billing/modules`. A failed module load is retried by the next `LoadAsync`.

## One size scale for controls, rows and titles (user direction 2026-09-17: "spacing, size and font should be uniform")

An audit script measured every parameterless page (buttons, button-row gaps, titles, body text) before anything changed:
buttons at 34 and 39px, dropdowns at 44px, inputs at 48px, row gaps of 2 to 16px, titles at 22 to 32px, body text at 14 or 16px.
Fonts were already right (Poppins, with Montserrat titles). The scale now lives in two places and nowhere else:

- **`q-components.css` tokens**: `--q-control-h` 32px (`-sm` 28, `-lg` 38), `--q-control-font` = `--qm-text-base` 13px
  (`-sm` = `--qm-text-sm` 12px) and `--q-row-gap` 8px. Every `QButton`, text or time input, `QSelect` / `QMultiSelect`
  trigger and `QDatePicker` uses them; an icon-only button is square; on a phone every control is at least 40px.
  *(These were 36/30/44 at 14px until 2026-09-19; see "One type scale" below for why they moved.)*
- **`layout.css`**: `.header-actions` and the shared row names (`.form-actions`, `.modal-actions`, `.card-actions`,
  `.filter-actions`, `.dialog-actions`, `.q-modal__footer` and a few more) use the row gap; `.qm-main` body text is
  `--qm-text-base` (13px); `.qm-main h1` is Montserrat `--qm-text-xl` (20px), weight 700 (`--qm-text-lg`, 17px, on a phone).
- **A page must not set a control's height, padding or font size, a shared row's gap, or its title's size.** 77 page rules that
  did were removed, and that is where the drift came from. A new row of buttons uses a shared row name or `var(--q-row-gap)`.
- **A hand-styled `<button>` is a `QButton`; a hand-rolled tab strip is `QTabs`.** Segmented pickers that carry meaning in
  colour (the welfare case type, the scanner direction) keep their look on the control tokens.
- **Outside the app shell the scale deliberately does not apply**: the kiosk, the public display and signage, public feedback,
  booking, ticket status, shared documents and print sheets keep their own large-format or paper sizes. `.qm-main` scoping is
  what keeps them out, and a sweep of page rules must skip them (the first sweep did not, and was reverted for those files).
- **Pulled onto the scale 2026-09-18, on the user's instruction** ("all four"): Profile's action-menu rows (now
  `min-height: var(--q-control-h-lg)` rather than a 55px of their own), the notification rows in both the panel and
  `/notifications`, Branding's palette swatches and theme tiles (their LABELS and gaps only — the swatch and the
  Dark/Light preview stay a preview of a theme, not a control), and the chart and fieldset legends, now
  `--q-control-font-sm`. On `.notif-row` the `font-size` must come AFTER its `font: inherit` shorthand, or the
  shorthand resets it.
- **THE TRAP THAT BIT: `.header-actions`'s flex rule and the `min-width: 0` rule must stay separate blocks.**
  The 2026-09-17 sweep inserted `.header-actions {` between `.q-card > *,` and that rule's declarations, which
  (a) deleted `min-width: 0` outright — the ROOT-CAUSE layer of the never-scroll-sideways fix — and (b) put
  `display: flex; flex-wrap: wrap` on **every direct child of every `.admin-page` and every `.q-card`**. The
  visible symptom was a component's own `<style>` element rendering as CSS text on the page (a `<style>` is
  `display:none` only until something overrides it) — found on the portal, 2026-09-18, by driving the app in a
  browser. A CSS audit that measures fonts and control heights cannot see this; **only opening the page can.**


## Vertical density: a data-driven page spends its height on data (user direction 2026-09-18)

*"this project is data driven, and therefore all forms and pages have to be compacted so user does
not have to scroll infinitely."* Reported against the School Day page, but every shape named was in
the shared stylesheets and therefore on every page, so it is part of the size scale above rather
than a patch to one screen.

- **`.form-row` IS a responsive grid, and the missing base rule was the single biggest cause.** It
  is used **59 times across 23 admin pages** and had no rule anywhere outside
  `.login-container .form-row` — so on every admin page it was an unstyled `<div>` whose children
  stacked full-width, one control per row. The give-away that this was a loss rather than a design:
  the mobile block in `layout.css` has always collapsed `.form-row` to `grid-template-columns: 1fr`,
  overriding a property nothing ever set. It is now
  `repeat(auto-fit, minmax(240px, 1fr))`, so two controls share a line on a desktop, a single
  control still fills the width, and a phone still gets one column with no new breakpoint.
  **Put related fields in a `.form-row`; use `.form-group--wide` for one that genuinely needs the
  full width.**
- **The page header band is decided in `app.css`, not `layout.css`.** `app.css` is loaded LAST
  (`App.razor`), so `.page-header h1` there beats `.qm-main h1` at equal specificity. A density rule
  written in `layout.css` silently loses. The title's SIZE lives in `.qm-main h1` (24px, 20px on a
  phone) and `.page-header h1` sets layout only — one home for the scale.
- **A card header is a label, not a section.** `.q-card__header` and `__footer` are `0.7rem`
  vertical against the body's `--q-card-padding`; the body keeps its breathing room. A header spent
  64px to say one word before this.
- **Measure it, do not eyeball it.** `scripts/e2e/browser/density-check.mjs` reports the "furniture"
  of a page — the header band plus every card header, i.e. the height spent before any data — as a
  live **A/B in one page load**: it injects the previous values, measures, removes them, measures
  again. That is better evidence than two runs of two builds, which a caching difference can fake.
  The change above: furniture across five pages **1132px → 944px (17%)**, and School Day's document
  **2487px → 2323px**.
- **A `wwwroot` change needs a REBUILD, not a restart** (`@Assets[]` fingerprints at build time).
  This cost a wrong measurement once in this very session: the first run reported h1 still at 28px,
  which read as "the rule did not apply" when the rule was in the losing stylesheet.

- **A data page uses the width it has; a document page keeps a reading width (2026-09-18).** Reported
  as *"why is this page not spreading to page width?"* against Duties & Registers, which sat in an
  1100px column on a 1900px screen. Ten pages were capping and centring their own content with
  `max-width` plus `margin: 0 auto`. The cap is **removed** where the page is a TABLE or a grid of
  data — `StaffDuties`, `StaffNotices`, `Portal` — and **kept** where the page is one document a
  person reads: `PortalNotice`, `PortalRecord`, `PortalAppraisal`, `StaffRegister`, `Notifications`.
  Long lines of prose are genuinely harder to read; a table is not prose. The public and kiosk pages
  (`FeedbackPage`, `JoinQueue`, `TicketStatus`) centre deliberately and are outside this, the same
  set every other scale rule excludes.

### The band, the controls and the prose (2026-09-19) — and the two rules that had been inert

Reported as *"that page heading bar and the tabs are extremely exaggerated… the content is coming
over 3/4 of the page"*, then *"the heading / title bar is still big… yet it has mainly the page
title"*, then *"reduce the button size"*. Measured across 15 pages: **furniture 171px → 96px**, of
which the header band was **107px → 48px**.

**TWO RULES WERE SET IN 2026-09-18 AND NEVER REACHED THE SCREEN.** That is the important part; the
values were never the problem.

- **`qm-theme.css` carried `.page-header h1 { font-size: 28px }`** — equal specificity to
  `layout.css`'s `.qm-main h1` and LATER in the cascade, so the title reduction lost every time.
  Read off `document.styleSheets` in the running page, not inferred. The same file also carried
  `padding: 32px`, a full-bleed negative margin paired with a padding set in another file (the exact
  trap this document already names), and a **300px radial-gradient blob** drawn by `::before` — the
  last decorative gradient in the app, missed by the 2026-08-19 flattening because a pseudo-element
  has no `background` to grep for. All four are gone. **`.qm-main h1` is the one home for the title
  size and nothing else may set it**; `uniform-check.mjs` now asserts both the computed size AND
  that the winning rule is that one.
- **`app.css` carried `.form-group { margin-bottom: 20px }`**, beating `--qm-field-gap` the same way.

**A Razor `<style>` block is NOT scoped.** Blazor injects it as written, so a page that redefines a
shared class name overrides the global scale for every element of that name while the page is
mounted — a hub's own frame included. There were **170 such overrides of 14 names**; 142 were swept
and the rest are page-owned compounds or the excluded set. Three were doing real damage:
`.page-header { margin-bottom: 24px }` beat the token on whichever page was open;
`.subtitle { font-size: 14px }` in 24 components beat the band's own size; and
`.admin-page { max-width: 900px; margin: 0 auto }` in BrandingSettings and KioskSettings **capped
the entire Appearance hub hosting them** — 312px of a 1212px page unused, which is the second
screenshot. **`scripts/e2e/style-leak-check.mjs` fails the moment one comes back.**

The scale itself, for reference — change the token, never a page:

- **Title `.qm-main h1`: 20px** (18px on a phone). **Controls 32 / 28 / 38px at 13px**
  (`--q-control-h`, `-sm`, `-lg`), down from 36 / 30 / 44 at 14px. **The phone floor of 40px is NOT
  scaled with them** — that is a finger, not a pointer.
- **Tabs are a control**: `--q-control-h-sm`, 6/13 padding, 13px, 12px margin. They were 48px tall
  under a 20px margin, the largest fixed cost on a hub page and, since the hubs, on eleven pages.
- **The band is ONE ROW.** `.page-header .header-content` is a centred flex row: title, then the
  subtitle beside it taking what is left and truncating. **`align-items: center`, never `baseline`**
  — a `QInfo` in the `h1` is an inline-flex element that moves the line box's baseline and pushed
  the subtitle onto a second line (52px against 33px for the same markup). **The title itself never
  shrinks** (`flex: 0 0 auto; white-space: nowrap`) or a long subtitle wraps it instead.
- `--qm-page-header-gap` 6px, `--qm-block-gap` 10px, `--qm-field-gap` 12px.
- **A data page has no width cap; a document page keeps its reading width; a hub SECTION never caps
  width at all** — the hub owns the frame. `/notifications` lost its 960px cap (a list of rows is not
  prose); the portal's record, notice and appraisal pages keep theirs.

**`QInfo` is where standing explanation goes, and it is NOT a tooltip.** 155 blocks of permanent
prose — page subtitles, `.form-hint`, `.settings-hint` — sat on screen for something most people
read once. 52 moved into a popover beside the thing they explain. The app already had 153 `title=`
attributes and that is what a reader would otherwise get: a native tooltip cannot hold a sentence,
never appears on a touch screen, is not reachable by keyboard and cannot carry a link. **A warning
is not a hint and stays on the page** — if the sentence changes what somebody DOES ("cannot be
undone", "only ever points here", anything safeguarding), it is not eligible; the sweep's `KEEP`
list encodes that.

**Short, definitive labels** (user instruction): *"simply write No Students yet. do not use too many
words."* 28 empty-state and failure headings shortened — a heading names the STATE ("No students
yet", "Record not available"), and the sentence under it, where one is needed, explains what to do.

**Two measuring scripts, because eyeballing this is how it drifted.**
`scripts/e2e/browser/furniture-check.mjs` reports header, tabs, furniture, first-row position and
unused width per page; `density-check.mjs` still does its A/B in one page load.

### A timeline renders a page, never its history (2026-09-19)

**`QTimelinePaging` is the one home** (`Components/Shared/UI/QTimelinePaging.cs`): 25 items, a
`ShowMore()` step, and a `Reset()` that must be called whenever the underlying list changes — a new
period, a different person, a branch switch — or the reader keeps a depth they scrolled to on a list
that is no longer the same list. The staff file, the student welfare chronology and the portal all
use it.

Every item in a `QTimeline` is a card with a marker, a day header, chips and its own actions — a
component subtree, not a row. On Blazor Server the renderer diffs each one and ships the result down
a SignalR circuit, so a 400-record file spent that cost on the wire and on a school's connection.
It pages what is RENDERED rather than what is fetched: the queries are already scoped by period and
the payloads are small, so the expensive half is the render, and paging it needs no round trip.

### A dashboard's figures carry a period — except the ones that cannot (2026-09-19)

Every figure was all-time, which answers "how many ever". The dashboard now carries a
`QDateRangePicker` (default 30 days, remembered per browser in `localStorage`), and **two kinds of
panel that must not be conflated**:

- **PERIOD** — welfare, feedback, teaching. Scoped to the range, and each panel states it under its
  own heading, so a scoped figure is never read as all-time (the same rule as
  `WelfareSummaryDto.ScopedToClasses`). `welfare/summary` has taken `dateFrom`/`dateTo` all along
  and the dashboard simply never passed them.
- **LIVE** — Now Serving, counters, visitors on site. **A date range must never apply to who is
  standing in the building**; a ranged "currently on site" is a number that reads like the truth and
  is not. That panel is labelled *Live*.

The two summary endpoints spell their parameters differently (`dateFrom`/`dateTo` versus
`fromDate`/`toDate`). Both are wire formats; the caller names them rather than either being renamed.

## One type scale: six sizes, and a page never sets one (2026-09-19)

*"the font still looks big. we need to use uniform font and size across the project."* Measured first with
`scripts/e2e/browser/type-audit.mjs`: **eighteen** distinct sizes were on screen across fifteen pages, most of them
accidents of a rem against a 16px root (0.8rem = 12.8px, 0.85rem = 13.6px, 0.95rem = 15.2px). Now there are six.

- **The tokens, in `layout.css`**: `--qm-text-xs` 11 · `-sm` 12 · `-base` 13 · `-md` 15 · `-lg` 17 · `-xl` 20, plus
  `--qm-text-stat` 20 for a figure on a tile. Body text and controls are 13px, so a table cell and the input above it
  agree. **1,378 raw `font-size` declarations** across 124 files were mapped onto them by nearest value. **Change the
  token, never a page.** The excluded set (kiosk, displays, public feedback, booking, ticket status, shared documents,
  print sheets, sign-in pages) keeps its own sizes, as every scale rule here does.
- **Two deliberate exceptions**, both commented where they sit: the two iOS rules in `app.css` stay at a literal
  **16px**, because Safari zooms the whole page when a focused input is under 16px; and an icon glyph above 30px
  (a kiosk preview, a media play button, the connection overlay) is geometry, not type.
- **The headings were the biggest leak.** Only the `h1` had a size, so an `h2`/`h3`/`h4` with no class rule fell
  through to **Bootstrap's 32/28/24px** and rendered LARGER than the page title — "Additional Metrics" on Usage, a role
  name in Users & Roles. `:where(.qm-main) h2 { lg } h3,h4 { md } h5,h6 { base }` in `layout.css` now catches exactly
  those: `:where()` keeps it at a bare element's specificity, so any class a component sets still wins.
- **Radzen's grid carries its own 14px** through its own custom properties on a selector more specific than anything
  here. Setting its VARIABLES (`--rz-grid-cell-font-size` and friends) on `.qm-main` wins without a specificity fight.
  A plain `font-size` rule lost — measured, not assumed.
- **A table row is a line of data**: `.data-table` cells are 9px × 12px (were 14px × 16px), and two shared cell kinds
  live in `app.css` — `.num` (right-aligned, tabular digits) and `.row-actions` (a row's buttons at its right end).
  A figure card is `QStatTile` in a `QStatRow`: eight pages had hand-built their own (platform dashboard and analytics,
  reports overview, queue analytics, the feedback report and tab, visitors, expected visitors) and were moved onto it.
- **A list of things is a table, not a grid of cards.** The Branches hub's three tabs (Branches, Counters, Service
  Types) were grids of tall cards — one branch took a quarter of the screen for a name, a code and three zeros; four
  branches now take 260px. Also shared in `app.css` for any table: `.row-muted` (a disabled row), `.cell-sub` (a muted
  line under a cell's main text) and `.cell-tags` (a wrapping row of chips). Core Queue's per-branch figures show only
  to a tenant holding Core Queue — to anyone else they are zeros that mean nothing.
- **Guards:** `type-audit.mjs` fails on any size off the scale across its fifteen pages; `type-sweep-all.mjs` is the
  wide net — every in-shell page and every hub tab, signed in as a tenant administrator and as the platform SuperAdmin,
  printing each off-scale, wrong-family or extreme-weight placement with its selector. Both read zero.

## A page's buttons have ONE location: top right, on the title row (2026-09-19)

Reported with two screenshots: *"inconsistent location of the buttons. some are on top right… standardise the location
of the action buttons."* On every hub the open section's buttons sat in a second band **under** the tab strip, on the
**left**. Structural, not cosmetic: a hub owns the band, so a section hid its title with `@if (!Embedded)` and was left
rendering a `.page-header` holding only its buttons — and `.page-header` is space-between, so a lone child sits at the start.

- **`QPageActions` is the one home.** A section writes `<QPageActions Embedded="@Embedded"><Title>…</Title><Actions>…</Actions></QPageActions>`.
  Embedded, its buttons go into the hub's own title row through Blazor's `SectionOutlet` / `SectionContent`
  (`QPageActions.Id`); standalone, it renders the ordinary band. **Every hub's `.header-actions` holds
  `<SectionOutlet SectionId="QPageActions.Id" />`.** Not a cascading `RenderFragment` a section assigns: that re-renders
  the hub from inside the child's render, and a lambda is a new object every time, so no equality check can stop the loop.
- **A filter is not an action.** A period picker, a search box or a range belongs in `QFilterBar` below the band — the
  title row holds buttons only. Two period pickers and the Dashboard's range moved for this reason.
- `.page-header .header-actions` carries `margin-left: auto` as a backstop, and `:empty` hides an outlet with no section.
- **Guards:** `scripts/e2e/section-actions-check.mjs` now also fails a section that renders its own `header-actions`,
  `toolbar-right` or similar band instead of `QPageActions`; `scripts/e2e/browser/action-location.mjs` opens every tab
  of every hub and asserts one band, buttons right of centre and above the tabs.

## An empty state is one line of news, and there is one of it (2026-09-19)

*"those empty states are unnecessarily big. compact them"*, then *"those empty states must be uniform across the
project"*. They were ~230px — 60px of padding round a 64px icon and a 20px title — and **50 components** set their own
sizes on the shared `.empty-state` name, which, since a component `<style>` is not scoped, restyled it app-wide while
the page was open.

- **`QEmptyState` is the component, and the older `.empty-state` class reads the same tokens** (`--qm-empty-pad`,
  `--qm-empty-icon` in `layout.css`). The icon sits BESIDE the heading and sentence (a grid, not a stack) and a button
  sits to the right on the same row; on a phone the button goes underneath. Inside a `QCard` the border and background
  drop. Typical height is now 63–77px; `scripts/e2e/browser/empty-state-check.mjs` fails anything above 110px or an icon
  above 30px.
- `empty-state`, `empty-state-icon` and `q-empty` are on `style-leak-check.mjs`'s list: a page may not restyle them.
  Seven page-owned variants (survey questions, campaign stats, the Document Library, the Dashboard's "nothing yet", the
  delivery log, the Feedback report's `empty-card`) became `QEmptyState`. One-line notes inside a chart
  (`.chart-empty`) are a different thing — a muted sentence in a card, not a block — and stay.

## Billing is one hub, and every billing address comes from `BillingLinks` (2026-09-19)

*"these billing pages are totally off"*, *"the worst page is this one… that dark background / heading is unnecessary"*,
then *"implement the billing rework, use your recommendations"*. The plan artifact is
https://claude.ai/artifact/TuKvFVjSoJAQZb2cyfQUKm. Five sidebar entries and five routes became **`/billing`** with tabs
Overview · Modules · Invoices · Payment · Usage (`Components/Pages/Billing/BillingHub.razor`); the five old routes are
**deleted, not aliased**, and the sections carry `Embedded` and use `QPageActions`.

- **`QMgr.Domain.Constants.BillingLinks` (Q-Mgr.Shared) is the one home for every billing address** — used by the API
  (emails, `upgradeUrl` / `purchaseUrl` in refusals, `TenantStatusMiddleware`'s `actionUrl`, Stripe return URLs) and by
  the Web (56 files: every module gate, the sidebar, the account-status page). **Nine links pointed at pages that never
  existed** — `/billing/plans` in four emails and four API refusals, `/billing/update-payment`, `/billing/reactivate`,
  `/billing/success`, `/billing/cancelled` — none was ever an `@page` in this repository. `route-audit.mjs` now reads
  `EmailTemplates.Link(`, `upgradeUrl` / `purchaseUrl` / `actionUrl` / `returnUrl` values and `WriteForbiddenResponse`,
  and resolves `BillingLinks.Hub`; run against the last commit's code it reports the dead three.
- **The Modules tab is open to everyone signed in; the other four need `billing.view`.** Every module gate sends a person
  without a module there, whoever they are — gating it would turn "not part of your plan" into "not allowed here". Add /
  Pay / Remove show only to a holder of `billing.manage`, which is what the API enforces. A person without
  `billing.view` also sees no Billing entry in the sidebar.
- **A card checkout returned to the API's LOOPBACK.** `ModulesController.PurchaseModuleCard` built Stripe's return
  address from `Request.Host`, and its same-origin check compared against it too — so in production neither the default
  nor a URL the Web passed could be right. It now uses the public base (`SaaS` platform setting, else `SaaS:BaseUrl`),
  the resolution `BillingController` already used. **The same `ApiBaseUrl` rule as uploads: a link a browser or gateway
  follows is never the request host of a Web-to-API call.**
- **The public address has ONE reader: `IPlatformSettingsService.GetPublicWebBaseUrlAsync`** (consolidated
  2026-09-19; the configuration half is `PublicWebBase`). The address the server ANSWERS ON wins —
  `MediaStorage:PublicBaseUrl` (the unit's `https://$HostName`), then `App:PublicWebBaseUrl` — and only then the
  SaaS setting's `BaseUrl`, then `SaaS:BaseUrl`. Eleven places built links from the SaaS setting, which is SEEDED
  as `https://cashbook.ug` — a different site — so on an install where nobody edited it, every password-reset,
  verification, onboarding and billing email linked to the wrong place. The Platform Settings field now says it is
  only a fallback. **A new link a person or gateway follows calls that method; it never reads `SaasSettings.BaseUrl`.**
- **Both ways to pay are real**, each switched on platform-wide by `GET billing/payment-providers`: Mobile Money paid at
  each purchase (nothing stored) and cards kept on file with Stripe. The Payment tab says which are on; the purchase
  dialog offers only those. The old page talked only of Stripe while Modules said Mobile Money.
- **Four silent bugs went with the old pages**: Usage formatted storage **megabytes as bytes** (6 MB showed "6 B", a
  5,000 MB limit "4.88 KB"); its label map expected `apiCalls` while the API sends `api_calls`, so the raw key was on
  screen; Overview's private invoice record read an `Amount` the API never sends (it is `Total`), so every recent
  invoice showed 0; and Overview read payment methods as a bare list when the API sends `{ paymentMethods }`, so it
  always showed none. **`InvoiceDto` now lives in `Q-Mgr.Shared`** — the Web had two private copies — which is the
  DTO-duplication rule above, again.
- **The dashboard's "Unable to load queue data" after sign-in was a RACE, not a failed module list** (found and
  fixed 2026-09-19). `OnInitializedAsync` created `moduleResolution` only after three awaits; Blazor renders at the
  first and runs `OnAfterRenderAsync`, so a loader could find it null, skip the wait and read the defaults —
  `hasCoreQueue = true` sent a queue call the API refused (403), and the other sections' `false` defaults skipped
  welfare, visitors and communication. Worst with the permission cache cold, i.e. right after sign-in: 3 of 4
  simultaneous sign-ins lost it. The guard is now a `TaskCompletionSource` that exists from construction and is
  ALWAYS completed in a `finally`. **Any "wait for X before loading" guard must exist before the first await.**
- A module's price on Overview is the **agreed** price when one was captured, else list — the grandfathering rule. An
  annual price is shown as its monthly share. Trial dates show only while a module is actually `Trialing`.
- **Verified by `scripts/e2e/browser/billing-hub.mjs`** (57 checks): one sidebar entry, the five retired routes gone,
  five tabs and every `?tab=` deep link, the shared layout on each tab (one band, buttons top right, no hero, no
  breadcrumb, none of the old layout classes), the three display bugs, and a class teacher reaching the Modules tab only,
  with no Add / Pay / Remove.

## Four stylesheets that styled nothing were deleted (2026-09-19)

`css/components/content.css`, `reports.css`, `shared.css` and `queue.css` — **3,481 lines, downloaded on every page** —
held only `.qm-*` selectors, and not one of those class names appeared in any component. `admin.css` is partly used
(13 of 51 names) and was kept. Before adding a stylesheet, check a component actually renders its classes; before
trusting one, grep for them.

## Duty rota build: the rules Phase 0 left (2026-09-17)

The plan is `docs/plans/DUTY_ROTA_AND_TIMETABLE.md`; progress and the resume point are Phase 89 in the tracker.

- **The student scope has TIERS, and the old names mean PASTORAL.** `StudentAccessTier {None, Teaching,
  Pastoral, Unscoped}`. `ApplyAsync`, `VerifyStudentAccessAsync` and `CanSeeStudentAsync` answer for class
  teachers and assistants only; a subject teacher (`ClassTeacherRole.SubjectTeacher`) reaches a student only
  through `ApplyAnyTierAsync` / `VerifyAnyTierAccessAsync` / `GetTierAsync`. The tier-blind
  `GetClassNamesAsync` was DELETED on purpose — choose `GetPastoralClassNamesAsync` or
  `GetTeachingClassNamesAsync`. **Welfare, guardians, flags and evidence stay pastoral; never switch a
  welfare route to the any-tier method.** A Teaching-tier student is blanked field by field in
  `StudentsController.MapTeachingTier`, never in markup.
- **Reporting a concern is not reading one.** A Teaching-tier caller may CREATE a welfare record about a
  student they teach (policy `SubjectTeachersMayLogConcerns`), and reads back only what they wrote (the
  author rule in `GetRecord`, below Restricted).
- **The `teacher` role is `AssignedClasses` now.** A teacher with no subject-teacher assignment sees no
  students — that is fail-closed, not a bug.
- **One reminder engine.** `IReminderLadderService` decides the stage (collapse, quiet hours, pinned hour);
  `ReminderLadderJob` (`staff-reminder-ladder`, every 15 min) claims it with a conditional
  `ExecuteUpdateAsync` BEFORE sending. A new ladder is a method there plus a `ReminderSubject` default in
  `DefaultLadders` — never a new single-shot timestamp job. e2e that triggers it must switch quiet hours
  off, or an evening run holds every non-interruptive stage.
- **Nobody marks their own register entry** except on a Lesson duty, where it is a `SelfReport`.
- **A user transaction must go through the execution strategy.** The context uses
  `NpgsqlRetryingExecutionStrategy`; `BeginTransactionAsync` outside `CreateExecutionStrategy().ExecuteAsync`
  throws (found live: the Subjects page's first read returned 400). A helper that may be called inside an
  existing transaction checks `Database.CurrentTransaction` and joins it (`SubjectDefaults.SeedIfEmptyAsync`).
- **A rota slot is a `StaffDuty` of kind Rota; `StaffRota` is the one home for its rules** (default cadence, warnings,
  the seeded "Teacher on Duty" parameter, the assignment notice). Warnings never refuse. A duty's kind never changes, a
  Lesson is never created by hand, and the Duties & Registers page lists Session duties only — the rota has its own page.
- **The rota is readable by every member of the branch (a displayed MoES record); who has ACKNOWLEDGED is not** — only
  duty managers and the slot's supervisors get the map. Acknowledgement is the atomic jsonb `||` pattern, and a
  reschedule clears it. A supervisor is told a COUNT by the ladder; names appear only on their portal to-do.
- **Dates in API-built text must be `string.Create(CultureInfo.InvariantCulture, …)`.** The dev machine is en-GB, so a
  bare `{x:dd MMM}` reads "Sept" locally and "Sep" on the server. A sweep on 2026-09-19 found no bare month format left
  in the API; keep it that way.
- **The e2e suite's password `E2eTeacher!2026` is refused by the blocklist for NEW accounts** (it is "teacher" plus a
  year). Accounts the suites create use `NEW_PW` and sign-in tries both. Section 15 is `scripts/e2e/duty-rota-e2e.mjs`.
- **Who reads a duty report is `StaffDutyReports.AccessForAsync`, and nothing else decides it** — the controller and
  `UploadAuthorizer` (report evidence) both call it. A draft's text is the author's alone, even from the supervisor; the
  supervisor's report is hidden from the teacher on duty unless the policy says otherwise; out of reach is 404. A
  permission gate on a note/review request must not sit behind a `[Required]` body, or a non-reviewer gets 400 not 403.
- **A section key in the report template is a wire format** (answers are stored under it): the editor locks a saved
  key, and an empty stored template means "the defaults", read through `DutyReportTemplateDefaults`.
- **`Branch.Settings` has three writers (vocabularies, class colours, the timetable), and every one takes
  `BranchSettingsLock` inside its transaction and re-reads under it.** A fourth key must do the same, or a save of one
  silently drops another. The timetable key is read only through `ITimetableSettingsService`; cycle arithmetic only
  through Shared `TimetableCycle`.
- **`TimetableChecker` is the only diagnosis**: the editor, publish and the integrity sweep call the same function, so
  the clash a master saw is the clash the sweep re-checks. The browser only colours what the API said. Hard never
  publishes; soft publishes with a note. A timetable write refuses a scoped caller even with `timetable.manage`.
- **Published timetable versions may not overlap in dates, and that is checked under the publish lock, not by an
  index** (overlap needs a gist exclusion constraint, i.e. an extension); the unique index is on the start date only. A
  published or archived version is never deleted: a change is a new draft published over it.
- **A lesson is a StaffDuty of kind Lesson, and `StaffLessons` is its one home** (materialise, status, flag). Its status is
  derived, never stored: "unrecorded" and "not recovered" are time passing, not marks. Lesson supervisors are decided
  per request (`timetable.lessons.flag` + staff scope), never stored on the row. A teacher's own mark is a SelfReport a
  supervisor confirms or overrides; the teacher can never replace a supervisor's mark.
- **Re-publishing must not churn lessons.** A new version is a copy with new lesson ids: an unchanged lesson keeps its row
  and is re-pointed; a lesson cancelled by hand ("Cancelled: …") stays cancelled. Cancelling and recreating would reset
  reminders and tell teachers a lesson "moved" to where it already was.
- **An e2e that places lessons "now" needs teachers with nothing else on their day** — a colleague's leftover meeting over
  a lesson is a genuine hard clash, so section 15.6 makes its own two teachers.
- **Teaching figures have one builder, `TeachingReportBuilder`, and one taught % rule**: (taught + recovered) ÷
  (taught + missed without permission + recovered + not recovered). Missed with permission is outside it; unrecorded is
  shown, never counted. The page, the print, the dashboard tiles and the Monday email all call the builder.
- **A weekly job's "once a week" gate is anchored to the REAL current week, never to a reported week
  (2026-09-18).** `SendWeeklyLessonAnalysisAsync` takes `force` and `weekStartOverride`, used only by the
  Development-only `POST …/staff/reports/teaching/weekly-analysis/run?weekStart=` (404 elsewhere, unscoped
  `timetable.manage`) — it exists because the analysis fires only on Mondays and so shipped unexercised for a
  day. Two rules came out of exercising it: the Monday it reports from is **the Monday of the local week**, not
  "today" (it was `DateOnly.FromDateTime(local)`, correct only because the day gate guaranteed Monday, and
  wrong the moment anything else calls it); and the dedupe window uses that real Monday even when the reported
  week is overridden, or a forced run cannot see the messages it just sent and sends twice. Exercised live by
  **e2e section 15.9**, which clears the accounts' held analyses first — otherwise the guard suppresses the run
  under test and every assertion passes vacuously.
- **A background job that must honour a person's staff scope uses `StaffScopeService.VisibleUserIdsForAsync`** — the
  same static core the request path uses. Never re-derive "which staff can this person see" in a job.
- **A timetable import resolves rows the way a hand placement would, into a draft only, one import per draft at a time**
  (`ProcessTimetableJobAsync`); what it cannot place it refuses per row with a reason, and clashes it may place are left to
  the diagnosis. CSV text on the Web is split by `CsvText` only.
- **Rooms have one writer: `PUT …/timetable/rooms` (the Bell Schedule page).** They are stored in `BranchVocabulariesDto.Rooms`,
  but `UpdateVocabularies` keeps the stored rooms whatever it is sent. A rename moves draft and published lessons and upcoming
  lesson duties; a room with live lessons is retired, never removed.
- **A class rename moves timetable lessons too** (Draft and Published; Archived keeps the old name), in the same save
  that moves students and class-teacher assignments.
- **A print route is `QPrintSheet`, and its phone margins come out of `max-width`** — a 100% sheet plus side margins
  scrolled every print page sideways by 8px until 2026-09-17.
- **An individual timetable extract is `?key=` on the print route, and that route is its ONE home (2026-09-20).**
  `/admin/timetable/{id}/print?by=teacher&key=<user id>` prints one sheet; `key` is comma-separated for a chosen
  few; **empty means EVERY sheet, never none** — a print route that defaults to nothing prints nothing. The keys
  are byte-identical to the editor's own view keys (`Timetable.razor`'s `InView`: teacher = user id, class and
  room = `TimetableCycle.Normalize` of the name), which is what lets the editor's **Print** button and the
  portal's **Print my timetable** hand a key straight over rather than each deriving its own. Before this the
  editor's Print button dropped the key and always printed all forty teachers.
  - **The picker writes the key back into the URL** (`SyncUrl`), because a narrowed sheet that is not linkable
    cannot be sent to the person it is about — and publishing it to the Library is the same act. It guards on the
    query values **last acted on**, never on current state, or the write reads itself back and fights the picker:
    the `HubTabs.FollowQuery` rule, one level down. **Switching axis clears the key**, since a class key means
    nothing once the axis is teachers.
  - **The document's NAME follows the selection** (`DocTitle`, `SelectionSummary`, `FileSlug`), so a published
    extract lands in the Library as *"TERM 3 2026 — Martin Kato"* rather than a fortieth *"… — by teacher"*.
    Option 4 of that afternoon's plan needed no code of its own; it needed the naming to stop lying.
  - **The portal card needs a PUBLISHED timetable in force today.** `MyTimetableCard` reads
    `…/timetable/current`, which answers **204** when none is, and the card then hides itself by design — so a
    suite that signs in as an administrator with no lessons reports SKIP and proves nothing. Section 9 of
    `scripts/e2e/browser/timetable-print.mjs` signs in as a **teacher**, which is the half of the feature that is
    meant to need nobody else: reading a published version carries no permission beyond branch membership
    (`TimetableController.GetTimetable` has no permission attribute; only a Draft needs `timetable.manage`).
  - **Verified by `scripts/e2e/browser/timetable-print.mjs`, 29 checks, nothing skipped.** It **refuses to run**
    against a version with one teacher in it, because "one teacher, not the school" would pass vacuously there.
    Its Library cleanup matches on the timetable's name AND the teacher's — an earlier bare-name match reached an
    unrelated *"Staff performance report — <the same teacher>"* left by another suite and deleted it.
  - **A teacher's sheet also carries their DUTIES (2026-09-20)**, from `GetDutiesAsync` over the timetable's own
    effective range, because a sheet that shows lessons and hides the Friday duty is one somebody will still
    double-book themselves against. `DutyKind.Lesson` is EXCLUDED — those ARE the grid above. A duty belongs on a
    sheet when the person is expected (`ExpectedUserIds == null` means everyone, which is how an all-staff briefing
    is stored), records it, or supervises it. The load **never fails the print**: a failure prints the timetable
    with a line saying the duties could not be read, the same call as `VisitorsController.TryIssueVisitToken`.
    The toggle is deliberately NOT in the URL — `?key=` is the *selection*, which must be linkable; this is a
    preference about the same selection.

### A register is taken at the size of a staff meeting, not a five-person duty (2026-09-20)

`StaffRegister.razor` is ONE page and serves every register — a session, a meeting, a **rota slot** ("Teacher on
duty") and a lesson. A report against one of them is a report against all four; do not fix one shape of register.

- **It is a DATA page: no `max-width`, no centring.** It capped at 760px, so a staff meeting used a quarter of a
  1900px screen while its own rows wrapped inside that quarter.
- **One person is one line** (a grid row, ~52px), not three stacked blocks at ~150px. Under 900px it stacks again
  and the mark buttons go back to a **44px** target — that floor is a finger and is not scaled with the desktop
  size tokens.
- **Above `SmallRegister` (40) rows the list `<Virtualize>`s**, with its own scroll region on a desktop so the
  filters above and the footer below stay put. Blazor Server diffs every row of a segmented control and ships it
  down the circuit; 135 people is the case this exists for, and below the threshold it costs nothing.
- **Bulk is the point at scale.** Tick rows (or everyone the filter shows) and apply one outcome; **"Rest
  present" fills only the UNMARKED**, because a sweep that overwrote deliberate marks would be an undo button
  disguised as a shortcut. **A selection may only ever cover rows the reader can SEE** — `PruneSelection` runs on
  every filter and search change, or "apply to 20 selected" silently reaches people the filter is hiding.
- **THE CLIENT ENFORCES WHAT `SubmitRegister` ENFORCES.** Two server refusals were reachable only by submitting
  and being told, which loses the whole register to one 400: **nobody marks themselves** (except a Lesson duty,
  where the teacher's own mark is a self-report) — that row now renders no buttons and says why — and **a rota
  slot marked Not completed needs a reason**, which blocks the submit with a count and a jump to the first
  offender. A new server-side refusal in this controller needs its counterpart here.
- **Closing with people unmarked asks first**, and the footer carries an **unsaved** count (`Mark.SavedOutcome`
  is what the server last said; the difference is what "unsaved" means).
- Verified by `scripts/e2e/browser/register-and-duty-sheet.mjs` — **22 checks, 0 failed**, against a real 135-row
  branch-wide register, at 1600px and at 390px. A one-row register passes every one of those assertions while
  proving none, so seed a branch-wide meeting before trusting a run.

## There are TWO doors, and the sign-in page names both (2026-09-20)

Reported from the sign-in page: *"Don't have an account? **Create one**"* led to `/register`, headed
*"Create Your Account"*, whose first field is the ORGANIZATION name. A teacher at a school that
already uses Q-Mgr reads that line, tells the truth, and ends up with a **duplicate school** on a
trial, themselves as its administrator, with no connection to the real one. The door that served
them — `/join/{Code}`, with its allowed domains, emailed code and approver queue — existed all along
and was **unreachable from the sign-in page**.

- **`/login` now offers both, in the order a reader needs them**: *"Joining a school or business that
  already uses Q-Mgr? Use your join link"*, then *"Setting up a new one? Register an organisation"*.
  `/register` is headed **"Register your organisation"** and says under it that this creates a NEW
  one. **Never reintroduce "Create one" here** — the words were the whole bug.
- **`/join` (no code) is the way in.** `Components/Pages/JoinCode.razor` takes the code or the whole
  pasted URL and forwards to `/join/{Code}`. It **looks nothing up**: an unknown code must read the
  same as a revoked or expired one, and `JoinStaff` already makes that judgement carefully. A second
  copy of that rule here is how the two would drift.
- **`IOrganizationHintService` answers "does this email domain already belong to a tenant?" — and
  DISCLOSURE IS CONSENT-BASED.** A tenant is named only when it has switched staff sign-up ON, has a
  live join link, and listed the domain **itself**. Nothing else matches: not the tenant's contact
  address, not a similar organization name. Naming a school on a guess is a leak; repeating a domain
  the school published is repeating its own invitation.
  - **A free mailbox is never matched** (`gmail.com`, `outlook.com`, …) or the endpoint becomes a way
    of enumerating which schools use the product.
  - **The join code is never in the answer.** It is the school's secret and rotating it is how a
    school closes the door; the answer names the school and sends the applicant to `/join`.
  - It **never fails a sign-up**: a lookup that throws is logged and read as "found nothing", because
    all it can ever do is add a warning.
- **The warning WARNS; it never blocks.** CLAUDE.md's standing rule — a wrongly refused sign-up is a
  lost customer who cannot appeal — and a second campus is a real thing. Continuing sets
  `AcknowledgedExistingOrganization`, which adds `ScoreContinuedPastExistingOrganization` (30) and a
  signal in the reviewer's own words. On its own that does not reach the flag threshold of 50; with
  anything else it does, which is the right shape.
- **Provisioning by an administrator stays the primary path and the defaults already say so**:
  `StaffOnboardingPolicyDto.JoinEnabled` is **false** until a school turns it on, `RequireApproval`
  is **always true**, and `AllowedEmailDomains` narrows who may even apply. Nobody joins a school
  without an administrator, which is the sector norm for a system holding safeguarding records.
- Verified by **`scripts/e2e/registration-doors-e2e.mjs` (section 18, 25 checks)** and
  **`scripts/e2e/browser/registration-doors.mjs` (18 checks)**, both 0 failed. The browser suite
  **signs out first** — the headless profile carries a session from whatever ran before, and a
  signed-in visitor is sent to the dashboard, where every assertion would pass or fail by accident.
  Both put the tenant's onboarding settings back as they found them.

## A list page has one shape, and `QBulkBar` is its one home (the sweep, 2026-09-20)

The register rework of the same day generalised: `docs/plans/LIST_PAGE_STANDARDISATION.md` names five
faults and **`scripts/e2e/list-page-audit.mjs` measures them on every list page in the app shell**, with
a DONE list that FAILS when a swept page regresses. Final state: **58 list pages — 23 swept, 35 ruled
out of scope, 0 outstanding.**

- **A data page takes the width it has. A row is a line. A list that can pass ~50 rows pages or
  virtualises. Bulk acts on the VISIBLE selection.**
- **`Components/Shared/UI/QBulkBar.razor` is the bar** — filter chips with counts, a search box,
  "select all shown", and the actions row that appears when something is ticked. It was extracted FROM
  `StaffRegister` before anything else used it, because a filter that decides what a bulk action
  reaches is the worst possible place for a second copy that drifts.
  - **The bar is a state holder and a renderer; the PAGE owns its visible rows and its selection**,
    because only the page knows what a row is.
  - **THE RULE THE COMPONENT CANNOT ENFORCE: a selection may only ever cover rows the reader can SEE.**
    Call the page's `PruneSelection` on every filter and search change, or "apply to 20 selected"
    silently reaches people the filter is hiding.
  - `ShowSelection="false"` where there is nothing to apply. **A tick box with no action is worse than
    no tick box.**
- **A WAIVER IS A DECISION AND IS WRITTEN DOWN.** `list-page-audit.mjs` carries `waive:` with a reason
  per page, and `OUT_OF_SCOPE` for the pages triage ruled out (a settings form, a dashboard, a week
  grid, a print sheet, one person's own file). **Never invent a bulk endpoint to satisfy the
  checklist** — "closing a welfare follow-up is a decision per child", "a wrongly refused sign-up is a
  lost customer who cannot appeal". Both are waivers, not gaps.
- **The `row` heuristic cost two corrections and the lesson generalises:** any auto-fit grid looks the
  same in CSS, so a stat strip, a form grid, an hours editor and a colour palette all read as
  "card-per-item". It now requires the grid's class to appear **within a few hundred characters of a
  `@foreach`**. A measurement that flags the wrong thing is worse than no measurement, because work
  gets done to satisfy it.
- **Bulk over several rows loops and REPORTS WHAT FAILED, by name.** `JoinRequests.RunOverSelectionAsync`
  is the pattern: each person is approved individually and individually checked (`RoleAssignmentGuard`
  refuses a role above the approver's rank), and the ones refused are listed. A bulk action that
  swallows failures is worse than one that does not exist. Its dialog also says plainly that everything
  in it applies to all of them, and **class-teacher assignments are withheld from the bulk path** —
  two people cannot hold the same class.
- **Phase 3, and it found one: the client enforces what the server enforces.**
  `scripts/e2e/refusal-audit.mjs` pairs every API refusal with the page that posts to it and reports
  pages with no pre-submit check at all. The finding was **the School Day page**: the school day and
  the rooms are TWO calls and the rooms are the second, so a blank room — and "Add a room" creates one
  — was refused *after* the school day had saved. `TimetableSettings.PreSubmitProblem` now refuses it
  first, naming the row.
- **Verified in a browser, not by the build**: `scripts/e2e/browser/list-sweep.mjs` opens all sixteen
  changed pages (52 checks). It detects the **module redirect** and skips with that reason rather than
  passing on the Billing page it landed on — a vacuous pass is the trap this project keeps rediscovering
  — and it skips honestly where the dev tenant's list is genuinely empty.

## Minutes of a meeting: adoption is the boundary (built 2026-09-20)

The standards are written at the top of `Q-Mgr.Shared/Application/DTOs/MinutesDto.cs` — Robert's Rules for the
CONTENT, open-meeting and records law for the LIFECYCLE, ISO 15489 for the QUALITIES — and the rules that are easy
to break are on `StaffMinutesController`. Before this, "minutes" meant a PDF written elsewhere and attached by
hand (`StaffDuty.MinutesMediaContentId`); that column survives and is now the ADOPTED snapshot.

- **ATTENDANCE IS THE REGISTER, NEVER RETYPED.** `BuildAttendanceAsync` reads the meeting's own Final performance
  records and names them in the language of minutes — **Excused IS "apologies received"**, Late is present AND
  late. No field anywhere writes an attendance figure, so the minutes and the register cannot disagree, and
  correcting the register corrects the minutes. Until the register is CLOSED the page and the printed sheet both
  say the figures are provisional.
- **`MinutesStatus` None → Draft → Circulated → Approved, and never backwards. There is no unapprove.** Adoption
  is what makes minutes the official record, so an adopted document is immutable for everyone: a later change is
  an append-only `MinutesCorrectionDto` carrying who, when and what. Same idiom as a welfare visibility change and
  a reopened register. `MinutesApprovedAtDutyId` records WHICH meeting adopted them, because "adopted at the
  meeting of 4 October" is what the record has to be able to state, and a meeting may not adopt its own minutes.
- **Writing needs `staff.duties.manage` OR membership of the meeting's recorder list** (the minutes-taker
  delegation the duty already carries); adopting needs the permission. **Reading is deliberately wider**: a
  circulated or adopted set is readable by everyone who was expected, because circulation for correction is the
  entire point of that rung. A refusal is **404, never 403**.
- **A CLOSED PERIOD REFUSES ADOPTION, NOT A DRAFT** — the same call as the register: recording a meeting that
  happened is never blocked; the act that makes it official is.
- **An action point is a ROW (`StaffMinuteAction`), and that is the one place the enhance-before-add constraint
  argues FOR a table.** It is queried across meetings by PERSON (the portal's "my actions"), it has a lifecycle
  that outlives the draft, and it needs a `ReminderStage` COLUMN — every ladder in this module claims its stage
  with a conditional `ExecuteUpdateAsync` before sending, and a stage buried in jsonb cannot be claimed that way.
  The rest of the minutes (sections, motions, corrections) IS jsonb on the duty, read only through the
  controller's serializer.
  - An action with **no person is allowed and is never chased** — a meeting can minute one for a body. One with no
    date was never given a deadline; inventing one would be the system making up the minutes.
  - A line removed from the document is **Cancelled, not deleted**: the minutes said it. An existing id is UPDATED
    rather than re-inserted, so its reminder stage and its completion survive an edit around it.
- **The PDF is the browser's job, so "attached automatically on approval" means the PUBLISH attaches it.**
  `MinutesPrint.razor` renders, publishes to the Library and calls `AttachMinutesAsync` in one act — and only for
  an ADOPTED record, because attaching a draft would let an unadopted sheet stand as the record. **A draft prints
  marked DRAFT.**
- **The template is `MinutesTemplateDefaults` in Shared, tenant-editable through the policy blob**, read only
  through `IStaffPerformancePolicyService.MinutesTemplate`. A section key is a wire format and locks once used.
  **Quorum defaults to 0 = not tracked**: most school staff meetings have no constitutional quorum, and a page
  announcing "quorum not met" at every meeting teaches people to ignore the one time it matters.
- **Deliberately absent**: transcription or AI summarisation (a recording pipeline plus a server dependency this
  project has ruled out) and e-signatures (adoption by motion IS the legal act).
- Verified by **`scripts/e2e/minutes-e2e.mjs` (section 17, 47 checks)** and
  **`scripts/e2e/browser/minutes-ui.mjs` (21 checks)**, both 0 failed. Note the API suite must pick a person who
  does NOT hold `staff.duties.manage` for its "a reader cannot write" assertion, or several seeded roles make it
  pass for the wrong reason. **A meeting whose register is closed is never cancelled**, so each run leaves one
  meeting behind, titled with its run id.
- **A password is temporary after an import or an administrator's RESET, not after account creation**
  (plan §12.3). A test that expects a forced change after `POST /users` is wrong, as the first live run was.

## White label: a tenant's own domain, its own assets, and our name coming off (built 2026-09-20)

Asked for as *"I have a tenant domain dashboard.maryhillug.net ... resolve the tenant and even hide the
register organisation"*, then *"I would prefer C a fully white labelled experience"*, then *"make SACC
SOFTWARE Branding a higher tier removal feature"*. The plan is the artifact linked from
`docs/TASK_TRACKER.md`; the rules that are easy to break:

- **`TenantResolutionMiddleware` resolves a tenant's own domain as a SIBLING of the subdomain branch,
  never inside it.** It used to be nested in `if (!string.IsNullOrEmpty(slug))`, and
  `ExtractSubdomainAsync` returns null unless the host ends in the platform's base domain — which a
  tenant's own domain never does. **The feature was wired end to end and had never once run.** The
  lookup is cached 30 minutes, negative answers included (the platform host would otherwise pay a
  query per request), and `ForgetCustomDomain` is what `ICustomDomainService` calls when a domain goes
  live or is released. Without the eviction a new domain "does not work" for half an hour.
- **`ICustomDomainService` is the one home, and the ORDER is the point**: accept the domain, give the
  DNS instructions, verify by TXT, issue the certificate, and only then route. `CustomDomain` is
  written ONLY at the end; `CustomDomainPending` holds an unproved claim, so traffic never reaches a
  host nobody proved they own. **A claim in progress outranks the live domain in the status** — a
  tenant MOVING between hosts has both columns set, and reading the live one first hid the new TXT
  record so the move could never finish (found by e2e 19.1 on a second run).
  - **An apex is refused** and that is DNS, not policy: the standard forbids a CNAME at a zone root.
  - **Back-off is a hard requirement.** A failing domain retried in a loop spends the box's weekly
    Let's Encrypt budget and blocks issuance for every other tenant. The daily sweep skips anything
    tried within the hour and gives up after seven failures; a human pressing "Check now" is never
    throttled. **It is the unattended loop that has to stop, not the person.**
  - **Every failure names the STEP.** "We could not find the TXT record" sends somebody to their
    registrar; "the certificate could not be issued" sends them to us. A single "failed" sends them
    to the wrong one.
- **`IDnsTxtLookup` is a hand-written DNS/UDP query** (`DnsTxtLookup`), because .NET has no TXT
  lookup at all and a NuGet resolver is a dependency this project does not take. Behind an interface
  so `StubDnsTxtLookup` can answer from memory — **Development only, and the environment is checked
  as well as the `Dns:Stub` key**.
- **The certificate is issued by ONE root-owned helper, `/usr/local/bin/qmgr-tenant-domain`**, run
  through a sudoers drop-in that permits that command and nothing else. The API stays `www-data`
  under `ProtectSystem=strict`: handing a web application the ability to rewrite nginx is a far
  larger grant than one argument-validated command. The helper **validates the domain itself** — it
  is the privilege boundary, so the check lives on its side of it. `build-linux.ps1` generates it and
  the sudoers file; `install.sh` installs both, creates `/etc/nginx/qmgr-tenants` and the ACME
  webroot, and `visudo -c`s the drop-in (a malformed one locks sudo out of the box). **The nginx
  include must exist before `nginx -t` runs**, which is why the directory is created first.
- **Only the platform sets a domain** (decision 2). The tenant's Branding page shows it read-only with
  a line saying who to ask; the certificate step touches the host.

**`TenantHostContext` is the ONE home for "which host is this".** Resolved in `App.razor` — the only
file that can see `Request.Host`, and server-rendered on every request whatever the render mode — and
cascaded from `Routes.razor`. **`IsTenantHost` is false for any host that is not a LIVE tenant domain**,
so an unverified or half-configured host is indistinguishable from the platform host; a mistyped DNS
record must not half-brand a page. On a tenant host the sign-in page wears the tenant's name, logo and
palette, the favicon and PWA manifest are theirs, and **"Register Organisation" is hidden** — the
address already belongs to one organisation, so offering to create another there is the duplicate-school
mistake the sign-in page was fixed for two days earlier, with the school's own name over it.

### `BrandPalette` derives the WHOLE token family — three tokens is why it looked broken

Reported as *"looks like whitelabeling engine is not working ... all pages should use tenant scoped css
tokens"*, and it was right. Every branded surface set `--qm-primary`, `--qm-secondary` and
`--qm-accent-orange`, while `qm-theme.css` defines seven more as **hardcoded wine literals**:
`--qm-primary-dark` (every hover), `--qm-primary-light` (every chip and tint), `--qm-primary-glow`,
`--qm-primary-rgb` (every `rgba(var(--qm-primary-rgb), α)` wash) and the two `--qm-secondary-*`. A
school that chose green got green buttons on wine hovers with wine tints behind them.

- **`Web/Services/BrandPalette.cs` is the one home**, used by `TenantHostContext`, `MainLayout`,
  `KioskLayout` and `DisplayLayout` — all four had their own three-token copy. It derives `-dark` by
  mixing toward black, `-light`/`-glow` at the theme's own alphas, `-rgb` as the triple, and
  **`--qm-text-on-primary` from the brand's WCAG luminance**, because a school may pick yellow and
  white text on it is unreadable. No colour library; it is thirty lines of arithmetic.
- **`--qm-info` was a second copy of the wine literal and is now `var(--qm-primary)`** (both themes,
  with `--qm-info-rgb` to match). "Info" is the brand-toned status, not a hue of its own, so it was
  invisible to white-labelling — a rebranded tenant got wine info tiles scattered through green
  chrome. Nothing changes for an unbranded install, where the token IS that wine.
- **A new `--qm-*` colour token must derive from an existing one or be genuinely theme-invariant.** A
  literal is the drift coming back, and it will not be visible until somebody rebrands.

### Attribution removal is a HIGHER TIER, and it is THREE strings

- **`FeatureCodes.RemoveAttribution` is a code of its own and must stay one.** The obvious move was to
  hang it off `WhiteLabel` — but `engagement-communications` grants `WhiteLabel`, and every tenant
  running signage buys that, so it would have been free for most of the customer base on day one.
  Granted by the `white-label-plus` catalogue add-on, or by a platform override. **The price is a
  placeholder; the Module Catalog editor is where it is set.**
- **`ModuleCodes.Functional` exists beside `All`** because that add-on opens no screen: "holds any
  module" turns ads off and grants report exports, and paying to remove a footer line is not buying a
  reporting feature.
- **It cannot ride `FeatureFlags.CustomFeatures`.** `GetFeatureValue` falls through to that dictionary
  and **nothing anywhere populates it** — a real entitlement needs a member on the record, a line in
  `ApplyModuleGrants` and a case in `GetFeatureValue`.
- **A platform override lives in `Organization.Settings["FeatureOverrides"]`**, is applied AFTER the
  module grants, and can only ever turn a flag ON — taking away what a module grants is a refund
  question, not a switch. **OFF is a removal, never a stored false.** The endpoint calls
  `InvalidateCacheAsync`, or the person who just granted it watches nothing happen for five minutes.
- **THREE strings, one flag**: `<p class="powered-by">` on six public pages (now the `PoweredBy`
  component), `MainLayout`'s footer, and `EmailTemplates`' own footer line. A tenant who pays and then
  reads "Q-Mgr" at the foot of their own password-reset mail has not got what they bought.
- **`AttributionRemoved` defaults to FALSE**, unlike `WhiteLabelEntitled` beside it, and the asymmetry
  is deliberate: a dropped request would otherwise REMOVE the attribution. On the public pages the
  source is the HOST; in the shell and in email it is the ORGANISATION, because most tenants sign in
  on the platform address and should not need a domain to get what they paid for.
- **Attribution removal requires white-labelling to be ON and entitled.** Taking our name off a page
  that still says Q-Mgr everywhere is a gap, not a product.

### Brand-asset uploads: the bytes decide

- **`ImageProbe` reads PNG/JPEG/WebP headers by hand** — the container formats, not a library — and the
  file is stored under the extension the BYTES imply, never the client's name or declared type.
  `UploadFileTypes` lets a listed extension win within a family, so passing the client's name through
  would let `x.webp` holding PNG bytes be stored as `.webp`.
- **SVG is refused.** OWASP: it carries ECMAScript in almost every context, and the mitigation is to
  serve it as `text/plain` or from a separate content domain. A logo served as text/plain is not a logo.
- **Replacing an asset DELETES the old file** (unless another column still points at it). Without that
  a school trying five logos leaves four orphans, and an orphan is the one class the authorizer can
  only ever serve token-gated.
- **`UploadOwnerKind.Branding` is public by intent** — a sign-in page, a kiosk and a display all fetch
  it anonymously. Matched on `LogoUrl`/`FaviconUrl`, so a hand-typed link classifies the same way.
- Limits are stated **before** the picker: 1 MB / 2048px for a logo, 512 KB / square 64–1024px for a
  favicon. The browser refuses an over-size file and **the server refuses it again** — a client-side
  check is a courtesy, never a control.

**Verified by `scripts/e2e/white-label-e2e.mjs` (section 19, 52 checks, wired into
`class-teacher-e2e.sh`) and `scripts/e2e/browser/white-label-ui.mjs` (20 checks).** The browser one is
the answer to the "not working" report and could not have been an API suite: it brands the dev tenant
green and **sweeps every visible element on six pages for the shipped wine**, which is what finds a page
reading a literal instead of a token. Colour PREVIEWS are excluded by selector and by reason — the
Branding page offers the wine as a ready-made palette and that chip has to be wine.

**Two things remain unexercised and are stated rather than claimed.** The certbot and nginx path (Phase 3)
can only run on the server; it was checked by rendering the generated helper from `build-linux.ps1` and
`bash -n`-ing it, the same way the `/uploads/` nginx block was. And **no real certificate has been
issued** — the e2e runs with `CustomDomains__SkipCertificate=true`, which is Development-only.

## A tenant has a life, and the end of it leaves nothing (built 2026-09-20)

*"I expect many trial accounts, most of which will not translate into business ... capacity to clean
out a tenant fully, including all related user accounts and any data relating to the tenant, from any
table ... flexible ... in case we add features ... should not leave any trace."* The plan is the
artifact linked from `docs/TASK_TRACKER.md`.

**What was there before: a whole pipeline hanging off a value nothing set.** `TenantStatus.Deleted`
was READ in four places — `TenantStatusMiddleware`, `PlatformAnalyticsController`,
`RegistrationGuardService` and a nightly purge in `BillingJobs` — and WRITTEN in none. A tenant could
not be deleted at all. And had it ever reached that state the purge would have thrown: it did a bare
`Organizations.Remove(org)` against **36 foreign keys with `DeleteBehavior.Restrict`**, sharing one
`SaveChangesAsync` with the notification pruning and the usage resets, so it would have taken the
whole nightly cleanup down with it. That block is gone; the comment where it was says why.

### The purge derives itself from the model — that is the "future tables" answer

78 entities: **39 carry an `OrganizationId`, 39 do not**, about six of the second group are the
platform's own and the other ~33 are tenant data hanging off a parent. A hand-written "delete these
tables in this order" list would have to encode all 33 join paths AND the safe order, then be
extended correctly by every future feature — and when it drifted **nothing would break**; the rows
would simply stay.

- **`TenantPurgeModel` walks `IModel`**: it finds each table's shortest FK path to an
  `OrganizationId` (breadth-first, so a welfare note goes through its record in one hop rather than
  its record's student's branch in three), builds the nested `EXISTS` predicate for that path, and
  orders every table dependents-first so the 36 `Restrict` keys are satisfied rather than fought.
  Add a table with an `OrganizationId` and it is picked up with no code change.
- **`TenantDataManifest` is the ONE declared thing**: `TenantOwned` · `TenantDerived` ·
  `PlatformOwned` · `StatutoryRetention`. **`TenantPurgeModelGuard` FAILS STARTUP** when a table in
  the model is not classified — hard, not a log line, because the cost of shipping an unclassified
  table is a purge that reports success and leaves rows behind, found long after a tenant was told
  their data was gone. Same fail-closed shape as `UploadAuthorizer.LookUpAsync`.
- **The ordering tolerates the one real cycle** (`Organization.SubscriptionId` ↔
  `Subscription.OrganizationId`); the purge breaks it explicitly by nulling the pointer first, which
  is what that key's own `SetNull` intends.

### The order is load-bearing, and step one cannot be recovered from

1. **FILES FIRST, while the rows that name them still exist.** Uploads are GUID-named in one flat
   directory and nothing in the name says whose they are, so **once the rows are gone the bytes are
   unattributable and permanent** — a photograph of an injured child, a visitor's face. The columns
   are found from the model (`FileUrl`, `FilePath`, `PhotoUrl`, `LogoUrl`, `FaviconUrl`,
   `CoverImageUrl`, `ThumbnailUrl`), so a new table with a `PhotoUrl` is swept without anybody
   remembering. **Getting this backwards is not fixable.**
2. **Hangfire.** It runs on **PostgreSQL storage** — the same database, its own schema, which the EF
   model knows nothing about — and `DispatchAsync(..., string recipient, string subject, string
   message)` puts **a recipient's email address and the message body** into `hangfire.job`. A purge
   that only walks `IModel` misses every byte of it. This is the one place the design deliberately
   reaches outside the model.
3. Rows, dependents first, in one transaction through the execution strategy.
4. Statutory rows **de-identified in place**, never deleted.
5. **VERIFY, before the commit.**
6. Caches, the custom domain and the external processor, after it.

### Verification is the deliverable, not the delete

Uganda's DPPA requires destruction "in a manner that prevents its reconstruction in an intelligible
form"; NIST SP 800-88 calls verification "the linchpin". Neither is satisfied by a `DELETE` that
returned without throwing.

- **The completeness check reads `information_schema`, NOT the EF model**, and that is the whole
  point: a table the model forgot is still in the catalogue. Two overlapping passes — the catalogue
  for anything with an `OrganizationId`, the model for the derived tables. **Anything left rolls the
  transaction back and nothing is deleted.**
- **Declared survivors are excluded by class, not by name.** `StatutoryRetention` AND
  `PlatformOwned` — the tombstone, the certificate and the lifecycle log each carry an
  `OrganizationId` and are meant to outlive the organization. The purge's own last act writes two of
  them, so without this exclusion **a purge could never commit**: it would find the rows it had just
  written and roll itself back. Found by the e2e on its first real run, which is exactly what the
  check is for.
- **`TenantPurgeCertificate`** records every purge — counts, files, jobs, duration, pass/fail — and
  is written whether it worked or not. **`purge-reverification`** re-checks a sample monthly by a
  different route, so a bug in the purge cannot hide in its own check.

### The lifecycle, and the two lanes

Six states with published clocks (`TenantLifecycleService`): Pending →14d→ Suspended →60d→ Cancelled
→30d→ **PendingDeletion** →14d→ purged. **104 days**, three chances to come back, one explicitly
reversible window at the end, and the tenant is emailed before each step. The numbers are in
**Terms §8a** and the Privacy Policy, because a grace period nobody is told about is just latency.

- **`PendingDeletion` is a state, not a flag on Cancelled.** They are different promises: one says
  your data is here and you can have it, the other says it is going.
- **A purge is never reachable from a customer-facing action** — only the daily sweep on an elapsed
  clock, or a platform administrator who has scheduled it AND typed the organisation's name back.
- **`Deleted` (5) is legacy and nothing writes it.** A purged tenant has NO ROW AT ALL.
- **The two lanes.** A tenant that PAID keeps `Invoice` and `Payment` for five years (Uganda's Tax
  Procedures Code), de-identified — amounts, dates and references kept, contact details blanked —
  and `purge-expired-statutory-records` removes them when the five years are up. **A trial that never
  paid has no tax record, so it purges whole.** That is the common case here, not the exception.

### What survives, and why it is not a trace

**`TenantTombstone`: two one-way hashes and some counts.** No name, no address, no email. It earns
its place three times over — `RegistrationGuardService` can still see a purged tenant coming back
(+25, which flags rather than refuses: a school returning to buy properly is the good case);
"what happened to this tenant" has an answer; and it is **the ICO's own condition** for treating
backup data as "put beyond use" — a suppression list any restore checks.
**`TenantTombstoneHash` is the one home for that hash**, because the purge writes it and the
registration guard reads it, and a second copy that drifted would silently stop matching with
nothing looking broken.

**Backups are answered honestly rather than claimed away.** `qmgr-backup-db.sh` keeps 30 days and
prunes by age, so a purged tenant is genuinely gone from every backup within 30 days — that exact
sentence is in the Privacy Policy. `qmgr-restore-db.sh` gained `suppression_report` (the drill says
what a dump would bring back) and `suppression_reapply` (a live restore re-deletes the purged
tenants, with `session_replication_role = replica` for that transaction only).

**Verified**: `scripts/e2e/tenant-purge-e2e.mjs` (section 20, **32 checks**, wired into
`class-teacher-e2e.sh`). It **creates its own tenant and destroys it** — it cannot run against the
dev tenant, because a successful run ends with the tenant gone. It resets its own sign-up budget
first through a **Development-only** endpoint (404 elsewhere), because three sign-ups an hour is
right in production and unworkable for a suite that must create a tenant to destroy one.

## A `var()` fallback hides a wrong token name (2026-09-21)

`var(--qm-bg-subtle, #f4f4f5)` looks like a themed panel and is not one: `--qm-bg-subtle` has never
existed, so that rule ALWAYS took the literal — a light grey panel behind theme-coloured text,
invisible in dark mode, on the tenant dialog, and nothing failed. The same shape with NO fallback is
worse: `var(--surface-color)` on the `/unauthorized` card made the whole declaration invalid, so that
page's card had no background, no radius and no shadow for as long as it existed. Neither is visible
in a build, a type check or a code read — only the resolved value shows it, and by then somebody has
to already suspect the token.

**`scripts/e2e/css-token-check.mjs` is the guard**: every token a rule reads must be defined
somewhere — a stylesheet, a component's own block, an inline style, or a C# string that writes one
(`BrandPalette` derives the whole `--qm-primary-*` family at runtime, so those tokens exist in no
`.css` file). Vendor prefixes (`--rz-`, `--bs-`, `--fa-`, `--swiper-`, `--stf-`) are excluded, and
comment bodies are blanked before scanning so a note quoting the broken rule it replaced does not
report itself for ever. **Real tokens only, no fallback literal**, and check a colour change in BOTH
themes.

### Text on a FIXED fill is a different question from text on the BRAND fill

`--qm-text-on-primary` is derived from the tenant's own brand colour by `BrandPalette`, so a
dark-branded tenant got `#ffffff` on amber `#f59e0b` — about 1.9:1, unreadable, and the reason a
filled warning button looked washed out. Amber is the same in both themes and is always light, so
**`--qm-text-on-warning` is near-black and theme-invariant**. A new fixed-colour fill needs its own
foreground token and must never borrow the brand's, because the brand's follows a colour the tenant
chooses and this one sits on a colour they do not.

### A colour picker's first swatch is the tenant's brand, stored as NULL

`WelfareCategoriesSetup` and `ServiceTypesSetup` both led their palette with Q-Mgr's own shipped wine
(`#8c2f52`) and preselected it, so every category and service a white-labelled school created was
stamped with our colour rather than theirs. The first swatch is now the organization's own brand and
is **stored as `null`, never as a hex** — every render site already reads `Color ?? var(--qm-primary)`,
so a null follows the palette wherever it appears, including after a rebrand, which a stored hex never
would. `Color` is `string?` on both forms for exactly that reason.

## Two certificate decisions on one day, and they do not conflict (2026-09-21)

Read both before changing either. The tracker's 2026-09-21 section carries the full account.

**Morning** — *"the certificate exists on the server and is already shared properly with other
applications"*, then "use shared cert, never issue". `ICertificateIssuer`/`CertbotCertificateIssuer`
became **`ITenantDomainActivator`/`NginxTenantDomainActivator`**: it writes the tenant's nginx block
against the installed certificate and reloads, and **REFUSES a domain that certificate does not
cover** — a domain marked live behind a browser warning is worse than one that is not live yet. The
per-tenant renewal date went with it (logged, never stored): the certificate belongs to the server,
not to the tenant, so a stored copy would go stale the moment it was renewed and would then be a
wrong date shown confidently.

**The coverage check is the load-bearing part and stays exactly as written.** A wildcard matches
exactly ONE label, so `a.b.example.com` is NOT covered by `*.example.com`; the names come from the
SANs plus the common name, the latter for an older certificate carrying no SAN extension at all.

**Afternoon** — *"external domains like dashboard.maryhillug.net should be supported also. this is the
reason for whitelabelling."* The installed certificate answers for `*.cashbook.ug` and `cashbook.ug`
only (Sectigo DV, read off the live host on 2026-09-21), so it can never cover somebody's own domain.
The morning's work therefore becomes the **fast path** of a hybrid rather than the whole answer, with
per-domain issuance restored as the branch underneath the coverage check. That build is workstream B
of the completion plan; its rules are written up where it lands, not here.

## Process note for future sessions

Design/reference decisions like the one above must be written here (or somewhere durable) at the
time they're made, not left to survive only in conversation context — this file didn't exist
before 2026-08-17 despite the Webster reference having been consulted earlier in that session,
which is why the font-pairing decision survived (it made it into code) but its rationale and the
broader template-selection work did not (nothing to point back to after context compaction).

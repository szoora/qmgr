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
  feedback page's stars now render at the 36px the dead rule intended. `WelfareStatusColor()` still
  exists in two pages and should get one home.
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

- **`q-components.css` tokens**: `--q-control-h` 36px (`-sm` 30, `-lg` 44), `--q-control-font` 14px (`-sm` 13) and
  `--q-row-gap` 8px. Every `QButton`, text or time input, `QSelect` / `QMultiSelect` trigger and `QDatePicker` uses them; an
  icon-only button is square; on a phone every control is at least 40px.
- **`layout.css`**: `.header-actions` and the shared row names (`.form-actions`, `.modal-actions`, `.card-actions`,
  `.filter-actions`, `.dialog-actions`, `.q-modal__footer` and a few more) use the row gap; `.qm-main` body text is 14px;
  `.qm-main h1` is Montserrat 28px, weight 700 (22px on a phone).
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
  bare `{x:dd MMM}` reads "Sept" locally and "Sep" on the server. Older API code still has the bare form (see Phase 89).
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
- **A password is temporary after an import or an administrator's RESET, not after account creation**
  (plan §12.3). A test that expects a forced change after `POST /users` is wrong, as the first live run was.

## Process note for future sessions

Design/reference decisions like the one above must be written here (or somewhere durable) at the
time they're made, not left to survive only in conversation context — this file didn't exist
before 2026-08-17 despite the Webster reference having been consulted earlier in that session,
which is why the font-pairing decision survived (it made it into code) but its rationale and the
broader template-selection work did not (nothing to point back to after context compaction).

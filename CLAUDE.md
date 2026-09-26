# SACC Dashboard (repository: Q-Mgr Web)

**The product is "SACC Dashboard" since 2026-09-24; it was Q-Mgr.** Multi-tenant SaaS for staff, student welfare
and the front office — queues, visitors, signage, secure documents. The repository, projects, namespaces, schema and
every other wire format keep the old "qmgr" name on purpose; see "The product's name has ONE home" below before
renaming anything. (The earlier descriptor "Front Office" is retired; the tagline is "Staff, welfare and front office".) ASP.NET Core API (`src/Q-Mgr.API`) + Blazor Server web app
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

## The product's name has ONE home: `ProductBrand` (rebrand 2026-09-24)

Asked as *"rebranding the platform as The Dashboard or simply Dashboard?"*, settled as **SACC Dashboard** (a generic
word cannot be registered or searched; SACC carries the distinctiveness). Plan: `docs/plans/SACC_DASHBOARD_REBRAND.md`
and https://claude.ai/artifact/F21ZYLMNNsGtTRt1KxViqN — decisions B1–B10 all taken as recommended.

- **`Q-Mgr.Shared/Application/Branding/ProductBrand.cs` is the one home**: `Name` ("SACC Dashboard"), `Company`,
  `Descriptor`, `LegalEntity`, `Tagline`, `MessagePrefix`, asset paths, `NameFor`, `ShortNameFor`,
  `ValidateBrandName`, `SuggestFor`. **`scripts/e2e/brand-literal-check.mjs` (in `guards.sh`) fails on "Q-Mgr",
  "SACC Dashboard", "SACC Software" or "Powered by" typed anywhere else in `src/`** (comments and Migrations aside).
  It reported 152 on the commit before the rebrand. A future rename edits ProductBrand and nothing else.
- **`Organization.BrandName` IS A SCHOOL'S WHOLE APP NAME, exactly as typed** (user direction: *"why do we have to
  force the dashboard word on to the tenant?"*). Nothing is added to it; we only SUGGEST "WORD Dashboard"
  (`SuggestFor`). Shown only while white-labelling is entitled AND on (`NameFor`), otherwise "SACC Dashboard".
  Rule: 2–40 characters, one line, must not contain "SACC", not unique across tenants. A stored value that fails the
  rule is ignored, never rewritten. Set at registration (optional), on Appearance, or by the platform
  (`PUT admin/tenants/{id}/brand-name`).
- **`Organization.Name` is the organisation.** Twenty-one readers used `BrandName ?? Name` as the school's name on
  printed sheets, emails, SMS and the mobile app; they read `Name` now. Printed headers name the organisation;
  footers say "Printed from {app name}". SMS to guardians and staff are headed by the ORGANISATION's name.
- **DTOs carry the resolved name**: `ProductName`/`ProductShortName`/`OrganizationName` on `OrganizationBrandingDto`
  and `TenantHostBrandingDto` (the latter's old `BrandName` was removed so every reader had to be revisited);
  `EmailBrand` has `Name` (the app) and `OrganizationName` (the signatory and, with attribution removed, the copyright).
- **The only credit is "© YEAR SACC"** — never "Powered by" (user, 2026-09-24). With attribution removal it credits
  the ORGANISATION, not its app name.
- **The mark is `ProductMark`** (same folder): an S of eleven lit dashboard tiles on a 3×5 grid, wine, flat. The Web
  serves every `/brand/*.svg` from it (`Program.cs`), the API's `branding/product-logo` emits it, and the PNGs in
  `wwwroot/brand/` are rendered from the served SVGs by `scripts/brand/render-brand-assets.mjs` — re-run it whenever
  ProductMark changes. The mobile app's `artwork/build-brand-assets.py` mirrors the geometry; change both together.
  The old queue-loop files (`images/*.svg`, `favicon.svg`, the static `manifest.json`) are deleted; `/app-manifest.json`
  serves every host.
- **NEVER RENAMED, and each is load-bearing:** `SetApplicationName("QMgr")` (Data Protection purpose — payloads stop
  decrypting), JWT issuer `qmgr-api`/audience `qmgr-clients` (everyone signed out), Android `applicationId ug.qmgr`
  (a different app; every phone reinstalls), the `qmgr` schema, namespaces and projects, `qmgr-*` localStorage keys,
  service names and install paths, the `X-QMgr-Signature` webhook header.
- **SMS sender IDs were deliberately not rewritten** by `RebrandToSaccDashboard`: the gateway may accept only a
  registered sender. The fallback when a tenant has none is now `ProductBrand.Company` ("SACC") — confirm that sender
  is registered with the sacc.ug SMS gateway before relying on it.
- The home page is labelled **Home** (route `/` unchanged), so nobody reads "SACC Dashboard → Dashboard".
- Found on the way: the service worker's precache named four stylesheets deleted on 2026-09-19, and `cache.addAll` is
  all-or-nothing, so the worker had not installed since. Once it could, its update banner sat under the phone bar —
  `layout.css` now lifts it clear.
- Verified by **section 38** (`product-brand-e2e.mjs`, 35 checks) and **`browser/product-name.mjs`** (31 checks),
  both 0 failed, plus the mobile app's `verify-brand-assets.py` (14/14).

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

- **There is no SSO (user decision, 2026-09-17)**; `IdentifyResponse.SsoEnabled` was removed and SSO would be its
  own plan. **Push is real since 2026-09-22/24** (the mobile shell's per-device sessions and Firebase, configured
  through the platform `Push` settings) — the 2026-09-17 note that there was no push sender is history.

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

### THE EMAIL RETRIES CAN STARVE THE HANGFIRE QUEUE, AND THEN A SUITE FAILS FOR NO PRODUCT REASON (2026-09-22)

Running the suites repeatedly in one sitting left **221 queued `NotificationDispatchJob.DispatchAsync`
jobs and nothing else** — 43 of them in `Processing`, occupying every worker on SMTP timeouts to
`@qmgr.local` addresses, with 161 scheduled retries behind them. Section 14's staff import then sat at
`Pending` with `startedAt: null` until the suite's 120-second wait expired, and five assertions failed
with nothing wrong in the code.

CLAUDE.md already recorded that those mailbox failures are *correct* for a domain that does not exist.
What it did not record is that they are **slow**, they **retry three times**, and enough of them will
crowd out every other background job — which reads as a product bug in whichever suite happens to need
one next. The give-away is a job row whose `StartedAt` is null: nothing has looked at it.

    SELECT statename, invocationdata->>'Method', count(*) FROM hangfire.job GROUP BY 1,2 ORDER BY 3 DESC;
    -- all Dispatch%? then clear the Scheduled/Enqueued/Failed ones and RESTART the API to free the
    -- workers already blocked in Processing.

**A real failure in that block looks identical to this**, so check the queue before believing one.
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

    bash scripts/e2e/class-teacher-e2e.sh   # the API suite, 28 sections, 15 Node suites inside it
    bash scripts/e2e/guards.sh              # the 13 static guards — no server, seconds, run by rebuild.sh
    node scripts/e2e/browser/all.mjs        # the 27 browser suites, against a local headless Chrome

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

### Four warning codes are BUILD ERRORS, and that is deliberate (2026-09-21)

`CS0649;CS0472;CS0168;RZ10012` are in `WarningsAsErrors` in all three project files. **Do not take
them back out to get a build through** — every one of the four has already produced a silent,
user-visible bug in this codebase, and in each case the compiler had been reporting it on every
build for days while nobody read it:

- **CS0649 — field never assigned.** `StaffMinutes.meId` stayed `Guid.Empty`, so
  `a.AssignedUserId == meId` was false for everybody and **nobody could close their own minute
  action point**, ever. The roster import's null `SourceFileName` was the same code.
- **CS0649 again — `WelfareReports.pageIndex`**, never assigned with no pager on the page at all, so
  a safeguarding search matching 200 records rendered 50 and said nothing.
- **CS0472 — a comparison that is always true.** `r.Outcome != null` on a non-nullable
  `DutyOutcome`, which let unmarked records into a meeting's attendance and undercut the rule the
  whole feature rests on: attendance is the register, never retyped.
- **RZ10012 — an unresolved component tag**, which renders as inert literal HTML rather than
  erroring. `<RedirectToLogin />` sat out of tag scope for weeks doing nothing.

The remaining ~21 warnings are nullable-reference noise and stay warnings. The cost of the bar is
that a half-written field stops the build while you are still writing it; that was accepted
knowingly. **If a fifth code earns its place, add it here and say which bug bought it.**

## A staff member may have NO EMAIL ADDRESS, and most of them do not (2026-09-21)

`User.Email` and `User.NormalizedEmail` are **nullable**. 133 of the 184 staff on the first real
school list this product imported have no address, and the old schema could not store that at all —
two unique indexes sit on those columns, so an empty string would have been refused the second time.
**NULL, never "": PostgreSQL treats NULLs as distinct, which is the single fact this whole change
rests on.** The plan is `docs/plans/STAFF_WITHOUT_EMAIL.md`; the rules:

- **Three keys identify a person, in this order: email → employee number → username.** The staff
  number is no longer a label: `idx_users_employee_number_unique` is a per-organization partial
  unique index on `(OrganizationId, EmployeeNumber)`, and the import, the precheck and the add-staff
  dialog all key on it for anybody with no address. `PersonName.SuggestUsername` is the ONE home for
  what an account is called when there is no address to take it from.
- **Sign-in already worked and still does** — `Email == identifier OR Username == identifier`, a
  2026-08-21 decision. But the null test comes FIRST in that predicate now: `u.Email.ToLower() == ""`
  would otherwise match every emailless person the moment an empty identifier was posted.
- **A missing address is never a refusal; a MALFORMED one always is.** The importer warns and names
  the username the person will type; the only refusal is an INVITATION delivery, which has nowhere
  to go. The client refuses exactly what the server refuses, in the same words.
- **Sending degrades, it does not fail.** A null address means the email channel is *skipped with a
  reason* — never `Success = false`, which is the delivery log's standing rule. Every sender is
  guarded at the point of send (`TenantProvisioningService`, `StaffOnboardingController`), not hoped
  for.
- **An empty cell in a list is a bug; "No email address" is a fact.** Users & Roles, the staff
  record, the class-teacher card and the import preview all say it.
- **The one real loss, and the school must be told: they cannot reset their own password.** Every
  reset for those 133 is an administrator issuing a temporary one — by SMS where there is a number
  (164 of the 184 rows carry one), on a printed slip where there is not.
- **`EmployeeNumber` on a create request is load-bearing**, and a duplicate is refused in words
  rather than left to surface as a unique-index violation nobody can read.
- Verified by **section 22** (`staff-no-email-e2e.mjs`, 17 checks) and the import wizard's browser
  suite (22 checks). The staff list went **49 ready → 182 ready**; the two remaining refusals are
  typos in the school's own file.

### A component parameter Razor never checks, and the guard that does (2026-09-21)


**`<QModal Open="@askOpen">` compiled, shipped, and killed My Workspace's teaching tab.** QModal
takes `Visible`. An unknown parameter is not a compile error of any kind, so Blazor threw *"Object of
type 'QMgr.Web.Components.Shared.UI.QModal' does not have a property matching the name 'Open'"* the
first time the component rendered, the circuit was torn down, and the whole page became "An unhandled
error has occurred" with nothing left on it. The build was clean and the self-service API suite
(section 21) passed throughout — it is a curl suite, and a tab is client-side.

- **`scripts/e2e/component-param-check.mjs` is the guard**, in `guards.sh`: every parameter passed to
  one of our own components must exist on it. It skips a component that captures unmatched values,
  and it skips attribute VALUES including the quotes that nest inside an `@( … )` — reading a
  lambda's own `=>` as a parameter name is what made its first draft report 200 findings that were
  not there. It is the same class as RZ10012, except that the compiler says nothing at all.
- **`scripts/e2e/browser/portal-tabs.mjs` is the layer above it.** A tab can be killed by any
  exception on its first render, not only by a bad parameter name, and the only way to see that is to
  open it: all four My Workspace tabs, by deep link and by press, as a teacher and as an
  administrator, failing on a visible `#blazor-error-ui`.
- **The rule this belongs to: a hub's tab is not verified until something OPENS it.** Four tabs
  shipped with nothing in any browser suite touching them, on a page every member of staff lands on.

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

### Three local-run constraints that still bite

- **`dotnet run` serves a snapshot compiled at startup** — a `.razor` edit needs a restart (or `dotnet watch`).
- **Bind to `127.0.0.1`, HTTP only.** Visiting `https://localhost:…` once pins HSTS on every `localhost` port, and
  binding both HTTP and HTTPS re-enables `UseHttpsRedirection`. Trusting the dev certificate changes the machine's
  root store — ask first.
- **Ports are not fixed.** On the 2026-09-25 machine 5001 is the CRM API; run on other ports and pass
  `WEB=`/`API=` to the suites (every suite and `browser/login.mjs` read them).

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
### The import is a WIZARD now, and the mapping is the reader's (2026-09-21)

Built against two files a real school exports, both of which the old importer could not read at all:
`Staff List.xlsx` (184 staff — a TITLE in row 1, headers on row 2, one name column written surname
first) and `Students_2026-09-21.xls` (1,711 students — an **HTML table wearing an .xls name**, SHOUTED
names, and the class split over `Class` + `Stream`). The plan is `docs/plans/BULK_IMPORT_SYSTEM.md`;
the rules that are easy to break:

- **Three steps, and the middle one is the feature**: choose a file → **match the columns** → check and
  import. Alias matching is only the first GUESS; the reader sees every match, can change it, and is
  told which of their columns were read and ignored. A header we do not recognise used to be lost in
  silence, which is how a school's own export becomes "the import does not work".
- **`ImportParsing.ReadGrid` finds the header ROW**, scanning the first twelve for the one that matches
  the most declared columns. Assuming row 1 is why a file with a title in A1 refused every row.
- **A field can take TWO columns** (`ImportBinding.Sources` + `Joiner`): `Class` + `Stream` → `S1A`,
  declared as `AlsoJoin` on the column so the join is automatic and still visible. And ONE column can
  fill two fields — a combined name — through `PersonName`, never a generic "split on character N",
  which produces silent rubbish.
- **`PersonName` in `Q-Mgr.Shared` is the ONE home for a name split.** A comma always wins and always
  means `Family, Given`; particles stay with the family name; a one-word name WARNS rather than
  refusing (W3C: never require a family name). **The order is ASKED, never guessed** — the picker shows
  the file's own first name split both ways — because "Abaho Jude" is surname-first in Kampala and
  given-name-first in Cork. Default: family first. **A student is never split at all**; they keep one
  `FullName`, which is what the schema already did and is now a cited decision.
- **`ImportColumn.SatisfiedBy` is how REQUIRED means "this, or that"**: first and last name are required
  unless a combined name column is mapped, and the badge then reads *Covered*.
- **Cleaning is two tiers and it is SAID.** Always and unswitchable: trim, collapse, strip zero-width
  and control characters (an invisible `U+200B` is how one person is imported twice). Switchable, with
  a count shown — un-SHOUTING a name (never touching one already mixed case, so `McDonald` survives),
  normalising a phone number, folding an email's case. The panel prints *"164 phone numbers normalised
  · 2 names tidied out of capitals"*.
- **`ImportRules` in `Q-Mgr.Shared` is what BOTH sides run.** A row the preview called good and the
  import then refuses, in different words, is the worst answer an import can give. `CsvWriter.Field`
  also neutralises a leading `=`, `+`, `@`, tab or CR (OWASP CSV injection / CWE-1236) — quoting does
  not stop Excel running a formula; only the apostrophe does. A leading minus is left alone when the
  value parses as a number, or every negative figure in every export would be mangled.
- **Duplicates are a QUESTION, asked before anything is sent** (`CheckExistingAsync` → "23 of these are
  already here"), not a result reported afterwards. Answering *update* updates a person's DETAILS only:
  **role, permissions, branch, username, email and password are never an import's to change**, a blank
  cell never blanks a stored value, and somebody belonging to another tenant is never touched whatever
  was asked.
- **JSON and NDJSON are read too**, flattened to dot paths onto the same grid, so nothing downstream
  knows. An array inside a row is joined and warned about rather than becoming rows — inventing rows
  silently changes the record count.
- **A page's `Options` render on every step except the running one.** The name-order question was first
  put on the FILE step, where no row has been read yet and the question cannot be asked; an option that
  changes how a row is BUILT belongs beside the rows. `QImportPanel.FirstValueOf(field)` is how a page
  asks a question about the data at the mapping step.
- **Verified by `scripts/e2e/browser/import-wizard.mjs` — 19 checks, 0 failed, against the two real
  files.** It stops before pressing Import: reading the file correctly is what is under test, and
  importing 1,711 students on every run leaves a mess for somebody. **133 of the 184 staff have no
  email address**, which the staff import treats as identity — that is a question for the school, and
  it is §11 of the plan, not a bug.

- **`OnFileChosen` is how the page learns the file name** for `SourceFileName` on the job. The roster
  passed a field nothing ever assigned once its own file picker moved into the panel — CS0649 on
  every build — so every roster import since has recorded a null name in the Import History.

### A re-import is the normal case, and the reader is told what would MOVE (2026-09-22)

Asked for as *"importing the file again for both staff and student should detect any student account
or staff code (unique on this project) and suggest updation, in case the data is different"*, and
*"identify possible student duplication, alert the user for confirmation that actually the students
are different"*. A school re-imports its own sheet every term, so this IS the common path — and the
reader learned how much of a file landed on people already on file only from the summary
**afterwards**, and never learned WHAT it had changed about them. The roster had no precheck at all.

- **`POST branches/{b}/students/import-jobs/precheck` is new; the staff one was widened.** Both take
  the ROWS now, not a list of keys: what would change, and which names look like one person twice,
  cannot be worked out from keys. Read-only, side-effect free, gated exactly as the import is.
- **The answer names the FIELDS, never the stored values.** The diff is made on the SERVER for that
  reason — a reader deciding whether to overwrite does not need every colleague's national ID read
  back to their browser to be told so. Asserted by e2e 24.5 and 24.10.
- **`StaffImportChanges.Compute` returns a list of `(Label, Apply)` and IS the one home.** The
  precheck reads the labels; `RosterImportProcessorJob` runs the same list and applies it. A change is
  an action rather than something applied in place, so the precheck cannot write by accident however
  the entity reached it. **A preview that says "nothing will change" and an import that then changes
  three fields is the worst answer an import can give** — the same rule `ImportRules` already carries.
- **A value the file LEFT BLANK is never a change** (`ImportMatching.Differs`). A sheet exported from
  a system that does not hold national IDs must not blank the school's.
- **`ImportMatching.NameKey` SORTS the words**, so "Aine Grace" and "GRACE, AINE" are one key. A
  school's two exports routinely disagree on name order, and the duplicate that hides behind a flipped
  name is the one nobody spots by eye.
- **A likeness ASKS; it never refuses.** Two children on one roll genuinely share a name, so
  `QImportPanel` holds the Import button behind a confirmation rather than dropping a row on a guess —
  the sign-up guard's rule one level down. Raised both for two rows of the file and for a row whose
  name matches somebody already on file under a different number.
- **The name lists are read in memory, capped**, because the key folds case, punctuation and word
  order and SQL cannot do that here without an extension this project does not take.
- **Verified by section 24 (`import-precheck-e2e.mjs`, 12 checks) and
  `scripts/e2e/browser/import-reimport.mjs` (17 checks), both 0 failed.** Note the browser suite's
  first run reported THREE product bugs that were the fixture: a guardian phone is required on every
  row and a guardian the roll does not hold is a real change, so the "unchanged" row has to carry a
  guardian the child already has. It seeds one and removes it. **A row that is not actually identical
  cannot test "identical changes nothing".**

### The localisation stack reaches the screen now, and the bug under it was invisible (2026-09-22)

Recorded that morning as built-and-unwired: `AddLocalization`, `UseRequestLocalization`, the
`/culture/set` cookie endpoint, `SupportedCultures` (English, Swahili, Luganda),
`LanguageSelector.razor` and three `.resx` files carrying 28 translated strings each — and **not one
component injected `IStringLocalizer`**. Wiring it up found a SECOND fault underneath, and that one
is the more useful lesson.

**`AddLocalization(options => options.ResourcesPath = "Resources")` made every lookup miss, in every
language.** `ResourceManagerStringLocalizerFactory` builds the prefix as
`<root namespace>.<ResourcesPath>.<type name with the root namespace trimmed>`, and the root
namespace it uses is the **assembly** name, `Q-Mgr.Web` — which is not a prefix of
`QMgr.Web.Resources.SharedResources` (the hyphen), so nothing is trimmed and it looked for
`Q-Mgr.Web.Resources.QMgr.Web.Resources.SharedResources`. The satellites really do carry
`QMgr.Web.Resources.SharedResources.lg.resources`. **The marker class already lives in the
`Resources` folder, so the path is in its namespace and adding it again is what broke it.** The call
is now a bare `AddLocalization()`; do not put the option back to tidy it.

- **NOTHING ANYWHERE WOULD HAVE TOLD YOU, and this is the rule to keep.** `IStringLocalizer` never
  throws on a missing resource — it returns the KEY, and the keys here **are** the English text. So a
  completely broken lookup and a correct English render are byte-identical: a clean build, a code
  read, and any English-only assertion all pass either way. **The only thing that can tell them apart
  is asserting that a non-English string appears**, which is what
  `scripts/e2e/browser/localization.mjs` does (15 checks, in `all.mjs`).
- **Injected on the customer-facing screens only** — kiosk, customer display, signage, join-the-queue,
  ticket status and feedback. The staff application stays English, which is what `SharedResources`'
  own header says; `QueueBoard` is inside the app shell and carries staff controls, so it is staff.
- **`@L[GetWelcomeMessage()]` is the pattern for a string a switch already chose.** The industry
  variants ("Welcome to Our Healthcare Facility") have no translation, and passing them through the
  localizer means they degrade to themselves today and need only a resx entry — never a code change —
  to be translated later.
- **A KIOSK CARRIED A FAKE PICKER, and it was removed.** Two buttons reading **EN** and **AR** sat in
  the footer with **no handler of any kind**, offering a language this product has never had one
  resource for. The real `LanguageSelector` is in the header now, and on join, ticket status and
  feedback. The same class as `QAvatar.PhotoUrl` and `MainLayout`'s inert notification rows: a control
  that looks like a feature and does nothing.
- **The caps on the display moved to CSS.** `NOW SERVING` / `WAITING` were typed in the markup, so
  `text-transform: uppercase` on `.section-header .label` keeps the English screen byte-identical and
  uppercases a translation the same way.
- **A wall display has no one to press the picker**, so it follows whatever cookie that browser
  carries and there is no per-organization display language. Adding one is a column and a decision,
  not a tidy-up — ask first, as with `Organization.DisplayTheme`.

**What is still English, stated rather than implied**: only the 28 strings in the resource files are
translated, so these screens are part English (the kiosk's "Customer Information", the feedback
survey's own questions, ticket status's outcome sentences). **No translation was invented** — the
existing Luganda and Swahili came from somewhere that could vouch for them, and guessing a school's
language is not a thing to do from here. Adding one is now a resx entry and nothing else.
**Two paths could not be exercised on the dev tenant**: the kiosk's service-card strings and the
feedback page, both behind Core Queue, which this tenant does not hold. The suite SKIPS them and says
so rather than passing vacuously.

**The dead-code sweep that found this (2026-09-22) turned up nothing else.** No unused stylesheet, no
unused script, no component nothing renders, and no DI registration nothing injects in either
`Program.cs` — the four dead stylesheets and Mapster were already gone. A scan listing "types nothing
outside their own file names" is the wrong measurement here and was discarded: EF configurations are
found by assembly scan, controllers by routing, and a `[FromBody]` record is used only inside its own
file.

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
- **A hub's OWN buttons that act on one tab's list show on that tab only** (2026-09-23). Staff Directory's Log
  record · Add staff · Import a list · Export act on People, and on Class teachers they sat beside that section's two
  — six in one row, 84px (`action-location`, `uniform-check`). The hub also owns the open tab's `QInfo` now, because
  an embedded section's own title — and the explanation on it — is hidden.
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
- **CANCELLING A ROTA ENDS A SLOT UNDER WAY NOW (2026-09-24).** It used to cancel only slots not yet started, so a
  weekly rota cancelled mid-week changed nothing visible, a second press reported 0, and the running slot went on
  asking for daily reports to its end. Now the running slot's `EndsAt` becomes now — its days so far, acknowledgements
  and reports stay, and its close-out is still taken — because reports, reminders and the register chase all read
  `EndsAt`. The answer is `RotaCancelResult(Cancelled, Ended, Kept)` and the toast says which happened. The per-person
  count below the rota is **"Duty load"**, not "Fairness", and sits UNDER the rota: the duties are what the page is for.
  Section 15 and `browser/rota-layout.mjs`.
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
- **THE TIMES DECIDE A SCHOOL DAY'S ORDER (2026-09-24).** `TimetableCycle.Validate` sorts periods by start time and
  stores them that way; the School Day page re-sorts its rows the moment a start changes (`SortByStart`, rows keyed so
  focus follows); and **saving the school day enqueues `LessonGenerationJob`**, as publishing does — a lesson's time is
  copied into its duty when generated, so without that a moved period left every teacher's day at the old time until
  01:00. Keys never change, so every lesson placed in P4 moves with P4. Section 39 and `browser/school-day-order.mjs`.
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

### A timetable has an OWNER now, and publishing over a live one refuses (2026-09-22)

Asked as *"is it not dangerous if many users have access to the time table? ... one of the teachers is
appointed as time table master ... if the time table is opened to all administrators with a role, there
is a possibility to abuse the feature"*. The plan is `docs/plans/TIMETABLE_OWNERSHIP.md`; §7 records what
changed while building it.

- **`TimetableAccess.MayWrite` is the one home**: `caller ∈ Timetable.ManagerUserIds || caller holds
  timetable.manage`. Byte for byte the rule `StaffDuty.RecorderUserIds` has always had — the timetable
  was the outlier. **Every write endpoint DROPPED its `[RequirePermission]` attribute** and calls
  `GuardWriteAsync`, because an attribute refuses before the handler runs and cannot let an appointed
  master through. **A new write endpoint that keeps the attribute silently excludes the people the
  feature exists for.**
- **THE STAFF-SCOPE REFUSAL DOES NOT APPLY TO AN APPOINTED MASTER** (`NeedsUnscopedStaffView`), and this
  is what makes the feature reachable rather than theoretical: the seeded `teacher` role is
  `StaffScope.SelfOnly`, and a teacher is exactly who a school appoints. The first cut 403'd the master
  before ownership was consulted. **The appointment IS the unscoping** — an explicit per-object grant
  made by somebody holding the permission. Delegation is not scope.
- **Appointing is the permission holder's act alone.** A master may not add or remove masters, including
  themselves, or the control is decoration. `SetManagers` checks that BEFORE the scope refusal, or a
  teacher is told about somebody else's situation ("built by an unscoped timetable master") and nothing
  about their own — found by e2e 26.1h.
- **The administrator override STAYS and is never silent.** A school whose master leaves mid-term must
  not be locked out of its own timetable; a system that can be bricked by one person's absence gets
  worked around with a shared login. So a non-owner write writes `ActivityActions.TimetableOverridden`
  and notifies every named manager, and the editor says so BEFORE the write (`WouldBeOverride`).
- **PUBLISHING OVER A LIVE VERSION REFUSES UNLESS `Replace` IS ASKED FOR.** It used to archive every
  overlapping published version silently — and because every lesson query filters Published over today's
  date, that stops every lesson materialising school-wide: no registers, no teaching figures, no portal
  card. 409 `WOULD_REPLACE_PUBLISHED`, naming what it would take out of service.
- **"Three timetables in a term" cannot be three `Timetable` rows**, because only one may cover a date.
  General teaching is the timetable; **exam supervision is a series of Session duties**, which is what
  `DutyKind.Session` and the seeded Exam Supervision parameter have always been for. Grouping those
  under a name and an owner is not built yet.
- **`TimetableStatus.Expired` is DERIVED on read** (`Timetable.StatusOn`), never stored — the
  `StaffEmploymentStatus` call. `ReminderSubject.TimetableExpiring` chases it on the existing ladder and
  **skips a version that already has a successor published**.

### A one-off swap is TWO COVERS; a permanent one is a RE-PUBLISH (2026-09-22)

*"there are incidences where a staff member talks to another staff member for a possible switch of the
times allocated on the time table."* The swap machinery existed and **could not do it**: the apply path
called `CurrentDraftAsync`, so a mid-term swap — which is when that conversation happens — was accepted,
agreed by the colleague, approved by a decider, and then refused with *"there is no draft timetable to
change any more"*. Three people acted and the system refused last. And **nothing in the app ever posted
`MyLessonId`**, so the journey was reachable only by curl, which is why nobody saw it.

- **`TimetableLessonException` is a one-day departure: `Cover` or `Cancelled`, one per lesson per date.
  There is deliberately NO `Moved`** — a one-off swap is two covers, each teacher taking the other's
  lesson at its own time. A one-off does not change WHEN a class is taught, only who teaches it, so no
  class, room or cohort can be disturbed and `TimetableChecker` has nothing to say about it.
- **THE MATERIALISER RECONCILES ON `(TimetableLessonId, StartsAt)` AND A COVER CHANGES NEITHER.** Without
  the explicit reassignment pass in `StaffLessons.MaterialiseAsync`, an existing duty is found, left
  alone, and the cover silently does nothing for any lesson inside the 14-day window — which is most of
  them. The pass keeps the duty's row (its reminder stage, its place in My Day); a duty somebody has
  already flagged is never reassigned, because the register was taken. **An API assertion cannot see
  this** — the row exists either way. It was checked by publishing a version covering today and reading
  the duty back.
- **A permanent swap is `ITimetableRepublishService`**: copy the published version into a draft, trade
  the two SLOTS (never the teachers — that would move a class to somebody not assigned to it), run
  `TimetableChecker`, publish over. **The date range is KEPT, not split at the swap date**, because
  cycle day 1 is anchored on `EffectiveFrom` and moving it would shift every cycle day of an A/B
  timetable.
- **The decider sees what the two teachers cannot.** `PreviewSwapAsync` fills
  `StaffConfigRequestDto.DecisionWarnings` — only issues the swap ITSELF introduces, because a published
  timetable routinely carries acknowledged soft ones and listing those back makes every swap look as
  though it broke something. That is why a decider is still in the loop for a mutual agreement.
- **The decider rule is UNIFIED with the write rule.** They disagreed in both directions: an appointed
  master could not decide a swap on their own timetable, and a permission holder could decide swaps on
  one they had nothing to do with. A `ClassAssignment` carries no timetable and stays on the permission
  alone — it grants Teaching-tier access to a class of children.
- **Cover needs the colleague's agreement exactly as a swap does.** Nobody is volunteered to stand in
  front of a class by somebody else.
- **Future cover does NOT survive a permanent swap** (the exceptions cascade with the version). Carrying
  them across by matching teacher, slot and class would be a guess, and a wrongly carried cover puts the
  wrong person in front of a class.
- Verified by **section 26 (`timetable-ownership-e2e.mjs`, 54 checks, 0 failed, run twice back to back)**.
  It cost two fixture lessons worth keeping:
  - **A lesson cannot be placed for a teacher with no `ClassTeacherAssignment` for that class and
    subject** (`TeacherNotAssigned`, a hard clash, so publishing refuses). The suite seeds two and
    removes them — and they GRANT Teaching-tier access to a class of children, so leaving them behind
    would quietly widen what two accounts can see.
  - **A SUITE THAT PUBLISHES A TIMETABLE MUST TAKE A UNIQUE DATE WINDOW AND ARCHIVE WHAT IT PUBLISHED.**
    The first version used a fixed "today + 400", and the SECOND run failed four assertions against the
    FIRST run's leftovers — and the refusal it produced read *exactly* like the product bug the section
    exists to prove is fixed. A published version cannot be deleted by design, so a fixed window
    guarantees a collision on every re-run. It now takes a random offset AND archives on cleanup.
    Same class as *"a suite whose first assertion depends on the last run's tidiness will lie eventually"*.

### An opening default is not a rule about a tab somebody just pressed (2026-09-21)

Reported from production as *"that class tab not working. previously default tab was class"*.
`ChooseDefaultKey` carried the editor's OPENING default — a teacher arriving at `/admin/timetable`
sees their own week — and `OnViewChanged` called it on every press. Pressing **Class** clears the
key, the next line saw an empty key, and the axis flipped straight back to **Teacher**: the Class tab
was unpressable for anybody who taught a single lesson, the timetable master included. Nothing looked
broken, which is why it survived — the press simply appeared to do nothing.

- **A default belongs to ARRIVING at a page.** `ChooseDefaultKey(opening: true)` is passed only from
  `OnInitializedAsync`; every other caller — a view change, a version switch, a new draft — picks a
  usable key and leaves the axis where the person put it.
- **A timetable master opens on Class**, never on their own two lessons: they came to build the week.
  The own-week default is for somebody who cannot manage the timetable.
- **`scripts/e2e/browser/timetable-views.mjs` covers it** and REFUSES to run when the teacher it signs
  in as teaches nothing in the version on screen — the flip would never arm and all fourteen checks
  would pass while proving nothing. The manager half seeds a lesson of the administrator's own and
  removes it afterwards, for the same reason: the dev tenant's administrator teaches nothing.

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
- **A BULK ACTION NEEDS NO BULK ENDPOINT, and should not have one (2026-09-21).** The Staff Directory
  moves a ticked group into a department and gives a ticked group the same line manager by calling
  the ordinary per-person `PUT …/staff/structure/members/{id}` once per row. Everything that endpoint
  already enforces therefore still holds — nobody is their own line manager, the person is told who
  now supervises them — and a refusal is **about one person, so it can be named**. A new bulk
  endpoint would have to re-implement all of it and would report one aggregate failure.
  - **Send the OTHER field back unchanged.** That endpoint replaces departments AND line manager, so
    assigning a manager without re-sending `DepartmentIds` would quietly clear everybody's
    departments. This is the trap in reusing a PUT for a partial change.
  - **Say which it is: "move" REPLACES.** The dialog states it, because "move them to a department"
    could just as reasonably be read as adding one.
  - **Skip the obvious self-case rather than reporting it.** Somebody ticked who IS the line manager
    being assigned is passed over silently; the server would refuse them, and a refusal the reader
    has to think about should be one they could not have predicted.
  - Verified by `scripts/e2e/browser/staff-bulk.mjs` — **17 checks, 0 failed**, which ticks real
    people, drives the QSelect with press-hold-release (never `element.click()`), and **puts every
    person's department and line manager back as they were**.

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
- **The certificate is issued by ONE root-owned helper, `/usr/local/bin/qmgr-tenant-domain`**,
  reached through the spool and the root worker (**not sudo — see "THE HELPER IS NOT REACHED BY
  SUDO" below**, which is how every activation failed for a day). The API stays `www-data` under
  `ProtectSystem=strict`: handing a web application the ability to rewrite nginx is a far larger
  grant than one argument-validated command. The helper **validates the domain itself** — it is the
  privilege boundary, so the check lives on its side of it. `build-linux.ps1` generates the helper
  and the two worker units; `install.sh` installs them, creates the spool, `/etc/nginx/qmgr-tenants`
  and the ACME webroot, and removes the old sudoers drop-in. **The nginx include must exist before
  `nginx -t` runs**, which is why the directory is created first.
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
- **A PUBLIC PAGE MUST CARRY `style="@Host.BrandingStyle"` ON ITS `.login-container`, and
  `scripts/e2e/public-branding-check.mjs` is the guard (2026-09-22).** Three of the nine did not —
  `VerifyEmail` (which had a whole card of its own, `.verify-container`/`.verify-card`),
  `ForgotPassword` and `JoinCode` — so a school clicking a verification or password-reset link from
  its own domain met the shipped wine one click from its own crest. **Two of them cascaded
  `TenantHostContext` and read it nowhere.** Nothing could see it: the build is clean, `QBrandMark`
  still shows the school's LOGO so the page looks branded at a glance, and **`white-label-ui.mjs`
  cannot reach it by construction** — that suite signs in and walks the SHELL, while a public page is
  branded from the HOST, and on `127.0.0.1` no host resolves to a tenant by design
  (`GetBrandingForHost` matches `Organization.CustomDomain` exactly), so a browser sweep of these
  pages against a dev box would report the platform palette either way. The rule is a property of the
  MARKUP, which is why the check is static and instant.

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
- **WHERE THE FLAG IS READ — and it is no longer a list of strings (rewritten 2026-09-22).** It was
  three literals: `<p class="powered-by">` on the public pages, `MainLayout`'s footer and
  `EmailTemplates`', the first of them behind a `PoweredBy` component that HID its line when the flag
  was set. The public pages carry `QCopyright` now and **`PoweredBy` is deleted** — an entitled tenant
  reads its OWN name at the foot of its sign-in page rather than reading nothing, which is the pattern
  `SharedDocument` and `MainLayout` already used. The readers are `QCopyright` (the shell and the nine
  public pages), `EmailTemplates`, `SharedDocument` and the mobile shell's `TenantInfoController`.
  A tenant who pays and then reads "Q-Mgr" at the foot of their own password-reset mail has not got
  what they bought.
- **The catalogue's description was corrected on 2026-09-23** — it still sold removing "Powered by SACC
  Software" a day after those pages stopped carrying it. `ModuleCatalogDefaults.WhiteLabelPlusDescription`
  and the migration `ExamSeriesEmploymentTypesAndCatalogWording`, which rewrites only a row still holding
  the shipped text (the `FoldStaffPerformanceIntoWelfareModule` rule).
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

## A component parameter nothing passes is a feature nobody has (2026-09-22)

Reported as *"i added a photo, but can not view it anywhere, not even in profile on top right and
Good morning savior … even on the staff list"*, and every word of it was true. The upload worked,
the bytes were stored, `UploadAuthorizer` classified them as `UploadOwnerKind.StaffPhoto` and gated
them correctly, `ProfileController` signed the link it answered with — and then the photograph
appeared on exactly nothing, for weeks, because **no DTO that any avatar reads carried the column.**

**`QAvatar` had taken a `PhotoUrl` parameter since the day it was written and not one of its
sixteen call sites passed one.** That is the shape to remember: a component whose API is complete,
a storage path that is complete, a gate that is complete, and a field missing from the middle. A
build cannot see it, a code read of any one file cannot see it, and the API suite would have passed
asserting the endpoint.

- **`User.PhotoUrl` now reaches eight DTOs**, each signing at its own mapper: `UserInfo` (the header,
  built at four places in `AuthController`), `UserDto` (the users list), `StaffMemberDto` (the
  directory, the portal greeting, the staff file, the structure tree), `StaffProfileDto` (the profile
  dialog), `RegisterRowDto` (a register is read to recognise who is in the room) and
  `StaffAppraisalDto.SubjectPhotoUrl`.
- **A signing call cannot live in an EF projection.** `UsersController`'s list and by-id queries
  `Select` into `UserDto` server-side, so they carry the RAW column and sign after materialisation
  (`d with { PhotoUrl = UploadLinks.Sign(d.PhotoUrl) }`). Putting `Sign` in the `Select` compiles and
  throws at run time.
- **`UserInfo` is the one that is PERSISTED**, in localStorage at sign-in, and a signed link dies in
  an hour — so two things had to be true. `IAuthService.CurrentUserChanged` is raised when
  `RefreshCurrentUserAsync` replaces the stored copy, and `MainLayout` listens and re-reads; the photo
  upload paths call it, or the picture shows on the page that uploaded it and nowhere else until the
  next sign-in. And **`QAvatar` now renders the initials ALWAYS, with the photograph layered over
  them and `onerror="this.remove()"`** — an expired token, or a deleted file, falls back to initials
  instead of the browser's broken-image glyph. A new avatar surface needs nothing for this to hold.
- **The Profile page was the only place with no way to CHANGE a photo** once the onboarding checklist
  was finished. It has one now, beside the avatar.
- **`scripts/e2e/browser/profile-photo.mjs` (19 checks, in `all.mjs`)** uploads a real PNG and then
  opens every surface. It asserts **`naturalWidth > 0`, not the presence of an `<img>`**: a wrong or
  expired token renders an `<img>` all the same, so counting elements would pass against a 404.
  **It also has to NARROW each list to its own row first** — the dev tenant has 143 people and the
  lists show 25 and 10, so without that the probe reads twenty-five colleagues who have no photograph
  and reports a bug that is not there. Measuring the wrong rows is this suite's real trap.

## An explanation of a FIELD is `Hint`, and it renders beside the label (2026-09-22)

*"grow the size of this modal, and also use the more info feature, instead of cluttering the form
with repetitive information."* Add-a-member-of-staff carried four `<p class="form-hint">` blocks —
one of them four lines long — inside a 560px dialog, which made it 830px tall and scrolling.

- **`QInput`, `QSelect` and `QMultiSelect` take `Hint`**, which renders a `QInfo` in a
  `.q-field__label-row` beside the label. That is the one home for explaining a control; a page
  writing its own `<p class="form-hint">` under a field is the standing prose this app already swept
  once. **A warning is still not a hint** — if the sentence changes what somebody DOES, it stays on
  the page, which is the rule `QInfo`'s own header states.
- **The info button is a SIBLING of the `<label>`, never a child.** A click anywhere inside a
  `<label>` is re-dispatched to its first control, so a nested button toggles straight back shut.
  This is the same rule already recorded as "never put a `QSelect` inside a `<label>`" and it cost
  nothing to obey only because that note existed.
- The dialog is `Size="lg"` (800px), which also lets `.form-row`'s `auto-fit minmax(240px, 1fr)`
  settle into three columns rather than two: **473px tall against 830px, with nothing removed.**

## An onboarding checklist is a STRIP, not a card (2026-09-22)

*"Finish setting up your account is taking too much vertical space. what is the industry standard
for appearance of such feature?"* It is a slim one-row bar, and Stripe's onboarding bar, Linear's,
Slack's and Intercom's all do the same three things:

1. **one row carrying a progress meter and how many steps are left**;
2. **the NEXT incomplete step's own control inline**, so the common case needs no expanding at all —
   this is the half that actually saves the space, because the reader is never shown the three steps
   they have already done;
3. **the full list one click away**, with open-or-shut remembered per browser in `localStorage`
   (wrapped, because a private window throws, and shut is the right default).

Measured on the portal: **80px collapsed against ~230px, 204px opened.** The collapsed row and the
open list render the SAME control through one `ActionFor(step)` rather than two copies that drift.

**It is deliberately NOT dismissible**, unlike most of the products above: two of the four steps are
how the school reaches somebody and how children's information is protected. The answer to "it takes
too much room" is one row, not a dismiss button.

## A student code and a staff number are KEYS, not labels (2026-09-22)

*"student code and staff number should be unique for each company and mandatory. implement
aggressive validations both in ui and backend of the student and staff codes."*

Both already carried a per-organization partial unique index, which is the half that looks finished
and is not. **Both indexes were EXACT-MATCH**, so `MH/S/001` and `mh/s/001` were two different
people; neither field was required anywhere; and each write path did its own `Trim()` and its own
duplicate query — **`UsersController.CreateUser` and `UpdateUser` did neither**, writing whatever
they were given and leaving Postgres to throw, which reaches a browser as a 500 with no field named.

- **`Q-Mgr.Shared/Domain/Identity/PersonCode.cs` is the ONE home**, beside `PersonName` and
  `RegistrationIdentity` for the same reason. `Normalize` (trim, collapse internal whitespace, empty
  → null), `Key` (that, upper-cased — what is COMPARED), and `Validate` returning the sentence a
  form, an import row and a problem detail all show verbatim. The API, the two import processors and
  the three forms call it, so the preview, the page and the server cannot disagree about what a code
  is.
- **UNIQUENESS FOLDS CASE, and the database enforces it through a FUNCTIONAL index on
  `upper(column)`** — raw SQL in `20260921215214_PersonCodesAreMandatoryAndCaseInsensitive`, because
  EF cannot model one and `citext` is a Postgres extension this project does not take. **No second
  normalised column was added**: `upper()` is plain SQL and the index serves the lookups. What the
  school TYPED is what is stored and shown; only the comparison is folded. **`PersonCode.Key` must
  stay byte-for-byte in step with that `upper()`**, or a duplicate the API accepts is refused by
  Postgres as a 500 instead.
- **MANDATORY IS ENFORCED ON THE WRITE PATH, NOT WITH `NOT NULL`, and that is deliberate.** 138 of
  143 users on the development tenant carry no staff number: a `NOT NULL` constraint could only be
  satisfied by inventing one each, and **a fabricated staff number in a MoES return is worse than a
  blank one**. So every endpoint and every form requires one and the rows that predate the rule keep
  their null until somebody supplies the real number.
- **The migration RESOLVES CASE-COLLISIONS BEFORE BUILDING THE INDEX.** Rows that were legal under
  the old exact-match index can collide under the new one, and a unique index that fails to build
  takes the whole deploy down on a customer's server. The later row (by `CreatedAt`, then `Id`) is
  **suffixed ` (2)`, never deleted or merged**: two people wearing one number is the school's data
  problem to settle, and a suffixed code is visibly wrong in every list and every export, which is
  how somebody comes to fix it. The `Down` restores only the indexes — the normalisation and the
  suffixes are not recoverable and inventing them back would be worse.
- **The IMPORTS refuse a row without one, and that is the point rather than a tightening.** Both
  importers UPSERT on these codes, so a row without one could only ever create a new record — which
  is how a second run of the same sheet doubles a roll or a staff list. Both columns are
  `Required` in the wizard now, with the reason on the field.
- **`UsersController` is the one place required is CONDITIONAL**: a platform-only role
  (`RoleCodes.PlatformOnly`) has no staff number, so the role decides. `UpdateUser` validates a
  number it is GIVEN and leaves one it is not — that endpoint also carries a bulk role change and a
  branch move, neither of which is about the number — but it can never blank an existing one.
- Verified by **`scripts/e2e/person-codes-e2e.mjs` (section 23, 28 checks, wired into
  `class-teacher-e2e.sh`)**, which **races five simultaneous posts of one code in mixed case and
  asserts exactly one student appears** — the only way to prove an index holds is to race it — and in
  a browser: both forms refuse before submitting, name the field, and mark it with the asterisk.

## The bell's rows were inert, and 96 of 100 notifications point somewhere (2026-09-22)

*"is that notification intended to open the details when user clicks on it? currently, it does not
respond? no signs that has been read."* Both halves were true and the cause was structural:
**the panel's rows in `MainLayout` were plain `<div>`s with no handler at all.** `MainLayout` even
carried a `MarkAsRead(NotificationItem)` that nothing anywhere called. So from the bell a
notification could not be opened, could not be marked read, and never lost its unread styling —
while the identical list on `/notifications` was a `<button>` doing all three. Measured on the dev
tenant: **96 of the last 100 notifications carry an `ActionUrl`**, so almost every notification this
product sends was unreachable from the place people actually look.

- **A row that does something is a `<button>`.** It un-inherits the element's own font, colour,
  background and border in `layout.css`, and carries the unread dot and the chevron `/notifications`
  already had — the two rows now read the same because they now DO the same.
- **The count is the SERVER's, not arithmetic.** `MarkAllAsRead` answers with the caller's remaining
  unread count and `NotificationService` pushes `NotifyUnreadCountAsync` after every mark — so the
  client never assumes zero (`MarkAllAsReadRemainingAsync`; **null means the call failed**, and the
  badge is then left alone rather than cleared on a request that never landed).
- **`OpenNotification` does not decrement when it navigates.** The API's own count push is already
  on its way, and subtracting locally as well double-counts. It decrements only for the row that
  stays put, which is the interim before that push lands.
- **`INotificationHubService.MarkAsReadAsync` swallows every failure and returns void** — the
  `IQueueApiService` rule, in the notification client. `TryMarkAsReadAsync` reports success, and the
  row stays visibly unread when the server refused rather than lying and reverting on the next load.
- **A push must be deduped on its own id.** `HandleNewNotification` inserted unconditionally and did
  `notificationCount++` regardless, so a reconnect replay or two overlapping handlers counted one
  notification twice and the badge drifted upward for the life of the circuit. It now ignores an id
  already in the list and only counts an arrival that is actually unread.

**CORRECTED 2026-09-23 — this said a recipient-less notification was "latent, nothing creates one
today". Payments and module trials had been creating them all along, and a teacher read the school's
failed payments. There is no such row any more; see "Every notification names ONE person" below.**

## A phone gets a navigation BAR, chosen from the person's own permissions (built 2026-09-22)

Under 768px all **47 destinations** sat behind the hamburger, and hiding the main navigation
**cuts discoverability almost in half** ([NN/g](https://www.nngroup.com/articles/hamburger-menus/)).
Four role-chosen slots plus Notifications and More: inside Material 3's three-to-five, and it keeps
Apple's warning about over-using a More tab intact — More is overflow, never a daily task.

- **`Components/Shared/MobileNav.cs` is the ONE home**, beside `HubTabs` and for the same reason.
  It is a pure decision: `SlotsFor(hasPermission, hasModule)` and `MoreFor(...)`. `QMobileNav.razor`
  only draws what it returns, and `mobile-nav.mjs` reads the same list.
- **Built from PERMISSIONS, never the role code.** A Director of Studies also teaches; a manager
  also covers the front desk. `MainLayout` reads the WHOLE permission set once
  (`GetPermissionsAsync`) and hands `MobileNav` a synchronous predicate — one awaited call per
  candidate would be a dozen round trips and would stop `MobileNav` being pure.
- **Module gates first**, exactly as the sidebar does, so a bank never sees a welfare slot.
- **A slot whose permission or module is absent is NOT RENDERED.** Three plus More is a legitimate
  bar; a greyed slot that refuses on press is not. `mobile-nav.mjs` signs in as a teacher and opens
  every slot it was offered, asserting none lands on `/unauthorized` or the Billing hub.
- **THE BAND IS RESERVED, NOT OVERLAID.** `--qm-mobilenav-total` = 56px + `env(safe-area-inset-bottom)`,
  applied as `.qm-main`'s bottom padding under 768px. **Nothing in this app reserved a bottom safe
  area before**; six pages placed real controls in the bottom 64px, and a bar laid over them would
  have covered every one. The date picker (fixed `bottom: 12px` under 480px) and the bottom sheet
  were lifted clear in the same change.
- **Labels are always on** — Material 3 permits label-on-active-only and NN/g's finding argues
  against it. **Slots are 48px**, clearing WCAG 2.5.5 (44px AAA) and the project's 40px phone floor.
- **More is a SHEET, not a route**: no back-stack entry, and it shuts on any navigation — a sheet
  left open over a page the reader did not choose reads as the app having frozen.
- **The bar is hidden by a MEDIA QUERY, never a C# width check.** The server cannot know the
  viewport.

### The mobile readiness pass, and the correction that mattered

`scripts/e2e/browser/mobile-readiness.mjs` — 14 pages at a real 390×844, now **56 passed, 0 failed**.

Its first run reported **27 failures including overlaps on every page, and they were not real.** It
compared bounding boxes. The collapsed sidebar is 1px wide with `overflow: hidden`, so its nav links
keep 260px boxes that cross every page and are clipped out of the hit test entirely;
`document.elementFromPoint` showed taps landing on page content. Two rules came out of it:

- **An overlap must be HIT-TESTABLE to count** (`elementFromPoint` at the element's centre).
- **A FIXED overlay crossing flowed content is scrolling, not an overlap** — and the test is the
  fixed ANCESTOR, not the element: a slot inside the fixed bar is itself `position: relative`, so
  testing the element alone reported the bar against every page it scrolled over.

**A measurement that flags the wrong thing is worse than no measurement, because work gets done to
satisfy it.** Four real findings it did surface and that are now fixed: `QInfo`'s button at 20px
(on ten of fourteen pages), the roster's two raw checkboxes at 13 and 16px, the roster name link at
21px, and — introduced the same day — `.row-actions` collapsing to **3px** on a phone, because
`.data-table .row-actions { width: 1% }` is two classes deep and beats `table.q-stack`'s
`width: 100%`.

## `QCopyright` is the one home for the copyright line (2026-09-22)

Asked as *"do we have a shared component for branding … such that we have SSoT? perhaps in future
privileged user can have custom branding of the copyright message"*. There was one for the
ATTRIBUTION line (`PoweredBy`) and none for the copyright: **eight copies, and five of them typed
the year in** — Docs, a docs article, Privacy, Support and Terms all read
"© 2026 SACC Software Limited" and would have gone on saying 2026 for ever. The shell had two
branches of its own and `EmailTemplates` a ninth.

- **IT SWAPS THE NAME; IT NEVER HIDES THE LINE.** `PoweredBy` answered the other half — whether
  "Powered by SACC Software" appears at all — and was **deleted on 2026-09-22**, once the nine public
  pages moved onto this component and it had no callers left. A copyright is never hidden, because a
  document has to carry one. The paid entitlement still decides, through the same
  `AttributionRemoved` flag it always read: set, the line credits the TENANT; unset, it credits us.
- **`Owner` falls back to the HOST, so a public page passes nothing.**
  `<QCopyright Class="login-copyright" />` is the whole call on all nine: out there nobody is signed
  in, so the host is the only thing that can name a tenant. Requiring every caller to pass `Owner`
  would put one rule in nine places, and the one that forgot would credit US at the foot of a page the
  tenant had paid to clear — with nothing looking broken. The SHELL still passes it explicitly,
  because there the source is the ORGANISATION: most tenants sign in on the platform address and
  should not need a domain of their own to get what they paid for.
- **The year is `DateTime.Now`, never typed.**
- **The words are "© 2026 SACC"** (user decision 2026-09-23) — the suite name, not "SACC Software
  Limited". The line is a copyright notice, not a legal identification; Terms and Privacy keep the
  full name because there it is the contracting party. `EmailTemplates` and the mobile sign-in
  screen say the same.
- **`Text` is the extension point for a privileged tenant's own wording.** Deliberately a parameter
  rather than a stored column: there is nothing to store until somebody may set it, and when they
  can, it is read once here and every surface follows.
- **The descriptor is "Front Office", two words** (user decision 2026-09-22): the hyphen went, and
  "Platform" went earlier the same day as redundant.
## "S1A" and "S1 A" are one class, and a missing class is said ONCE (2026-09-22)

The first real school roll this product imported: 1,711 students, and
*"S1A is not one of this branch's classes"* on **1,584 of the rows**. The sentence was true, it was
on every row, and there was nothing the reader could do with it.

Two faults, and the first is the one that matters.

- **`Q-Mgr.Shared/Domain/Identity/ClassName.cs` is the ONE home for comparing a class name.**
  There were three copies of the rule — `ClassTeachersController.NormalizeClassName`,
  `TimetableCycle.Normalize` and the scope service — and every one was `Trim().ToLower()`, so a
  space was exactly the failure CLAUDE.md already warns about for the class-teacher scope: *a
  student invisible to their own class teacher because of a stray space is a safeguarding failure,
  not a cosmetic one.* `Key()` keeps letters and digits only, upper-cased, so `S1 A`, `S1-A`,
  `S1/A` and `s1a` are one key. `Display()` is what is STORED — the school's own spelling, only
  tidied. **Never store a Key; never compare a Display.**
- **A class written differently is adopted silently.** `BuildRosterRow` folds the typed value to the
  configured spelling, so the roll lands in the class the school already has rather than beside it.
- **A class the branch has NOT got is collected, not warned per row.** `QImportPanel` gained a
  `Review` slot — rendered at the check step, above the rows, with the parsed rows handed to it —
  and the roster puts a panel there: each missing class once, with how many students are in it, an
  **Add** button, and a picker of the nearest existing names. "Nearest" is a shared prefix of two
  characters, so `S1A` offers `S1` and not the school's other forty forms.
- **ADD is not the only answer, which is why MAP is offered beside it.** A roll that says `S1A` when
  the list says `Senior 1 A` is not a new class; adding it would leave the school with two classes
  for one form, and every register, timetable and class-teacher assignment then has to pick one.
- **The mapping is applied on the way OUT**, in `StartRosterImport`, with `with` on an init-only
  record — the rows the preview showed are the rows that were checked, and backing out costs
  nothing.
- **Adding a class RE-READS the vocabularies first.** `UpdateVocabularies` writes the blob OVER the
  stored value, so sending only the new class would delete every house, dormitory and room. That is
  the standing trap on that endpoint.
- **There is no per-row warning left.** Repeating it was the original report.

Verified by **`scripts/e2e/browser/import-classes.mjs` (14 checks, in `all.mjs`)**. It **builds its
CSV from what the branch actually has** — it finds a configured class shaped like `S2A`, writes it
back as `s2 a` and `S2-A`, and asserts neither appears as missing — so it cannot pass vacuously
against a tenant whose list differs, and it says so and skips when there is nothing to fold against.
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
per-domain issuance restored as the branch underneath the coverage check. **That build landed the same
day; its rules are below.**

### THE HELPER IS NOT REACHED BY SUDO, AND NEVER COULD BE (found in production, 2026-09-21 night)

`dashboard.maryhillug.net` was reported as not working. DNS was right, the TXT record was published
and correct, the build on the box was current, certbot was installed, and the API's own log carried
the answer four times over:

    [06:27:41 INF] Domain dashboard.maryhillug.net verified for organization 4d1ee83a…
    [06:27:41 ERR] Domain helper issue failed (exit 1): sudo: The "no new privileges" flag is set,
                   which prevents sudo from running as root.

**The API unit sets `NoNewPrivileges=true` and the activator escalated with `sudo`. Those two cannot
both be true.** That flag exists precisely to stop a setuid binary — sudo is one — from gaining
privilege, so the sudoers drop-in the package installed could never be used. The design was
self-contradicting from the day it was written, and **no suite could see it**: they run with
`CustomDomains:SkipCertificate=true`, where the helper is never invoked at all.

- **The fix is NOT to relax the flag.** Weakening an internet-facing process's hardening to reach a
  setuid path, for one call a tenant makes once, is the wrong trade on a box that also runs ERP,
  CashBook and the rest. **The web process no longer escalates at all.**
- **`CustomDomains:SpoolPath` (`/var/lib/qmgr/domain-spool`, root:www-data 0770) is the channel.**
  The API writes `<id>.req` holding `"<action> <domain>"` and polls for `<id>.res`;
  `qmgr-domain-worker.path` notices the file and runs `qmgr-tenant-domain --drain` as root. The
  answer's first line is `exit=<n>`, and **an answer that does not start that way is read as a
  failure** — the one thing worse than a domain that will not go live is one reported live that is
  not. The result is written under a temporary name and moved into place, because the API is polling.
- **`--drain` re-enters the helper per request** rather than reimplementing anything, so the domain
  validation stays where it belongs: on the far side of the privilege boundary. Proven locally —
  `enable not-a-domain` answers `exit=2 refused: 'not-a-domain' is not a valid tenant domain`.
- **The grant is strictly narrower than the sudoers entry it replaces**: a compromised API can cause
  exactly this helper to run, and no other setuid binary on the box. `install.sh` REMOVES the old
  `/etc/sudoers.d/qmgr-tenant-domain` — a grant nothing uses is a grant nobody reviews.
- **Prove it on the server without the app**, which is the check that would have caught this:

      echo "enable dashboard.maryhillug.net" > /var/lib/qmgr/domain-spool/test.req
      sleep 5 && cat /var/lib/qmgr/domain-spool/test.res

  The systemd half can only be exercised there; the drain itself is testable anywhere.

### The hybrid, and the order inside it

`/usr/local/bin/qmgr-tenant-domain` (generated by `build-linux.ps1`, reached through the spool and
the root worker above — never sudo) decides per domain, and **the order is the whole design**:

- **Covered by the installed certificate → the FAST PATH.** Nothing is issued, nothing renews and
  nothing can be rate-limited. A tenant on a subdomain of the platform's base domain goes live in one
  reload. This is the common case and the morning's work is exactly what serves it.
- **Not covered → a certificate of its own**, over http-01. A tenant's own domain can never be on the
  platform's certificate, so this is the only way `dashboard.maryhillug.net` reaches a padlock.
- **The shared certificate is only a CANDIDATE.** Missing or expired is no longer fatal: a domain it
  could not have covered was always going to be issued one of its own.
- **Taking a domain live behind a name mismatch is the one outcome neither branch may produce**, so
  every failure names its step and stops.

**Q-Mgr still installs nothing.** If certbot is absent the helper **refuses** — naming both ways out
(install an ACME client, or add the domain to the shared certificate) and listing the names that
certificate actually answers for. The standing no-server-dependencies rule is intact, and it is not
hypothetical that the box already has one: `admissions.maryhillug.net` carries its own Let's Encrypt
certificate on that same host, read off the live internet on 2026-09-21.

### The rules that are easy to break

- **THE CHICKEN AND THE EGG, and it is the reason for the two-phase enable.** http-01 asks for a file
  over **port 80 at that hostname** — and the server block that would serve it is the one the helper
  is there to write. So it writes the **port-80 half first, reloads, issues, then rewrites the whole
  block with TLS and reloads again.** Without that, a FIRST issuance can never succeed while renewals
  always would, because by then the block exists: the shape of bug that passes every test and fails
  on the first real customer.
- **A failed issuance leaves NOTHING behind.** The conf is removed and nginx reloaded, because a
  stranded port-80 block answers 503 for a domain that is not live — which reads worse than the
  domain simply not resolving yet.
- **There is no wildcard `server_name` anywhere and there must not be.** Every tenant domain,
  subdomain or not, gets its own block in `/etc/nginx/qmgr-tenants`. A wildcard on a box shared with
  ERP, CashBook and the rest would quietly catch hostnames belonging to somebody else. (A wildcard
  server block was the obvious shortcut for the covered case; this is why it was not taken.)
- **The back-off must not be deleted a second time.** `--keep-until-expiring` makes a repeat run a
  no-op rather than a request against Let's Encrypt's five-duplicates-a-week limit, but that only
  covers a SUCCEEDING domain; the caller's own back-off — daily sweep, skip anything tried within
  the hour, give up after seven failures, a human pressing Check now never throttled — is what stops
  a FAILING domain spending the box's budget for every other tenant. Five failed validations per
  hostname per hour is the real ACME number.
- **`--cert-name` and Q-Mgr's own webroot (`/var/www/qmgr-acme`) keep this clear of whatever else
  renews on that box.** Q-Mgr must not become a second thing editing another application's renewals.
- **Validation lives in the helper, because that is the privilege boundary.** Lower-case letters,
  digits, hyphens and dots; at least three labels (an apex cannot be CNAMEd, so it is never a tenant
  domain); and never the platform's own host, which would overwrite the real site's server block.
- **The proxy rules are DUPLICATED into each tenant block, not `include`d.** A shared fragment would
  make a change to the main site silently change every tenant's site too.
- **The reported expiry is the certificate ACTUALLY serving that domain**, not the shared one. Reading
  the shared certificate on both branches reports the platform's expiry for a tenant served off its
  own — real, confident and about a different certificate (fixed 2026-09-21, found while writing this
  section). It is still **logged and never stored**, for the morning's reason.
- **Withdrawal removes the server block only.** An issued certificate is left in place and left
  renewing: withdrawing is usually temporary, a kept certificate makes re-enabling instant, and
  revoking would spend the issuance budget again on every re-enable.

### Proving ownership is not the same as pointing the domain here

Until 2026-09-21 the panel asked for a **TXT record and nothing else**, so a tenant could prove they
owned a hostname that went on resolving to their old web host — and the first anybody heard of it was
a certificate that could not be issued for a domain nobody could reach. `CustomDomainStatusDto` now
carries `RoutingRecordName`/`RoutingRecordValue` beside the TXT pair, and verification checks the name
actually resolves here (`PointsHere`, `RoutingHint`).

**`PointsHere` WARNS, it never refuses**, and `null` means the check could not be made — which is not
the same as `false` and must not be shown as a problem. DNS propagates for up to a day and a
split-horizon or CDN answer can be legitimately different; the real gate is the certificate step,
which fails with its own reason. Refusing on a stale lookup would block a tenant who had done
everything right twenty minutes ago. Same call as the sign-up duplicate check.

**Still unexercised, and stated rather than claimed: no certificate has ever been issued by this
path.** The suites run with `CustomDomains__SkipCertificate=true` (Development only); the helper was
verified by rendering it out of `build-linux.ps1` and `bash -n`, never against a host. B7 in the
completion plan is the run that would change that.

## Staff self-service: what a teacher may take, and what they may only ask for (built 2026-09-21)

Asked for as *"creating a provision for staff to make configurations will greatly reduce on the
administration workload ... classes taught and lesson times on time table ... if the time is already
allocated on the time table, it should not be available for any other user, and in this case, bigger
role can make the adjustment"*. The plan is the artifact linked from `docs/TASK_TRACKER.md`.

**THE RULE THAT DECIDES EVERYTHING ELSE. A live `ClassTeacherAssignment` with
`Role = SubjectTeacher` is not a record of who teaches what** — `StudentScopeService` reads it and
grants `StudentAccessTier.Teaching` over every child in that class, and a Teaching-tier caller may
file a welfare record about them. So **declaring a class you teach is a DATA-ACCESS GRANT** and can
never be self-approved, however aggressive the validation around it. Claiming a free period for a
class you ALREADY hold grants nothing new, because the access came with the assignment. That one
asymmetry is the whole tier split:

- **Tier 1 DECLARE** — reaches nobody else. Unavailability, teaching preferences. Instant.
- **Tier 2 CLAIM** — can collide, cannot widen. Instant, but only into a slot that is provably free,
  inside an assignment already held, on a **Draft**.
- **Tier 3 REQUEST** — widens access, or collides. Decided by somebody else, never by the asker.

**ONE QUEUE and one decider: a holder of `timetable.manage`** (user decision, 2026-09-21), which
among the seeded roles is **Director of Studies and Academic Assistant** — not Head of Department,
and not Teacher, which holds only `timetable.lessons.flag`. **The direct write paths did NOT move:**
`POST …/class-teachers` keeps `classes.teachers.manage` and `POST …/timetables/{id}/lessons` keeps
`timetable.manage`. What changed is who may approve somebody *else's* request.

- **THE ASKER CAN NEVER BE THE DECIDER, and it is checked on the decision path, not at the
  permission layer.** A Director of Studies holds `timetable.manage` and also teaches, so the
  permission says yes on their own request and `StaffSelfService.RefuseDecision` is what says no.
  `CanIDecide` carries the same rule to the client so the button is absent rather than refused.
  NIST SP 800-53 AC-5; "nobody marks their own register entry" and "a meeting cannot adopt its own
  minutes" are the same rule already in this codebase.
- **A swap cannot be decided until the other teacher has agreed.** Automating the corridor
  negotiation would not replace it, it would take it away from the people having it. And a swap
  swaps the **slots**, never the teachers: swapping teachers would move a class to somebody not
  assigned to it, which is a data-access change wearing a timetable change's clothes.

### Nothing new was invented that already existed

- **`TimetableChecker` stays the one home for a clash rule.** Its Hard/Soft split is the timetabling
  literature's, and the self-service grid, the master's diagnosis and the nightly sweep all read it.
- **Unavailability and preferences live in `Branch.Settings["Timetable"]`**, beside the
  unavailability list that was already there — no new table and no new blob key. The checker already
  loads that blob, so they cost no extra query, and they are written under `BranchSettingsLock`,
  which is the standing rule for that column's fourth writer.
- **`UnavailabilityReason` is a SHORT LIST, never free text.** "Hospital appointment, Thursdays"
  typed into a note is health information about a member of staff, and once it exists it has to be
  gated, retained and eventually blanked like anything else. A category tells the timetable
  everything it needs.
- **The school's dials are in `Organization.Settings["StaffPerformance"]`**, through
  `IStaffPerformancePolicyService` — the one reader that already takes the organization lock —
  rather than a fourth writer of `Branch.Settings`. **`Enabled` is false by default**, the same call
  as `StaffOnboardingPolicyDto.JoinEnabled`.

### The rules that are easy to break

- **A self-service write takes the SAME advisory lock the master takes** (`pg_advisory_xact_lock` on
  the timetable) and re-reads inside it. That lock is what makes the class and room checks safe at
  all — they are code checks, not unique indexes — so a fifth write path that skipped it would
  remove the protection from the other four.
- **A TEACHER MAY NOT CREATE A CLASH A MASTER MAY.** `AddLesson` refuses only a *teacher* clash and
  leaves class and room to the publish gate, deliberately: a master builds a draft, holds a clash
  for a minute and fixes it. So no unique index was added for class or room — it would refuse a save
  the master is allowed to make. The **claim path** refuses both instead, and offers the request
  that would resolve each one, so the answer is "ask" rather than "no".
- **The fingerprint is what makes a "pick a free slot" interface honest.** The grid carries a hash of
  the lessons it was built from; a claim sends it back, the server re-reads under the lock, and a
  stale claim is refused **with the fresh grid attached** rather than applied to a slot the teacher
  never saw. It hashes ids and slots and never names, so a master and a teacher reading the same
  grid produce the same fingerprint.
- **POSTGRESQL TREATS NULLS AS DISTINCT IN A UNIQUE INDEX.** The duplicate-request index was first
  written across the seven payload columns and **never fired for a `ClassAssignment`**, which carries
  a null `CycleDay` and `PeriodKey` — four simultaneous identical requests made four rows. It is on
  a computed, never-null **`DedupeKey`** column now. A single column rather than `NULLS NOT DISTINCT`
  because it needs no minimum PostgreSQL version and the rule can be read instead of inferred.
- **A teacher sees their own name and nobody else's.** A slot a colleague holds reads "taken" — no
  name, no subject, no class. Names are gated on `timetable.manage`, the same single code that gates
  building the timetable. The grid reports the **least revealing true reason** a slot cannot be used:
  the caller's own declaration before the class being busy, the class before a colleague.
- **A self-service write replaces only the caller's own lines.** `DeclaredBySelf` is what lets it
  leave every line the timetable master entered exactly as it was — a teacher clearing their
  declarations must not quietly undo a constraint the school put on them.
- **Approving a class assignment is recorded at the Confidential rung**, because that is the act that
  grants access to a class of children.
- **An approval the world has moved under becomes `Superseded` with the reason**, never silently left
  Pending: "what happened to my request" always has an answer.

### My Workspace is a hub now, and the hub IS the lazy loading

`/portal` was 1,213 lines and one scroll of sixteen cards behind a single `GetPortalAsync()`
returning nine collections. Four tabs — **Today · My teaching · My performance · My file** — and a
section renders only while its own tab is open, which is the rule the Staff and Administration hubs
already follow. On Blazor Server every card's render tree is diffed and shipped down a SignalR
circuit, so the cost was paid on the school's connection whether or not anybody scrolled that far.

- **The greeting, the score ring and the stat tiles stay ABOVE the tabs**: they are the page's
  summary, not one tab's content.
- **`/my-day` is deliberately NOT folded in.** "My Day → My School Day because that page is
  date-scoped and a name without the day in it loses what the page is organised around" is a recorded
  user decision, and six API-side callers name that route.
- **The interface is the validation.** A taken period is never offered, a class the caller does not
  teach cannot be chosen, and a control the server would refuse is not rendered. A form that accepts
  anything and refuses afterwards is how people learn to stop trying.

### Verifying it

`scripts/e2e/self-service-e2e.mjs` — **section 21, 38 checks**, wired into `class-teacher-e2e.sh`.
Node, because the whole risk is concurrency: it fires the same claim from five callers at once and
the same request from four, and checks that exactly one row appears. It **seeds what it needs** — a
subject-teacher assignment and a draft timetable — and removes both.

**`PUT /api/v1/staff/policy` is NOT branch-scoped**: it resolves the organization from the caller's
tenant CONTEXT, while every self-service route resolves it from the BRANCH. For a tenant user those
agree; for a platform SuperAdmin they do not, so the policy saved and read back on while every branch
route went on seeing it off. Section 21 signs in as a **tenant administrator** for that reason.

**A suite must force the state it asserts.** 21.1 forces self-service off rather than assuming it;
the first version measured whatever the tenant happened to have, so one run that threw before its
cleanup left the next run reporting a product bug that was not there.


## Every notification names ONE person — there is no school-wide row (2026-09-23)

Reported from Maryhill with a screenshot: a teacher onboarded that morning opened the bell and read
*"The payment of UGX 120,000 for Communication did not go through."* The note above this one called a
recipient-less notification "latent, nothing creates one today". **It was wrong, and had been for
days:** `PaymentLedger.NotifyAsync` and both module-trial notices in `BillingJobs` built a
`CreateNotificationRequest` with no `UserId`. Then three things made that a leak:

- **Every reader took `UserId == null` as everybody's** — the bell, the count, the centre, Mark all read.
- **The live push sent it through SignalR's `Clients.All`** — every signed-in browser on the PLATFORM,
  every tenant. A payment failure at one school rang the bell at every other school with a page open,
  and vanished on reload, which is why nobody reported that half.
- **One read flag for the whole school**: the first teacher to clear the bell cleared the bursar's warning.

**The rules now, and each is enforced rather than hoped for:**

- **`CreateInAppNotificationAsync` THROWS without a recipient**, and `Notifications.UserId` is `NOT NULL`
  (`20260923050333_NotificationsNameOneRecipient`, which deleted the 414 recipient-less rows on the dev
  tenant rather than guess who they were for — user decision D1). Several people = **`NotifyManyAsync`**,
  one row, one push and one preference decision each.
- **`NotificationAudience.HoldersAsync(org, permission, branchId?)` is the ONE home for "who hears about
  this"**: role AND post holders, active, not the platform account, employment not ended.
  `StaffLookups.UsersWithPermissionAsync` delegates to it — it used to read the role alone, so a head of
  department never received the `staff.reports.view` notices their post lets them read.
- **Billing notices go to holders of `billing.view`** (D2) under `NotificationEventKeys.BillingPayment` /
  `BillingTrial`. `NotificationEventDefinition.Permission` keeps those rows off a teacher's preferences
  page — offering a switch for "a payment for this school" says such notices exist.
- **There is no `SendToAllAsync` and no branch-wide notification push.** `SendToUserAsync` only. The
  branch groups remain for the visitor board. The Web client also DROPS a push whose `UserId` is not the
  signed-in person, and reconnects when the signed-in user changes on a circuit.
- **Read, mark, delete and count match `UserId == caller`, full stop.** Mark-read and delete are single
  conditional statements, so a colleague's row is simply not found — 404, same as a missing one.
- **The unread-count push carries the moment counting began** (`UnreadCountUpdated(count, ticks)`), and
  the client keeps the newest. Two notifications landing together could otherwise leave the badge one short.
- **`POST api/v1/notifications` requires a recipient** (400), takes the organisation from the recipient's
  own row, never the request, and answers 404 for a person in another tenant.
- **The "delivery is failing" alert** goes to every `notifications.manage` holder through the ordinary
  service (it wrote rows directly to five arbitrary administrators, so nothing was pushed), once per
  channel per six hours under an advisory lock, and **names the failed notification's category, never its
  title** — the title was another person's notification, and could be a welfare record's.
- **Trial reminders are CLAIMED before they are sent** (`OrganizationModule.TrialReminderSentAt`, a
  conditional UPDATE), and the trial-ended lock-out claims the status change the same way, so a retried
  job cannot tell the same administrators twice. A payment notice was already once-only: `SettleAsync`
  reaches `NotifyAsync` only on a real change of status, under the payment's own advisory lock.

**`scripts/e2e/notification-recipient-check.mjs` is the guard** (in `guards.sh`; it reports all eight
faults in the commit before this one). **Section 27 (`notification-routing-e2e.mjs`, 39 checks)** drives
a refused purchase through the gateway stub and asks who heard — in the list, in the count, and LIVE, over
a bare SignalR client — including a signed-in platform account in no tenant, which must hear nothing, and
five settlements racing one payment. It borrows the two `e2e.teacher.s*` accounts (the dev tenant is over
its user cap) and puts them back. **Not exercised live:** a head of department actually receiving a
post-derived notice — every sender of one is a weekly or monthly sweep that needs a week of lesson data.

## A school chooses how a name is written; `PersonNames` is the one home (2026-09-23)

*"Why is the name display starting with first name? Is it a configuration? Where is the ui?"* It was not,
and there was none: `User.FullName` and about seventy hand-written `$"{FirstName} {LastName}"` fixed the
order in lists, notification text, emails, exports and error messages.

- **`Organization.Settings["People"]` → `PeopleNameSettingsDto { DisplayOrder, SortOrder? }`**, read only
  through `PersonNames` (API). **Default: given name first** (D4) — nothing moved until a school chose.
  **`SortOrder` null = follow the display order** (D5); a school may show "Agatha Ayebare" and still file by surname.
- **`PersonNames` has a static face** for the same reason as `UploadLinks`: names are written in static
  mappers, EF projections and the `User.FullName` getter. `PersonNameSettingsCache` (a singleton,
  attached in `Program.cs`) holds each organisation's choice for five minutes and is dropped on save.
- **The server writes the name, not the browser**, so a screen, an export and an email agree. The Web
  assembles one only in the staff import preview, through `IPeopleNamesService` + `PersonName.Join`.
- **An EF projection selects the two halves and formats after materialisation**; a search matches
  BOTH orders (`StaffRecordsController`, marked `// name-format: search`); a typed teacher name in a
  timetable import matches either order through `PersonNames.Matches`.
- **Person DTOs carry `SortName`** (`UserInfo`, `UserDto`, `StaffMemberDto`, `RegisterRowDto`,
  `OnboardingStatusRowDto`, `TemporaryPasswordSlipDto`, `JoinRequestDto`), and every Web list of people
  sorts on `SortName ?? FullName`. `JoinRequestDto` also gained a server-written `FullName`.
- **Students are not affected** — one whole name, never split (a standing decision), so there is no order.
- **The screen is Settings → General → People's names**, with a two-name preview; saved by the page's own
  Save button through `PUT api/v1/organizations/{id}/people-names` (`settings.edit`).
- **`scripts/e2e/name-format-check.mjs` fails the build on a new hand-written name** (it finds 59 in the
  previous commit). Section 28 (`name-order-e2e.mjs`, 22 checks); the browser half is in `controls-and-names.mjs`.

## `Organization.Settings` has ONE writer: `OrganizationSettingsLock.MutateAsync` (2026-09-23)

Found while adding the `People` key. The 2026-09-18 audit made `organization-settings:{id}` "the one key
they all share" — and **one writer took it**. The staff policy and staff onboarding kept a second key
(`staff-policy:`), and **industry settings, feature overrides, visitor retention and the Stripe module
billing key took no lock at all**, so any of them could silently drop another's key. The staff monthly
summary saved back the WHOLE policy it had read at the start of its run, overwriting a closure made meanwhile.

- **`MutateAsync(db, orgId, org => …)`**: takes the one lock, re-reads (reloading a tracked copy), mutates,
  saves. Joins an open transaction; advisory locks are re-entrant, so the staff policy editor can hold it
  around its own read-modify-write. **Change other organisation columns INSIDE the mutation** — the re-read
  discards edits made before the call. **`WithKey(json, key, value)`** is the merge every writer hand-rolled.
- `IStaffPerformancePolicyService.MarkSummarySentAsync` replaces the sweep's whole-policy save.
- Section 28 races ten interleaved saves of two keys and checks both survive.

## Every tick box is `QCheckbox`, every on/off setting is `QSwitch` (2026-09-23)

*"The checkboxes look like plain bootstrap."* Sixty-five raw `<input type="checkbox">` across twenty-one
files: forty Bootstrap `form-switch`es on eleven settings pages and twenty-five browser tick boxes.

- **`QSwitch` is new** (`Components/Shared/UI/QSwitch.razor`): same parameters as `QCheckbox`, a real
  `<input role="switch">`, the tenant's `--qm-primary` on the track. **A switch when flipping it changes the
  setting; a tick box when it chooses** (rows to act on, an option in a form that is then submitted).
- The forty converted switches carry the setting's visible label as `aria-label` — the Bootstrap ones had
  no programmatic name at all.
- **`QCheckbox` gained a keyboard focus ring** (it had none — the input is visually hidden) and a **24px
  minimum target** (WCAG 2.2 SC 2.5.8), moved from the one page that had it. `q-checkbox-row--top` aligns
  the box with the first line of a paragraph label.
- A page's own `(ChangeEventArgs e)` handler for a checkbox became `(bool on)`, never a faked event.
- **`scripts/e2e/raw-checkbox-check.mjs`** fails on any raw checkbox or `form-switch` outside the three
  shared components. `controls-and-names.mjs` presses them with real mouse input on five pages.
- **Radio buttons followed on 2026-09-23** — see "Every one-of-a-few choice is `QRadioGroup`".

**`component-param-check.mjs` was blind to lowercase attributes, and that cost a broken page.** It
skipped them as "an html attribute, never a parameter" — but Blazor hands EVERY attribute on a component
to it as a parameter, case-insensitively, and one that captures no unmatched values throws on first
render. `<QSelect id="…">` passed the build and the guard and left Settings stuck on "Loading settings…".
It now checks lowercase names too, case-insensitively, and reads past an implicit expression's call —
`"@errors.Contains("x")"` — whose nested quotes it used to take for the next attribute.

## School calendar, the programme import and gates (built 2026-09-23)

The plan is `docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md` (artifact linked at its top); decisions D1–D12
were settled on the recommendations. **The five source documents live in `D:\QMGR\DATA` and carry staff phone
numbers — they are never copied into this repository**; the suites read them through `E2E_DOCS_DIR` and skip
without it.

- **Events are `SchoolEvent`, never a kind of duty** (D1): a duty carries a `ParameterId` and every register,
  reminder and scoring query assumes staff are marked on it. A meeting that is also an event is ONE row in each,
  linked by `SchoolEvent.DutyId`. **Who sees an event has one home, `SchoolEventVisibility`**: a
  `calendar.manage` holder sees all; anyone else sees Staff-audience events, events naming them, or naming one of
  their departments. Students/Guardians audiences are stored and shown to nobody yet (D5). The personal feed and
  My School Day use the personal rule, never the manager override.
- **`calendar.manage` is in all three catalogues** (Manager, DoS, Academic Assistant; Admin by "all"). Reading
  needs no code. The calendar is base product; importing meetings or rota slots additionally needs
  `staff.duties.manage` and the Welfare & Performance module (D8).
- **The feed is RFC 5545 written by hand (`IcsWriter`)** — CRLF, 75-octet folding without splitting UTF-8, §3.3.11
  escaping, all-day `VALUE=DATE` with an EXCLUSIVE `DTEND`. The link is a 160-bit secret shown once; only its
  SHA-256 is stored (`User.CalendarFeedTokenHash`), the document-share rule.
- **Documents are read with no dependency.** `.docx` by `ZipArchive` + `XmlReader` honouring `gridSpan`/`vMerge`;
  `.doc` by a hand-written [MS-CFB] + [MS-DOC] reader (piece table for text, PAPX `sprmPFTtp` for ROW ends — a
  0x07 on a TTP paragraph ends a row, any other 0x07 a cell); PDFs by pdf.js runs in the browser; scans refused
  (D3). The readers also feed the EXISTING `QImportPanel`, so staff and student imports take Word and PDF tables.
- **The weekday is a check digit.** All 47 weekday+date pairs in the school's documents agree, which is how
  "23rd–29th November 2025" is read as 2026 and SAID. A suggestion is only ever a one-edit date change that fills
  a gap and creates no new problem (the real rota yields exactly one: Musiime Naomeh 02/11→02/12), and it is
  offered, never applied.
- **The server re-checks every import row**; preview and commit take the same body, so what the preview said is
  what the commit does. Re-importing an unchanged document creates nothing (`SourceKey`). The batch is a
  `RosterImportJob` of Kind `Programme`; undo removes what it created EXCEPT a duty whose register was taken.
- **A question the reader must answer is asked where it arises, never only at submit.** A meeting whose
  attendance resolves to nobody on the staff list was refused at "Check with the server" after the page had said
  every question was answered — found by the browser suite on the dev tenant (whose staff are not Maryhill's).
  It now counts against the Check button and offers "Import as an event only" on its own row.
- **Gates**: `BranchVocabulariesDto.Gates`, ONE writer (`PUT …/visitors/gates`), `UpdateVocabularies` keeps them,
  a used gate is retired never removed, and `VisitorGateRule` (Shared) is the one rule both sides run.
  `Visitor.EntryGate/ExitGate` store the NAME it had that day; `CheckedInByUserId/CheckedOutByUserId` record who
  — until 2026-09-23 a visit recorded no person at all.
- **Bulk check-out never set `Status`** (found while building gates): `CheckedOutAt` was written and `Status`
  stayed `CheckedIn`, so the visitor read as on site everywhere AND could never check in again (the partial unique
  index allows one CheckedIn visit per profile). Fixed, and its undo leaves the old visit closed if the person has
  since come back.
- Verified: sections **29** (gates, 59), **30** (calendar, 107), **31** (programme import, 80 — the counts measured
  from the real documents asserted by the product code); browser suites `visitor-gates` (22), `calendar-ui` (42),
  `programme-import` (24 with `E2E_COMMIT=1`).

## The user limit counts ACTIVE people, and every way back in asks first (2026-09-23)

The dev tenant sat at 260 of 250 and about 65 API checks failed at their first "create a user" step. That was
a product defect, not test clutter: `UsageTrackingService` counted every user row, while the product's only
way to remove a person — `DELETE /users/{id}` — is a SOFT delete that sets `IsActive = false`. A school that
removed a teacher who had left never got the seat back; once full, it was full for good.

- **The count is active people, not pending join requests** (`u.IsActive && u.PendingApprovalAt == null`).
- **`UserSeats` (API `Application/Services`) is the one home for "is there room for n more?"** Counting active
  makes re-enabling an ADD, so every path that turns somebody on asks it: the Users & Roles toggle (402,
  `LIMIT_EXCEEDED`), the bulk "enable accounts" batch (refused in the RESOLVER, so the preview says it, the run
  refuses it and the Hangfire job — which re-resolves — cannot slip past), undoing a bulk DISABLE (refused whole,
  before a write), `POST …/staff/structure/members` (`[CheckLimit("users")]` — it had no limit check at all), and
  the staff import (room read once and counted down per account, because it saves in chunks; rows past it fail by
  name). **A new path that activates a user and does not ask `UserSeats` is a way round the limit.**
- **Disabling never needs room.**
- **The edit form's "User is active" tick box did nothing**: bound, never sent, since the PUT has no active field.
  It now goes through the toggle and shows the refusal; on a new account it is not shown.
- Verified by **section 34 (`user-seats-e2e.mjs`, 19 checks)**, which registers a scratch tenant with a 10-user
  limit, fills it, drives every path above, and purges the tenant — there is no endpoint to lower a limit.

## Every list pages through `QPager`, and the roster was never paged (built 2026-09-23)

Plan `docs/plans/STUDENT_ROSTER_AND_LIST_STANDARD.md`; decisions L1–L7 settled on the recommendations.

- **`QPager` is the one pager** and now takes `PageSizeOptions` (`QPagerDefaults.Sizes` 10/25/50/100), `AllowAll`
  (`QPager.All == 0`), `PageSizeChanged`, `StorageKey` (remembered per list per browser), `SelectedCount`,
  first/last and a page jump. `QPaging` is the page-side helper; **`Reset()` on every filter, search, size and
  branch change**. "All" above `QPaging.VirtualizeAbove` (200) renders through `<Virtualize>` so it never freezes.
  Server-paged lists (staff activity, staff records, notifications, tenants) get sizes but NO All — their API caps
  a page at 200, so All would silently be one page. **`pager-check.mjs` fails a list page whose pager offers no size.**
- **The list-page audit was fooled by `.Take(`.** It counted any `.Take(` as paging, and the roster's
  `s.Flags.Take(3)` (the first three flag chips) made a page with NO paging pass the sweep. It now accepts only
  `QPager`, `<Virtualize` or `QTimelinePaging`. A detector that matches a substring is measuring the source, not
  the behaviour.
- **`GET …/students` silently returned the first 100 by name** (default `limit=100`, clamp 500, and no caller ever
  passed one). The roster showed 100 of Maryhill's 1,711 with nothing saying so, and the welfare timeline — which
  finds its own student in that list — lost the guardians and the other-students picker for every child past the
  hundredth. It returns the whole roll now (`WholeRoll` 10,000). **A list endpoint that truncates without saying
  so is the bug; paging is the page's job.**
- **The roster**: filters (class with counts via `ClassName.Key`, status, house, dormitory, guardian, flags) live
  in the address; summary tiles apply their filter; `QBulkBar` replaced the hand-built bar; "Select all N
  matching" is an explicit second press and every filter change prunes the selection; class teachers open on
  their own classes (from `WelfareSummaryDto.ScopedToClasses` — a roster field would be cleaner).
- **Bulk log (`POST …/welfare-records/bulk`)**: the single create's body is `BuildRecordAsync` +
  `ResolveStudentForWriteAsync`, called by BOTH paths, so a rule added to one binds both. One transaction, cap
  200, one out-of-scope student refuses the whole batch with the unknown-student wording. **Welfare case type is
  never bulk** (L7). One incident = Behaviour only. Alerts are coalesced (`NotifyRecordsLoggedAsync`): one message
  per recipient. No guardian messages (there is no per-category "tell guardians" setting; the single create never
  messaged guardians either). The staff system award is credited once per batch, not per record.
- **Tidy names** (`POST …/students/tidy-names`, students.manage, pastoral scope): `PersonName.FixShouting`, and a
  name with ANY lower-case letter is skipped outright.
- Verified: section **32** (bulk log + tidy names, 60/0), browser `pager` (40/0), `student-roster` (31/0 — it
  seeds 60 scratch students and deactivates them; a list that fits on one page proves nothing about paging).

## Exam supervision is a named series of duties, never a second timetable (built 2026-09-23)

The Phase 3 the timetable-ownership plan left open. A series is the Session duties sharing a `SeriesId`, with
`StaffDuty.SeriesName` and `StaffDuty.SeriesManagerUserIds` carried on every row (no new table). The Duties hub's
**Exams** tab (`ExamSeries.razor`) and `StaffDutiesController.Series.cs`; section 35 and `browser/exam-series-ui.mjs`.

- **`DutySeriesAccess` is the one home**: a manager or a holder of `staff.duties.manage` writes; only the holder
  appoints (a manager asking to change managers gets 403 in words). Reading is managers, invigilators and holders;
  anybody else 404. The generic duty editor still refuses a manager — the series endpoints are their only door.
- **Managers are kept IN every slot's `RecorderUserIds`**, and a manager change rewrites that across the series
  (`DutySeriesAccess.Recorders`), so every existing reader of recorders — the register, the portal to-do, the chase,
  the digest — treats a manager as the person who takes the register without being taught about series.
- **Every sitting goes through `ApplyAsync`**, the rules a hand-made duty obeys; an error names the sitting.
- **A double-booked invigilator is WARNED, never refused**, and **each invigilator gets ONE notice per write**
  (`staff.invigilation-assigned`), never one per sitting.
- **A manager usually holds no duty permission, so the sidebar has no Duties entry for them** —
  `ExamSeriesCard` on My Workspace's My teaching tab is their door. A new delegated role needs a door, not just a rule.
- **A series' sittings NAME their invigilators** (the duty mapper named only rota slots' people). The browser suite
  found the Invigilators column reading "—" on every sitting.

## Employment type is the school's own list (2026-09-23)

`StaffEmploymentType` is gone: nothing branched on its six values, so by the behaviour test it is data.
`StaffPerformancePolicyDto.EmploymentTypes` (seeded with the six) and `User.EmploymentType` holds the NAME.

- **`EmploymentTypes.Resolve` is the one reader** (Shared) — the import preview, the job, the precheck and the profile
  save all resolve a cell against the ACTIVE list with it. `StaffFieldParsing.EmploymentType` was RENAMED to
  `EmploymentTypeName` because its meaning changed: a changed meaning behind an unchanged name compiles at every
  wrong caller (the `GroupFor` lesson).
- **A type somebody holds cannot be removed — the server refuses, naming it — only retired.** A person keeps a retired
  type on an unrelated save; nobody new can be given one.
- **The migration is add, backfill, drop, rename** (`ExamSeriesEmploymentTypesAndCatalogWording`). EF scaffolded an
  ALTER COLUMN from integer to varchar, which PostgreSQL does by implicit cast — every "Permanent" would have become
  "0". Checked on seeded rows before trusting it: 1 → Contract, 3 → Part time.
- The server now also refuses duplicate staff-group names; the editor had been the only check.

## Every one-of-a-few choice is `QRadioGroup` (2026-09-23)

Five pages drew radios five ways, three with Bootstrap's `form-check`. `QRadioGroup<TValue>` takes a list of
`QRadioOption(Value, Label, Description?)` — native radios sharing one name (arrow keys, "1 of 3"), the QCheckbox
family's look, a 24px target, a focus ring. `raw-checkbox-check.mjs` now fails a raw radio too; the one exception,
Register's module CARD picker, is named in the guard with its reason. A long or data-driven list is a `QSelect`.

## A Web client reads JSON with the app's options, or an enum breaks it AFTER the write (2026-09-23)

`ISelfServiceApiService` read with the framework defaults, which cannot read an enum sent as a string — and the API
sends every enum that way. "Ask a colleague → Send the request" therefore created the cover request and then told the
teacher it had failed. **`json-options-check.mjs` (in `guards.sh`) fails any `ReadFromJsonAsync<T>()` in `Services/`
without options.** Found by `browser/timetable-ownership-ui.mjs`, which also found that an **appointed timetable master
could not see Publish**: `TimetableDetailDto.CanManage` asked `IsUnscopedAsync` alone, while the write path had always
exempted a master (`NeedsUnscopedStaffView`). A rule the API applies must reach the flag the page reads.

## The school chain is seeded, and a PERSON has a gate as well as a role (2026-09-24)

*"seed head teacher and deputy head teacher roles. fix the display labels. a school may not use manager.
next to DOS is Deputy, then head."*

- **The order is Administrator → Head Teacher → Deputy → Front Office Manager → Director of Studies**
  (R1, user instruction "implement your recommendations and decisions", 2026-09-24). The head may assign
  Front Office Manager and takes the "manager or above" visitor override; the deputy may not change a Front
  Office Manager's account. Manager moved down only past the two roles seeded that morning, so no older pair
  of roles changed order. **The Web keeps its own copy of the order** (`IPermissionService.TierOrder`), and it
  lacked both school roles for a day — change both or neither.
- **Both permission sets live in `Permissions.cs`, read by BOTH seeders.** `DbSeeder` ADDS role
  permissions too, so two lists would hand the role their union. The head is derived:
  `HeadTeacherPermissions` = every visible tenant code minus `HeadTeacherWithheld` (billing, settings, API
  keys, role design, branch create/delete). A new school permission therefore reaches the head without
  anyone remembering. The deputy is an explicit list: the DoS's set plus welfare to CONFIDENTIAL (never
  restricted), the roll, class teachers, visitors and onboarding.
- **Labels: Manager → "Front Office Manager", Staff → "Front Desk Staff".** The codes are wire formats
  and did not move. `UsersSetup` picked a new user's default role by DISPLAY NAME ("Staff"), and now
  picks it by code.
- **THE PERSON-SIDE GATE.** `RoleAssignmentGuard` bounded the role being GIVEN; nothing bounded the
  person being CHANGED. So anyone with `users.edit` could demote a superior, switch them off, delete
  them, or re-point the email their password-reset link goes to. `UsersController.SubjectRefusalAsync`
  (update, toggle, delete) and `BatchOperationService.SubjectRefusalAsync` (bulk role and on/off, one
  Failed row per refused person) now refuse an account whose CURRENT role the caller could not have
  given. That is ResetPassword's rule; one's own account is exempt. The bulk role change also had no
  target-role guard at all, only a SuperAdmin check, and now runs `RoleAssignmentGuard`.
  **`IBatchOperationService.ResolveAsync` takes the actor**; the job passes `CreatedByUserId`.
- Verified by **section 36 (`school-roles-e2e.mjs`, 24 checks)**, wired into `class-teacher-e2e.sh`.
- **`PUT /users/{id}` REPLACES the branch and counter (found by the full run, 2026-09-24).** Names, phone and staff
  number are "leave alone if not sent", but `AssignedBranchId`/`AssignedCounterId` are always written — the edit
  form sends them every time and choosing "no branch" must be able to clear one. So a caller that sends only
  `roleId` takes the person OFF their branch. Sections 36 and 37 did exactly that to three `e2e.sp.*` accounts,
  and a teacher then opened the timetable on "Choose a branch". **Any role change through that endpoint carries the
  person's placement.** The endpoint was left as it is on purpose; a partial update would need a separate route.

## The RBAC review, built: posts that grant and expire, a review, leavers, governors (2026-09-24)

The review is https://claude.ai/artifact/QRHKYKfyvVxGPGGAF2c8wU; *"implement your recommendations and decisions"*
took all eight. Verified by **section 37 (`leadership-e2e.mjs`, 60 checks)** and **`browser/access-ui.mjs`
(12 checks)**, both wired in.

- **`Organization.Settings["Leadership"]` holds three kinds of post, and `LeadershipPosts` is its ONE reader and
  writer** (API `Application/Services`): the designated safeguarding lead and up to three deputies, an acting head
  for a dated period, and house/dormitory posts per branch. Writes go through `OrganizationSettingsLock`. Each is
  a POST in the `PostPermissionService` sense, so `GrantsForAsync` reads it and every one of the six readers of
  "what may this person do" follows with no change.
- **YOU MAY DELEGATE ONLY WHAT YOUR ROLE HOLDS.** Appointing the safeguarding lead or an acting head needs the
  appointer's ROLE (never their posts) to hold every permission the post grants — so the Administrator or the Head
  Teacher, and a deputy safeguarding lead cannot appoint another. The RoleAssignmentGuard rule, for posts.
- **Safeguarding lead**: holder must be `RoleCodes.SeniorLeadership` (Admin, Head, Deputy, DoS — an explicit
  list, because the front office ranks among them and is not the SLT; KCSIE). Grants
  `LeadershipPosts.SafeguardingLeadPost` including `welfare.restricted.view`, **whole school**:
  `PostGrants.HasOrganizationWidePost` makes `IsUnscopedOnStudents` true whatever the role's own scope.
  `GET api/v1/leadership` is readable by everybody — staff must know who the lead is.
- **Acting head**: a Deputy or the DoS (`ActingHeadEligible`), never oneself, end date REQUIRED and within 120
  days, a reason of ten characters. Grants exactly `HeadTeacherPermissions` **only while the period covers today**,
  checked on read, so it lapses on its own. The permission cache means up to five minutes' lag at a boundary.
  **`RoleAssignmentGuard` still reads the ROLE**, so an acting head cannot assign a role above their own.
- **House and dormitory posts** (`PUT branches/{b}/pastoral-posts`, `classes.teachers.manage`, Staff Directory's
  Houses tab): the class teacher's pastoral grant, reaching the students whose `House` / `DormitoryOrStream`
  match. `StudentScopeService.FilterByClasses` and `GetTiersAsync` match all three; a house post is always the
  pastoral tier. **They are NOT in `ClassTeacherAssignments`**: six listings key that table on ClassName (the
  roster of who teaches what, coverage, history, mine…) and a house would have leaked into all of them.
  `GetPastoralScopeLabelsAsync` is what the welfare summary's "these figures cover…" line reads now, so a
  housemaster is told "House Nile", not nothing. **Welfare ALERTS reach house and dormitory post holders too
  (2026-09-24)**: `WelfareAlertService.GetPastoralRecipientsForStudentAsync` (renamed from
  `GetClassTeachersForStudentAsync` when its meaning widened) adds them, and the alert's title names the HOUSE for a
  housemaster. It matches EXACTLY as `StudentScopeService` does (`Trim().ToLower()`), never more loosely: an alert
  names a child, and telling somebody the scope would not let open the record is a disclosure. (Both still compare
  classes that way rather than through `ClassName.Key`, so "S1 A" and "S1A" are two classes to the scope — a
  scope-wide change, deliberately not made in passing.)
- **`NotificationAudience.HoldersAsync` includes all three**, filtered through the same people query.
- **Every leadership write names the people on BOTH sides to `PostChangedAsync`** and tells them
  (`access.post-changed`); writes are logged at Confidential.
- **The leaver sweep** (`AccountLifecycleJobs`, `deactivate-leavers`, 02:15 UTC): an account whose
  `EmploymentEndDate` has PASSED is switched off, its class assignments ended, its department seats cleared and
  its leadership posts removed; account managers are told by name (`access.leavers-deactivated`). **An
  organisation's last active Administrator is never switched off** — named in the notice instead. Timetable and
  exam-series manager lists are left as they are; an inactive person in one can do nothing.
- **The access review** (`GET api/v1/access-review`, `users.edit` + `roles.view`; Users & Roles → Access): one
  row per active person with the six sensitive capabilities, computed from role plus every post in bulk (no
  per-person query). **Recording it** needs `users.edit` AND `welfare.restricted.view` effective — the
  Administrator, the Head or an acting head — a note of ten characters, and is an `access.reviewed` activity event.
- **`welfare.reports.aggregate`** (three catalogues): the welfare summary and cohorts ONLY, with `ByStaff` blanked
  for a caller who holds neither `welfare.reports.view` nor `.own`. Every other welfare report code also opens
  the record search, which names children. **The Board Member role** (`board-member`, below Support Staff) holds
  dashboard, notifications and this. **A figures-only reader's welfare SUMMARY counts Standard AND Confidential, never
  Restricted (2026-09-24)** — every Welfare-type record is forced to Confidential, so a board used to read zero welfare
  cases; counts by category with nobody named are what KCSIE expects the safeguarding lead to report to governors.
  **The cohort breakdown stays at Standard**: house × sex × residency cuts small enough to point at a child. A Board Member account is a user, so it appears in staff lists.
- **Front Office Manager and Front Desk Staff are offered only to a tenant holding Core Queue or Visitor
  Management** (`RoleCodes.ModulesFor`, which replaced `ModuleFor`). Visibility only; holders keep the role.

## A link is shown exactly when its page would open — one rule per destination (2026-09-25)

Reported with a screenshot: a teacher at Maryhill opened Administration → Settings and read the school's SMS gateway
configuration. Three places drew that link and gave three answers — the sidebar gated it on
`settings.view || notifications.view` (nine seeded roles hold the second), the phone sheet on `settings.view`, and
the user menu and the account page on nothing at all. The plan is `docs/plans/CLOSE_OUT_RBAC_AND_ACCOUNT.md`.

- **`Components/Shared/NavGates.cs` is the one home** for a destination more than one place links to: the Settings
  hub (whose sections live there and the hub renders), Billing, Kiosk, Counter Terminal, Appointments, Customer
  Display, Reports. Each rule takes the two synchronous predicates `MainLayout` builds once from the whole permission
  set (`HasCode`, `HasModule`); `MobileNav.Slot` carries a `Gate` for a rule that is not one permission, and Home's
  quick actions read the same rules. `HubTabs.AnyVisible` is the link-side twin of `HubTabs.VisibleAsync`.
- **Settings is for people who can CHANGE something there** (decision D1): `settings.edit` or `notifications.manage`,
  the Administrator by default. A read-only view of the school's messaging and integrations is nobody else's.
- **`nav-gate-check.mjs`** (guards) fails an admin/billing/platform link drawn with no condition;
  **`browser/rbac-links.mjs`** signs in as every seeded role, opens every link it is shown, and asserts the
  administrator-only destinations are absent for the rest.

### A view permission never gates a write, and a secret never leaves the API

- **`GET notifications/settings` returned the school's SMS key and password, SMTP password, Telegram and WhatsApp
  tokens in clear to every `notifications.view` holder.** It needs `notifications.manage` now, and
  **`Application/Services/SecretMask.cs` is the one home** for how a secret leaves and comes back: the eight-dot mask
  means "set", empty means "not set", and a save carrying the mask keeps the stored value. The platform settings
  controller uses the same constant. **A new secret property goes through it or it leaks.** The Integrations tab
  reads `settings/{org}/channels` (four flags) instead of the whole payload.
- **`view-on-write-check.mjs`** (guards) fails a POST/PUT/PATCH/DELETE whose only gate is a `*.view` code. It found
  ticket printing (`tokens.view`, and the request's printer address made the server open a TCP connection anywhere —
  it prints only to the stored branch printer now), feedback links and visitor badges. The one allowed exception is
  named in the guard with its reason (the restricted rung's single code).
- **A field a reader does not need is blanked by the server**: a colleague's national ID, date of birth and next of
  kin unless `staff.structure.manage` or `staff.confidential.view`; other people's phones on the account list unless
  `users.edit`; module prices, cycle and trial dates on `modules/mine` unless `billing.view`; customer names on the
  anonymous queue hub, always. Verified role by role by **section 40** (`rbac-settings-e2e.mjs`).

### My account is how you get in; My file is how the school knows you (2026-09-25)

- **`/profile` is "My account"**: photo, username, email (the one identity field a person maintains), password, the
  devices holding a session with "sign out everywhere", notification preferences, the calendar feed. Built from
  shared components; no page-owned header, no tiles. `IAccountApiService` is its one client.
- **A person's name is the school's** (D3): `PUT profile` ignores names; the staff directory changes them.
- **Contact detail has ONE writer, `PUT api/v1/profile/contact`, with no module requirement** — the portal route was
  deleted, because a tenant without Welfare & Performance has no My Workspace. **`MyDetailsCard` is the one
  component**: on My file with the employment half, and on the account page (contact half only) for a tenant without
  the module. **`PhoneConfirm` is the one confirm-by-code control** (the onboarding strip uses it too).
- **A confirmation belongs to a NUMBER** — `QMgrDbContext.ApplyIdentityNormalization` clears `PhoneVerifiedAt` when
  `Phone` changes to a different number, whoever writes it (self, an administrator, an import). It was one writer's
  rule, so a number changed anywhere else kept the old confirmation and SMS resets went to it.
- **The email check is on the canonical form** (`RegistrationIdentity.NormalizeEmail` against `NormalizedEmail`), so
  a variant of a colleague's address is refused in words instead of failing the unique index as a 500.

## Calendar audiences, notices, sound and the Import inbox (built 2026-09-26)

Reported from Maryhill: every past event on the Term view, a "Staff" box that meant everybody, nobody told of a new
event, no sound, and *"several uploads of the school programmes and duty, some meetings, are not appearing in the
registers"*. The plan is `docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md` (artifact
https://claude.ai/artifact/5Q4EZtkKWUKGCSEdGtARLT); E1–E12 were taken as recommended, and §6a lists the departures.

- **"Is this person in this audience" has ONE test: `StaffAudienceRule` (Shared), fed by `StaffAudience` (API).** An
  event carries `AllStaff` or groups / roles / departments / people. The group comes through `GroupFor` with its
  fallback, and leavers and the platform account are excluded. The calendar, the feed, event notices and Staff Notices
  all ask it. **`audience-home-check.mjs`** fails a hand-written staff-group comparison anywhere else.
- **Mine vs Whole school is a VIEW choice, not a permission.** `SchoolEventVisibility.CanSee` still decides what
  exists for a person. `AudienceOnly` events are hidden from everybody outside the audience, and a students-only
  event is openable but on nobody's Mine. Past events are hidden by default, with a switch. Every choice is
  remembered in `User.UiPreferences`, read and written only through `IUserPreferencesService`.
- **An event notice is exactly-once per VERSION.** A material change bumps `Version`, and `SchoolEventNotifier`
  claims `NotifiedVersion` with a conditional UPDATE before anything is sent. A typo fix is not material, so it tells
  nobody; a cancellation is. Edits race on the `xmin` row version (409, never a silent overwrite).
  `ReminderSubject.SchoolEventStart` rides the reminder ladder.
- **Sound is synthesised** (`wwwroot/js/notificationSound.js`, Web Audio, no file), throttled in the script, off in
  quiet hours, muted per device (`qmgr-sound-muted`). It always comes with a toast, because sound is never the only cue
  (WCAG 1.3.3).
- **B4: a preference save must COPY the record with `with`.** `NotificationPreferenceResolver.SaveAsync` hand-listed
  properties and dropped `PushEnabled`, so every save switched push back on. **`preferences-roundtrip-check.mjs`**
  guards both preference stores.
- **Why imported meetings made no registers:** a meeting defaulted to "event only" unless its title looked like a
  staff meeting, and the attendance words were never read. A meeting is now **Undecided** until somebody answers,
  and `AudienceTextResolver` reads the words, learning offices as aliases once approved. A **Registers panel** asks
  who takes each register. **Import health** on a past job lists every meeting without a register and why, with
  "Give it a register" (`POST …/calendar/events/{id}/register`), which links the event to its meeting so the two move
  together.
- **Undo keeps what a LATER import relies on (B13)**, but only when that import created or updated something of its
  own. A duplicate re-submission is the same import. Undo takes each register's lock before cancelling its duty.
- **`SchoolEvents.SourceKey` is unique per (organization, branch, key)** through a raw-SQL expression index on
  `COALESCE(BranchId, empty guid)`, so two whole-school events collide too. EF cannot model it; do not "tidy" it into
  a `HasIndex`.
- **The Import inbox (`/imports`) — nothing is written before approval.** A routed document is split into parts
  (events, meetings, rota, staff list, student roll, timetable). **Each part is approved by whoever may import that
  kind of record, through that kind's own importer**, so every rule still holds. The school may require a second
  approver (NIST AC-5). The approval is claimed under `import-inbox:{job}`, so two approvers cannot both commit.
  Student and staff parts open their own wizard pre-filled (`InboxHandoff`); a timetable part is download, then
  "Mark imported".
- **A page that reads `?x=` must BIND it with `[SupplyParameterFromQuery]`.** The inbox read its query in
  `OnInitializedAsync`. "Review and approve" is a same-route navigation, which never re-runs that, and Blazor gives
  new parameters only to a page with something bound to the query. Moving the read to `OnParametersSetAsync` alone
  did NOT fix it. Found by `browser/import-inbox.mjs`: opening the address directly worked, and only a real press
  on the button showed the fault.
- Verified: API sections **41** (`calendar-audiences-e2e.mjs`, 65), **42** (`import-routing-e2e.mjs`, 40), **43**
  (`import-inbox-e2e.mjs`, 30), with 30 and 31 still green. Browser: `calendar-scope` (21), `notification-sound` (12),
  `import-inbox` (14).

## Segregation of duties has ONE home: `DutySeparation` (2026-09-26)

An audit for the lesson-plan work found "the author cannot decide" written by hand in seven places and **missing in
seven more (G1–G7)**. The worst was in a live feature: an appraisal could be reviewed, moderated and signed by one
person, its own subject included. `Application/Services/DutySeparation.cs` is the rule now: **nobody decides on their
own work or about themselves, and nobody takes two stages of one item** (NIST SP 800-53 AC-5). `Refusal(...)` returns
the sentence a person reads; `Allows(...)` feeds the DTO's `CanI…` flag, so a button the server would refuse is never
drawn. A stage with nobody else to take it is SKIPPED and recorded, never handed to the author.

- **G1, appraisals:** the subject never reviews, moderates or signs; whoever wrote the review never moderates or signs.
  `StaffAppraisal.ReviewedByUserId` records who actually wrote the review, which may be an approver standing in.
- **G2–G3:** no performance record of any kind about oneself, and nobody annuls, re-scores or re-rungs one about
  themselves. The right of reply (`/respond`) stays open.
- **G4:** whoever last wrote the minutes does not adopt them. Section 17 now writes as the administrator and adopts as
  the Director of Studies.
- **G5:** a report's author never excuses it ("no duty") nor returns or reviews it.
- **G6:** the colleague in a swap or cover agrees; somebody else decides.
- **G7:** nobody appoints themselves head or deputy of a department. The post GRANTS permissions and reach, so
  appointing oneself is choosing one's own privilege.
- **`scripts/e2e/separation-check.mjs` (guards) fails a decision endpoint that does not call it.** Any action on those
  controllers whose route ends in approve/forward/return/moderate/sign/decide is checked, whether or not it is listed.
  It found the duty report's Return on its first run.
- Left to the school, and SAID on Users & Roles → Access: the Import inbox's second approver is a setting, and the access
  review is recorded by one person.

## Lesson plans and schemes of work (built 2026-09-26)

Plan `docs/plans/LESSON_PLANS_AND_SCHEMES_OF_WORK.md` (artifact https://claude.ai/artifact/GrjY8qNAeEjuY3jsp2ntUu).
L1–L12 were taken as recommended, **the form is the main route**, and §11 lists the departures.

- **`TeachingPlans` is the one new table**, and §5.1 says why nothing existing could carry it. The trail, the file and
  the content are columns and JSON on the row; templates are in the staff policy blob; the curriculum list is in
  `Organization.Settings["Curriculum"]` (`CurriculumStore`, through the organisation lock); records of work are derived.
- **The chain.** The teacher submits. The head (or deputy) of the SUBJECT's department takes stage 1. A holder of
  `teaching.plans.approve` with the author in scope takes stage 2. A scheme always takes both stages; a lesson plan
  takes the school's setting (default: the head of department alone, with the DoS told in the Monday digest).
  **`TeachingPlans.AccessForAsync` is the one read-and-act rule**, used by the controller, the upload authorizer and the
  reports. 404, never 403. `teaching.plans.review` comes from the head-of-department POST, never a role.
- **A submitted plan is frozen; an approved one is never edited** — a change is a new version that becomes current only
  when approved. **Retire the old version FIRST, in its own statement, inside the approval's transaction**: in one save
  the database may apply the two updates in either order and the one-current-plan index refuses the pair. That bug
  shipped for a day and was found by running section 45 twice.
- **Every transition saves through the row's xmin**: five approvers pressing at once, one lands.
- **Nobody plans for a class they do not teach** (live subject-teacher assignment; a lesson's own teacher for a cover).
- **The PDF is the fallback, shrunk IN THE BROWSER** (`wwwroot/js/planPdf.js`: pdf.js + pdf-lib from jsDelivr, loaded
  on demand). Text is rebuilt in Standard-14 fonts; a scan becomes a 1-bit image; the teacher sees before and after.
  **`PdfGate` checks every file again on the server** with the base library alone: it inflates every stream, decodes
  `#xx` in names, refuses script, automatic actions, embedded files and forms, counts pages, and caps inflation. Limits
  come from `TeachingPlanLimits` / the school's settings (50/64 KB and 4 pages for a lesson plan, 150/200 KB and 30 pages
  for a scheme), clamped on read.
- **Word templates are written with `ZipArchive` and read back into the FORM — never stored**
  (`TeachingPlanTemplates`). `.docx` stays off the stored-upload allow-list.
- **Storage now counts every kind of upload** (`StorageUsage`), not the Library alone.
- **Printed plans are headed by the school** through `PlanSheetHead`, which reads the BRANCH's public branding (the settings
  endpoint needs `settings.view`, which a teacher lacks). **A printed sheet pins its colours to black**: under the dark theme a
  heading inherits a light colour and prints white on white. That was found only by looking at a screenshot, and it is
  asserted now. A blank sheet in the school's format is `/plans/blank/print?kind=lesson|scheme`.
- **The report names every teacher's scheme by state** (not started to approved, and late), and the Monday email lists the
  teachers whose schemes are not yet approved.
- Verified by **sections 44 (25 checks) and 45 (99 checks)** and the browser suites `lesson-plan-ui`, `plan-pdf` and
  `plan-templates`, all 0 failed.

## A period's type is the school's; what it DOES is two switches (2026-09-26)

*"where is the ui to add a kind? we only have Assembly, Lesson, Break"*, then *"can this be a dynamic feature?"*. The three
kinds were fixed because each is a behaviour; the answer was the "data vs behaviour" rule below, applied: the NAME is data,
the behaviour is a small fixed set of switches.

- **`TimetableSettingsDto.PeriodTypes`** (in `Branch.Settings["Timetable"]`, no new table) is the school's list; each
  `PeriodTypeDto` carries **`Teaching`** (lessons can be placed) and **`OnPersonalTimetable`** (the My Workspace card and a
  teacher's printed sheet). A period stores `Type` (the type's key). A new behaviour is a new switch AND the code that reads it.
- **`BellPeriodDto.Kind` is DERIVED, never chosen**: `TimetableCycle.KindFor` (teaching → Lesson; else shown → Break; else
  Assembly) is the one place a type becomes a kind, and `ApplyPeriodTypes` runs on every read and inside `Validate`. So every
  older reader of `Kind` (the checker, materialisation, self-service, the grid, print) needed no change, a document saved
  before today reads as Lesson / Break / Assembly, and a client that claims a kind is overruled.
- **A type in use is retired, never removed** (the server refuses a period naming a type not on the list, naming the period).
- **A period with PUBLISHED lessons cannot stop taking lessons** — by a type change or by switching the type off
  (`TimetableController.StrandedLessonsProblemAsync`, versions still in force only). A draft is left to the diagnosis.
- A new type's key is minted on the page (`t-xxxxxxxx`), not from its name, so a period can pick it before the first save
  and renaming the type later moves nothing.
- Verified by **section 46** (`period-types-e2e.mjs`, 23 checks) and **`browser/period-types-ui.mjs`** (10 checks).

**The phone bar is re-chosen when the module list arrives** (`MainLayout.ChooseMobileSlots`, from `HandleModuleStateChanged`
too). It was a snapshot taken before the list loaded, and since `HasModule` stays closed until it does, a teacher's bar lost
My Day and Workspace for the life of the circuit while the sidebar showed both. Workspace is now second, and **Calendar comes
before Home** (user, 2026-09-26: Home repeats My Workspace). `mobile-nav.mjs` asserts both for a teacher.

## Process note for future sessions

Design/reference decisions like the one above must be written here (or somewhere durable) at the
time they're made, not left to survive only in conversation context — this file didn't exist
before 2026-08-17 despite the Webster reference having been consulted earlier in that session,
which is why the font-pairing decision survived (it made it into code) but its rationale and the
broader template-selection work did not (nothing to point back to after context compaction).

## A POST grants permissions AND scope, as a pair (built 2026-09-22)

Making somebody the class teacher of S4B gave them the pastoral **scope** over S4B and not one
pastoral **permission**: the assignment saved, the page showed them as responsible, the coverage
warning stopped naming the class — and every welfare feature silently refused them, because
`PermissionAuthorizationHandler.GetUserPermissionsAsync` is one query, `Users → Role →
RolePermissions`, and nothing else contributed. Head of department had the identical defect on the
staff axis. The plan is `docs/plans/DERIVED_POST_PERMISSIONS.md`.

**`Application/Services/PostPermissionService.cs` is the one home**, a static class with declared
constants (`PastoralClassPost`, `DepartmentHeadPost`). It is read by the API gate, by
`AuthController.BuildUserInfoWithPostsAsync` (what the browser's `@if (HasPermission(…))` renders
on) and by both scope services.

- **IT RETURNS `PostGrants`, NOT A LIST OF CODES, AND THAT IS THE WHOLE POINT.** Deriving
  permissions alone opens a **school-wide leak**: `support-staff` and `viewer` are
  `DataScope: Organization` and hold no welfare permission, so the MISSING PERMISSION was the only
  thing stopping a matron who is a class teacher reading every child in the school.
  `StudentScopeService.ApplyAsync` begins `if (await IsUnscopedAsync()) return query;`, so an
  organization-scoped caller passes **all 29 guards in `WelfareController`** by construction — the
  controller is not the layer that can save you, and it was already doing everything right.
- **The rule: the POST's scope applies to the POST's permissions; where the ROLE already grants the
  permission, the ROLE's scope wins.** A Tenant Admin who also teaches a class keeps the school.
  `PostPermissionService.IsUnscopedOnStudents` is the one home for that test.
- It reads as a **narrowing** on the student axis (Organization → the classes held) and a
  **widening** on the staff axis (SelfOnly → the departments headed), because the baselines differ.
  `StaffScopeService.ComputeVisibleAsync` unions the headed departments **outside** the switch, so a
  line manager who also heads a department sees both.
- **Subject teachers grant NOTHING here.** `SubjectTeacher` already yields the Teaching tier;
  deriving welfare from it would hand every teacher pastoral access to every class they teach.
- **CACHING IT IS SAFE ONLY BECAUSE EVERY POST WRITE INVALIDATES IT.**
  `IStaffProfileChangeNotifier.PostChangedAsync` is the one home; `ClassTeachersController` (assign,
  assign subject, end) and `StaffStructureController` (department create and update) call it.
  **The update passes BOTH SIDES — `before.HeadUserId` as well as the new one** — because the person
  being REMOVED is the one who otherwise keeps access for five minutes.
- **A new endpoint that assigns or ends a post and does not call the notifier is that window again.**

### THERE WERE SIX READERS OF "WHAT MAY THIS PERSON DO", AND ONE KNEW ABOUT POSTS (2026-09-22)

Found by e2e section 15 the same day the posts work shipped, and it is the more useful half of that
story: deriving the permissions was the easy part, and **the permissions were then read in six places.**

`PermissionAuthorizationHandler` was updated. The other five each wrote
`SelectMany(u => u.Role.RolePermissions)` by hand and so ignored posts entirely:
`StaffPerformanceControllerBase.HasPermissionAsync` (**73 call sites**), `BatchController`,
`ClassTeachersController`, `ContentController`, `DocumentSharesController` — plus **`GET /auth/me`**,
which hand-built its own `UserInfo`.

- **The symptom was inconsistency, not a clean failure.** A derived permission passed an endpoint's
  `[RequirePermission]` attribute and then FAILED the in-code check inside the same endpoint. So a class
  teacher's or department head's access worked or did not depending on which style a given endpoint
  used. `staff/reports/teaching` refused a department head outright (403 from `MayReadAsync`), and nine
  section-15 assertions cascaded off that one call.
- **`/auth/me` was the worst of the six because it is the REFRESH path.** The browser persists `UserInfo`
  in localStorage and `AuthService.RefreshCurrentUserAsync` re-reads it from there, so every
  `@if (HasPermission(…))` silently lost the derived permissions on a refresh while the API gate went on
  honouring them — the UI vanishing from under somebody who could still make the calls. It returned 4
  permissions where `/auth/login` returned 12.
- **`PostPermissionService.EffectiveCodesAsync` is now the ONE home for the union**, and all six call it.
  A controller memoises it per request; the authorization handler keeps the only cache over it (five
  minutes, invalidated by `IStaffProfileChangeNotifier`). **Never write a role-permission query by hand.**
- **The one hand-built `UserInfo` that is CORRECT** is the password-change-only login response, which
  advertises no permissions on purpose. It is commented as such so a sweep does not "fix" it.
- **The lesson for the next derived thing:** a new source of authority is not finished when the gate
  honours it. Grep for every reader of the thing it derives — `RolePermissions`, `new UserInfo`, and any
  local `HasPermissionAsync` — because the ones that are missed fail *inconsistently*, which is harder to
  find than failing outright.

**`class-teacher` and `head-of-department` are DELETED** (user decision: *"what is the point in
having them if they cannot be assigned? redundant features create more confusion"*). `RoleCodes`
keeps `RetiredClassTeacher`/`RetiredHeadOfDepartment` for the migration only; both are out of
`All`. **Removing them shifted no ranks and that was checked, not assumed:** `Rank` is relative
(`Rank(x) <= Rank(Manager)`) and both sat below Manager. The migration **reassigns holders to
`teacher` — never `staff` or `support-staff`**, because those are `DataScope: Organization` and a
derived welfare permission on one reads the whole school.

**Anybody on the staff can hold a class now.** `UsersSetup.CanHoldAClass` replaced
`IsClassTeacherRoleSelected`, and the two inline warnings on the assignment dialogs saying "this
person's role is not Class Teacher, so this grants nothing" were **removed — they were true, and
they described this defect**.

### Class Teachers is a tab in Staff Directory, not a page

`/admin/students/class-teachers` is **deleted, not aliased**. `/admin/staff` now carries
**People · Class teachers · Subject teachers · Departments · Coverage · Import**.

**The test that decided it: a screen that grants a person access belongs where the other access
grants are.** Once the assignment confers the permissions, pressing *Assign a Class Teacher* is the
same kind of act as setting a role, naming a department head or giving somebody a line manager —
and filing the one that reaches children's safeguarding records elsewhere puts it where an
administrator auditing *"who can read welfare records?"* would not look. The earlier reasoning
("it is about children, so it is welfare") was wrong because **"what is it for" cannot separate this
page from any other staff-assignment screen**.

- `ClassTeachers.razor` is ONE component with `Embedded` + `EmbeddedTab`; `Pastoral` picks the half.
  Two tabs rather than two components because both are the same class-centric grid over the same
  coverage payload — splitting would duplicate the load, five dialogs and the fail-closed rules.
- Gated on `classes.teachers.manage`, which is **neither a subset nor a superset** of
  `staff.records.view` — hence its own `HubTabs.Section` entries.
- `MainLayout`'s `CanViewWelfareSection` dropped `canManageClassTeachers`: a holder of that
  permission alone now has nothing to open in the Student Welfare group.
- Five callers were repointed, and **one was outside the pages** — `MobileNav.cs:143`.

## A field that carries no BEHAVIOUR is data, not an enum (2026-09-22)

Asked as *"is there a way not to hard code the parameter features?"*, answered with a test:

> **Does a value carry behaviour?** No → taxonomy → a `VocabularyItemDto` in a JSON settings blob.
> Yes → it names a code path → it stays an enum, and the honest fix is to say so in the UI.

**`StaffGroup` failed it outright and is gone.** It was three values resolved by
`roleCode == "support-staff" ? Support : Teaching`, so every custom role a school created — and
`admin`, `manager`, `viewer` — counted as TEACHING staff; and since `RosterImportProcessorJob`
defaults a missing role to `teacher`, an imported staff list arrived entirely teaching and the
bursar, matron and driver were scored on Lesson Attendance. One set-membership test read it.

- **`StaffGroups` (Shared) is the one home for the comparison** — `Applies(required, actual)` with
  `Key()` folding case and punctuation, the `ClassName.Key` rule. **Null required = everybody**,
  which retires `AllStaff`: "applies to everyone" is the absence of a restriction, not an entry
  somebody could rename.
- The list is `StaffPerformancePolicyDto.StaffGroups`, **organization-scoped, not branch** —
  `PerformanceParameter` is organization-scoped, so a branch list would let a parameter name a group
  that exists on one branch and not the next. That blob also already writes under
  `WithPolicyLockAsync`, which `Branch.Settings` had to have bolted on.
- **`Role.StaffGroup`** points a person at one, set in the role editor beside `DataScope` and
  `StaffScope`. `PerformanceParameter.AppliesToGroup` and `StaffNotice.AudienceStaffGroup` are names.
- **The migration ADDS, BACKFILLS, THEN DROPS.** EF scaffolded drop-then-add, which would have reset
  every parameter to "all staff" silently — a Lesson Observation applying to the bursar, with
  nothing anywhere saying so. Same class as `AddClassTeachersAndWelfareVisibility`.
- **`User.EmploymentType` followed on 2026-09-23** — see "Employment type is the school's own list".

**`GroupFor` TAKES THE ROLE'S GROUP, NOT ITS CODE — and three call sites went on passing the code
(found 2026-09-22 by e2e section 14).** When `StaffGroup` stopped being an enum, the signature changed
from a role code to the role's group, and `StaffScoringService` (both paths) plus
`StaffStructureController` were not updated. So a teacher's group resolved to `"teacher"`,
`StaffGroups.Applies("Teaching staff", "teacher")` was false, and **every parameter carrying a group
silently vanished from every staff score and breakdown** — Lesson Attendance, Lesson Observation,
Exam Supervision, Prep Supervision, Records & Schemes of Work, Lesson Recovery: the teaching half of the
appraisal evidence. Nothing errored, no parameter was retired, the records were all there; the breakdown
just came back short, and a signed appraisal froze the wrong score. The staff record also displayed
`"teacher"` where it should read "Teaching staff".

**The lesson, and it is the same one as the fourteen permission readers:** changing what a method TAKES
is more dangerous than changing what it returns, because every wrong caller still compiles. `string?` to
`string?` is the worst case. When a signature's MEANING changes, rename the parameter and grep every call
site — and prefer a distinct type over a second `string?`.

**RUNNING AN OLDER BUILD AGAINST A MIGRATED DATABASE RE-SEEDS WHAT A MIGRATION RETIRED (2026-09-22).**
A worktree at the previous commit was started to get a "before" baseline, and its pre-retirement
`Permissions.DefaultRoles` re-created the `class-teacher` and `head-of-department` global roles the
retirement migration had deleted — rows with permissions, held by nobody, which the migration's own
delete is guarded against removing a second time. The baseline run was worthless anyway: a forward
migration is not backward compatible, so the old code crashed on the `StaffGroup` column it still read.
**Do not reach for an old build to establish a baseline once migrations have been applied** — instrument
the current one instead. If it has already happened, the retired rows need deleting by hand.

**`ParameterKind` stays an enum, and the fix was the WORD.** Each value is a branch in
`StaffScoringService.ScoreParameter` — a school adding "Kind: Punctuality" would get a parameter the
server has no `case` for, scoring `null` silently — and it is read in **24 files**, deciding sign
rules, which outcomes are offered and what may offset what. The field is labelled **"How it is
scored"** now; the school already names the parameter itself.

**The real rigidity was three magic numbers, now in the policy** (`LateCreditFraction` 0.5,
`DefaultEntriesPerPeriod` 10, `NeutralScore` 50), clamped on read AND validated in the editor.
**`Excused` stays out of the denominator and is not configurable** — the MoES rule that
organisational factors are not a performance gap. **A tenant-authored formula is refused**: it fails
silently, and a score feeding an appraisal has to be explainable to the person it is about.

## A roster row is a name, a code and a class. The guardian is optional (2026-09-22)

*"since when did guardian and email mandatory for student? the only mandatory fields are Name, code
and class."* `RosterImportProcessorJob` refused any row without a guardian name AND a phone or
email, so a school's own export — 1,711 children, most rows with no guardian contact — became 1,711
failures. The requirement was never a property of a student: it was the guardian find-or-create
written as a precondition.

- Required: student name, student code (the upsert key), **class** — `Student.ClassName` is what
  `IStudentScopeService` matches on, so a child imported without one is invisible to their own class
  teacher.
- **A PARTIAL guardian is the one shape still refused**, and it refuses the LINK, not the row: the
  student imports and the message names which half was missing. A name with no contact, or a contact
  with no name, would create an unreachable or nameless guardian nobody could later correct.

## Sorting: `NaturalOrder` is the one home, and dropdowns opt IN (2026-09-22)

*"sort this and similar lists, also in the shared dropdown / select component."*

- **`Q-Mgr.Shared/Application/NaturalOrder.cs`** — digits compare as NUMBERS, so `S2C` comes before
  `S10A`. An ordinal sort gives a school with ten forms S1, S10, S2, which reads as broken on
  exactly the list a school builds first.
- **`QSelect.Sorted` / `QMultiSelect.Sorted` are opt-in, deliberately.** Terms run in calendar
  order, bands worst to best, a rota by day — sorting those would be a bug. The caller says when the
  order is arbitrary.
- **The vocabularies editor SORTS BY WRITING `SortOrder`, not by re-displaying.** That list has an
  order the school owns (the up/down arrows set it, and it decides every class picker in the
  product), so a display-only sort would make the arrows appear to do nothing. It is an action with
  a Save behind it, undone by Cancel.

# Q-Mgr Web

Multi-tenant queue-management SaaS. ASP.NET Core API (`src/Q-Mgr.API`) + Blazor Server web app
(`src/Q-Mgr.Web`), Postgres via EF Core, CQRS via the `Mediator` source-generator library (not
MediatR). **Git was adopted 2026-08-25**, and **there is a GitHub remote:
`origin` → `https://github.com/szoora/qmgr.git`** — this line said "local-only, no remote" until
2026-09-06, which was stale and materially misleading: anything that rewrites history here is
published, not private. Before that it said no git repo existed at all. See `git log` for history
from 2026-08-25 forward rather than this file's older prose for anything after that date.

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
- **Radius — reconciled; this note was stale and is corrected 2026-09-05.** It described the
  tokens as 6/10/16/24px and the reconciliation as undecided. `qm-theme.css` has in fact carried
  `--qm-radius-sm/md/lg/xl` = **3/4/6/8px** since the repository's baseline snapshot — Webster's
  flat convention, already adopted. Keep new work on these tokens rather than reintroducing a
  softer radius; there is nothing outstanding here.
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
- Known and not changed: `GET /api/v1/platform/settings/Email` returns `SmtpPassword` in clear to a
  SuperAdmin. That is how the Platform Settings editor round-trips it and is true of every platform
  secret, not just this one — worth fixing as its own piece of work, not as a side effect of this.

## Verification: there is no test project, and that is the decision (2026-09-05)

**Do not propose, scaffold, or ask for a test project.** Earlier handovers listed "no automated
test coverage" as a standing gap in this repo; the user closed that question on 2026-09-05 —
there is not going to be one, and it should stop being carried forward as outstanding work.

**There IS now one e2e script**, `scripts/e2e/class-teacher-e2e.sh` — **94 assertions** (72 until
2026-09-13) over the class-teacher scope, the visibility tiers, the alert, the reports gate, the
notification preferences, the delivery log, the three leak shapes above, and real email delivery.
It is not a test project and is not run by a build; it is a curl script against a live API and a
real tenant, which is exactly what the rule below asks for, written down so it can be re-run.
Extend it rather than starting a new one.

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

A SuperAdmin with no organization chosen sees an empty Category list on the create form until the
page is reloaded after picking an organization and branch. Not investigated.

**OPEN — uploads are served as static files, ahead of any authorisation check (found 2026-09-14,
not yet fixed).** Uploads live under the API's own `wwwroot`, so the static-file middleware serves
them before authentication runs; there is no per-file authorisation on that path. The practical
consequence is the one that matters here: **a permission check on the record that owns a file is
meaningless while the bytes are also reachable directly**, and an upload URL, once it leaks, is
permanent and unrevocable. Treat every upload surface as world-readable until this is closed.

**Do not add a new upload surface, or build anything that relies on per-file access control, before
reading this.** OWASP's rule is that uploaded files should never be directly reachable and that
public access belongs behind a handler mapping an id to a file; that is the shape of the fix, scoped
as Phase 1 of `docs/plans/SECURE_DOCUMENT_SHARING.md` — which does not depend on the rest of that
feature being built.

**This repository is public, so the reproduction, the affected lines and the list of exposed data
are deliberately NOT here.** They are in `SECURITY-UPLOADS.local.md` in the repository root, which
is untracked and covered by the `*.local.md` rule in `.gitignore`. Fold that file into
`docs/TASK_TRACKER.md` and delete it once the fix has shipped — a *fixed* finding is normal
engineering history and worth publishing; an unfixed one against a live host is not.

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

**Known and deliberately not changed:** `Program.cs` runs `UseStaticFiles()` *before* `UseCors()`,
so the API's `/uploads/*` files never carry CORS headers. Production is unaffected (nginx makes them
same-origin), but it means adding `http://127.0.0.1:5003` to `Cors__AllowedOrigins` **cannot** make a
local cross-origin upload fetch work — the middleware never runs for those files. Locally, serve the
PDF from Web's own `wwwroot` instead. Reordering that middleware is a security-relevant edit.

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

## Process note for future sessions

Design/reference decisions like the one above must be written here (or somewhere durable) at the
time they're made, not left to survive only in conversation context — this file didn't exist
before 2026-08-17 despite the Webster reference having been consulted earlier in that session,
which is why the font-pairing decision survived (it made it into code) but its rationale and the
broader template-selection work did not (nothing to point back to after context compaction).

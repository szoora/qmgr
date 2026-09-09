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

## Verification: there is no test project, and that is the decision (2026-09-05)

**Do not propose, scaffold, or ask for a test project.** Earlier handovers listed "no automated
test coverage" as a standing gap in this repo; the user closed that question on 2026-09-05 —
there is not going to be one, and it should stop being carried forward as outstanding work.

**There IS now one e2e script**, `scripts/e2e/class-teacher-e2e.sh` — 43 assertions over the
class-teacher scope, the visibility tiers and the alert. It is not a test project and is not run by
a build; it is a curl script against a live API and a real tenant, which is exactly what the rule
below asks for, written down so it can be re-run. Extend it rather than starting a new one.

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

**Still open as of 2026-09-09: `Q-Mgr.Web` has no `AddDataProtection` call at all.** Under the same
`ProtectSystem=strict` + `ProtectHome=true` unit it therefore falls back to an ephemeral key ring,
so antiforgery tokens rotate on every restart. Same class as the API bug above, failing softer —
it degrades rather than throwing, which is why it has not announced itself.

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

**Migration is incomplete and that is a real task, not tidying.** As of 2026-09-09, `QDateFormat`
is used in 3 files and 62 hand-typed formats remain; `QDateRangePicker` is adopted by the Visitor
Report, Reports Overview, Welfare Reports and the welfare timeline, while Campaigns, Schedules,
Appointments, Invoices, StudentRoster, ExpectedVisitors, StudentPicture and SystemSettings are
still on loose pickers or raw date inputs. **Migrate the file you are already touching.**

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

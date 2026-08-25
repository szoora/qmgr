# Q-Mgr Web

Multi-tenant queue-management SaaS. ASP.NET Core API (`src/Q-Mgr.API`) + Blazor Server web app
(`src/Q-Mgr.Web`), Postgres via EF Core, CQRS via the `Mediator` source-generator library (not
MediatR). No git repository exists in this working directory — the user has declined `git init`.

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
- **Radius — currently diverges from Webster, worth deliberate reconciliation.** Webster's
  dominant convention is a flat, minimal `border-radius: 3px` on cards/buttons/inputs (with 50%
  circles for avatars). Q-Mgr's current tokens (`--qm-radius-sm/md/lg/xl` = 6/10/16/24px) are
  noticeably rounder/softer — this divergence is real and is part of why pages can still read as
  "generic AI dashboard" rather than matching the shared reference. Reconciling this (which radius
  scale to standardize on) is outstanding work, not yet decided.
- **Shadow — Webster's convention** is a soft, low-spread card shadow (`0px 3px 10px
  rgba(0,0,0,0.1)`) plus a larger ambient shadow for section depth (`0px 0px 50px rgba(0,0,0,
  0.05)`). Compare against `--qm-shadow-sm/md/lg` before changing anything broadly.

## Known theme gaps (user-reported, not yet fixed as of 2026-08-17)

1. **Largely addressed 2026-08-19** (see Phase 41 in `docs/TASK_TRACKER.md`) — the generic
   AI-dashboard patterns this item originally referred to (diagonal gradients, neon glow shadows,
   hardcoded blue bypassing the token system, and — the single biggest offender, found via live
   e2e — 198 raw Bootstrap `btn-primary`/`text-primary`/etc. usages across 42 files rendering stock
   Bootstrap blue regardless of any `--qm-*` token work) were swept app-wide and fixed. Not
   exhaustively re-verified page-by-page beyond the pages spot-checked live during that session, so
   treat as "should be close to fully fixed" rather than "guaranteed zero remaining instances."
2. **Dark/light mode has too much overlap and inconsistent coverage** — some features have no
   light-mode styling at all. `qm-theme.css` uses `[data-theme="light"]` overrides on top of a
   dark-first `:root` (see lines ~89+); this pattern needs a systematic audit against every page,
   not just the ones already covered.
3. **Public display pages (`CustomerDisplay.razor`) are hardcoded dark-only** — no admin control
   over the public-facing display's theme at all. Needs a real admin-configurable
   light/dark choice for these screens (scope — per-organization vs. per-display — still needs to
   be settled with the user).

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

## Process note for future sessions

Design/reference decisions like the one above must be written here (or somewhere durable) at the
time they're made, not left to survive only in conversation context — this file didn't exist
before 2026-08-17 despite the Webster reference having been consulted earlier in that session,
which is why the font-pairing decision survived (it made it into code) but its rationale and the
broader template-selection work did not (nothing to point back to after context compaction).

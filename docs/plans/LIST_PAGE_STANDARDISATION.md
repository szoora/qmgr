# List-page standardisation — the sweep after the register

**Status:** planned, not started · **Written:** 2026-09-20 · **Owner:** next session

## Why this exists

The register (`StaffRegister.razor`) was reported against a real staff meeting on 2026-09-20 and
reworked the same day. The five things wrong with it were not specific to registers:

| # | Fault | What it looked like on the register |
|---|-------|-------------------------------------|
| 1 | **Width** | Capped at `max-width: 760px; margin: 0 auto` — a quarter of a 1900px screen used, and the rows wrapped inside that quarter. |
| 2 | **Row** | One person = three stacked blocks ≈ 150px. Eleven people filled a screen; 135 meant scrolling past the footer the whole time. |
| 3 | **Bulk** | No way to act on more than one row. Nobody taps *Present* forty times. |
| 4 | **Scale** | Every row rendered, every row diffed, every row shipped down the SignalR circuit. |
| 5 | **Client vs server** | Two refusals in `SubmitRegister` (self-marking; a rota's missing reason) were reachable only by submitting and being told — losing the whole register to one 400. |

The user's response was *"i suggest you plan to fix this and similar pages"*. This is that plan.

## Phase 0 — triage, because "similar pages" has to be measured

`scripts/e2e/list-page-audit.mjs` reports the five faults per page. **Baseline, 2026-09-20: 59 list
pages in the app shell; 36 carry three or more of the five faults.** The two that come back clean
are `StudentRoster` (which already had bulk) and the reworked `StaffRegister` — which is the check
that the detector is measuring the right thing.

**The script is a triage list, not a verdict.** A page listing four service types needs none of
this, and the script cannot know that. Phase 0 is a human pass over the table producing three tiers.
The proposed tiering, to be confirmed:

**Tier 1 — lists that genuinely grow (do these).** People, records, and anything a year of
operation accumulates:
`StaffDirectory` · `StaffRecords` · `StaffRota` · `StaffNotices` · `DutyReportsQueue` ·
`JoinRequests` · `ExpectedVisitors` · `VisitorAuditLog` · `VisitorReport` · `WelfareOpenActions` ·
`StudentWelfareTimeline` · `MediaLibrary` · `Notifications` · `RegistrationReview`

**Tier 2 — configuration lists that grow slowly (width and row only).** A school has twelve
classes and six counters; they do not need virtualisation, but a grid of tall cards for six rows is
still the wrong shape (the Branches hub made exactly this mistake and was fixed on 2026-09-19):
`BranchesSetup` · `CountersSetup` · `ServiceTypesSetup` · `WelfareCategoriesSetup` ·
`StaffParameters` · `StaffSubjects` · `Playlists` · `Campaigns` · `Schedules` · `DocsManagement` ·
`ApiClientsSetup` · `ModuleCatalog` · `ClassTeachers`

**Tier 3 — not this kind of page. Leave alone.** Settings forms, dashboards, document pages and the
one-off screens: `BrandingSettings` · `PlatformSettings` · `IntegrationsSetup` ·
`NotificationPreferences` · `Dashboard` · `MyDay` · `CounterTerminal` · `StudentPicture` ·
`EvacuationReport` · `PaymentMethods` · `Billing/Overview` · `StaffAppraisalDetail` · `Docs`.
The portal's record/notice/appraisal pages and every public and print route are **already excluded
by the script**, and must stay excluded: a document keeps its reading width.

## Phase 1 — extract the register's bar, change nothing else

The register now has the shape the rest should adopt. Move it to one home before copying it by hand
into fourteen pages — that is this codebase's most-repeated bug.

**`Components/Shared/UI/QBulkBar.razor`**, lifted from `StaffRegister`'s `.reg-bar`:

- filter chips with live counts, a search box, "select all *shown*";
- a selection that **may only ever cover rows the reader can see** (`PruneSelection` runs on every
  filter and search change) — "apply to 20 selected" silently reaching hidden rows is the worst
  available bulk bug;
- an actions slot the page fills with whatever applying means for it;
- an "apply to the remainder only" sweep, which must never overwrite deliberate marks.

Plus a short convention in `CLAUDE.md`: **a data page takes the width it has; a row is a line; a
list that can pass ~50 rows virtualises; bulk acts on the visible selection.**

Phase 1 ships with the register still the only caller, and `register-and-duty-sheet.mjs` green.

## Phase 2 — Tier 1, one page at a time

Per page: width, row, `QBulkBar`, `<Virtualize>` above ~40 rows, and a browser check that measures
rather than eyeballs (row height, no sideways scroll at 390px, DOM rows < total). One commit per
page so a regression is bisectable.

**Order by how much it hurts today:** `StaffDirectory` (135 staff on the dev tenant alone) →
`VisitorAuditLog` and `VisitorReport` (append-only, grow for ever) → `StaffRecords` →
`Notifications` → `MediaLibrary` → the rest.

## Phase 3 — the audit nobody thinks to do

For each Tier 1 and Tier 2 page, list the refusals its controller can return and check the client
makes each one visible **before** the submit. This is fault 5, and it is the one that loses work
rather than merely annoying. The register's two are now enforced on the row; the others have not
been looked at.

Output: a table of *endpoint → refusal → where the client shows it*, and a fix where the answer is
"it doesn't".

## Phase 4 — a guard, so it does not come back

Extend `list-page-audit.mjs` with a **done list**: a page that has been through the sweep must keep
its zero, and the script fails if one regresses. Pages not yet swept stay reported and do not fail.
Same shape as `style-leak-check.mjs` and `section-actions-check.mjs`.

## Cost

Phase 1 is half a day. Phase 2 is roughly half a day per Tier 1 page including its browser check —
call it six to seven days for fourteen. Phase 3 is a day of reading plus whatever it finds. Tier 2
is an afternoon in total, because it is width and row only.

**Tier 1 alone is the 80%.** Tier 2 is tidiness; Phase 3 is the one most likely to surface a real
bug, and it could be pulled forward if that matters more than the visible work.

## Decisions wanted before Phase 1

1. **Scope** — all three phases, or Tier 1 + Phase 3 and leave Tier 2?
2. **Order** — visible work first (Phase 2), or the refusal audit first (Phase 3)?
3. **Virtualisation threshold** — the register uses 40. Same everywhere, or per page?
4. **Tier 2's row shape** — tables (as the Branches hub now is), or compact cards?

## Explicitly out of scope

- The kiosk, public displays, signage, public feedback, booking, ticket status, shared documents,
  print sheets and the sign-in pages. Every scale rule in this project excludes them and so does
  this one.
- Document pages, which keep their reading width: the portal's record, notice and appraisal pages.
- Any change to what a page *does*. This sweep is shape, scale and honesty about refusals — not
  new features.

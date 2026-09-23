# The student roster, and every list like it

**Status: decisions L1–L7 settled 2026-09-23 on the recommendations ("use your recommended decisions … implement
the plan fully"). Build started 2026-09-23.** Designed version: https://claude.ai/artifact/3e4uDfM3eLxrjecydfDhNZ.
It follows `LIST_PAGE_STANDARDISATION.md` (2026-09-20) and fixes what that sweep's detector missed.

> *"do we have a shared pagination component on this project? if not, create one. Add pagination to this and
> similar list pages. the only available filter feature on this page is the search. i need to be able to filter
> students of a class … show a summary of the total records, and page size and user should select the required
> page size, including all, next, etc. is this page using the shared bulk action component? in the bulk action,
> include a feature to log a record for selected group of students."*

## 0. The short answer

- **`QPager` exists** (25 pages) — "Showing 1–25 of 1,711", previous, next, numbers — but has **no page-size
  choice, no All, no first/last**; every page fixes its size. It is ENHANCED, not duplicated.
- **The roster uses none of it**: it draws every student (1,711 at Maryhill), search is its only filter, and it
  runs a hand-built bulk bar instead of `QBulkBar`.
- **Why the audit passed it**: `list-page-audit.mjs` counted any `.Take(` as paging, and the roster has
  `s.Flags.Take(3)`. Fixed in Phase 1, with a new guard.

## 1. The pager (one home: `QPager`)

- New, opt-in parameters: `PageSizeOptions` (e.g. 10 · 25 · 50 · 100), `AllowAll` (adds **All**), `PageSizeChanged`,
  `StorageKey` (the chosen size remembered per list per browser, localStorage wrapped), `SelectedCount` (caption
  "· 3 selected"). **`PageSize == QPager.All` (0) means every row.**
- First and last page; a page jump at ten or more pages; on a phone "‹ Page 2 of 9 ›" at 44px.
- `QPaging` (Shared/UI) is the page-side helper: `Apply(rows)`, `Reset()` on any filter or size change, `IsAll`.
- **All renders through `<Virtualize>` above 200 rows** (L3), so it never freezes a page.
- Default size 25 (L4, Carbon's threshold).

## 2. The roster

- **Filters** (`QFilterBar`): Class (`QMultiSelect`, `Sorted`, "S2A (47)", compared with `ClassName.Key`), Status
  (Active · Inactive · All — replaces the tick), House, Dormitory (when the branch has any), Guardian (has · none
  yet), Flags (has an active flag — only for callers who see flags). Search searches within the filters.
  Filters live in the address (`?class=S2A&status=active`); active filters show as chips with Clear all.
- **A class teacher opens on their own pastoral classes** (L6), preset, removable.
- **Summary** (`QStatRow`): on the roll · match the filters · classes · no guardian yet · with an active flag.
  Each tile applies its filter. Scoped figures say so.
- **`QBulkBar`** replaces the hand-built bar: Log a record · Print cards · Export selected · Promote to next class ·
  Set a field · Add to a list. Selection: this page by default; "Select all N matching" is an explicit second press
  (L2); any filter change prunes the selection to what is visible.
- Smaller fixes: the row's Log button is secondary; "Tidy names out of capitals" (managers, previewed, uses
  `PersonName.FixShouting`, never touches a mixed-case name); the per-row info icon only where it explains
  something; `QEmptyState` with Clear filters for an empty result.

## 3. Logging a record for a group (`POST …/welfare-records/bulk`)

- **L1**: one record per student by default; "one incident involving all of them" (StudentId + existing
  `AdditionalStudentIds`) for Behaviour only.
- **L7**: Welfare case type is NEVER bulk — the form offers Achievement and Behaviour only and says why.
- Every student checked against the caller's scope on the server (`VerifyStudentAccess` semantics); one out of
  scope refuses the whole batch with the unknown-student wording. Synchronous, one transaction, **cap 200**, so a
  class teacher can log a merit for their own class (the Hangfire batch path refuses scoped callers).
- The SAME single-record creation code runs per student: late entry, closed periods, category rules apply.
- **L5**: alerts coalesced — one message per recipient; guardians only when the category says so.
- The confirmation states the consequence: records cannot be deleted, only annulled one by one. The activity log
  shows the batch as one line.

## 4. The same standard everywhere

The 25 `QPager` pages gain `PageSizeOptions`/`AllowAll`/`StorageKey`; remaining hand-built bulk bars move onto
`QBulkBar`; `list-page-audit.mjs` counts only `QPager`/`<Virtualize>`/`QTimelinePaging` as paging; new static
guard `pager-check.mjs` fails a list page whose `QPager` passes no `PageSizeOptions`.

## 5. Decisions — settled 2026-09-23

L1 one record each by default + shared incident for behaviour · L2 page selection, explicit "all matching",
pruned on filter change · L3 All offered everywhere, Virtualize above 200 · L4 default 25, remembered · L5 no
guardian message unless the category says so; staff alerts coalesced · L6 class teachers open on their classes ·
L7 welfare concerns never bulk.

## 6. Build record (2026-09-23)

Built by three parallel slices, compiled once (two Razor errors: `ChildContent` beside `Actions`, and a literal
"<style>" inside a CSS comment that Razor read as a tag). No migration. Section 32 60/0; browser `pager` 40/0 and
`student-roster` 31/0; 14/14 static guards.

- Found on the way: **`GET …/students` returned only the first 100 students** — the roster and the welfare timeline
  were truncated for every school over 100; now the whole roll. 23 pages swept onto page sizes (four server-paged
  ones without All); the audit's `.Take(` false pass fixed; `pager-check` added.
- Differences: "Add to a list" left out — the Lists dialog edits classes/houses/dormitories, there is no list of
  students to add to; bulk "Log a record" is disabled when the selection holds students the caller only TEACHES
  (those get "Log a concern" one at a time); the per-row consent icon now shows only when consent is on file; the
  staff award from a bulk log is credited once per batch.

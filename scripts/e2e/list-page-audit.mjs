// THE LIST-PAGE SWEEP — the triage (Phase 0) and the guard (Phase 4).
// Plan: docs/plans/LIST_PAGE_STANDARDISATION.md
//
// The register was reworked on 2026-09-20 after being reported against a staff meeting: capped at
// 760px, one person per ~150px, no way to mark more than one at a time, no paging, and two server
// refusals reachable only by losing the whole submit. The question that followed was "which other
// pages are like this", and the answer had to be MEASURED rather than remembered.
//
// The five faults:
//   width    the page caps and centres its own content (a data page should not)
//   row      a row is built as a tall card in a grid rather than a line in a table
//   bulk     no way to act on several rows at once
//   scale    no paging, no virtualisation, no page size — every row ships down the circuit
//   search   no search or filter over the list
//
// PHASE 4, THE GUARD: a page in DONE below has been through the sweep and must KEEP its zero, so a
// fault reappearing there fails this script. Every other page is reported and fails nothing —
// triage is not a verdict, and a page listing four service types needs none of this.
//
// A WAIVER IS A DECISION, NOT A GAP. "bulk" is the common one: a read-only log has nothing a
// selection could be applied to, and inventing a bulk endpoint to satisfy a checklist would be the
// sweep making product decisions. Each waiver says why, in the page's own terms.
//
// Run: node scripts/e2e/list-page-audit.mjs
import fs from 'node:fs';
import path from 'node:path';

// Outside the app shell these rules do not apply: the kiosk, the public screens, the print sheets
// and the sign-in pages have their own large-format or paper sizes, and a document page KEEPS its
// reading width. Same exclusion set as every other scale rule in this project.
const EXCLUDE = /Kiosk|CustomerDisplay|SignageDisplay|PlaylistPlayer|FeedbackPage|JoinQueue|TicketStatus|BookAppointment|Print\.razor|Portal(Record|Notice|Appraisal)|Login|Terms|Privacy|Support|Shared[/\\]/;

/**
 * TIER 3 of the plan: pages the triage examined and ruled OUT. A settings form, a dashboard, a
 * detail page and a till screen all render a loop with a button in it, which is all the detector
 * can see — they are not lists that grow, and width, bulk, paging and search would each make them
 * worse. Named here so the outstanding count means "still to do" rather than "still on the table".
 */
const OUT_OF_SCOPE = {
  'Admin/BrandingSettings.razor': 'a settings form; its loop is the colour palette',
  'Admin/PlatformSettings.razor': 'a settings form, one section per category',
  'Admin/IntegrationsSetup.razor': 'a settings form, one card per integration',
  'Admin/NotificationSettings.razor': 'a settings form, one row per event',
  'Pages/NotificationPreferences.razor': 'a settings form, one row per event',
  'Pages/Dashboard.razor': 'a dashboard of tiles and panels',
  'Pages/Portal/MyDay.razor': 'one person, one day — bounded by construction',
  'Queue/CounterTerminal.razor': 'a till screen; its width is deliberate and it shows one customer',
  'Admin/StudentPicture.razor': "one student; the loops are that student\\'s own sections",
  'Admin/EvacuationReport.razor': 'who is in the building right now — bounded, and printed rather than searched',
  'Admin/Staff/StaffAppraisalDetail.razor': 'one appraisal; the loops are its own parameters',
  'Admin/Staff/DutyReportPage.razor': 'one duty report; the loops are its own sections',
  'Admin/Staff/StaffTimelineReport.razor': 'a print sheet for one person',
  'Admin/Staff/StaffAppraisalReport.razor': 'a print sheet for one appraisal',
  'Admin/Staff/TimetableSettings.razor': 'the bell schedule and the room list — a handful of rows a school sets once',
  'Admin/WelfareTimelineReport.razor': 'a print sheet for one student',
  'Pages/Billing/PaymentMethods.razor': 'the cards on file, of which there are one or two',
  'Pages/Billing/Overview.razor': 'a billing summary, not a list',
  'Pages/Docs.razor': 'the help centre index, curated and short',
  'Pages/Register.razor': 'a sign-up form',
  'Admin/IndustrySettings.razor': 'a settings form, one card per industry preset',
  'Admin/Analytics.razor': 'charts and tiles',
  'Pages/Platform/PaymentReconciliation.razor': 'a decision queue bounded by the reconciliation window',
  'Admin/Staff/StaffRota.razor': 'a WEEK GRID, not a list: bounded by the period and navigated rather than scrolled, and a slot in a cell is not a table row',
  'Reports/VisitorReport.razor': 'a report of aggregates over a chosen range — visits by day, top hosts, by type, by company — not a growing list',
  'Admin/Staff/StaffReports.razor': 'the same, for staff performance',
  'Admin/Staff/Timetable.razor': 'the timetable grid, bounded by the cycle',
  'Admin/Staff/Lessons.razor': 'lessons for a chosen day, bounded by the bell schedule',
  'Admin/Staff/StaffMinutes.razor': "one meeting's minutes; the loops are its own sections, decisions and actions",
  'Admin/Staff/StaffTimeline.razor': "one person's file; it pages through QTimelinePaging",
  'Admin/WelfareCategoriesSetup.razor': "a school's own welfare categories, grouped by case type and set once — about twenty rows with a colour each",
  'Pages/Platform/ModuleCatalog.razor': "the platform's modules; there are six",
  'Admin/ApiClientsSetup.razor': 'the API keys a tenant has issued — a handful, and each row is a secret rather than a record',
  'Admin/Staff/StaffParameters.razor': 'the scoring parameters, about a dozen, ordered by hand',
  'Admin/CountersSetup.razor': 'the counters of ONE branch',
  'Admin/ServiceTypesSetup.razor': 'the service types of ONE branch',
  'Admin/BranchesSetup.razor': "a tenant's branches, already a table; a chain with enough branches to need a search would need server paging, which is API work rather than a sweep",
  'Admin/DocsManagement.razor': 'the platform help centre — curated articles written by the platform team',
  'Admin/Staff/StaffStructure.razor': 'departments and the reporting tree of one branch',
  'Admin/ClassTeachers.razor': "one row per CLASS, so a school's thirty; the coverage warnings above it are the way in",
  'Pages/Portal/Portal.razor': "one person's own workspace, bounded by their own record",
  'Pages/Calendar/CalendarView.razor': 'a MONTH GRID and a term agenda, bounded by the period and navigated rather than scrolled; category chips and a search filter it, and an event is edited one at a time',
  'Pages/Calendar/CalendarSettings.razor': "a settings form: the school's own calendar categories, about eight",
  'Admin/Calendar/ProgrammeImport.razor': "an import WIZARD over one term's documents: its tables are a preview of what is about to be written, bounded by the term, and the day-by-day agenda already steps with 'Show more days'; the import history grows by a few jobs a term. Paging a preview would hide the rows somebody is being asked to confirm (ruled out 2026-09-23, when the scale detector stopped accepting .Take)",
  'Pages/GetApp.razor': 'the mobile app download page; its one loop is the earlier releases, a handful, each a download link rather than a record',
  'Pages/Platform/NationalCalendar.razor': 'a year of national dates — three terms, the holidays and UNEB — edited in place; year chips and a search filter it',
};

/** Pages that have been through the sweep. `waive` = faults that are decisions, with the reason. */
const DONE = {
  'Admin/Staff/StaffRegister.razor': { waive: [], why: 'the page the sweep came from' },
  'Admin/StudentRoster.razor': { waive: [], why: 'the page this detector got WRONG: it passed on s.Flags.Take(3) while drawing every student (1,711 at Maryhill) with no paging. It fails scale until the roster rework (plan STUDENT_ROSTER_AND_LIST_STANDARD §2) puts it on QPager, and must hold at zero after' },
  'Admin/VisitorAuditLog.razor': { waive: ['bulk'], why: 'a deletion log is read-only; a selection would have nothing to apply' },
  'Admin/Staff/StaffDirectory.razor': { waive: ['bulk'], why: 'role, permission and deactivation changes live in Users & Roles by decision (2026-09-19); the directory is deliberately narrower' },
  'Admin/Onboarding/JoinRequests.razor': { waive: [], why: 'bulk approve applies one role and branch to the ticked people and names any the guard refuses' },
  'Admin/Onboarding/StaffOnboardingPage.razor': { waive: [], why: 'one list under QBulkBar - the chips pick expired, not signed in or not acknowledged, and re-issue applies to the ticked (2026-09-23)' },
  'Admin/WelfareOpenActions.razor': { waive: ['bulk'], why: 'closing a welfare follow-up is a decision per child with its own record; a bulk resolve would be the sweep inventing a safeguarding action' },
  'Admin/ExpectedVisitors.razor': { waive: ['bulk'], why: 'the action here is checking ONE person in at the desk' },
  'Admin/Staff/StaffNotices.razor': { waive: ['bulk'], why: 'a notice is written, published and retired one at a time; the acknowledgement work is inside a notice' },
  'Admin/Staff/DutyReportsQueue.razor': { waive: ['bulk'], why: 'reviewing a duty report is reading it and writing a remark' },
  'Admin/Staff/StaffRecords.razor': { waive: ['bulk'], why: 'a staff record is append-only and is annulled one at a time with a note; export is the multi-row action and is already on the page' },
  'Admin/StudentWelfareTimeline.razor': { waive: ['bulk'], why: 'a chronology is read, not acted on in bulk; it already pages through QTimelinePaging' },
  'Pages/Platform/RegistrationReview.razor': { waive: ['bulk'], why: 'a wrongly refused sign-up is a lost customer who cannot appeal (CLAUDE.md), so each is decided with its own reason' },
  'Content/MediaLibrary.razor': { waive: ['row','bulk'], why: 'a cover image is not a table cell, so the card stays; deleting media is one decision at a time because a file may be on a playlist or a share link' },
  'Admin/VisitorManagement.razor': { waive: [], why: 'a day of front-desk traffic, now paged; its search and status filters were already there' },
  'Admin/WelfareReports.razor': { waive: [], why: 'a term of safeguarding records, now paged — which narrows what the batch bar can select as well as what is drawn' },
  'Content/DisplayZones.razor': { waive: ['bulk'], why: 'a display is configured one at a time; its zones are inside it' },
  'Admin/Staff/StaffSubjects.razor': { waive: ['bulk'], why: 'a subject is retired one at a time because a live one holds lessons' },
  'Content/Playlists.razor': { waive: ['row','bulk'], why: 'a playlist card carries its cover and item count, which is not a table row; a playlist is edited one at a time' },
  'Content/Campaigns.razor': { waive: ['row','bulk'], why: 'the same, for campaigns' },
  'Content/Schedules.razor': { waive: ['row','bulk'], why: 'the same, for the schedule picker; the week grid below it is bounded by the week' },
  'Admin/CampaignMarketing.razor': { waive: ['bulk','search'], why: 'broadcasts are paged now; a sent broadcast is not acted on again, and the list is short enough per page to read' },
  'Pages/Platform/Tenants.razor': { waive: ['bulk'], why: 'already SERVER-paged; a tenant is suspended or edited one at a time, with a reason' },
  'Admin/UsersSetup.razor': { waive: ['row','search'], why: 'the users list inside it is the Users tab of the hub and carries its own filters; the loops flagged here are the ROLE cards' },
  'Pages/Notifications.razor': { waive: ['bulk','search'], why: 'Mark group read IS the bulk action and a per-row selection would add nothing; the list is SERVER-paged by event key, so a client search would search one page and look like it searched all — a real search needs the API to take a query' },
};

const files = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})('src/Q-Mgr.Web/Components');

const rows = [];
for (const file of files) {
  const rel = file.replace(/\\/g, '/');
  if (EXCLUDE.test(rel)) continue;
  const s = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
  if (!/@page|\[Parameter\] public bool Embedded/.test(s)) continue;

  // A LIST page: a foreach whose body carries a per-row action. A loop that only prints text
  // (a summary, a legend, a chart's labels) is not what this sweep is about.
  const loops = [...s.matchAll(/@foreach\s*\(([^)]*)\)\s*\n?\s*\{/g)];
  let listLoops = 0;
  for (const m of loops) {
    const body = s.slice(m.index, m.index + 2500);
    if (/<QButton|<button|QCheckbox|NavigateTo/.test(body)) listLoops++;
  }
  if (listLoops === 0) continue;

  const width = /max-width:\s*\d+px/.test(s) && /margin:\s*0 auto/.test(s);
  const bulk = !/QBulkBar[\s\S]{0,600}ShowSelection="@?true|QBulkBar[\s\S]{0,600}ShowSelection="@\w|QBatchDialog|ApplyToSelected|selectedIds|selected\.(Add|Contains|Count)/.test(s);
  // Only the three real mechanisms count (plan STUDENT_ROSTER_AND_LIST_STANDARD §4). It used to accept any
  // `pageSize`, `PageSize` or `.Take(`, and the student roster passed on `s.Flags.Take(3)` — three chips in
  // a row — while drawing all 1,711 students. A detector that accepts a word near the right idea is how
  // that hid.
  const scale = !/<QPager\b|<Virtualize\b|QTimelinePaging/.test(s);
  const search = !/QBulkBar|QFilterBar|placeholder="Search|Placeholder="Search|searchTerm|search\b/i.test(s);
  // A CARD-PER-ITEM grid, and only that. Any auto-fit grid looks alike in CSS — a row of stat
  // tiles, a form grid, an hours editor, a colour palette — so the class has to be shown to be
  // WRAPPING THE LIST: its name must appear within a few hundred characters of a @foreach.
  // Measured twice, after RegistrationReview was flagged for its stats strip and BranchesSetup for
  // its opening-hours form while both already rendered their list as a table.
  const gridClasses = [...s.matchAll(/([^{}\n]*)\{[^{}]*grid-template-columns:\s*repeat\(auto-(?:fill|fit)/g)]
    .map(m => m[1].trim())
    .filter(sel => !/stat|tile|summar|metric|kpi|form-|widget|chart|swatch|preview|legend|theme|hours|palette|colou?r/i.test(sel))
    .flatMap(sel => [...sel.matchAll(/\.([a-zA-Z0-9_-]+)/g)].map(m => m[1]));

  const wrapsAList = gridClasses.some(cls => {
    for (const m of s.matchAll(new RegExp(`class="[^"]*\\b${cls}\\b[^"]*"`, 'g'))) {
      if (/@foreach/.test(s.slice(m.index, m.index + 400))) return true;
    }
    return false;
  });
  const card = wrapsAList && /flex-direction:\s*column/.test(s);

  const faults = [width && 'width', card && 'row', bulk && 'bulk', scale && 'scale', search && 'search'].filter(Boolean);
  rows.push({ page: rel.replace('src/Q-Mgr.Web/Components/', ''), listLoops, faults });
}

rows.sort((a, b) => b.faults.length - a.faults.length || b.listLoops - a.listLoops);

const pad = (s, n) => String(s).padEnd(n);
const regressions = [];
const swept = [];
const ruledOut = [];

console.log(`\n${pad('page', 52)}${pad('lists', 6)}faults`);
console.log('-'.repeat(100));
for (const r of rows) {
  if (OUT_OF_SCOPE[r.page]) { ruledOut.push(r.page); continue; }
  const done = DONE[r.page];
  if (done) {
    const unwaived = r.faults.filter(f => !done.waive.includes(f));
    if (unwaived.length) regressions.push({ ...r, unwaived, why: done.why });
    swept.push(r.page);
    continue;
  }
  console.log(`${pad(r.page, 52)}${pad(r.listLoops, 6)}${r.faults.join(', ') || '—'}`);
}

const outstanding = rows.filter(r => !DONE[r.page] && !OUT_OF_SCOPE[r.page]);
const worst = outstanding.filter(r => r.faults.length >= 3);
console.log(`\n${rows.length} list page(s): ${swept.length} swept, ${ruledOut.length} ruled out of scope, ${outstanding.length} outstanding, ${worst.length} of those carrying three or more faults.`);

// A DONE page the scan no longer sees is usually a PASS, not drift: the detector only counts a
// loop that carries a per-row action, and a read-only log swept into a plain table has none left.
// Real drift is a name in DONE with no file behind it.
const missing = Object.keys(DONE).filter(p => !fs.existsSync(path.join('src/Q-Mgr.Web/Components', p)));
if (missing.length) {
  console.log(`\nDONE names ${missing.length} page(s) that no longer exist: ${missing.join(', ')}`);
  process.exit(1);
}
const seen = new Set(rows.map(r => r.page));
const quiet = Object.keys(DONE).filter(p => !seen.has(p));
for (const p of quiet) swept.push(p + '  (no per-row action left to count)');

if (swept.length) {
  console.log('\nSwept, and holding:');
  for (const p of swept) {
    const key = p.split('  ')[0];
    const w = DONE[key].waive;
    console.log(`  ${pad(p, 50)}${w.length ? `waives ${w.join(', ')} — ${DONE[key].why}` : DONE[key].why}`);
  }
}

if (regressions.length) {
  console.log('\nREGRESSIONS — a swept page has a fault back:');
  for (const r of regressions) console.log(`  ${r.page}: ${r.unwaived.join(', ')}`);
  process.exit(1);
}
console.log('\nNo regressions on the swept pages.');

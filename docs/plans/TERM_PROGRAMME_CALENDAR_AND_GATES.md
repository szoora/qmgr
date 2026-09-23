# Term programme, calendar, rota import and gates

**Status: PLAN v2, decisions D1–D12 settled 2026-09-23 ("implement your recommendations on the plan for
the decisions"). Build started 2026-09-23.** The designed version of this plan is the artifact
https://claude.ai/artifact/QmFHYh8MKaAtgC77St4CDT; this file is its durable copy in the repository.

> *"do we have a feature for updating the school program, and a kind of events calendar … make a
> comprehensive analysis of the documents … make enhancements on the existing bulk data import
> infrastructure, make it very intelligent to read such information from multiple document formats …
> previews of the expected result, duplicate checks and warnings and confirmation prompts … hydrate into
> the staff workspace, myday, registers, rota … for staff rota, the feature should nicely match the staff
> name to the date of duty"* — then *"to the visitor management module, add the gate, gate man should
> select the gate used by the visitor on entry."*

**The source documents are in `D:\QMGR\DATA` and carry 81 staff phone numbers. They are NEVER copied into
this repository** (public GitHub remote). Suites read them from `E2E_DOCS_DIR` and skip when it is unset.

---

## 0. The short answer (2026-09-23)

There was no school calendar, no events entity, no ICS anywhere, and nothing could read a Word document.
The staff half existed: meetings are `StaffDuty` Session duties, Teacher on Duty is a Rota slot, terms are
Staff Performance policy periods, and all of them already reach My School Day, registers and reminders.
So most of this plan ENHANCES; two things are new: a `SchoolEvent` table, and a document reader in front
of the existing import wizard.

## 1. The five documents (measured, not estimated)

| Document | Rows | A row is | Lands as |
|---|---|---|---|
| Activities of Term III 2026 (.docx) | 27 (+2 empty weeks) | an event under a merged week band | `SchoolEvent`; staff items also to My School Day |
| Beginning of Term III Programme (.docx, file name says 2025) | 25 + 1 in a footnote | a timed item of a 3-day programme | `SchoolEvent` series |
| Schedule of Meetings Term III 2026 (.docx) | 28 (18 staff meetings) | a meeting / briefing / retreat | staff meetings → Session duties; the rest → events |
| Staff Duty Rota Third Term 2026 (.docx) | 83 people, 58 with dates, 85 assignments | a person and 1–2 duty dates `DD/MM/YYYY` | daily Rota slots "Teacher on Duty" |
| Administrative Weekly Duty Rota (.doc, Word 97–2003) | 12 weeks | an administrator for a Roman-numeral week | weekly Rota slots "Administrator on Duty" |

Hard parts: merged cells carry meaning (week band, shared date/owner); four date shapes incl. ranges and a
year-only "2026"; two "2025" year typos in a 2026 document; am/pm carried from the end time only
(`8:00 – 5:00 pm`); open ends (`11:30 am onwards`); four parallel sessions under one merged time; an event
hidden in a footnote; owners are offices ("Biology HOD", "Academic Office"); in the rota the date column
also holds job titles for 21 people and 4 are blank; 76 honorifics (Mr/Ms/Sr/Mrs/Fr), name order varies;
7 phones lost the leading zero, 2 blank; the .doc is binary.

**47 of 47 weekday+date pairs are internally consistent**, so the weekday is a check digit: it is what
repairs "23rd – 29th November 2025" (a Sunday in 2025, a Monday in 2026, and the bands start on Mondays).

## 2. Across the documents

- **Conflict**: S.4 UCE briefing Thu 8 Oct (Activities; matches UNEB's 2026 timetable) vs Mon 12 Oct
  8:30am (Meetings). Ask, never pick.
- **Duplicates**: both retreats, MOGA guidance, start-of-term assemblies, UACE briefing appear in 2–3 docs →
  merge to one entry each.
- **Gap**: Wed 2 Dec has no Teacher on Duty. **Doubles**: Sat 12 Sep, Mon 2 Nov.
- **Suggestion**: Musiime Naomeh `02/11 → 02/12` fills the gap AND clears the Mon 2 Nov double — offered,
  never applied automatically.
- **Agrees**: MoES Term III 14 Sep – 4 Dec 2026.
- **Links**: the rota's 11 "Prep Supervisor" rows are the attendance of the "Prep Supervisors Meeting";
  its 5 "Administrator" rows confirm the admin rota's names. "Ms Asiimwe Aisha Peace" = "MRS. TWINOMUJUNI
  AISHA ASIIMWE PEACE" (married name); "Grace T." = "GRACE TUSHABE".

## 3. Where each thing lives (enhance before add)

| Content | Home | Change |
|---|---|---|
| Staff meetings | `StaffDuty` Session, "Meeting attendance" parameter ensured like Teacher on Duty | existing |
| Teacher / Administrator on Duty | `StaffDuty` Rota, one `SeriesId` per imported rota | existing |
| Term dates + theme | `PerformancePeriodDto` + `Theme` | one field |
| Venues | Rooms vocabulary, else `Location` text | existing |
| Import batch | `RosterImportKind.Programme = 5` (appended) | one enum value |
| Learned aliases | `Organization.Settings["ImportAliases"]` via `OrganizationSettingsLock.MutateAsync` | one key |
| School events / programme items | **new `SchoolEvent` table** | new |
| Gates | `BranchVocabulariesDto.Gates` | one list |
| Gate + who, on a visit | `Visitor.EntryGate`, `ExitGate`, `CheckedInByUserId`, `CheckedOutByUserId` | four columns |
| Personal calendar feed | `User.CalendarFeedTokenHash` | one column |
| National dates | platform setting `NationalCalendar` | one settings row |

Events cannot be a duty kind: every duty carries a `ParameterId` and every register/reminder/scoring query
assumes staff are expected and marked. `SchoolEvent`: OrganizationId, BranchId?, Title, Description?,
StartsOn/EndsOn (date) + StartTime?/EndTime?, Category (vocabulary text), Audience flags
(Staff/Students/Guardians/Public) + ClassNames[], Location?, ResponsibleText + ResponsibleUserIds[] +
ResponsibleDepartmentIds[], DutyId?, SeriesId?, ImportJobId?, SourceKey.

## 4. Reading any document

Pipeline: file → reader → `ImportDocument` (tables of cells with row/col spans + paragraphs around them)
→ structure (fill merges, header, title, notes) → classify (activities / programme / meetings / person
rota / period rota; overridable, remembered) → parse values → resolve (people, offices, rooms, term) →
check → preview → confirm → write one batch with one undo.

| Format | Reader | Dependency |
|---|---|---|
| .xlsx .xls .csv .json .html | SheetJS in the browser (unchanged) | none new |
| .docx | .NET `ZipArchive` + `XmlReader`, `w:gridSpan` / `w:vMerge` (ECMA-376) | none |
| .doc | hand-written [MS-CFB] + [MS-DOC] piece table; cells by `0x07`, rows by `sprmPFTtp` in PAPX | none |
| .pdf (text) | pdf.js (already shipped for the flip-book) text runs clustered to rows/columns | none new |
| scanned PDF / photo | refused with a sentence (D3) | — |

The document readers also feed the EXISTING `QImportPanel`, so the staff and student imports accept Word
and PDF tables too.

## 5. Parsers (one home each, `Q-Mgr.Shared`)

Dates: weekday optional, ordinals/abbreviations/full stops; ranges are one event; missing year from the
document's term, checked against the weekday; day-first numeric dates; "X to Y" spans ≤92 days; week bands;
Roman numerals. Times: am/pm carried back from the end unless that puts start after end; open ends; single
moments. Merged cells filled only within their region. Notes containing a date and a time become candidate
rows marked "found in a note".

## 6. Matching people (never split a name; compare sets)

1 staff number/email/username · 2 phone (`UgandaPhone`, restores a lost zero) · 3 same word set minus
honorifics · 4 initials agree · 5 most words agree, Jaro–Winkler per word → SUGGEST, confirm · 6 one word or
several fit → ASK · 7 not on staff → offer "Add to staff" or skip. Confirmed matches become aliases. A rota
never edits the directory. Offices resolve to departments/posts once and are remembered; unmapped stay text.

## 7. Checks

Must decide: documents disagree; rota gap; weekday/date disagree; undated row. Warnings: duplicates (merged
by default), double booking, person busy (`StaffRota.CheckAsync`), outside term/holiday, whole-school event
on a teaching day ("lessons still run", D6), differs from the national calendar. Updates: a re-import
matches `SourceKey` and shows exactly what changes; nothing the new file omits is deleted unless "replace
this term's programme" is ticked. Suggestions: only a one-edit fix that resolves a problem and creates none.

## 8. Preview and confirm

Issues with inline decisions; a term calendar preview; a per-person preview; the last press states counts
("Create N events, 18 meetings and 97 duty slots, and tell 63 people their dates"); one undo (ImportJobId +
SeriesId) except duties whose register was taken.

## 9. Where it shows up

Rota slots → rota page, My School Day "On duty", My Workspace, acknowledgement, `RotaStart` reminder, duty
report, timetable print sheet. Meetings → register, minutes, `SessionStart` reminder, "Coming up". Events →
Calendar page (term/month/agenda), My School Day, "Coming up", term-programme print sheet, Library.
Everything personal → private RFC 5545 feed. Public events → signage zone "Coming up". ONE notification per
person after confirm ("You are Teacher on Duty on Wed 23 Sep and Wed 11 Nov"), never one per slot.

## 10. Gates at visitor check-in

Visits recorded no gate AND no person. Gates are a branch list (Rooms pattern, one writer
`PUT …/visitors/gates`, `UpdateVocabularies` keeps them, retired never deleted). Required with ≥2 active
gates, filled with 1, hidden with 0; server validates against the branch's active gates and the form
refuses the same thing first. The device remembers its gate (localStorage, wrapped) and it stays visible.
Check-out records exit gate + who. Pre-registered and pass check-ins record it too. Surfaces: dialogs,
list column + filter, detail, live board, reports by gate / hour-by-gate, export column, evacuation grouped
by entry gate, rota `Location` may be a gate.

## 11. Decisions — settled 2026-09-23

- **D1** Events get their own `SchoolEvent` table. **D2** .doc is read (fallback message: save as .docx).
- **D3** No OCR. **D4** No AI model. **D5** Staff first + Public on signage; other audiences stored, not
  shown. **D6** Whole-school events warn, never cancel lessons. **D7** Teachers on Duty per day is a school
  setting, default 1. **D8** Calendar is base product; rota/meeting import is Welfare & Performance.
- **D9** National dates kept yearly by the platform administrator, warnings only. **D10** Gates are a
  branch list. **D11** Gate required where there is a choice. **D12** Exit gate and who saw them out too.

## 12. Phases

G gates (independent) · 0 readers · 1 parsers + matching · 2 calendar · 3 import · 4 feed, signage zone,
national calendar. Proof: API e2e sections for gates, calendar and programme import; browser suites for
gates, calendar and the five-document import (preview counts, conflict, gap + suggestion, married-name
question, import, re-import = 0 new, undo = 0 left).

## 13. Build record (2026-09-23)

Built in one pass by four parallel slices, compiled once at the end (2 trivial compile errors, 1 Razor child-
content error), migration `AddCalendarProgrammeImportAndGates` (additive: one table, seven columns).

- Section 29 gates 59/0 · section 30 calendar 107/0 · section 31 programme import 80/0 · browser: visitor-gates
  22/0, calendar-ui 42/0, programme-import 24/0 (commit, re-import = nothing new, undo).
- Differences from the plan: the signage "Coming up" panel is switched on by the signage link (`?events=1`, a
  switch on Customer Links) because displays render no zones by type; rota rows are never updated in place (a
  changed slot is created beside the old one with a warning); undo does not reverse UPDATES an import made to
  existing rows; meetings with no end time last one hour, with a warning; the phone key sent to the browser for
  matching is a salted one-way hash, never the number; a "Tell each person their dates now" switch was added to
  the confirm step (a school may load next term early).
- Found and fixed on the way: bulk visitor check-out never set Status; a meeting expecting nobody was refused
  only at submit.

# Bulk import: one shared component for every page that takes a spreadsheet

**Artifact:** https://claude.ai/artifact/3qt7ChKC15ZeXxL7NRLtSA (revision 2 carries the build result)
**Status:** written and BUILT 2026-09-21. Supersedes nothing — `QImportPanel` (2026-09-19) is the
foundation this builds on, not a thing to replace.
**Asked for:** *"implement a modern import system that allows mapping of the columns dynamically …
the excel import feature should be a shared component as it will be used on so many features on this
project … the upload process must go through some steps to sanitise the data"*, then *"aggressive
validations, dynamic column mappings, clearly show the mandatory fields, ability to split or
concatenate columns into the mappings … validations should be both in ui and backend"*, and
*"among the import file types, the feature should also support json."*

---

## 0. The two real files this has to swallow

Both were supplied on 2026-09-21 and both defeat the importer as it stands today. They are the
acceptance test, not an illustration.

| | `Staff List.xlsx` | `Students_2026-09-21.xls` |
|---|---|---|
| What it really is | xlsx, one sheet, 184 staff | **an HTML table** with an `.xls` name, 1,711 students |
| Row 1 | `Staff List` — a title, not headers | headers |
| Header row | **row 2** | row 1 |
| Name | **one column**, `Staff Name`, `Abaho Jude`, `Ahabyoona Katah Egidious` — **surname first** | one column, `Student Name`, `ABAASA BARBRA` — **surname first, shouted** |
| Class | — | **two columns**, `Class` = `S1` and `Stream` = `A`; Q-Mgr stores one `S1A` |
| Other | `Sex`, `Department`, `Mobile` (`0772945515`), `Email` (missing on many rows), `PAYE` (`▲ 40% Govt`), `Status` | `Type`, `Gender`, `Status`, `Payment Code` |

Four separate reasons today's importer cannot read them, each of which is a design requirement:

1. **The header row is assumed to be row 1.** `Staff List` in A1 means every declared column is
   unmapped and every row is refused.
2. **The staff import demands `FirstName` and `LastName` as separate columns.** This file has one
   name column, in the order this country writes names.
3. **Nothing can join two columns into one field** (`Class` + `Stream` → `ClassName`).
4. **Nothing can be re-mapped by hand.** A header we do not recognise is simply lost, with no way for
   the person holding the file to say what it is.

---

## 1. What exists today, stated honestly

`Components/Shared/UI/QImportPanel.razor` already owns the client-side journey for **all three**
existing imports — the student roster, the staff list and the welfare history — and that consolidation
(2026-09-19) is why this plan is an extension rather than a rewrite:

- one file picker, one preview table, one tally, one progress bar over SignalR with a slow reconciling
  poll behind it, one row-by-row result;
- SheetJS reads the workbook **in the browser**, so the server needs no Excel library — the standing
  no-server-dependencies rule, and it also means **nothing is uploaded until the person presses
  Import**, which is the privacy posture every commercial importer now advertises;
- `ImportColumn` carries the header aliases, so `Student Code` / `student_code` / `StudentCode` are
  one column;
- a row builder per page (`BuildRow`) turns mapped cells into a typed row with a blocking **error** or
  a non-blocking **warning**, and the server re-validates everything it is sent.

What it cannot do: choose a sheet, find the header row, let a person map anything, join or split a
column, sanitise systematically, read JSON, or say what it cleaned.

---

## 2. What modern importers do, and which of it we are taking

Sources at the end; each row says what it buys us.

| Practice | Where it comes from | Taken? |
|---|---|---|
| Four steps: **upload → header row → map columns → review/fix** | OneSchema, "5 best practices for building a CSV uploader"; CSVBox's canonical flow | **Yes** — plus a sanitise step of our own, made visible rather than silent |
| **Suggested mappings** by fuzzy match *and by what this customer mapped last time* | OneSchema: *"suggested column mappings, typically based on fuzzy matching or historical uploads"* | **Yes** — aliases first, remembered mapping second |
| Three kinds of validation: **type**, **cross-column**, **referential** | OneSchema | **Yes** — and all three run twice, in the browser and on the server |
| 50+ no-code validations and transformations for "the messiest spreadsheets" | OneSchema's product | **In part** — a fixed, documented set (§5), not a rules engine |
| Row-level errors pointing at the exact cell, with the fix stated | CSVBox, OneSchema | **Yes**, with the line number **from the reader's own file** |
| Schema-first: required columns declared, headers case-sensitive, UTF-8, RFC-compliant CSV | 1EdTech **OneRoster 1.2 CSV binding** (the rostering standard SIS vendors implement) | **Partly** — we keep the schema-first discipline and UTF-8/RFC 4180 parsing, but deliberately **not** case-sensitivity: a school's own export is not a standards body's file |
| **Do not split names if you can avoid it**; if you must, label the halves honestly, never assume order, never require a family name | **W3C i18n, "Personal names around the world"**: *"it will be simpler, where it is possible, to just use the full name as the user provides it"*; *"avoid using the labels 'first name' and 'last name'"* | **Yes**, and it settles the student/staff asymmetry (§4) |
| **Never emit a cell starting `=`, `+`, `-`, `@`, tab or CR** into a spreadsheet | **OWASP** CSV Injection / WSTG, CWE-1236 | **Yes** — on our template downloads and every export, not on import |
| JSON/NDJSON read by flattening nested keys to **dot paths** (`guardian.phone`), one object per row | ClickHouse/pandas `json_normalize`, DuckDB's JSON reader, NDJSON convention | **Yes**, with limits (§7) |

**Where we deliberately differ from the commercial tools.** Flatfile and OneSchema both put an
editable spreadsheet in the browser so a person can fix cells in place. We are not building that in
this pass: it is the single largest piece of UI in those products, and on Blazor Server every cell
edit is a round trip over a SignalR circuit — the wrong shape for a 1,711-row file on a school's
connection. Our answer is **fix-by-rule, not fix-by-cell**: the sanitisers handle the mess that is
mechanical (spacing, capitals, phone shapes, invisible characters), and anything else is reported
against a row number the person can find in their own spreadsheet. If it turns out schools want
in-browser editing, it is an additive Phase 7, not a change of shape.

---

## 3. The steps

```
 1 FILE          2 SHEET & HEADER      3 MAP                4 CLEAN              5 CHECK            6 IMPORT
 ─────────       ─────────────────     ─────────────        ──────────────       ─────────────      ──────────────
 .xlsx .xls      which sheet           field ← column(s)    what will be         per-row errors     send · progress
 .csv .json      which row is the      required marked      changed, counted,    and warnings,      · created /
 .ndjson         header (detected,     split / join         each switchable      filter by state    updated /
 read in the     overridable)          remembered                                                   failed log
 browser         "using row 2"
```

Steps 2 and 4 are skipped in the UI when there is nothing to decide — one sheet, a header found on
row 1, nothing to clean — so the ordinary case stays the two clicks it is today. **A step that has a
decision in it is never skipped**, because the point of the step is that the person, not the
software, made the call.

---

## 4. Mapping: the part the whole feature turns on

**Model** (`ImportMapping`, already written): a dictionary of *declared field* → `ImportBinding`
`{ Sources: int[], Joiner: string }`, plus the file-level `NameOrder`, `FixShouting` and
`NormalisePhones`.

- **One column → one field** is the ordinary case, auto-matched by alias.
- **Many columns → one field** is `Sources.Length > 1` with a `Joiner`: `Class` + `Stream` → `S1A`
  (joiner none), `Surname` + `First Name` → a full name (joiner space). This is the "concatenate"
  half.
- **One column → many fields** is the "split" half, and it is done by *field kind* rather than by a
  generic splitter, because a generic "split on character N" is a sharp tool that produces silent
  rubbish. Today there is exactly one real case — **a combined name** — and it is handled by
  `PersonName.Split` with the order the person chose (§4.1). A second case (an address, a
  "Class/Stream" cell written `S1/A`) is added the same way: a declared kind with a rule and a
  preview, not a free-text formula box.
- **Mandatory is loud and is checked before anything else.** A required field shows a red
  `Required` badge in the mapping list, an unmapped one blocks the Continue button, and the message
  names the field in the reader's words. `SatisfiedBy` expresses *"this, or that"*: `FirstName` is
  required **unless `FullName` is mapped**, which is exactly how the staff file above becomes legal.
- **Unused file columns are listed**, so a person can see that `PAYE` and `Payment Code` were read
  and ignored rather than silently dropped.
- **The mapping is remembered** per import kind against a fingerprint of the header row
  (`ImportParsing.Fingerprint`), in `localStorage`. The same export next term maps itself; a
  *different* file shape gets the automatic guess instead of somebody else's answer applied to the
  wrong columns.

### 4.1 Names: the one place where we follow the standard by *not* splitting

W3C's guidance is unambiguous — keep the name whole where you can, and never infer order. So:

- **Students keep one `FullName` and are never split.** That is already true in the schema and it is
  now a deliberate, cited decision rather than an accident.
- **Staff are split only because `User` has `FirstName`/`LastName`** (sign-in, addressing, the MoES
  staff return). Where a file gives one column, `PersonName.Split` fills the two, and:
  - **the order is ASKED, never guessed** — `Given first` / `Family first`, with the first three rows
    of the actual file shown both ways, because "Abaho Jude" is surname-first in Kampala and
    given-name-first in Cork and nothing in the string says which;
  - **a comma always wins** and always means `Family, Given`;
  - particles stay with the family name (`van der`, `de la`, `bin`), honorifics are stripped, a
    generational suffix stays attached, and **a one-word name is a warning, not a refusal**;
  - the original string is kept on the row, so the import log shows what the file actually said.

---

## 5. Sanitising: mechanical, visible, and reversible

Two tiers, and the distinction is the whole design:

**Always, with no switch** — because nobody ever meant these:
trim; collapse runs of whitespace; strip zero-width and control characters (an invisible `U+200B`
from a copy-and-paste makes two identical-looking names compare as different, which is how the same
person is imported twice); drop entirely blank rows; drop rows that are a repeat of the header.

**Switchable, shown with a count of what it changed** — because a person might have meant them:

| Switch | What it does | Default |
|---|---|---|
| Fix SHOUTING | `ABAASA BARBRA` → `Abaasa Barbra`, never touching a name that is already mixed case, so `McDonald`, `O'Brien` and `van der Berg` survive | on |
| Normalise phone numbers | `0772945515`, `256 772 945515`, `+256-772-945515` → `+256772945515` | on |
| Lower-case email addresses | trim and case-fold | on |
| Day-first dates | `03/04/2026` is 3 April, the same precedence the welfare import already uses | on |

The Clean step prints what it did — *"168 names tidied out of capitals · 173 phone numbers
normalised · 12 values with stray spacing"* — so the transformation is a statement, not a secret.

---

## 6. Validation: aggressive, and the same rules on both sides

**The rule that makes "both in UI and backend" real: the validators live in `Q-Mgr.Shared` and both
sides call them.** A second copy on the server is how the two drift — this codebase's most-repeated
bug class (`OrganizationBrandingDto`, `ContentDto`, `NotificationDto`, `UserInfo`, `RoleListDto`, and
two disagreeing column-alias maps inside a single JS file). So `ImportRules` moves to Shared and the
API's import processors call exactly the functions the browser called.

Four layers, in order:

1. **File** — readable, under the size cap, a sheet chosen, a header row found, every required field
   mapped. One message for the file, not one per row.
2. **Cell/type** — email shape, phone readable, date parseable day-first, number numeric, enum in the
   allowed set (matched case-insensitively and reported with the allowed values), length within the
   column's limit.
3. **Row/cross-field** — the rules that need more than one cell: an end date not before a start date,
   a class teacher's class present in the vocabulary, a line manager's email present in this same
   file or already a user, `FirstName`+`LastName` **or** `FullName`.
4. **Set/referential** — duplicates *within the file* (first kept and warned, later ones refused, so
   it is visible which row won), duplicates *against the database* (an existing email, student code
   or employee number → "will be updated" rather than "will be created"), and foreign keys that must
   exist (department code, role code, subject code).

**Server-side, every one of those runs again** on the rows actually posted, because the browser is
not a security boundary. The existing import jobs already re-validate; what changes is that they call
the shared rules and return the *same sentence* the browser would have shown, so a person never sees
two different explanations of one problem. The server additionally owns what the browser cannot know:
tenant/branch ownership, the module gate, the permission (`staff.structure.manage`,
`students.manage`), the **refusal of a bulk import by a row-scoped caller** (a class teacher cannot
start one — the standing rule, since a Hangfire worker has no `IStudentScopeService`), the row cap
per job, and idempotency.

**Blocking vs warning, stated once and applied everywhere:** a row is **refused** when what it is
missing cannot be inferred and would create a half-formed record (no email for a staff member — the
email is the identity); it is **warned** when the import can proceed truthfully (an unknown
employment type that will be left blank, a name we could only read one half of). A warned row still
imports and the warning is kept in the job log.

---

## 7. JSON — yes, with limits, and here is the reasoning

**Worth doing.** A `.json` export is what another *system* produces — a SIS, a payroll package, our
own API — where `.xlsx` is what a *person* produces. Supporting it turns the importer into the
lightest possible integration path for a school whose data already lives somewhere with an API, and
costs almost nothing here because the file is read in the browser and flattened into exactly the same
grid every other format produces. Our own exports become re-importable, which is the cheapest
round-trip test a data model can have.

**What we accept:**
- an **array of objects** — the ordinary case;
- an **object wrapping an array** (`{ "data": [...] }`, `{ "records": [...] }`, `{ "items": [...] }`),
  detected by looking for the single array property;
- **NDJSON / JSON Lines**, one object per line, because that is what streaming exports emit.

**How it becomes a grid:** keys are flattened to **dot paths** — `guardian.phone` becomes a column
called `guardian.phone` — the union of all rows' keys becomes the header row, and the mapping step
then works unchanged. `null` becomes empty; `true`/`false` become `true`/`false`; numbers are
rendered as written, never re-formatted (a leading zero on a phone number must survive).

**What we refuse, and say so plainly:** an array *inside* a row (`{"subjects":["MATH","PHY"]}`) is
joined with `; ` into one cell and warned about, because inventing rows from a nested array silently
changes the record count; nesting deeper than **five levels** is refused rather than flattened into
unreadable column names; a file that is not an array of objects at the top is refused with the three
shapes we do accept named in the message.

**My honest reservation, for the record:** JSON will be the least-used of the four formats in a
school — nobody hand-writes it and Excel does not emit it. It earns its place as the *integration*
format rather than the *human* one, and it should not be allowed to complicate the mapping step for
the 99% who arrive with a spreadsheet. Hence: same grid, same mapping, no separate journey.

---

## 8. Security, privacy and size

- **CSV injection is an EXPORT problem, and we have exports** — the import template, the row-by-row
  result, every `QDataExport`. Per OWASP/CWE-1236, a cell whose first character is `=`, `+`, `-`,
  `@`, tab or carriage return must be prefixed with a single quote before it is written. This is a
  one-line change in the CSV writer and it is in scope for this plan because the importer ships a
  template download.
- **Nothing is uploaded until Import is pressed.** Parsing in the browser is already true and is now
  a stated privacy property: a mis-chosen file — a payroll sheet, a medical list — never leaves the
  machine.
- **Caps:** 5 MB per file (today's cap, kept), 5,000 rows per job, 200 rows shown in the preview
  while **all** rows are validated. The 1,711-row student file sits inside all three.
- **A row-scoped caller cannot start a bulk import.** Existing rule, restated because every new
  import kind inherits it.
- **PII in the job log**: `RosterImportJobEntry` carries names, so the existing scope rule on import
  history (a scoped caller sees only jobs they started) applies to every new kind too.

---

## 9. How another page adopts it

The contract stays what it is today — declare the fields, write a row builder, say how to start the
job — so adoption is about twenty lines:

```razor
<QImportPanel TRow="VisitorImportRow" BranchId="@branchId" InputId="visitor-import"
              Noun="visitor" Columns="@VisitorColumns" BuildRow="@BuildVisitorRow"
              MappingKey="visitors"
              DuplicateKey="@(r => r.Email)" DuplicateNoun="email address"
              StartAsync="@StartVisitorImportAsync" LoadJobAsync="@LoadJobAsync"
              LoadEntriesAsync="@LoadEntriesAsync" OnCompleted="@ReloadAsync" />
```

```csharp
private static readonly IReadOnlyList<ImportColumn> VisitorColumns = new[]
{
    new ImportColumn("FullName", true, "Name", "Visitor Name") { Kind = ImportFieldKind.Name },
    new ImportColumn("Phone", false, "Mobile")                 { Kind = ImportFieldKind.Phone },
    new ImportColumn("ExpectedOn", true, "Date", "Visit Date") { Kind = ImportFieldKind.Date,
                                                                 Hint = "Day first — 03/04/2026 is 3 April" },
};
```

Everything else — sheet choice, header detection, mapping UI, sanitising, the preview, progress,
the result log, the template download — comes from the component. **A page that writes its own file
input is the drift coming back**, and `scripts/e2e/import-adoption-check.mjs` (Phase 6) fails on one.

---

## 10. Phases

| # | What | State |
|---|---|---|
| **0** | `PersonName` in `Q-Mgr.Shared` — split, join, clean, un-shout; `NameOrder` | **built** |
| **1** | Grid + mapping model: header-row detection, `ImportBinding`, `ImportMapping`, `Resolve`, `Fingerprint`, field kinds, `SatisfiedBy`, `AlsoJoin`, sanitisers with counts | **built** |
| **2** | `rosterImport.js`: sheet list + chosen sheet; JSON/NDJSON reader that flattens to the same grid | **built** |
| **3** | `QImportPanel` steps: file → map (required badges, join, name order with the file's own sample, remembered mapping) → check → import | **built** |
| **4** | Staff import onto it: `FullName` column + `SatisfiedBy`, `StaffImportRow.FullName`, server-side split fallback, update-or-skip for people already here | **built** |
| **5** | Validators moved to `Q-Mgr.Shared` (`ImportRules`) and called by both sides; CSV-injection neutralising in `CsvWriter.Field` | **built** |
| **6** | `scripts/e2e/browser/import-wizard.mjs` driving **the two real files** — 19 checks, 0 failed | **built** |
| **7** | *Not now:* in-browser cell editing, AI mapping suggestions, scheduled/API-driven imports, an `import-adoption-check` guard | deferred, with reasons |

**Verified 2026-09-21 against the two real files**, headless Chrome, 19 checks and nothing skipped:
the staff file's header found on row 2 under its title; `Staff Name` matched to the combined name
field with First/Last showing **Covered**; the name order asked with *"Abaho Jude"* in the question;
**49 ready, 135 refused for a missing email**, every one of the 184 rows accounted for; 164 phone
numbers normalised. The student file: an `.xls` that is really HTML read, `Class` + `Stream` joined to
`S1A`, the SHOUTED names tidied, all 1,711 rows validated. Neither file was imported — reading is
what is under test, and importing 1,711 students on every run would leave a mess for somebody.

**The one thing the files revealed that is not a code problem: 133 of the 184 staff have no email
address**, and the staff import treats an email as the identity a person signs in with. See §11.

## 11. Decided, and the one question left

Answered 2026-09-21 and built as answered: **family first** is the default name order (shown on
screen with the file's own first name in it, every time); **duplicates prompt** — "23 of these are
already here: update them, or add only the missing ones?", defaulting to update, and the staff
importer now updates a person's DETAILS while never touching their role, permissions, branch or
password; **`Class` + `Stream` join to `S1A`** with no separator, as the automatic guess, visible and
undoable in the mapping step; **5,000 rows per job**.

**The open question, and it is the school's, not the code's: 133 of the 184 staff in `Staff List.xlsx`
have no email address.** Q-Mgr treats the email as a staff member's identity — it is what they sign in
with and what an invitation goes to — so those rows are refused rather than half-imported. Three ways
out, in the order I would suggest them:

1. **Collect the addresses.** Best, and what the school will eventually need anyway for payslips,
   notices and password resets. 51 of them already have one.
2. **Let a staff member exist WITHOUT an email**, signing in with a username (Q-Mgr's login already
   accepts either) and receiving their temporary password on a printed slip or by SMS — both of which
   this importer already offers and neither of which needs an address. This is a real product change:
   `Email` would become optional when a username or employee number is present and the delivery is a
   slip or SMS, and everything that emails a member of staff would have to cope with there being
   nobody to email. **It is the right change for a Ugandan school** and it is not a small one.
3. **Generate a placeholder address** (`700001@staff.maryhillug.net`). Cheapest, and it puts an
   address into the system that will bounce — which is the reason to say no to it.

---

## Sources

- OneSchema — [5 Best Practices for Building a CSV Uploader](https://www.oneschema.co/blog/building-a-csv-uploader):
  the four-step flow, header-row selection, suggested mappings from fuzzy matching or historical
  uploads, and the three validation types.
- CSVBox — [Add a CSV Import Feature to Your SaaS App](https://blog.csvbox.io/add-csv-import-feature-saas/),
  [Designing Mobile-Friendly Spreadsheet Import Flows](https://blog.csvbox.io/mobile-friendly-spreadsheet-import-flows/),
  [Privacy-First Approach to User Uploads](https://blog.csvbox.io/privacy-first-user-uploads/): the
  canonical select → map → validate → submit flow and client-side parsing as a privacy property.
- 1EdTech — [OneRoster CSV Binding 1.2.1](https://www.imsglobal.org/spec/oneroster/v1p2/bind/csv) and
  [Implementation & Best Practices Guide](https://www.imsglobal.org/spec/oneroster/v1p2/impl/); see
  also [Microsoft School Data Sync's OneRoster 1.2 CSV ingestion](https://learn.microsoft.com/en-us/schooldatasync/data-ingestion-with-oneroster-1.2-csv):
  schema-first required columns, UTF-8, RFC-compliant CSV.
- W3C Internationalisation — [Personal names around the world](https://www.w3.org/International/questions/qa-personal-names):
  keep the full name whole where possible, do not assume order, do not require a family name.
- OWASP — [CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection) and the
  [Web Security Testing Guide test for it](https://owasp.org/www-project-web-security-testing-guide/latest/4-Web_Application_Security_Testing/07-Input_Validation_Testing/21-Testing_for_CSV_Injection);
  [CWE-1236](https://cwe.mitre.org/data/definitions/1236.html).
- JSON flattening practice — [ClickHouse: flatten nested JSON](https://clickhouse.com/resources/engineering/flatten-nested-json-python),
  pandas `json_normalize`, and the NDJSON convention for streamed exports.

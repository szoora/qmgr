# Calendar audiences, reminders, sound, and routing imported documents

**Status: BUILT 2026-09-26, all six phases, decisions E1–E12 taken as recommended** (user: *"implement the plan fully"*). Drafted the same day. Artifact: https://claude.ai/artifact/5Q4EZtkKWUKGCSEdGtARLT. Where the build departed from the plan, §6a says so; the rest of this document is the plan as written.

This plan was drafted from two production screenshots on `dashboard.maryhillug.net`: the Term view, and the New event dialog. It also answers the school's report that *"several uploads of the school programmes and duty, some meetings, are not appearing in the registers"*.

Everything below was checked against the code on `8e96aa0` plus the uncommitted close-out tree. Four read-only audits covered the calendar, the programme import, the notifications and staff-group plumbing, and outside practice. The outside sources are listed in §9. Every source cited there was fetched and read, except where §9 says otherwise.

---

## 0. The answer in one paragraph

**The calendar shows every past event because nothing in it filters by date.** Term view lists every week of the term from week 1, and does not even scroll to today. The "Staff" box in the New event dialog is all-or-nothing, because an event has no way to name a staff group, a role or a set of people. **Nobody is told when an event is created**, since the calendar controller sends no notification at all. **There is no sound anywhere in the product**: no audio file, no audio code, and nothing in the notification preferences.

**Most imported meetings never became registers. That is the importer's default, not a failure.**
- A meeting row gets a register only when its title reads as a *staff* meeting ("Staff meeting", "Beginning of term staff meeting", "HOD meeting"). Everything else defaults to *"Event only — no register"*, and nothing asks about it.
- The attendance text written in the document ("Administration, Senior Ladies & Matrons", "Class teachers") is never read at all.
- The page gives one quiet hint: a small "Meetings" chip that looks almost the same as a real "Meeting" chip.

The fixes are small for past events and the import defaults, medium for audiences, notifications and sound, and larger for the reviewed, routed import inbox.

---

## 1. What exists today (verified)

### 1.1 Calendar
| Fact | Where |
|---|---|
| **No past filtering in any view.** Term loads the whole term, Agenda loads the whole month, Month loads the grid. Today gets a highlight only. | `Web/Components/Pages/Calendar/CalendarView.razor:502-507, 122-140` |
| **The view choice and the "My duties" switch are forgotten on every visit.** Nothing is saved in localStorage or the URL. | `CalendarView.razor:422-425, 478-483` |
| **Who sees an event** (`SchoolEventVisibility.IsPersonal`): Staff audience, OR named as responsible, OR in a named department. "Staff" therefore means every member of staff. | `API/Application/Services/SchoolEventVisibility.cs:33-39` |
| **An event cannot target a staff group, a role or chosen people.** The fields are `EventAudience` flags (Staff/Students/Guardians/Public), `ClassNames`, `ResponsibleUserIds` and `ResponsibleDepartmentIds`. | `API/Domain/Entities/Calendar/SchoolEvent.cs:46-59` |
| **Creating, editing or deleting an event notifies nobody and logs nothing.** | `API/Controllers/v1/CalendarController.cs:159-221` |
| **Delete is a hard delete.** | `CalendarController.cs:205-221` |
| **The people and department pickers appear only with Welfare & Performance.** The calendar itself is base product. | `CalendarView.razor:773-778` |

### 1.2 Staff groups, notices and notifications
- Staff groups are the school's own list (`StaffPerformancePolicyDto.StaffGroups`). A person's group comes from their **role** (`Role.StaffGroup`), resolved by `GroupFor` with a fallback to the first group. `StaffGroups.Applies` / `Key` is the one comparison.
- **Staff Notices already target an audience**: branch, departments, roles and staff group (`StaffNotice.cs:22-35`, `StaffNoticeFanOut`). Two faults must not be copied:
  - The fan-out compares the raw `Role.StaffGroup` with no `GroupFor` fallback, while the portal applies the fallback. So a person can see a group notice and never be told about it.
  - It does not exclude leavers.
- `NotifyManyAsync` sends one row per person. Preferences are read per event key. **There is no `calendar.*` event key**, and an unknown key bypasses preferences.
- **Live bug:** `NotificationPreferenceResolver.SaveAsync` drops `PushEnabled` on every save (`NotificationPreferenceResolver.cs:168-180`). The weekly digest saves every recipient's preferences, so **a person's push opt-out is wiped weekly**. The preferences page also has no push toggles at all.
- The reminder engine (`IReminderLadderService`, `ReminderLadderJob`, quiet hours 20:00–06:00) is generic, but every ladder skips a tenant without Welfare & Performance.
- **UI preferences** exist only in localStorage (`qmgr-theme`, `qmgr-pagesize`, …). No per-person server store exists apart from the notification preferences blob.

### 1.3 Programme import → registers
The routing is `ProgrammeClassifier` (table kind) → `ProgrammeTableParser` (row kind) → `ProgrammeImport.razor` (`MeetingRows` / `EventRows` / `RotaRows`) → `ProgrammeImportController.ApplyAsync`.

**Why a meeting does not become a register, most likely first:**

1. **Attendance defaults to `EventOnly`.** It is set otherwise only when the title reads as a staff meeting or HOD meeting, or when a rota in the **same** upload lists a matching role (`ProgrammeTableParser.cs:249-257`; `ProgrammeImport.razor:768-772`). This raises no must-decide question and does not hold the Import button.
2. **Meetings in the term-activities or timed-programme tables are always events.** Only a table classified as a *meeting schedule* can produce a Meeting row (`ProgrammeTableParser.cs:163, 188, 215`).
3. **The documents were uploaded separately.** Role-based attendance ("Prep supervisors' meeting" → the rota's prep supervisors) and the event↔meeting merge only work within one upload (`ProgrammeChecks.cs:86-111, 409-430`).
4. **The table was classified as something else.**
   - A "Staff to attend" column is read as a rota, because the rota check runs first.
   - A title column not headed "MEETING…" is read as activities.
   - An unrecognised table is dropped silently.
   - A wrong choice made once is **remembered per header shape** in localStorage and repeats on every later upload (`ProgrammeClassifier.cs:37, 99-143`; `ProgrammeImport.razor:699-743`).
5. **The importer lacked `staff.duties.manage`, or the module was off.** Every meeting then becomes an event, with one grey note at the top of the page.
6. **A register was made but is hard to find.**
   - The Registers tab opens on this week.
   - Recorders come only from a convener resolved as an *office*, so most imported meetings have no recorder, and only duty managers can take them.
   - A person who is neither expected nor a recorder never sees the register.
7. **Two meetings with the same title on one day.** The source key has no time, so the second one is refused.
8. **An earlier import was undone.** Undoing job A cancels duties that a later job B found "Unchanged" and relied on.
9. **Rows refused at commit are not reported.** The Done sentence ignores `Refused`.
10. **Meetings found in a note under a table are never imported.** `Include = false`, and no control sets it.

**What the page never does:**
- It never lists "these N meetings will be calendar events only, and why".
- It never reads the attendance text.
- It never offers "Everyone in department X", "Teaching staff", "Class teachers" or a list of people as attendance. The `MeetingAttendance.Chosen` value exists, but nothing reaches it.
- It never links the Done step to Registers.

**There is no approval step.** The same person previews and commits; nothing is staged. **There is one import path.** The Import tab does not recognise a timetable, a staff list or a student list and send it to the importer that already exists for it.

---

## 2. Industry practice (sources in §9)

| | Google Calendar | Outlook / Teams | Arbor (UK MIS) | iSAMS | Veracross |
|---|---|---|---|---|---|
| **Past events** | Shown, can be paged back to; optional "reduce brightness of past events" (reported in Google's Community threads, not confirmed in the help pages) | Shown; past reminders dismissed automatically | Shown | — | — |
| **Audience** | Invite a group (up to 100,000) | Distribution groups; a *channel* meeting is visible to the team, and personal invites are a separate choice | Participants: teachers, students, guardians, **groups**; staff can be marked **Required** | Publication rules per calendar and per event, **plus authorisation before display** | Group membership drives the household calendar |
| **Mine vs all** | Own calendars, plus opt-in subscriptions | Own calendar; "hide declined" | **"My Calendar" is personal by default**; others only via admin pages | Separate calendars per audience | Household / School / Athletics calendars |
| **Reminders** | Default per calendar; each event can override; pop-up or email | A default reminder the person sets | — | — | — |
| **Recurrence** | RRULE | RRULE | Weekly, fortnightly cycle, **term-time only** | Recurring wizard | — |
| **RSVP** | Yes | Yes (iTIP) | "Required" flag, no RSVP | — | — |

**What this means here:**
- **Past items are dimmed on a grid and hidden from a list** until the reader asks for them. A list's job is "what is coming", and a grid is a map of the month.
- **Targeting is by group at creation** (group, role, department, people), and **being in the audience is what puts an event in "My calendar"**. The whole-school view is an explicit opt-in (Arbor, Veracross, Teams channel versus invite).
- **Sound belongs to the person and the device, never the author.** Browsers refuse sound before the person has interacted with the page (Chrome's autoplay policy; `play()` rejects with `NotAllowedError`). WCAG 2.2 SC 1.3.3 says sound must never be the only cue, and SC 1.4.2 discourages sound nobody asked for.
- **Imports with human review route each extracted item to a queue by confidence and by who owns it.** Azure Document Intelligence confidence scores, UiPath Action Center and the iSAMS authorisation step all work this way. **Separation of duties** (NIST SP 800-53 AC-5) is the principle behind an optional second approver.
- **Concurrent edits use optimistic concurrency.** The Google Calendar API answers a stale ETag with 412 Precondition Failed. Npgsql maps PostgreSQL's `xmin` as a concurrency token with no migration column.

---

## 3. Decisions to take

| # | Question | Recommendation | Alternative |
|---|---|---|---|
| **E1** | Past events | **Hidden in Term and Agenda; a "Show past events" switch reveals them, dimmed.** Month keeps past days in the grid, **dimmed**. Term view opens scrolled to this week, with a line "Earlier this term: 23 events · Show". The switch is remembered per person. | Hidden everywhere, including Month. This breaks the map-of-the-month reading. |
| **E2** | Who an event is for (staff side) | **One audience picker:** *All staff*, or any combination of **staff groups, roles, departments and named people**. The result is the union. The responsible people are always in it. The same picker replaces the Staff Notices one, so the two cannot drift. | Staff groups only, as asked. This cannot express "administrators" (a role), "Maths department" or "the four house patrons". |
| **E3** | What "visible" means | **Two layers.** The **audience** decides who is *told* and whose **My events** it is. The **school calendar** stays readable by all staff, **except an event marked "Only its audience can see it"** (a disciplinary panel, an interview). | Everything is visible only to its audience. Staff then cannot see the school's programme, which the printed term programme already shows everyone. |
| **E4** | The view switch | **"My events | Whole school", defaulting to My events for everybody.** My events means events for all staff, events I am in the audience of, events I am responsible for, and my duties. Remembered per person, on the server. | Default to Whole school for calendar managers. |
| **E5** | Telling people | **On save, "Notify the N people in the audience" is ticked for a future event.** A change of date, time or venue, or a cancellation, sends a change notice to the same people. Past events never notify. Every notice is claimed with a conditional UPDATE before it is sent, so it goes exactly once. **An import sends ONE summary per person** ("7 events and 2 meetings were added to your calendar"), never one per row. | Notify silently on every save. This means notice fatigue, and every typo fix alerts 140 people. |
| **E6** | Reminders | **A day-before reminder at the morning hour, plus one an hour before a timed event, on the existing reminder ladder**, with quiet hours respected. The school sets the default and each event may switch it off. **Not gated on Welfare & Performance**, because the calendar is base product. | No reminders; people rely on the ICS feed's alarms. |
| **E7** | Notification sound | **A per-person "Play a sound for new notifications" switch (on by default), plus "Mute on this device"** for a shared office PC. The sound is a short synthesised chime made with the Web Audio API: no audio file, no licence, nothing to install. It plays at most once every 10 seconds, never during quiet hours, and **always alongside a visual toast**. "Important only" is an option. | Off by default. WCAG's intent favours it, but it is not what was asked. |
| **E8** | Where a person's view choices live | **A new nullable jsonb column `User.UiPreferences`** read through one service: calendar view and scope, past-events switch, sound. Notification choices stay in their own blob. Putting UI choices in that blob would flip the portal's "preferences reviewed" onboarding step. | localStorage only. This does not follow the person to their phone. |
| **E9** | Imported meetings that got no register | **The importer never decides "event only" silently.** Every meeting-like row, in any table, is a Meeting candidate. Its attendance is **read from the document's own text**. A row it cannot resolve becomes a question on that row. The confirm step shows "18 meetings: 14 get a register, 4 will be calendar events — reason each". | Keep the title rule and add only the summary. |
| **E10** | Maryhill's existing data | **An "Import health" view on each past import**: meetings that became events only, meetings with no recorder, and rows refused. Each has **"Give this a register"**, which turns the event into a linked Session duty with the attendance and recorder chosen there. Nobody re-uploads. | Undo and re-import. This loses any edits made since. |
| **E11** | Approval | **One Import inbox, routed by section.** A document is read once; each table goes to the section that owns it: calendar events, meetings (registers), duty rota, timetable (draft), staff list, student roll. **Nothing lands until that section's owner approves it.** A school may require **a second person** to approve (four-eyes, off by default). Owners are notified. | Keep one person, one step, and add the review screen only. |
| **E12** | Recurrence | **Build "Repeats weekly / fortnightly, term time only, until …"** (Arbor's model), stored as a series of rows sharing `SeriesId`, which already exists. It is exported to the feed as separate events. Full RRULE is not in this plan. | RRULE on one row. That means exceptions, RECURRENCE-ID and a far larger surface. |

---

## 4. Bugs and race conditions found (all verified in code)

**P0: wrong answers today**

| # | Defect | Where | Fix |
|---|---|---|---|
| B1 | **An imported meeting shows twice**: the event and its linked duty both render. The entity comment promises "one thing, not two". | `CalendarView.razor:632-649` | `ItemsOn` drops a duty whose id is some event's `DutyId`; the event shows the duty's badge. |
| B2 | **Editing an event linked to a meeting never updates the meeting**: dates, time, title and venue drift apart. **Deleting it leaves the duty behind.** There is no foreign key, so a deleted duty leaves a dangling `DutyId`. | `CalendarController.cs:227-303` | Edit moves the duty in the same transaction, refusing if its register was taken. Cancel, not delete. Add the FK with `SetNull`. |
| B3 | **"You take the register" shows to every duty manager on every meeting.** `CanIRecord = mayManage \|\| recorder`. | `StaffPerformanceMapping.cs:227`, `CalendarController.cs:95,113` | The calendar badge asks "am I a recorder?". Managing is a separate "Open the register" action. |
| B4 | **Push opt-out wiped weekly.** `SaveAsync` drops `PushEnabled`. | `NotificationPreferenceResolver.cs:168-180` | Copy every field; add push toggles to the page; add a guard test that round-trips the DTO. |
| B5 | **Last write wins on events.** There is no concurrency token, and the dialog edits a stale copy. | `SchoolEventConfiguration.cs` | Map `xmin` as the row version (Npgsql, no column). Send it with the edit and answer **409 "Somebody changed this event — here is theirs"**. |
| B6 | **A re-import overwrites hand edits**, and a manual edit is not serialised with an import. **A hand delete during an import 500s the whole batch** (`FirstAsync`). | `ProgrammeImportController.cs:945-990` | `SchoolEvent.EditedByHandAt`; a re-import never overwrites a hand-edited field and lists it instead. Manual writes take the same advisory lock. Use `FirstOrDefault`, and skip with a reason. |
| B7 | **`SourceKey` is not unique**, and a meeting's key has no time. | migration `…AddCalendarProgrammeImportAndGates` | Add the time to the key. Add a unique partial index on `(OrganizationId, BranchId, SourceKey)`. Give `StaffDuty` a `SourceKey` column so a re-import matches by key, not by title and day. |
| B8 | **`Take(2000)` before visibility.** A dense range silently drops events. | `SchoolEventVisibility.cs:124-132` | Filter visibility in SQL (array overlap on audience columns, GIN index) and page the Agenda. |
| B9 | **Web dates are the server's, not the school's.** "Today", the default event date and duty placement use `DateTime.Today` / `ToLocalTime()` on the Blazor server. The Duties page does the same. | `CalendarView.razor:423, 630`; `StaffDuties.razor:440` | One `BranchClock` from `Branch.Timezone`, used everywhere a person reads a day. |
| B10 | **Stale responses overwrite newer ones.** Rapid Prev/Next or view clicks race. Term view makes two calls. | `CalendarView.razor:509-535` | A load sequence number: drop any response that is not the latest. |
| B11 | **Chip counts do not match what is shown**: they ignore search and count out-of-month days. **Duties vanish whenever a filter is on.** | `CalendarView.razor:615-673` | Counts come from the same filtered set; the My duties switch is honoured under filters. |
| B12 | **Rows refused at commit are not reported**, and meetings in a note can never be included. | `ProgrammeImport.razor:1224-1235`; `ProgrammeTableParser.cs:290` | The Done step lists refusals by row; notes get an Include switch. |
| B13 | **Undo can remove what a later import relied on**, and a register opened mid-undo can have its duty cancelled. | `ProgrammeImportController.cs:375-388` | Undo takes the register's lock and refuses rows a later job matched, naming that job. |
| B14 | **Staff Notice fan-out** has no `GroupFor` fallback and no leaver exclusion. | `StaffNoticeFanOut.cs:34-68` | Both go through the new shared audience resolver (§5.1). |

**P1: smaller**
- **B15.** A multi-day event repeats on every day row in Term and Agenda. Show it once, with "until 3 Oct".
- **B16.** A timed multi-day event is a daily block on the web and one continuous block in the feed. Pick one meaning: a daily block, written as a series in the feed.
- **B17.** The feed has no `SEQUENCE` or `STATUS:CANCELLED`. `DTSTAMP` changes on every fetch.
- **B18.** POST is not idempotent, so a double click makes two events. Send an idempotency key from the dialog.
- **B19.** Editing converts an all-branch event (`BranchId` null) into this branch's event.
- **B20.** Month's empty state says "No events" while it counts duties.
- **B21.** An event with only Students or Guardians as its audience is invisible to every non-manager, and the dialog does not say so.
- **B22.** Event create, edit and delete write no activity log.

---

## 5. What to build

### 5.1 One audience model (E2, E3, E4)
**Widen `SchoolEvent`; add no table:**
- `AudienceStaffGroups text[]`, `AudienceRoleCodes text[]`, `AudienceDepartmentIds uuid[]`, `AudienceUserIds uuid[]`.
- `AllStaff bool` (true = every member of staff; it replaces the Staff flag's meaning).
- `AudienceOnly bool` ("Only its audience can see it").
- `NotifiedVersion int`, `ReminderStage int`, `EditedByHandAt`.

**The migration is add, backfill, drop.** Every existing event with the Staff flag becomes `AllStaff = true`, so nothing that anyone can see today disappears.

**`StaffAudience` (API, `Application/Services`) is the ONE home** for "is this person in this audience" and "who is in it". It:
- applies `GroupFor` with its fallback and `StaffGroups.Key` matching;
- excludes leavers, inactive accounts and the platform account;
- adds the responsible people;
- is branch-aware.

It is used by visibility, event notifications, reminders, the feed, **Staff Notices** (fixing B14) and the programme import (§5.4).

**`GET branches/{b}/calendar/audience-options` is ungated beyond `calendar.manage`.** It returns the school's staff groups, roles, departments and active people (names only), so a school without Welfare & Performance can still target "Teaching staff" or "Administrators". Staff groups resolve server-side without the module, because `GroupFor` falls back to the defaults.

**`QAudiencePicker` (`Components/Shared/UI`)** has an "All staff" switch. When the switch is off, it shows four combinable selectors and a live line: **"42 people: Teaching staff (38) + Bursar + Head Teacher"**. Staff Notices move onto it.

**Visibility rule:** a manager sees everything; otherwise `AllStaff || in audience || responsible` = **Mine**. The whole-school scope adds every event that is not `AudienceOnly`.

The **students, guardians and public** checkboxes stay as they are (D5 of the calendar plan still stands). The dialog now says under them: "Students and guardians do not see the calendar yet".

### 5.2 Past events and the view (E1, E4, E8)
- The toolbar gets a **"My events | Whole school"** segmented control, beside **"Show past events"** (a switch that applies at once, as NN/g's toggle guidance advises).
- Term view scrolls to this week and collapses earlier weeks behind **"Earlier this term: N events · Show"**.
- Past rows and pills are dimmed (a token, not an opacity literal) and keep their click target.
- The view, scope, switch and category are saved in `User.UiPreferences` through `IUserPreferencesService` (one home, the same shape as the notification resolver). localStorage caches them so the first paint does not flash.
- The URL carries `?view=&date=&scope=`, so a link opens the same view.

### 5.3 Notices, reminders and sound (E5, E6, E7)
- **New event keys** (a wire format):
  - `calendar.event-added`, `calendar.event-changed`, `calendar.event-cancelled`, `calendar.event-reminder`;
  - `calendar.import-summary`;
  - `NotificationType.Calendar`.
- **Exactly once.** Save bumps `EventVersion`, and a conditional `UPDATE … SET NotifiedVersion = v WHERE NotifiedVersion < v` claims the send before `NotifyManyAsync`. A retry or a double save never sends twice. A "material change" is a change of date, time, venue, audience or status. **A typo fix is not one.**
- **Cancel, not delete.** `Status` is `Scheduled` or `Cancelled`. A cancelled event stays on the calendar struck through, with a reason, notifies its audience, and is written to the feed as `STATUS:CANCELLED` with `SEQUENCE` bumped. Delete remains, for managers, for a mistake nobody was told about.
- **Reminders.** `ReminderSubject.SchoolEventStart` (appended value 8) with a default ladder: the day before at the morning hour, then an hour before a timed event. A new `SchoolEventsAsync` sweep in `ReminderLadderJob` checks the module **only for staff-performance ladders, not this one**. Stages are claimed with the existing conditional update. Quiet hours hold non-urgent stages.
- **Sound.** `UserPreferences.Sound = { Enabled = true, ImportantOnly = false }`, plus `qmgr-sound-muted` in localStorage for "mute on this device".
  - `wwwroot/js/notificationSound.js` synthesises a two-tone chime (about 350 ms) with the Web Audio API. It resumes the `AudioContext` on the first click on the page, catches `NotAllowedError` and does nothing.
  - `MainLayout.HandleNewNotification` calls it for a new unread row, **throttled to one chime per 10 seconds**. It is skipped in quiet hours, for `Low` priority, and when "important only" is set and the priority is below `High`. **Every chime comes with a toast** (WCAG 1.3.3).
  - The preferences page gets **Sound (with a "Play a test sound" button), the push master switch and per-event push boxes**, fixing B4.
  - The phone app's sound belongs to the OS channel `qmgr-alerts`. The page says so and points there.

### 5.4 The import reads attendance, and says what becomes a register (E9, E10)
- **Every row whose title contains MEETING, BRIEFING, CONFERENCE or PANEL is a Meeting candidate**, in any table kind.
  - A student-facing row (dorm, assembly, parents' day) stays an event and **says why on its row**.
  - A class group such as "S.4" is noted: a class meeting is a meeting of that class's teachers only when the attendance says so.
- **`AudienceTextResolver` (Shared, pure) reads the responsible/attendance text** into a `StaffAudience`:
  - "All staff" / "Whole staff" → AllStaff;
  - "Teaching staff" / "Support staff" → staff groups;
  - "HODs" → department heads;
  - "Class teachers" → the pastoral posts;
  - "Administration", "Bursary" → a department, or else a role with that name;
  - a person's name → `StaffNameResolver`;
  - "Senior Ladies & Matrons" → split on `&` and resolved part by part.
  - An unresolved part is a question on the row, and **the answer is saved as an office alias** (today `BuildRequest` sends only person aliases).
- **One audience, two uses.** The resolved audience becomes the event's audience **and** the duty's `ExpectedUserIds`.
- **Recorders:** the resolved conveners. When there are none, the row asks "Who takes the register?", offering the conveners' department heads first. A register nobody may take counts as a question, not a success.
- **The attendance picker gains "People or groups…"** (the `QAudiencePicker`), which finally reaches `MeetingAttendance.Chosen`.
- **Cross-upload.**
  - Role-based attendance also reads rota slots **already in the database** for that date, not only the same upload.
  - An event matching an existing Session duty (same local day, same title key) links to it instead of creating a second item.
- **The confirm step shows a "Registers" panel:** "18 meetings in these documents: **14 will get a register**, 4 will be calendar events". Each of the 4 is listed with its reason and a **"Give it a register"** control. The Done step links to **Registers**, filtered to this import, and lists refusals (B12).
- **Import health (E10).** `GET …/calendar/import/jobs/{id}/health` lists, for any past job, meetings imported as events only, registers with no recorder, and refused rows. **"Give this a register"** (`POST …/calendar/events/{id}/register`, `staff.duties.manage`) creates the Session duty from the event with the chosen attendance and recorder, links `DutyId`, and notifies the people expected. **This is how Maryhill's existing uploads are repaired without re-uploading.**

### 5.5 The Import inbox: routed, reviewed, approved (E11)
- **One page, `/imports`, one drop zone.** The shared readers (docx, .doc, PDF, sheets) read the file once. A **router** runs each table through every importer's classifier and assigns it to the one that claims it with the highest confidence:
  - a programme or activities table → Calendar;
  - a meeting schedule → Registers;
  - a person or period rota → Duty rota;
  - a timetable grid → Timetable draft (`ProcessTimetableJobAsync`);
  - a staff list → Staff import;
  - a class list → Student roll (`QImportPanel`).
  
  A table nobody claims is shown as "Not recognised", never dropped (the "Unknown is silently dropped" bug).
- **Nothing is written on upload.** The job is a `RosterImportJob` with `Kind = Inbox` and `Status = AwaitingReview`. **No new table:** the staged rows and each section's state live in its existing jsonb.
  - Each **section** has an owner permission (`calendar.manage`, `staff.duties.manage`, `timetable.manage`, `staff.structure.manage`, `students.manage`) and a state: *Awaiting review → Approved / Rejected / Partly approved*.
  - The owners are notified (`imports.awaiting-review`, one per person per job), and a bell-count badge sits on **Imports**.
- **Approving a section runs that importer's existing commit path.** Nothing is re-implemented, so every rule each importer already enforces still holds. Sections are approved independently. The job is complete when every section is decided.
- **Four-eyes is optional:** `Organization.Settings["Imports"].RequireSecondApprover` (off by default). When on, the uploader cannot approve their own sections (NIST AC-5; the same rule as "a meeting cannot adopt its own minutes").
- **Confidence is shown, not hidden.** Each routed table says why it was routed ("Headers DATE · MEETING · VENUE · CONVENER"), and the reviewer can re-route it. A re-route is remembered per header shape **only once approved**, never on a guess, which fixes the "remembered once, repeated for ever" trap.
- The existing Calendar → Import tab stays as a shortcut into the inbox with the Calendar section preselected.

### 5.6 Features that make it complete, and scale
- **Recurring events (E12):** weekly or fortnightly, term time only (skipping holidays and national dates), until a date. Stored as rows sharing `SeriesId`. Editing offers "this one / this and following / all".
- **Required or optional attendance** per audience part (Arbor's "Required"), shown on My School Day. There is **no RSVP in this plan**: a school register records attendance after the event, which is the record that matters.
- **Clash warnings at save.** "12 of the 42 people are teaching then; 2 are on duty", from the timetable and duties. **They warn and never refuse** (D6 of the calendar plan).
- **Add to my calendar** on any event: a single-event `.ics`. The per-person feed gains a **category filter** and `SEQUENCE`/`STATUS`.
- **Attachments:** link a Library document (an agenda, a letter) to an event. Its access follows the document's own rules.
- **Scale:**
  - Visibility is computed in SQL: array overlap on GIN-indexed audience columns, `StartsOn`/`EndsOn` range index.
  - The Agenda is paged.
  - An import's notices are coalesced per person.
  - Reminders are claimed per stage.
  
  Nothing loads a whole term's events into memory to filter them.

---

## 6. Phases

| Phase | Contents | Size |
|---|---|---|
| **1 — Correct what is there** | B1–B5, B8–B11, B19–B20; past events hidden with the switch; view remembered (localStorage first); Term scrolls to today | S–M |
| **2 — Audiences and the switch** | E2–E4 and E8: the `SchoolEvent` columns and migration, `StaffAudience`, `audience-options`, `QAudiencePicker`, Staff Notices moved onto it, the My events / Whole school scope, `User.UiPreferences` | M |
| **3 — Telling people** | E5–E7: event keys, exactly-once notices, Cancel, reminders on the ladder, sound, the preferences page with sound and push | M |
| **4 — Imports that make registers** | E9–E10: meeting candidates everywhere, `AudienceTextResolver`, recorders asked, the Registers panel, cross-upload matching, Import health and "Give this a register", B6, B7, B12, B13 | M–L |
| **5 — The Import inbox** | E11: router, staged sections, owners notified, per-section approval through the existing commit paths, optional four-eyes | L |
| **6 — Richer events** | E12 recurrence, required/optional, clash warnings, single-event .ics, feed filter, attachments, activity log (B22) | M |

**Phase 1 can ship alone and ahead of the rest.** Phase 4's *Import health* is what repairs Maryhill's missing registers. It is worth pulling forward to follow Phase 1 if the school needs those registers this term.

## 6a. As built — where the build departed from the plan

- **Visibility is filtered in memory, not in SQL.** `SchoolEventQueries.InRangeAsync` reads the range (ceiling 10,000 rows, never truncated before the filter) and `StaffAudienceRule` decides per row. A GIN index over the audience arrays was not added: a term's events per branch are hundreds, and one rule in C# is what keeps the page, the feed and the notices agreeing.
- **Staff Notices keep their own picker**, but decide membership through `StaffAudience` (the group through `GroupFor` with its fallback, leavers excluded), which is what B14 needed.
- **A timetable part of a routed document is "Download the table" then "Mark imported".** The timetable importer takes a draft and a grid shape the inbox cannot hand across; the student and staff parts open their own import wizards pre-filled (`InboxHandoff`).
- **Only the calendar and Duties pages moved onto `BranchClock`.** About 127 other `ToLocalTime` calls in the Web still use the server's zone; they are display-only and were left for a sweep of their own.
- **The preview's "people told" counts duty recipients only**; event recipients are counted at commit.
- **B13 is narrower than written.** A later import relies on a row only when it also created or updated something of its own. A pure re-submission of the same document (every row "unchanged") is the same import, and undoing the first removes the rows. Section 31.4 fires exactly that double commit, and the broad rule made undo remove nothing.
- **`notification-sound.mjs` wraps `qmgrSound.play`**, not `AudioContext`: it counts what the page ASKS for, which is the page's half of the contract; the throttle lives in the script.
- **Found in the browser, fixed: the inbox did not follow its own address.** "Review and approve" is a same-route navigation, which never re-runs `OnInitializedAsync`, and a page with nothing bound to the query is not given new parameters either. `job` and `section` are `[SupplyParameterFromQuery]` now.
- **Migration `20260926133928_CalendarAudiencesAndImportRouting`** is hand-edited: dangling `SchoolEvents.DutyId` values are cleared before the new key, duplicate `SourceKey`s lose their key (rows kept) before `ux_school_events_source_key` — an expression index on `COALESCE(BranchId, empty guid)` so whole-school events collide too. The `xmin` "column" EF scaffolds emits no SQL (Npgsql skips system columns); checked in the generated script.

## 7. Verification

- **API sections:**
  - **41, calendar audiences and notices.** Target a group, a role, a department and people. Assert who is told, who sees it under Mine and under Whole school, and that an AudienceOnly event is hidden. Race ten saves and assert exactly one notice per person. Assert a cancel notifies, and a typo fix does not.
  - **42, import routing.** Run the real Maryhill documents from `E2E_DOCS_DIR`. Assert every meeting is either a register or listed with a reason, and never silent. Assert attendance is read from the text. Assert "Give this a register" on a past job.
  - **43, the inbox.** Route a mixed document. Assert nothing is written before approval, per-section approval, four-eyes refusal, and a re-route remembered only after approval.
- **Browser suites:**
  - `calendar-scope.mjs`: past hidden and dimmed, the switch remembered, Term at today, chip counts equal the rows shown.
  - `notification-sound.mjs`: a chime is attempted once per burst and never when muted, always with a toast. It wraps `AudioContext` to count.
  - `import-inbox.mjs`.
- **Guards:**
  - `preferences-roundtrip-check`: every property of the preferences DTO survives `SaveAsync` (B4's class).
  - `audience-home-check`: no audience membership test is written outside `StaffAudience`.

## 8. Not in this plan

- Students or guardians seeing the calendar (a portal of its own).
- Full RRULE and RSVP.
- Two-way calendar sync.
- OCR of scanned documents.
- An AI model reading documents.

The standing no-server-dependencies rule is kept: sound is synthesised, and the readers stay hand-written.

## 9. Sources (fetched and read unless noted)

- Google Calendar views: https://support.google.com/calendar/answer/6110849
- "Reduce the brightness of past events" (Community thread only): https://support.google.com/calendar/thread/274219612
- Invite a group: https://support.google.com/calendar/answer/172013
- Subscribe to calendars: https://support.google.com/calendar/answer/37100
- Google Calendar reminders (default per calendar, per-event override): https://developers.google.com/calendar/api/concepts/reminders
- Google Calendar ETag / 412: https://developers.google.com/calendar/api/guides/version-resources
- Outlook reminders and "automatically dismiss reminders for past events": https://support.microsoft.com/en-us/outlook/calendar/add-or-delete-notifications-or-reminders-in-outlook
- Teams channel meetings (audience versus invite): https://support.microsoft.com/en-us/teams/teams-channels/channel-meetings-in-microsoft-teams
- Arbor events (participants, groups, Required, term-time recurrence): https://support.arbor-education.com/hc/en-us/articles/360020312494-Creating-and-managing-events
- Arbor My Calendar and syncing: https://support.arbor-education.com/hc/en-us/articles/203878231
- iSAMS Calendar Manager (publication rules, authorisation): https://www.isams.com/platform/modules/calendar-manager/
- Veracross calendars (a school's guide, not Veracross's own documentation): https://parkschool.net/parents-association/using-veracross-calendars/
- NN/g, toggle switch guidelines: https://www.nngroup.com/articles/toggle-switch-guidelines/
- NN/g, filter categories and values: https://www.nngroup.com/articles/filter-categories-values/
- NN/g, the power of defaults: https://www.nngroup.com/articles/the-power-of-defaults/
- Chrome autoplay policy: https://developer.chrome.com/blog/autoplay
- MDN `HTMLMediaElement.play()`: https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/play
- MDN `Notification.silent`: https://developer.mozilla.org/en-US/docs/Web/API/Notification/silent
- WCAG 2.2 SC 1.4.2 Audio Control: https://www.w3.org/WAI/WCAG22/Understanding/audio-control.html
- WCAG 2.2 SC 1.3.3 Sensory Characteristics: https://www.w3.org/WAI/WCAG22/Understanding/sensory-characteristics.html
- Android notification channels (the user owns the sound): https://developer.android.com/develop/ui/views/notifications/channels
- Smashing Magazine, notification UX guidelines (2025): https://www.smashingmagazine.com/2025/07/design-guidelines-better-notifications-ux/
- RFC 5545 iCalendar: https://www.rfc-editor.org/rfc/rfc5545
- RFC 5546 iTIP (SEQUENCE, CANCEL): https://www.rfc-editor.org/rfc/rfc5546
- RFC 7986 (NAME, COLOR, REFRESH-INTERVAL): https://www.rfc-editor.org/rfc/rfc7986
- RFC 9110 conditional requests (If-Match, 412; only the overview page rendered): https://www.rfc-editor.org/rfc/rfc9110#section-13.1.1
- NIST SP 800-53 AC-5 Separation of duties: https://csf.tools/reference/nist-sp-800-53/r5/ac/ac-5/
- Azure Document Intelligence confidence and human review: https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/accuracy-confidence
- UiPath Action Center (human-in-the-loop queues): https://docs.uipath.com/action-center/automation-cloud/latest/user-guide/introduction
- EF Core concurrency: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
- Npgsql `xmin` concurrency token: https://www.npgsql.org/efcore/modeling/concurrency.html
- Stripe idempotent requests: https://docs.stripe.com/api/idempotent_requests

**Could not be verified:**
- Google's own help text for dimming past events (it is in Community threads only).
- Finalsite's subscription page (403).
- Material 3's notifications page (did not render).
- An authoritative source for "sound preference per device". That point is inferred from the autoplay rules and OS channel control.

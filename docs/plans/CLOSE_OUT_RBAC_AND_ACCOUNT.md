# Close-out plan: RBAC visibility, the account page, the remaining code gaps, and the docs

**Status: BUILT 2026-09-25/26 and verified** — decisions D1–D7 taken as recommended. Artifact: https://claude.ai/artifact/T9LGeAzrQhk6Q7safRvZH8. §6 records what was built and where the build departed from this plan. Drafted 2026-09-25 from three production screenshots on
`dashboard.maryhillug.net`, signed in as a teacher, and a read of the handover documents.
Items that need the owner rather than code are left out on the user's instruction: registration and
filing, SMS sender registration, certificates on the server, the first real payment, iOS and the
Play listing. They stay listed in the tracker as "Yours".

Everything below was checked against the code on `8e96aa0`, not taken from the handover notes.

---

## 0. Decisions — all seven taken as recommended (user, 2026-09-25)

The recommendation, listed first each time, is what was decided.

| # | Question | Recommendation | Alternative |
|---|---|---|---|
| **D1** | Who sees **Settings**? | **Only people who can change something there**: `settings.edit` OR `notifications.manage` (the Administrator by default). | `settings.view`, which would also give the Head Teacher and the Front Office Manager a read-only view. |
| **D2** | What happens to `notifications.view`, which the Teacher and eight other roles hold? | **Keep the grants, and make the code gate nothing sensitive.** Word its description the same way in both catalogues ("View notification history"). | A migration that removes it from the seeded non-admin roles. This is riskier, and it gains nothing once the code gates nothing sensitive. |
| **D3** | Who may change a person's **name**? | **The school**, through the staff record (`staff.structure.manage`). The account page shows it read-only, with "Ask an administrator". Registers, MoES returns and printed slips carry the name, and the staff number and national ID are already the school's. | Keep self-edit on the account page and log it. |
| **D4** | Where does the **phone number** live? | **My file** (contact), with "Confirm your phone number" beside it. The account page no longer edits it. | Keep two editors, which is today's state and the source of a bug (§2.4). |
| **D5** | What becomes of the tracker's 11,500 lines of history? | **Delete them.** Replace the file with the open items only, plus one line per shipped piece of work with its commit. Git keeps every word, and the last full version's commit is named at the top. | Move them to `docs/history/TASK_TRACKER_ARCHIVE.md`. |
| **D6** | The `AdvancedAnalytics` / `WebhookIntegration` feature codes, which gate nothing | **Remove them.** | Wire each one to a real gate. That needs a pricing decision first. |
| **D7** | Merging duplicate staff accounts (`STAFF_WITHOUT_EMAIL` §4) | **Out of this plan.** It is its own feature, with its own plan. | Include it as Phase 5. |

---

## 1. Phase 1: RBAC. What you can see must be exactly what you may use (P0)

### 1.1 The finding the screenshot led to, which is worse than a visible link

**Every holder of `notifications.view` can read the school's messaging secrets in plain text.** That
includes every Teacher, Support Staff, Board Member, DoS, Academic Assistant, Deputy, Head and Front
Office Manager.

- `GET api/v1/notifications/settings/{orgId}` (`Controllers/NotificationsController.cs:377`) is gated on
  `[RequirePermission(Permissions.NotificationsView)]`.
- `MapToSettingsDto` (`:551–575`) returns `SmsApiKey`, `SmsPassword`, `SmtpPassword`, `TelegramBotToken`
  and `WhatsAppAccessToken` as stored.
- The page renders them in password boxes, which hides nothing, because the network response carries
  them. The eye icon in the screenshot reveals them.
- Nothing else in the API reads `notifications.view`: the bell endpoints are `[Authorize]` only. So the
  code grants nothing a teacher needs and one thing nobody but an administrator should have.
- This is the same class as the platform-secret leak closed on 2026-09-15 (`PlatformSettingsController.RedactSecrets`).
  It is fixed the same way.

**Fix:**
1. Gate that GET on `notifications.manage` (D1/D2).
2. **Mask every secret on the way out**, whoever asks: the eight-dot mask `••••••••` means "set", and
   empty means "not set". On the PUT (`:396–470`), **merge the stored value back wherever the mask
   comes in**, the `MergeSecrets` shape. Otherwise a save that did not retype a password blanks it.
   - One home: lift `RedactSecrets` / `MergeSecrets` from `PlatformSettingsController` into a small
     shared helper (`SecretMask`) that both controllers call. Do not write a second copy.
   - The PUT writes `TelegramBotToken` and `WhatsAppAccessToken` in two places (`:410–428` and
     `:460–463`), so both paths merge.
3. `IntegrationsSetup.razor:359` re-reads the whole settings payload only to get four on/off flags.
   Give it a flags-only answer, or read the flags off the masked DTO. Either way, no secret travels
   for a status badge.

**Verified by:** an API section (§5) that, for every seeded role, GETs the settings. It expects 403
for everyone except the administrator, and the mask, never a secret, for the administrator. It also
checks that a PUT carrying the mask keeps the stored secret.

### 1.2 The Settings hub and its links

| Where | Today | After |
|---|---|---|
| Sidebar: `ShowAdminSettingsHub` (`MainLayout.razor:1303`) | `canViewSettings \|\| canViewNotifications \|\| …` | D1's rule. `canViewNotifications` is replaced by `canManageNotifications`. |
| Settings hub, Notifications tab (`SettingsHub.razor:56`) | `NotificationsView` | `NotificationsManage` |
| Notifications section's own guard (`NotificationSettings.razor:833`) | `NotificationsView`, renders read-only | `NotificationsManage` |
| **User menu, Settings** (`MainLayout.razor:147`) | **no gate** | same condition as the sidebar entry |
| **Profile, System Settings** (`Profile.razor:174`) | **no gate** | removed (§2). It is not an account action. |
| Mobile More, Settings (`MobileNav.cs:173`) | `SettingsView` | same rule as the sidebar |
| **Mobile More, Billing** (`MobileNav.cs:175`) | **no permission**, so a teacher reaches `/billing` | `BillingView`, the desktop sidebar's gate |

**One rule, one home.** The sidebar, the user menu and the mobile sheet each decide "may this person
see Settings" on their own, and today they give three different answers. Add
`NavGates.SettingsHub(hasPermission, hasModule)` beside `HubTabs` (or put the decision on `HubTabs`
itself, whose OR-of-sections is already the right definition). All three read it. The same applies
to Billing.

### 1.3 The rest of the sweep: every link against its page

Found by reading every nav entry against its target page's own guard:

- **Appointments** (`MainLayout.razor:200`) is shown for `queue.view || queue.manage`, but the page
  requires `queue.view`. A `queue.manage`-only holder sees a link that refuses them. Make the link
  follow the page.
- **Pages with no refusal of their own.** For each one, decide whether the API alone is the
  intended gate. If it is, the page must say "not available" rather than render an empty shell:
  - `DutyReportPage`, `StaffMinutes` and `StaffRegister` are authorised by **delegation** (recorder,
    minutes-taker), not by a permission. The API answers 404, so the page must show that 404
    honestly. No permission gate is added, because a permission gate would lock out the delegates
    these pages exist for.
  - `MinutesPrint`, `TimetablePrint` and `TeachingReportsPrint` follow the same rules as the
    documents they print. Add the refusal they lack.
  - `DutyReportsPrint` checks `staff.duty-reports.view` and does not redirect when the check fails.
    Add the redirect.
  - Platform: `ModuleCatalog` and `RegistrationReview` rely on the API alone. Add the SuperAdmin
    refusal the other platform pages have.
- **Customer Display** having no guard is intended (a public screen). Say so in a comment rather
  than leave it looking like an oversight. The same goes for the **Kiosk** being `[Authorize]` only.

### 1.4 Make it stay fixed

1. **Browser suite `browser/rbac-links.mjs`.** Sign in as **every seeded tenant role**, plus the
   platform SuperAdmin. For each one, collect every link in the sidebar, the user menu, the Profile
   page and the mobile More sheet, open each one, and fail on `/unauthorized`, on a module detour to
   `/billing`, or on a visible `#blazor-error-ui`. Then assert that the administrator-only links
   (Settings, Billing, Users & Roles) are **absent** for the roles that should not have them.
   `mobile-nav.mjs` already does this for the teacher's mobile slots; this generalises it.
2. **Static guard `nav-gate-check.mjs`** (in `guards.sh`): every `href` into `admin/`, `billing` or
   `platform/` inside `MainLayout.razor`, `Profile.razor` or `MobileNav.cs` must sit inside a
   permission condition or carry a permission code. The ungated user-menu link is exactly what this
   would have caught.
3. **CLAUDE.md** gains one section: *"A link is shown exactly when its page would open; the sidebar,
   the user menu and the mobile sheet read one rule."* It also records the secret-masking helper
   and states that any new secret property must go through it.

### 1.5 The wider sweep (2026-09-25): eleven more of the same shape

Each was verified by reading the code. The first two were also re-read by hand.

| # | Sev | Where | Gate today | Who reaches it | What leaks | Fix |
|---|---|---|---|---|---|---|
| L1 | **High** | `PrintController.cs:288` `POST …/tokens/{t}/print` | `tokens.view` (a view permission on a write) | Viewer, Front Desk, FOM, Head, Admin | The body's `PrinterIpAddress` wins over the stored setting (`:345`), and the server opens TCP to it and returns the error: a blind SSRF / port probe from inside the network | Gate on `tokens.create`, and **ignore the request's address**: print only to the stored branch printer |
| L2 | Medium | `StaffStructureController.cs:497` member profile | `staff.records.view` + staff scope | Academic Assistant (whole school), DoS, Deputy, Head | National ID, date of birth, phones, emergency contact of every colleague | Blank the identity/HR fields unless `staff.structure.manage` or `staff.confidential.view` |
| L3 | Medium | `ModulesController.cs:75` `GET modules/mine` | `[Authorize]` only | every role | Agreed prices, billing cycle, trial and activation dates | Codes and active status only, unless `billing.view` |
| L4 | Medium | `FeedbackController.cs:352` `POST …/feedback-link` | `feedback.view` (view on a write) | Viewer, Front Desk, FOM, Head, Admin | Creates feedback rows copying a customer's name, phone and email, and mints codes the anonymous page accepts | Gate on `feedback.respond` / `tokens.create` |
| L5 | Low | `VisitorsController.cs:1583` `POST …/badge-token` | `visitors.view` (view on a write) | Front Desk and above | Mints a fresh signed visitor QR | Gate on `visitors.checkin` |
| L6 | Low | `UsersController.cs:96, 170` users list and by-id | `users.view` | FOM, Academic Assistant, DoS, Deputy | Every user's phone number | Phone only to `users.edit` |
| L7 | Low | `QueueHub` + `QueueHubService.cs:36, 71` | anonymous hub, branch GUID | anyone with the GUID | Customer names on call and counter events | Display number only, unless the branch opts in to names on its board |
| L8 | Low | `SpotifyController.cs:43` playback token | anonymous (commented as an accepted risk) | anyone | A live token for the platform's Spotify account (playback scopes, one hour) | Tie it to a registered display, or leave it and restate the decision |
| L9 | Low (Web) | `Dashboard.razor:264` Quick Actions | module only | Teacher, Board Member… | Counter Terminal and Reports buttons that refuse on press | The sidebar's own permission checks |
| L10 | Low (Web) | Portal, My Day, PortalAppraisal/Notice/Record module redirect | module only | any role | Teachers are sent to the Billing page when the module is missing | Billing only for `billing.view`; otherwise say "not available" |
| L11 | Low (Web) | `MyDay.razor:201` "Open welfare" | owed actions > 0 | Teacher | Goes to Welfare Reports (refuses them) | Link to `/admin/welfare-my-actions` |

**Checked and clean:** API client listing (the webhook secret shows only as "set"), platform settings, payment gateway, onboarding (join-code hash only), kiosk/printer/branding/system settings, the notification hub, and the in-code checks on batch, leadership, calendar, timetable, rota, appraisals and duty reports. `dashboard.view`, `queue.view`, `staff.recognition.give` and (after 1.1) `notifications.view` gate nothing sensitive.
**Not swept, and named so it is not assumed clean:** every endpoint behind `students.view`, `welfare.*` and `welfare.reports.aggregate`. Those have their own suites (sections 1–13, 37) but no role-by-role sweep.

**The rule these share:** a VIEW permission never gates a WRITE, and a field a reader may not need (a secret, a national ID, a price) is blanked by the server for anyone without the narrower permission. A static guard, `view-on-write-check.mjs`, fails a POST/PUT/DELETE whose only gate is a `*.view` code, unless it is on a declared list (read-only POSTs such as a precheck).

---

## 2. Phase 2: My account and My file, one home each

### 2.1 The split

The test that decides each field: **does it control how you get IN, or does it describe you to the
school?**

| Field or feature | Today | Home after |
|---|---|---|
| Photo | Profile | **Account.** It is how you appear everywhere, and it is set by you. |
| Name | Profile (self-edit) | **School record** (D3). Account shows it read-only. |
| Username | Profile (read-only) | Account, read-only |
| Email (sign-in and recovery) | Profile | **Account** |
| Password | Profile | **Account** |
| Devices signed in, "sign out everywhere" | **API only**: `GET auth/devices` and revoke-all exist, and the web has no screen | **Account.** New section, reusing the mobile endpoints. |
| Notification preferences | `/profile/notifications` | Account (a card linking to it, or folded in) |
| Private calendar feed | Profile | Account. It is a credential: a secret link. |
| Member since, last sign-in | Profile | Account, as security facts |
| Phone, second phone, office, emergency contact | **Both.** Profile edits the phone, and My file edits all four. | **My file only** (D4), with the phone confirmation beside it |
| Role, branch, staff number | Profile (read-only) | **My file**, where the rest of the school's record is. Account keeps the role only as a line under the name. |
| System Settings, Logout | Profile's action list | **Removed.** Settings is not an account action (§1.2), and Logout is in the user menu. |

**Tenants without Welfare & Performance have no My Workspace**, and `StaffPortalController` carries
`[RequireModule(StudentWelfare)]`. A bank's staff must still be able to change their phone, so:

- **One writer for contact details: `PUT api/v1/profile/contact`** on `ProfileController`, with no
  module requirement. The portal endpoint is **deleted**, not aliased, and `MyDetailsCard` calls the
  new one.
- **One component, `MyDetailsCard`**, rendered on My file. It appears on the account page **only
  when the tenant has no My Workspace**. One component and one endpoint means no second copy that
  drifts.

### 2.2 The account page (the second screenshot)

Title: **"My account"**. The route `/profile` does not move; it is a wire format that links and
emails carry.

- **The standard band**: `.page-header` with the title and subtitle on one row, and actions top
  right through `QPageActions` ("Change password"). There is no hand-built header and no pencil
  button.
- **Cards, not stacked tiles.** Today each fact is a ~95px tile with an icon box: nine tiles in
  ~800px. After, there are two columns of `QCard`s:
  - **You**: photo, name, role line, username and email, as a compact `dl` of one-line `dt`/`dd`
    rows (the shape `MyDetailsCard` already uses).
  - **Sign-in and security**: password, last sign-in, and devices with "sign out everywhere".
  - **Notifications**: the preferences.
  - **Calendar on your phone**: the existing `CalendarFeedCard`.
- **Only shared components**: `QCard`, `QAvatar`, `QButton`, `QModal`, `QInput`, `QPageActions`,
  `QInfo`. The edit dialog's four raw `<input class="form-control">` become `QInput` (the `@bind`
  rule).
- **Target: the whole page in one screen at 1080p** (≈700px), measured with `furniture-check.mjs`
  and `density-check.mjs` rather than eyeballed.
- **The "Choose File / No file chosen" text over the avatar is a CSS bug.** `Profile.razor:315`
  writes `.avatar-large__change :deep(input[type="file"]), …` inside a plain `<style>` block.
  `:deep()` is only valid in scoped CSS, and **one invalid selector drops the whole selector list**,
  so the rule hiding the native file input never applies. Remove the `:deep()` line.

### 2.3 My file (the third screenshot)

- It keeps contact editing. It gains **Confirm your phone number** next to the phone field, instead
  of that step living only in the onboarding strip.
- It gains a read-only **Employment** block: role, branch, staff number, start date. It shows what
  the school holds, with "Ask an administrator to change this".

### 2.4 Bugs the split fixes on the way

- **Changing your phone on My file keeps it marked confirmed.** `ProfileController` clears
  `PhoneVerifiedAt` when the number changes. The portal contact writer (`StaffPortalController:134`)
  does not, so a new, unconfirmed number keeps the old confirmation, and SMS password resets go to
  it. The single writer in §2.1 closes this.
- **The profile email check compares `Email == lower`**, not the canonical form, and does not check
  the format. The unique index is on `NormalizedEmail`, so a Gmail-dot variant of a colleague's
  address passes the check and then 500s at the index. Use `RegistrationIdentity` and the same
  wording the Users form shows.

**Verified by:** a browser suite `account-and-file.mjs`. It checks the page height, that the file
input is invisible, that the phone is edited in one place only, that a changed phone reads
unconfirmed, the devices list, the bank-tenant fallback (a tenant without the module sees contact on
the account page), and an API check that the old portal contact route is gone.

---

## 3. Phase 3: the remaining code gaps from the handover

1. **Feature codes that gate nothing** (D6): remove `AdvancedAnalytics` / `WebhookIntegration`.
2. **The build stamp says nothing when the tree is dirty.** `Get-RepoBuildVersion`
   (`scripts/deploy/Common.ps1:188`) appends the hash only. Append `-dirty` when
   `git status --porcelain` is non-empty, so a package built from uncommitted work says so on the
   health endpoint.
3. **Programme import undo does not reverse updates** (`TERM_PROGRAMME_CALENDAR_AND_GATES` §build
   notes). Record, per updated row, the fields the import changed and their old values (in the job's
   existing JSON, not a new table). Undo restores them unless the row has changed since, and in
   that case it reports the row by name and leaves it.
4. **The two catalogues describe `notifications.view` differently** (`Permissions.cs:323` "View
   notification settings" against `RbacSeeder.cs:147` "View notification history"). Align both on
   history (D2).

Not in this plan, stated so nobody assumes otherwise: duplicate-account merge (D7), in-browser cell
editing, AI column mapping, scheduled imports, splitting a timetable's date range, carrying future
cover across a permanent swap, and a general `QTable`. Each is a feature needing its own decision.

---

## 4. Phase 4: the documents

1. **`docs/TASK_TRACKER.md`** (D5): rewrite it as **Open** (code, then Yours), plus **Shipped**, one
   line per piece of work with its commit, newest first. Name the last full version's commit at the
   top. Before deleting any item, check it against the code, as was done for this plan. Items
   already confirmed closed in code include: the "Complete record" date, login initials, the
   teacher's empty Administration group, observer pairs, the exam series, the timetable-ownership
   browser suite, Firebase, the 260/250 cap, the radius unification, `WelfareStatusColor`, the
   SuperAdmin empty category list, the branch merge, the dev restore drill and the load test.
2. **Plan headers that read as unbuilt**: `DERIVED_POST_PERMISSIONS.md` ("plan only"),
   `LIST_PAGE_STANDARDISATION.md` ("planned, not started"), `MOBILE_APP_INTEGRATION.md` ("plan
   only") and `TIMETABLE_OWNERSHIP.md` §7.6 (it lists the exam series and the browser suite as not
   built). Each gets a one-line "Built — see …" header.
3. **`docs/SESSION_CHECKLIST.md`** is a 2026-09-18 working log, fully superseded. Delete it.
4. **CLAUDE.md**: remove passages that say they are kept "for history" (the theme gaps' original
   wording, the old local-run notes, the CORRECTION heading) and correct the two notes this plan
   overturns (the old "latent" recipient-less notification note was already corrected, so check it).
   **Rules are not removed**: only history and superseded wording.

---

## 5. Order, and how each phase is proven

| Phase | Ships as | Proof |
|---|---|---|
| **1.1** secret masking | its own commit, first. It is a live leak on production. | new API section 40 (`rbac-settings-e2e.mjs`): every seeded role × GET/PUT |
| **1.5 L1** print SSRF | with 1.1 (same urgency) | section 40 posts an arbitrary address as a Viewer: 403; as an admin: the stored printer only |
| **1.5 L2–L11** | one commit | section 40 role × field assertions; `view-on-write-check.mjs` |
| **1.2–1.4** links and guards | one commit | `browser/rbac-links.mjs`, `nav-gate-check.mjs`, existing `mobile-nav.mjs`, `action-location.mjs` |
| **2** account / My file | one commit | `browser/account-and-file.mjs`, `furniture-check`, `density-check`, `profile-photo.mjs` (must stay green) |
| **3** gaps | one commit per item | section 31 extended for undo; the health endpoint read after a dirty build |
| **4** docs | one commit | no code; `route-audit.mjs` and the guards still pass |

The full API run, the guards and `browser/all.mjs` all run before the last commit. **Deploying is
the owner's call.** Phase 1.1 is the reason to deploy soon: today every teacher at Maryhill can read
the school's SMS gateway key.


---

## 6. Built (2026-09-25/26) — and where the build departed from the plan

**Verified** against a scratch database (`qmgr_verify`) on ports 5101/5103, because this machine's `qmgr` database is
not the dev tenant (see the tracker's environment notes):

| Check | Result |
|---|---|
| API section 40 `rbac-settings-e2e.mjs` (new, every seeded role; wired into `class-teacher-e2e.sh`) | 67 passed, 0 failed |
| API section 31 `programme-import-e2e.mjs` with the new 31.5b (undo of updates) | 58 passed, 0 failed, 1 skipped (the school's documents) |
| Browser `rbac-links.mjs` (new; 11 roles, every link each is shown) — run in a visible Chrome | 44 passed, 0 failed |
| Browser `account-and-file.mjs` (new) — visible Chrome | 23 passed, 0 failed |
| Browser `profile-photo.mjs` (selectors moved to the new page), `mobile-nav.mjs` | 19/0, 21/0 (1 honest skip) |
| Static guards (two new: `view-on-write-check`, `nav-gate-check`) | 19/19 |

**Departures, each for a reason found while building:**

- **L10 was not changed.** A module gate sending a teacher to Billing → Modules is the documented design — that tab is
  open to everyone signed in, precisely so "not part of your plan" never reads as "not allowed here".
- **L8 was restated, not changed** (an accepted risk in its own code comment), and **L5** needed no suite of its own —
  the view-on-write guard covers it.
- **The four print pages needed nothing**: each already shows the API's refusal and disables Print; the API is the gate.
  The two platform pages did get the SuperAdmin refusal.
- **D6 turned out to be a comment, not code**: the two codes appeared only in a doc comment that promised seven grants
  the code never made. The comment now says what the code grants.
- **The phone-confirmation rule moved further than planned** — into `QMgrDbContext`, so EVERY writer of a phone number
  (an administrator's edit, the class-teacher card, an import) clears a stale confirmation, not only the self-service one.
- **The mobile sheet's Settings slot** reads the same `NavGates.SettingsHub` rule through a new `MobileNav.Slot.Gate`.
- **Found on the way:** every package built on this machine carried no commit hash (git refuses an exFAT repo without
  `safe.directory`); `browser/login.mjs` hard-coded port 5003, so no suite could run on other ports.

**Not yet done (2026-09-26):** committed, deployed, and the credentials rotated. Until the deploy, production still
serves the secrets to every `notifications.view` holder; after it, rotate every tenant messaging credential that was
set, because it was readable. The steps are in the tracker's handover.

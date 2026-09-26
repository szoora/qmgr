# SACC Dashboard — Task Tracker

**Rewritten 2026-09-25 to hold only what is open** (decision D5 of `docs/plans/CLOSE_OUT_RBAC_AND_ACCOUNT.md`).
Every completed item was checked against the code before it was dropped. The full history — 11,506 lines of
phases, handovers and findings from 2026-08-16 to 2026-09-24 — is in git:

    git show 8e96aa0:docs/TASK_TRACKER.md

Commit SHAs before 2026-09-06 in that history do not resolve (the attribution rewrite; see CLAUDE.md).
`git log` is the record of what shipped; the index at the bottom points into it.

Status: `[ ]` open · `[!]` needs a person, not code · `[-]` decided, kept here so nobody re-plans it

---

## ▶ HANDOVER — 2026-09-26 (read this first)

### State in one paragraph

The RBAC close-out (`docs/plans/CLOSE_OUT_RBAC_AND_ACCOUNT.md`, artifact
https://claude.ai/artifact/T9LGeAzrQhk6Q7safRvZH8) is **built and verified on this machine, UNCOMMITTED and NOT
DEPLOYED**. `git status` shows ~60 paths on `master` on top of `8e96aa0`. **Production still has the leak:** any
teacher (any holder of `notifications.view` — nine seeded roles) can read the school's SMS gateway key and password,
the SMTP password and the Telegram/WhatsApp tokens from `GET api/v1/notifications/settings/{org}`. Both
`dashboard.maryhillug.net` and `qmgr.cashbook.ug` answered health on 2026-09-26 with the old build.

### ALSO UNCOMMITTED: calendar audiences, notices, sound and the Import inbox (built 2026-09-26, later the same day)

`docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md` (artifact https://claude.ai/artifact/5Q4EZtkKWUKGCSEdGtARLT),
all six phases, E1–E12 as recommended; departures in its §6a, rules in CLAUDE.md's section of the same name. It sits
in the same working tree as the RBAC close-out, so **the commit and the package in "Do next" carry both**, and so
does migration `20260926133928_CalendarAudiencesAndImportRouting` (hand-edited; applied cleanly on 5101).

| Suite | Result |
|---|---|
| API §30 `calendar-e2e.mjs` | 108 / 0 |
| API §31 `programme-import-e2e.mjs` (with `E2E_DOCS_DIR`) | 91 / 0 |
| API §41 `calendar-audiences-e2e.mjs` (new) | 65 / 0 |
| API §42 `import-routing-e2e.mjs` (new) | 40 / 0 |
| API §43 `import-inbox-e2e.mjs` (new) | 30 / 0 |
| Browser `calendar-scope` · `notification-sound` · `import-inbox` (new) — visible Chrome on 9333 | 21 · 12 · 14 / 0 |
| Browser `calendar-ui` · `programme-import` · `student-roster` | 42 · 16 (1 skip) · 31 / 0 |
| Static guards (two new: `preferences-roundtrip-check`, `audience-home-check`) | 21 / 21 |

Found by the runs and fixed: undo removed nothing after a duplicate re-submission (B13 narrowed); the inbox did not
follow its own `?job=` on a button press; a meeting title rendered `@if` as text (`razor-email-transition-check`).
Also found by `rbac-links` on this tenant (Core Queue cancelled) and fixed: the Home and phone Reports link ignored the
module `/reports` needs (`NavGates.Reports` takes the module now, and the sidebar reads it); and `MainLayout.HasModule` failed OPEN
while the module list was still LOADING, so every sign-in briefly offered every module's pages. **Not re-verified: the
rerun of `rbac-links`, `mobile-nav` and `portal-tabs` after that last fix was stopped by the machine running low on
memory.** The run before it was rbac-links 43/1 (the loading window), mobile-nav 21/0, portal-tabs 23/0.
**Still not run: the full API and browser runs.**

### ALSO UNCOMMITTED: segregation of duties and lesson plans (built 2026-09-26, the same day)

`docs/plans/LESSON_PLANS_AND_SCHEMES_OF_WORK.md` (artifact https://claude.ai/artifact/GrjY8qNAeEjuY3jsp2ntUu), L1–L12 as
recommended, the form as the main route; departures in its §11, rules in CLAUDE.md's two sections of 2026-09-26. Migration
`20260926163934_LessonPlansAndSeparationOfDuties` (one table, two columns) applied cleanly on 5101. **Phase 0 changes live
behaviour**: an appraisal can no longer be signed by its subject or its reviewer; nobody logs, annuls or re-scores a
record about themselves; the writer of minutes does not adopt them; nobody appoints themselves head of department.

| Suite | Result |
|---|---|
| API §44 `separation-of-duties-e2e.mjs` (new) | 25 / 0, 1 skip (G6 needs a published timetable) |
| API §45 `teaching-plans-e2e.mjs` (new), repeated runs | 99 / 0 |
| API §14 · §15 · §17 · §26 (touched by Phase 0) | 296 · 261 · 48 · 54 / 0 |
| Browser `lesson-plan-ui` · `plan-pdf` · `plan-templates` (new) — visible Chrome on 9333 | 29 · 15 · 9 / 0 |
| Static guards (new `separation-check`) | 22 / 22 |

Found and fixed on the way: approving a revision failed for everyone (the one-current-plan index); a duty report's
return and review were separated only through access flags. **Still not run: the full API and browser runs.**

### Do next, in this order

1. **Commit** the working tree as one commit on `master` (user instruction pending — they were asked and chose to
   update the docs first). Do not `git checkout` any file to undo an edit; there is no other copy.
2. **Build the package**: `pwsh scripts/deploy/build-linux.ps1` (reads `scripts/deploy/secrets.local.json`). The
   version now reads `…+<stamp>.<hash>` and ends `.dirty` if anything is uncommitted — build AFTER the commit.
3. **Install on the server** — the user does this, or gives SSH from this machine (there is none today):
   `scp -P<port> <package> root@<server>:/tmp/` then `ssh root@<server> -p <port>` and run the installer the build
   prints. **The package is everything since `f2762c4` (2026-09-24), not the security fix alone**: the rebrand, the
   calendar, exam series, the access review and their migrations, which run at start-up.
4. **After deploy, rotate every tenant messaging credential that was set** (SMS gateway key/password, SMTP password,
   Telegram and WhatsApp tokens) — they were readable by every teacher until the deploy. That is the school's and the
   gateway's act, not code.
5. Confirm who renews the shared `*.cashbook.ug` certificate before **9 October 2026**.

### What was built (details in the plan's §6 and CLAUDE.md's 2026-09-25 sections)

- **Secrets**: the settings read needs `notifications.manage`; `SecretMask` masks every secret on the way out and
  keeps the stored value when the mask comes back; the Integrations tab reads a four-flag `…/channels` endpoint.
- **Eleven more leaks** (plan §1.5): ticket print is `tokens.create` and prints only to the stored printer (it opened
  TCP to any address in the request); colleagues' national ID / DOB / next of kin blanked below HR readers; module
  prices and trial dates only for `billing.view`; account-list phones only for `users.edit`; no customer names on the
  anonymous queue hub; feedback link and visitor badge need the act's permission; Home quick actions and My School
  Day's welfare link follow the page. L8 (Spotify) and L10 (Billing → Modules) are decided, not bugs.
- **One link rule**: `NavGates` for the sidebar, user menu, phone sheet and Home; Settings only for people who can
  change something there (the Administrator). Guards `nav-gate-check` and `view-on-write-check` (19/19 guards pass).
- **My account / My file**: `/profile` rebuilt from shared components (fits 1080p; the "Choose File" bug gone; devices
  and "sign out everywhere" added; no Settings/Logout; names read-only). Contact detail has one writer,
  `PUT api/v1/profile/contact` (no module), one card (`MyDetailsCard`), one confirm control (`PhoneConfirm`).
  `QMgrDbContext` now clears a phone confirmation whenever the number changes, whoever changes it.
- **Gaps**: programme-import undo restores the rows it updated (names any edited since); the build stamp carries the
  hash and `.dirty`; the feature-flag comment says what the code grants.
- **Docs**: this tracker rewritten (old text: `git show 8e96aa0:docs/TASK_TRACKER.md`); four stale plan headers
  corrected; `SESSION_CHECKLIST.md` deleted; CLAUDE.md pruned of "for history" passages, push note corrected, three
  new sections added.

### Verified (against `qmgr_verify`, ports 5101/5103 — see Environment notes)

| Suite | Result |
|---|---|
| API §40 `rbac-settings-e2e.mjs` (new; wired into `class-teacher-e2e.sh`) | 67 / 0 |
| API §31 `programme-import-e2e.mjs` (+31.5b undo of updates) | 58 / 0, 1 skip (school documents) |
| Browser `rbac-links.mjs` (new; 11 roles) — visible Chrome | 44 / 0 |
| Browser `account-and-file.mjs` (new) — visible Chrome | 23 / 0 |
| Browser `profile-photo.mjs`, `mobile-nav.mjs` | 19 / 0, 21 / 0 (1 skip) |
| Static guards | 19 / 19 |

**NOT run**: the full API run (`class-teacher-e2e.sh`) and the full browser run (`all.mjs`). They need the real dev
tenant and `D:\QMGR\DATA`, neither of which is on this machine. Run both on the machine that has them before or right
after deploying; the changed endpoints were grepped against every suite and only administrator callers use them.

### Left running on this machine

API on 5101 (database `qmgr_verify`), Web on 5103, headless Chrome on 9333, **visible Chrome on 9334** (the user
watches browser runs there — use `CDP_PORT=9334`). `qmgr_verify` holds two stand-in tenants ("E2E Verify School",
"E2E Queue Only Bank"); drop it when it is no longer wanted. Section 40's scratch tenants purge themselves.

---

## Open — code

Nothing from the handover or the close-out plan is left undone in code. What remains is either a feature that
needs its own plan, or a path that has never run against the real thing.

**Features that need their own plan first** (each was deliberately left out of the work that found it):

- [ ] **Merging duplicate staff accounts** (`STAFF_WITHOUT_EMAIL.md` §4) — the most useful of these; a school's
      re-import is the common way two accounts appear for one person.
- [ ] Self sign-up (`/register`) for somebody with no email address; a second address per person.
- [ ] Splitting a published timetable's date range at a date (`TIMETABLE_OWNERSHIP.md` §7.6).
- [ ] Carrying future cover across a permanent swap (same place — a wrong carry puts the wrong person in front of a class).
- [ ] A general `QTable` (`STAFF_PERFORMANCE_MONITOR.md`) — three table approaches coexist.
- [ ] Import extras: in-browser cell editing, AI column mapping, scheduled or API-driven imports (`BULK_IMPORT_SYSTEM.md` Phase 7).
- [ ] A per-display language, or a per-display theme — a column and a decision each; ask first (CLAUDE.md).
- [ ] Wiring `CustomBranding`, `MultipleDisplays`, the notification channels or webhooks to module grants — the
      feature-flag comment promised them and the code never granted them (corrected 2026-09-25). Needs pricing first.

**Never run against the real thing** (stated, not claimed):

- [ ] A push notification reaching a real phone (Firebase is configured; FCM accepted a validate-only send).
- [ ] A head of department receiving a post-derived notice (only weekly/monthly sweeps send one).
- [ ] The student and welfare endpoints have their own suites (sections 1–13, 37) but no role-by-role sweep like
      section 40. Not assumed clean.

**Accepted, and restated so nobody reopens them by accident:**

- [-] `GET spotify/playback-token` is anonymous — a one-hour, playback-scoped token for the platform's own account,
      needed by signage screens that have no sign-in. Accepted risk (its own code comment); revisit only if the
      account's scopes widen.
- [-] The queue hub is anonymous and a board subscribes with the branch id. Since 2026-09-25 it carries ticket
      numbers only — no customer name travels on it.
- [-] A module gate sends everybody — a teacher included — to Billing → Modules; that tab is open to every
      signed-in person on purpose (CLAUDE.md, Billing hub). Not a leak.

## Open — yours (not code)

- [!] **The shared Sectigo certificate for `*.cashbook.ug` expires 9 October 2026** (B8). It is renewed by something
      outside this product, and every `*.cashbook.ug` tenant domain's fast path depends on it. Confirm who renews it.
- [!] **Commit, then deploy** — see the handover. Nothing since the rebrand (`f2762c4`) is on production, and the
      close-out fixes a live leak: every teacher at Maryhill can read the school's SMS gateway key today. Then rotate
      the tenant messaging credentials.
- [!] B7 — the first certificate issued for a tenant's own domain, on the server (`dashboard.maryhillug.net`).
- [!] The first real money through the sacc.ug gateway ("Send UGX 500" on `/platform/payments`), after pasting the
      real key; the dev database carries the stub's key.
- [!] A live password-reset email read end to end since the public-address consolidation.
- [!] The production restore drill (`sudo bash …/qmgr-restore-db.sh <dump> --drill`) and `suppression_reapply` on a
      real restore; a load test against a scratch tenant on production.
- [!] A check on a real handset (the layout is measured at 390px by `mobile-readiness.mjs` and `mobile-nav.mjs`).
- [!] A security review against the real production configuration.
- [!] URSB search/filing for "SACC"; confirm "SACC" is a registered SMS sender; the Play listing text and icon.
- [!] iOS: an Apple Developer account and a Mac. The mobile repo (`D:\QMGR\Mobile\QMgr`) has no remote — add one,
      and back up the upload keystore off that machine.
- [!] `apps.cashbook.ug/qmgr` does not exist yet, so no app build is published.
- [!] Compliance review before any regulated vertical (the hospital adapter); integration partner outreach.

## Environment notes (this machine, 2026-09-25)

- **Port 5001 is the CRM API** (`D:\SACC_SOFTWARE\CRM`) on this machine. Run this product on other ports and pass
  them to the suites: `WEB=http://127.0.0.1:5103 API=http://127.0.0.1:5101`.
- **The local `qmgr` database is not the dev tenant.** It stops at the 2026-01-24 `InitDb` migration with tables
  its history does not list, so the API refuses to start against it. The close-out was verified against a scratch
  database, `qmgr_verify` (`ConnectionStrings__DefaultConnection=…Database=qmgr_verify…`), with a stand-in tenant
  "E2E Verify School" carrying the fixed e2e accounts. Drop it when done; the real dev tenant is elsewhere.
- **Drive E: records no file owners**, so git refuses the repository ("dubious ownership") unless called with
  `-c safe.directory=*`. `scripts/deploy/Common.ps1` does that now — every package built here before 2026-09-25
  carried no commit hash in its version.

---

## Shipped — an index into `git log` (newest first)

| Date | What | Commit |
|---|---|---|
| 2026-09-25 | RBAC close-out: messaging secrets masked, eleven leaks closed, one link rule, account vs My file, undo of import updates | **uncommitted** — see the handover |
| 2026-09-24 | Rota cancel ends a slot under way; Firebase push wired; school day follows its times | `8e96aa0` `2487cb9` `e7e4eff` |
| 2026-09-24 | Welfare alerts reach housemasters; governors count confidential cases; full runs pass | `c6b2e27` |
| 2026-09-24 | SACC Dashboard rebrand, exam series, employment types, the school chain, the RBAC review | `f2762c4` |
| 2026-09-23 | User limit counts active people | `c50b9a9` |
| 2026-09-23 | Posts, timetable ownership, one recipient per notification, calendar from Word, every list paged | `753da33` |
| 2026-09-22 | Mobile shell; localisation reaches the screen; photos, codes as keys, the phone bar, re-import diff | `66a03db` `0a96e0f` `3e3f6db` |
| 2026-09-21 | Tenant domains (AllowedHosts, the domain helper, the hybrid certificate); bulk staff actions | `e4bab42` `26a2516` `1f01249` `350c4fe` |
| 2026-09-21 | Staff self-service; bulk-import wizard; staff without email | `4ab5ec2`…`75b1d0c` `7d6f395` |
| 2026-09-21 | One door per kind of check; compiler warnings as errors | `369aff6` `aaff050` |
| 2026-09-21 | Payments, billing hub, minutes, list pages, white label, tenant purge | `f1aed03` |
| 2026-09-19 | Communication and Administration hubs; compaction; the staff record UI | `803ece4` |
| 2026-09-18 | Staff hubs; one branch; role gates; welfare export log; BranchAwareComponentBase; document sharing audit | `1e25ade`…`57eb5a6` |
| 2026-09-17 | Duty rota Phases 0A–6, timetable and lessons | `ec384eb` `efdd7b2` |
| 2026-09-16 | Staff Performance Monitor, Phases 0–6 | `5ad8b37`…`5acc54e` |
| 2026-09-15 and earlier | Gated uploads, secure document sharing, platform email, and Phases 1–84 | `git log 8002b1e` |

# A staff member with no email address

**Artifact:** https://claude.ai/artifact/T5VPoTq4GgbGA6kNMo7mpr
**Status:** BUILT and verified, 2026-09-21. Asked for after the bulk-import work found that
**133 of the 184 staff in `Staff List.xlsx` have no email address** and the importer refused them.

**Verified against the dev tenant and the school's own file:** section 22
(`scripts/e2e/staff-no-email-e2e.mjs`, **17 checks, 0 failed**) and
`scripts/e2e/browser/import-wizard.mjs` (**22 checks, 0 failed**). The staff list went from
**49 ready / 135 refused → 182 ready / 2 refused**, and the two it still refuses are typos in the
school's own data — `nabaasascovia273@gmail` with no top-level domain and `tuyishimedeborah@gmailcom`
with no dot. Refusing those is the point: they are a mistake somebody can fix, not a person with no
address. **Nothing is refused for a MISSING address any more**, and the suite asserts that directly.

**Two things the scope did not foresee, both found by running it:**

1. **The staff number could not be set at all.** `CreateStaffMemberRequest` had no `EmployeeNumber`
   field, so the key this whole design leans on was unreachable from the one dialog that creates a
   member of staff by hand — and a duplicate number was accepted silently. The field, the uniqueness
   refusal (in words, not a database error) and the form input all went in.
2. **EF quietly dropped `IX_users_OrganizationId`** when the composite index was added, because a
   convention index whose column another index starts with is considered redundant. But the new index
   is PARTIAL (`WHERE EmployeeNumber IS NOT NULL`), so it cannot serve the tenant-scoped lookups
   every query in this product makes. It is now declared explicitly so it survives. **Read the
   generated migration; do not assume what it will contain.**

---

## 1. The problem, precisely

Q-Mgr treats an email address as a staff member's identity: it is what `User.Email` holds, what two
unique indexes are built on, what an invitation is sent to, and what the staff importer keys a
duplicate on. `User.Email` and `User.NormalizedEmail` are **non-nullable** (`string`, default `""`),
so "no email" cannot even be stored — and it could not be represented as an empty string either,
because `idx_users_email` and `idx_users_normalized_email_unique` would then refuse the second such
person. That is the whole blocker, and it is a schema fact rather than a policy one.

**Everything else is already in place.** The product does not actually need an address to work:

| What a school does | Needs an email? |
|---|---|
| Sign in | **No** — `AuthController` already matches `Email == identifier OR Username == identifier` (a 2026-08-21 decision, "a bare-username identifier is a supported, intentional input shape here") |
| First password | **No** — printed **temporary password slips** or **SMS** are two of the three delivery modes the staff importer already offers |
| Be notified | **No** — the bell and SignalR are the instant path; email and SMS are the extras |
| Be on a rota, take a register, be appraised, hold a class | **No** — none of it reads an address |
| Reset their own password | **Yes**, and this is the one real loss (§5) |
| Receive an invitation email, a broadcast, a welfare alert by mail | **Yes**, and each must degrade to "skipped, because there is no address" rather than "failed" |

So the change is: **let the column be absent, make the few places that assume it cope, and give the
school a way to identify and reach the person instead.**

---

## 2. What identifies a person instead

Three keys, in this order — and it matters that they are ordered, because the importer, the
duplicate check and the "already here?" question all need one answer:

1. **Email**, when there is one. Unchanged for the 51 staff who have one.
2. **Employee number**, scoped to the organization. `User.EmployeeNumber` exists and is written by
   the importer today, but it carries **no unique index**, so nothing stops two rows sharing one.
   This scope adds a partial unique index on `(OrganizationId, EmployeeNumber) WHERE EmployeeNumber
   IS NOT NULL` — the school's own staff number becomes a real key, which is what it already is on
   paper (`700001`, `700483` in the file).
3. **Username**, which is globally unique already and is what an emailless person signs in with.

**Postgres treats NULLs as distinct in a unique index**, so a nullable `Email` needs no other change
to either existing index — the same fact the self-service duplicate index had to learn the hard way
(CLAUDE.md, 2026-09-21). It is the reason this is a small migration rather than a redesign.

---

## 3. The work, in the order it has to land

### A · Schema — one migration

- `User.Email` and `User.NormalizedEmail` → `string?`; drop `.IsRequired()` in `UserConfiguration`
  and `UserIdentityConfiguration`. Both unique indexes stay exactly as they are.
- Add the partial unique index on `(OrganizationId, EmployeeNumber)`.
- **No backfill**: every existing row has an address, so nothing changes for anyone already here.
- `QMgrDbContext.ApplyIdentityNormalization` currently does `user.Email.Trim()` **in memory** — this
  is the one guaranteed `NullReferenceException` in the change, and it is on the write path of every
  user in the system. It becomes: null in, null out, and `NormalizedEmail` stays null.

### B · Identity and auth

- **Sign-in already works.** `u.Email.ToLower() == identifier` runs in SQL, where `NULL = x` is
  false, so an emailless account is found by its username and nothing else matches it by accident.
  Worth an assertion in the suite rather than a change.
- **Audit the in-memory dereferences**: 19 in `Q-Mgr.API` and 6 in `Q-Mgr.Web` call a method on
  `.Email` outside a LINQ query. Each is either a guard or a `?? ""`. That is a countable job, not
  an open-ended one, and a compiler warning (`CS8602`) will point at every one once the column is
  nullable — which is the argument for nullable over "empty means none".
- **Forgot password** answers an emailless account exactly as it answers an unknown address — it
  must not disclose that the account exists — and the sign-in page gains one line: *"No email on
  your account? Your school administrator can issue a new password."*

### C · Creating one

- `CreateUserRequest.Email` and the staff dialog's field become optional, with one rule stated in
  both the form and the API: **an email or a username is required, and a staff member with no email
  must have a way to receive a password** (a phone for SMS, or a printed slip).
- **Username generation** today takes the email's local part. Without one it comes from the name and
  the employee number (`m.kato`, `m.kato2`, …) — the importer's existing dedupe loop already handles
  the collision.
- `RoleAssignmentGuard` and every permission rule are untouched: none of them reads an address.

### D · The importer

- `StaffImportRow.Email` stops being a blocking error **unless** the row's delivery is
  `Invitation` — you cannot email an invitation to nobody, and that refusal keeps its current
  wording. For a slip or SMS row it becomes a **warning** naming what will happen: *"No email —
  they will sign in as `m.kato` with a printed slip."*
- The duplicate key becomes email → employee number → username, in that order, both in
  `DuplicateKey` on the panel and in `ProcessStaffRowAsync`'s existing-account lookup.
- `StaffImportPrecheckRequest` takes employee numbers alongside emails, so the "already here?"
  question still has an answer for the 133.
- `LineManagerEmail` also accepts an **employee number**, or a line manager cannot be named for
  anybody who has no address.
- **The measurable outcome: `Staff List.xlsx` goes from 49 ready / 135 refused to 184 ready**, which
  is the acceptance test for this whole piece of work.

### E · Delivery

- The email channel already resolves a recipient and skips an empty one; with a null address it must
  record **Skipped with a reason**, never `Success = false` — the delivery log's existing rule, and
  the difference between "email is off for this person" and "every message to them failed".
- **Anything that composes a message to a group says how many it cannot reach** before it is sent:
  the broadcast composer, the staff notice fan-out, welfare alerts. *"142 of 184 staff have no email
  address and will get this in the app only."* A school must not learn that from an empty inbox.

### F · What a person sees

- Users & Roles, the staff directory, the staff record and the portal show **"No email address"**
  rather than an empty cell, with an *Add one* control wherever the record is editable.
- The onboarding/temporary-password page is the answer to "they cannot sign in" and already exists;
  the person's card links to it.

### G · Verification

- **API suite (a new section in `class-teacher-e2e.sh`)**: create a staff member with no email; sign
  in as them by username; create a SECOND one and prove the unique indexes tolerate both; post a
  notification and assert the email channel is **Skipped, not Failed**; import a two-row sheet with
  no email column at all; add an address later and prove it still collides correctly with an
  existing one.
- **Browser (`import-wizard.mjs`)**: the existing suite's staff assertions change from "49 ready,
  135 refused" to "184 ready", which is the honest one-line proof that this worked.

---

## 4. Deliberately NOT in this scope

- **Self sign-up** (`/register`) still requires an email: that flow is a stranger creating an
  organisation, and the address is the only identity anybody has for them.
- **Merging duplicate accounts**, if a school later adds an address to somebody who was also created
  a second time. Detection is covered by the employee-number index; merging is its own feature.
- **A second address per person**, SSO, or any change to how the platform itself sends mail.

---

## 5. What the school loses, stated plainly

A staff member with no address **cannot reset their own password**. Every reset for those 133 people
is an administrator issuing a new temporary one from the Onboarding page — by SMS where there is a
phone number (164 of the 184 rows carry one), on a printed slip where there is not. That is the
operational cost of this change and it should be said out loud to the school rather than discovered
in a term's time. It is still better than the alternative, which is 133 people who cannot be in the
system at all.

---

## 6. Size and shape

**One session, three commits** — schema and identity; the importer; the UI and the suites. The only
part with real spread is §B's audit of 25 dereferences, and making the column nullable turns that
from a search into a list the compiler hands over. Nothing here touches permissions, scope rules,
the welfare ledger or payments.

**The riskiest single line** is `ApplyIdentityNormalization`: it runs on every user write in the
system, and it is the one place where a null address would throw rather than quietly do nothing.

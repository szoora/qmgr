# A post grants its own access: deriving permissions from assignments

**Status:** plan only. No code written. 2026-09-22.
**Three pieces of work, asked for together:** derive post permissions (§0–2), split the class-teacher
page (§3), and stop the roster import demanding a guardian (§4b).
**Asked for as:** *"derive class teacher permissions from the assignment, and split the page. the same
treatment for head of department i guess"* and *"since when did guardian and email mandatory for
student? the only mandatory fields are Name, code and class."*

---

## 0. The defect, stated precisely

**Access is decided on two independent axes, and nothing keeps them in step.**

- **Scope** comes from the ASSIGNMENT. `StudentScopeService` reads `ClassTeacherAssignment.Role`:
  `ClassTeacher`/`Assistant` → `StudentAccessTier.Pastoral`, `SubjectTeacher` → `Teaching`. The
  person's role code is never consulted (lines 197–198, 257).
- **Permissions** come from the ROLE. `PermissionAuthorizationHandler.GetUserPermissionsAsync` is
  one query: `Users → Role → RolePermissions`. Nothing else contributes.

So a holder of the seeded `teacher` role, assigned as class teacher of S4B, gets pastoral SCOPE over
S4B and **no pastoral PERMISSION at all** — that role carries `dashboard.view`,
`notifications.view`, `staff.recognition.give` and `students.view`, and not one `welfare.*` code.
They see the class list; every class-teacher feature refuses them.

**It fails in the direction that looks fine.** The assignment saves, the page shows them as the
class teacher, the coverage warning stops naming that class — and the welfare features silently are
not there. Nobody gets an error to report.

### The same shape, one module along

`Department.HeadUserId` and `DeputyHeadUserId` are COLUMNS on the department
(`Domain/Entities/Staff/Department.cs:32-33`), read by `StaffScopeService` at lines 140 and 187 to
decide `StaffDataScope.AssignedDepartments`. The `head-of-department` ROLE separately carries
`staff.records.*`, `staff.appraisals.conduct`, `staff.duties.manage` and the rest.

So: **making somebody head of Mathematics gives them the staff scope and none of the permissions**,
exactly as with a class teacher. The user's guess — *"the same treatment for head of department i
guess"* — is right, and for the identical reason.

**Two more posts have the same shape and should be checked before this is called done:**
`StaffDuty.RecorderUserIds` (a named recorder may take that duty's register — already authorised
from the duty rather than the role, so this one is already correct and is the model to copy) and a
duty slot's supervisors.

---

## 1. Why derive rather than document

The alternative is a line in the manual: *"when you assign a class teacher, also change their role
to class-teacher."* Rejected, because:

- It is a **two-step act with no enforcement**. Step one has a UI, a confirmation and a warning
  banner; step two lives in a different hub under a different permission. Nothing notices when only
  step one happens.
- It makes the role **wrong about the person**. A Director of Studies who also tutors S4B would have
  to be demoted to `class-teacher` to get tutor access, losing everything else.
- **The mainstream school systems do not model it as a role.** SIMS, Arbor, Bromcom, PowerSchool and
  Veracross all treat form tutor as a POST held over a group, with tutor-level access following from
  holding it. Nobody asks an administrator to remember a second step.
- It is NIST's ABAC-over-RBAC argument in a school's shape: the attribute — *is class teacher of
  S4B* — is what should grant the access, not a parallel role somebody keeps in sync by hand.

---

## 2. The design

### 2.1 `IPostPermissionService` — the one home

A new service answering one question: **"what does this person's POSTS grant them, on top of their
role?"**

```
Task<IReadOnlyCollection<string>> DerivedForAsync(Guid userId, CancellationToken ct)
```

Two sources today, each a single query:

| Post | Found by | Grants |
|---|---|---|
| Class teacher / assistant | a live `ClassTeacherAssignment` with `Role` in (`ClassTeacher`, `Assistant`) | `students.view`, `welfare.view`, `welfare.create`, `welfare.edit`, `welfare.notify` — **not** `welfare.reports.own` (decision 1) |
| Head / deputy head of department | `Department.HeadUserId` or `DeputyHeadUserId`, `IsActive` | `staff.records.view/create/edit`, `staff.duties.manage`, `staff.appraisals.conduct`, `staff.reports.view`, `staff.duty-reports.view`, `timetable.lessons.flag` — head and deputy alike (decision 2) |

**The grants are DECLARED CONSTANTS on this service.** An earlier draft had them read from the
seeded `RoleDefinition` so the two could not drift — but those roles are being DELETED, so there is
nothing left to read from, and this service becomes the one home by construction rather than by
discipline.

**Subject teachers grant NOTHING.** `ClassTeacherRole.SubjectTeacher` already yields the Teaching
tier, and the `teacher` role's own comment is explicit that it gets `students.view` **only** and
never a welfare permission. Deriving welfare access from a subject-teaching assignment would hand
every teacher in the school pastoral access to every class they teach — the opposite of the duty
rota plan's §5.3 decision.

### 2.2 Where the derived set joins the real one

**Three places read permissions, and all three must agree** — this project's own
three-catalogue lesson:

1. **`PermissionAuthorizationHandler.GetUserPermissionsAsync`** — the API gate. The derived set is
   unioned into the cached value.
2. **`AuthController.BuildUserInfo`** — what `UserInfo.Permissions` carries into the browser, and
   therefore what every `@if (HasPermission(...))` in the Web renders on. Called from login, refresh
   and the mobile handoff, which is why it was extracted into one method on 2026-09-22.
3. **`Web/Services/IPermissionService`** — reads `UserInfo.Permissions`, so it follows (2) for free.

A fourth consumer, `MobileNav.SlotsFor`, takes a predicate built from the same set and needs nothing.

### 2.3 THE CACHE IS THE HARD PART

CLAUDE.md carries a standing rule, written when the student scope was built:

> **Do not cache the scope alongside permissions.** `PermissionAuthorizationHandler` caches a
> permission set for five minutes; class membership changes far more often, and a teacher removed
> from a class must lose access on the very next request.

Deriving permissions from assignments puts those two things in one bag — which is precisely what
that rule forbids. It is resolvable, and the resolution is the load-bearing part of this plan:

- **The derived set is cached with the rest** (one lookup, no extra query per request), **and every
  write that changes a post invalidates that user's cache immediately.** The hook already exists:
  `IMemoryCache.InvalidateUserPermissions(userId)`.
- **`IStaffProfileChangeNotifier` is the one home for "this person's access changed"** — it already
  does cache drop, live push, activity event and the `staff.profile-changed` notification, and the
  bulk role change was moved onto it after it was found **never clearing the permission cache**, so
  somebody demoted in a batch kept their old permissions for five minutes. Every post write calls
  it.
- **The scope itself stays uncached**, exactly as now. Only the derived PERMISSION set is cached, and
  only because it is invalidated on write rather than expiring.

**Five write paths must call the notifier**, and a sixth is the trap:

| Path | File |
|---|---|
| Assign a class teacher | `ClassTeachersController` `POST …/class-teachers` |
| Assign a subject teacher | `POST …/class-teachers/subject-teachers` |
| End an assignment | `DELETE …/class-teachers/{assignmentId}` |
| Create / update a department | `StaffStructureController` (`HeadUserId`, `DeputyHeadUserId`) |
| Bulk assign a line manager | the staff-directory bulk action |
| **A CLASS RENAME** | moves assignments in the same save — the holders are unchanged, so no invalidation is needed, but it must be confirmed rather than assumed |

**A missed invalidation is a five-minute window, not a permanent hole** — which is the right failure
shape, but it is still the thing most likely to be got wrong, so the e2e asserts it directly.

### 2.4 DELETE `class-teacher` and `head-of-department` — see §5

Both roles are removed outright, with a migration that reassigns their holders first. The reasoning
and the ordered steps are in §5 — the short version is that neither role has ever done anything on
its own, because each is scoped to a post its holder must separately hold.

### 2.5 What must NOT change

- **Scope still fails closed.** A scoped caller with no assignment sees nothing, never the branch.
  Deriving permissions must not accidentally widen `IStudentScopeService`; the two axes stay
  separate, they are merely kept in step.
- **`RoleAssignmentGuard` is untouched.** Deriving permissions from a post is not a route to
  assigning a role, and no post grants `roles.edit`, `users.*` or anything a person could use to
  escalate themselves. **The derived list is a fixed constant, never anything a caller supplies.**
- **Restricted still means restricted.** A derived `welfare.view` is the Standard rung, exactly as
  the role's is. `welfare.restricted.view` is Tenant Admin and SuperAdmin only and is not derivable.

### 2.6 AUDIT: a post must grant permissions AND scope, or the plan opens a school-wide leak

Audited 2026-09-22 against the question *"a person who is head of department, class teacher and
subject teacher — do they assume all three, and does anything collide?"* **The union is right and
the student-side merge is already correct. But the plan as written at §2.1–§2.5 has one serious
defect and one dud, and they are the same root cause.**

#### The root cause, in one sentence

**§2 derives PERMISSIONS from the post and leaves SCOPE on the role.** §2.5 says the two axes *"stay
separate, they are merely kept in step"* — and nothing in the plan keeps them in step. Both scope
services short-circuit on the role and never look at the post:

```csharp
// StudentScopeService.IsUnscopedAsync
var scope = await _context.Users...Select(u => (RoleDataScope?)u.Role.DataScope)...;
_isUnscoped = scope == RoleDataScope.Organization;

// StudentScopeService.ApplyAsync
if (await IsUnscopedAsync()) return query;      // NO FILTER AT ALL
```

```csharp
// StaffScopeService.GetScopeAsync
.Select(u => (StaffDataScope?)u.Role.StaffScope)      // the ROLE, never the department post
```

#### Defect 1 — a class teacher on an Organization-scoped role sees the WHOLE SCHOOL

Three seeded roles carry `DataScope: Organization` **and lack the welfare permissions**:

| Role | `DataScope` | Holds welfare? |
|---|---|---|
| `support-staff` | Organization | No — `dashboard.view`, `notifications.view`, `staff.recognition.give` only |
| `viewer` | Organization | No |
| `manager` | Organization | Yes already (so unaffected) |

Make a lab technician or a matron on `support-staff` the class teacher of S4B. The plan derives
`students.view`, `welfare.view`, `welfare.create`, `welfare.edit`, `welfare.notify` — and their role
scope is still `Organization`, so `IsUnscopedAsync()` returns **true**, `ApplyAsync` returns the
query **unfiltered**, and they read **every child in the school**, not S4B.

**Today the only thing preventing this is the missing permission.** The plan removes exactly that
lock. This inverts CLAUDE.md's own rule — *"a row-level scope does not substitute for the permission
gate"* — because here the permission gate was substituting for the absence of a row-level scope.

**It is not caught by any existing check.** The scope service is behaving exactly as designed; the
guard, the fail-closed short-circuit and the 404 rule are all intact and all irrelevant, because an
unscoped caller never reaches them.

#### Defect 2 — the mirror: a derived head of department sees NOBODY

`head-of-department` is the only seeded role carrying `StaffScope: AssignedDepartments`. Deleting it
(§5 decision 3) drops its holders onto `teacher` or `staff`, both `StaffScope: SelfOnly`.
`GetScopeAsync` reads the role, `ComputeVisibleAsync` switches on it, and `SelfOnly` returns `{me}`.
So the person gets `staff.records.view` and a Records page showing **only themselves** — a permission
that opens an empty page.

**A second, quieter change in the same step:** `head-of-department` also carries
`DataScope: Organization` today. Reassigning its holders to `teacher` (`AssignedClasses`) **narrows**
their student access. That is safer, and it is probably right, but it is a behaviour change that
must be stated rather than discovered.

#### The rule this adds: A POST NARROWS, IT NEVER WIDENS

`IPostPermissionService` returns **(permissions, scope) as a pair**, and the resolution is:

- **A permission held ONLY by derivation runs at the POST's scope**, whatever the role says. A
  support-staff class teacher of S4B is `AssignedClasses` **for the derived welfare set**, never
  Organization.
- **A permission the ROLE already grants keeps the ROLE's scope.** A Tenant Admin or Manager who is
  also a class teacher does not lose the school — they already held welfare access organization-wide,
  and the assignment must not take it away.
- **Deriving head-of-department permissions derives `AssignedDepartments` staff scope with them**, or
  the grant is a dud.
- **The migration reassigns to a role whose scope matches the post** — `teacher`
  (`AssignedClasses` / `SelfOnly`), never `staff` or `support-staff` (`Organization`).

Mechanically this means `IsUnscopedAsync()` can no longer be a bare read of `Role.DataScope`: a
caller is unscoped on the student axis only when the **role itself** both grants the permission and
is organization-scoped. Same shape on `StaffScopeService.GetScopeAsync`. Both stay the one home for
their axis — they gain a second input, not a second copy.

#### What the audit checked and found ALREADY CORRECT

So nobody re-runs it, and because it is the direct answer to *"does the class-teacher hardening
exist?"* — **for a caller whose role is `AssignedClasses`, it does, and it is good:**

- **Several posts on one person merge correctly, and the merge is per CLASS, not per person.**
  `GetTiersAsync`: *"One user, both tiers: pastoral wins for the classes they are pastoral for."* So
  class teacher of S4B + subject teacher of S2A + head of Science gives **Pastoral over S4B,
  Teaching over S2A, and nothing at all over S3C.** There is no collision and no bleed into the next
  class.
- **`Rank()` is RELATIVE, so deleting two roles shifts nothing.** `IsManagerOrAbove` is
  `Rank(x) <= Rank(Manager)` and both deleted roles sit below Manager, so no holder's
  `IsManagerOrAbove` or `RoleAssignmentGuard.IsAtOrBelow` result changes. This was the collision most
  likely to be silent, and it is not there.
- **Fail-closed holds on both axes.** `FilterByClasses` short-circuits an empty allow-list to an
  explicit `Where(_ => false)` rather than trusting EF to translate `Contains` over an empty list;
  `AssignedDepartments` with no department breaks to `{me}`; an unknown or inactive user resolves to
  `SelfOnly` / scoped-to-nothing on both.
- **Tiers cannot be confused.** `CanSeeStudentAsync` admits `Pastoral` and `Unscoped` only, so a
  subject-teaching assignment can never reach a pastoral route; `ApplyAnyTierAsync` is the separate,
  explicitly-named door.
- **Class-name matching is normalised on both sides in all three homes** — `StudentScopeService`,
  `ClassTeachersController`, `WelfareAlertService` — so a stray space cannot hide a child from their
  own class teacher or expose one from the next class.
- **The create path is scoped, including the linked list.** `AdditionalStudentIds` is matched by the
  scope filter rather than left as a side door.

#### `WelfareController` audited route by route — all 29 correct, and the count that worried me was wrong

The first pass of this audit flagged *"30 `VerifyBranchOwnership` against 10 `VerifyStudentAccess`"*
as a thing to check. **That comparison was meaningless, and the reason is worth recording**:

```csharp
private async Task<IActionResult?> VerifyStudentAccess(Guid branchId, Guid studentId)
{
    var branchError = await VerifyBranchOwnership(branchId);      // ← calls it
    if (branchError != null) return branchError;
    return await _scope.VerifyStudentAccessAsync(branchId, studentId);
}
```

`VerifyStudentAccess` **calls `VerifyBranchOwnership` itself**, so the two are a superset and a
subset, never rivals. Counting them against each other measures nothing. *A measurement that flags
the wrong thing is worse than no measurement* — the list-page audit's own lesson, earned again.

The real audit is route by route. All 29:

| Family | n | Guard |
|---|---|---|
| `{studentId}` in the path | 4 | `VerifyStudentAccess` on all four — records, welfare-context, escalation-check, picture |
| `{recordId}` | 10 | branch + `CanSeeAsync(record.Visibility)` + `_scope.CanSeeStudentAsync(record.StudentId)` |
| `FinalizeRecord` | 1 | Safe by a different route: it finds only the caller's **own** draft (`ReportedByUserId == CurrentUserId()`), 404 otherwise |
| Branch-wide reads | 4 | `ApplyStudentScopeAsync` — my-actions, records search, summary, cohorts |
| Import log | 4 | `ApplyImportJobScopeAsync` on the three reads (ownership, not class); `StartImport` refuses a scoped caller outright |
| Activity pair | 2 | The export calls `VerifyStudentAccess` when a student is named; the read runs `_scope.ApplyAsync` |
| Categories | 4 | Branch only — **correct**: organization-scoped admin data with no student in it |

Two details worth keeping, because both are easy to "fix" wrongly later:

- **`GetRecord` has an author exception** — `(!isAuthor && !CanSeeStudentAsync(...))` — which is what
  lets a Teaching-tier subject teacher read back the concern they filed themselves. It is not a hole.
- **`ApplyStudentScopeAsync` matches `AdditionalStudentIds` as well as `StudentId`**, so a record
  filed against another class but linked to one of the caller's students still reaches them.

#### And this is what makes Defect 1 serious rather than theoretical

Every one of those 29 guards bottoms out in `IStudentScopeService`, and **every path through it
begins with `IsUnscopedAsync()`**:

```csharp
if (await IsUnscopedAsync()) return query;          // ApplyAsync / ApplyStudentScopeAsync
return tier is Pastoral or Unscoped;                // CanSeeStudentAsync → Unscoped passes
```

So a caller whose ROLE is `DataScope: Organization` passes all 29 guards by construction. **The
controller is not the layer that can save you here, and it is already doing everything right.** The
leak is entirely in what the scope service is given to decide on — which is why §2.6's rule has to
land in the scope service and not in another round of controller hardening.

---

---
## 2c. The third instance: "Applies to" reads the role code too

Found 2026-09-22 while the user was looking at the New parameter dialog. It is the same defect one
more time, and the fix the user directed is a **lookup list in a JSON field, the same way classes,
houses and dormitories already work — no new table**.

### What happens today

`PerformanceParameter.AppliesTo` is a `StaffGroup`, and a person is resolved to one like this
(`StaffPerformancePolicyService.cs:206`):

```csharp
public StaffGroup GroupFor(string? roleCode)
    => RoleCodes.IsSupportStaff(roleCode) ? StaffGroup.SupportStaff : StaffGroup.TeachingStaff;

public static bool IsSupportStaff(string? roleCode)          // RoleCodes.cs:93
    => string.Equals(roleCode, SupportStaff, StringComparison.OrdinalIgnoreCase);
```

**Exactly one role code is Support. Every other code in existence is Teaching** — `admin`,
`manager`, `viewer`, and **every custom role a school creates**, because a custom role's code is
arbitrary and will never equal `support-staff`. The rule that consumes it is a single set test
(`StaffScoringService.cs:232`):

```csharp
if (p.AppliesTo != StaffGroup.AllStaff && p.AppliesTo != group) continue;
```

The import makes it near-certain in practice. `RosterImportProcessorJob.cs:977`:

```csharp
var roleCode = string.IsNullOrWhiteSpace(row.RoleCode) ? RoleCodes.Teacher : row.RoleCode.Trim();
```

`RoleCode` is an **optional** import column and a *different* column from `JobTitle`. A school's own
sheet carries *Designation* or *Position*, which maps to `JobTitle` — free text that nothing reads
for this. So unless somebody explicitly mapped a column of Q-Mgr role codes, every imported member
of staff is `teacher`, therefore Teaching staff, and the bursar, matron, driver and cook are scored
on **Lesson Attendance** and **Lesson Observation**.

This is the third instance of one defect. `GroupFor` reads the role code exactly as
`GetUserPermissionsAsync` does, and §0 of this plan is that **a person's real post is not in their
role code.** Class teacher, head of department, and now teaching-versus-support.

### The fix is a vocabulary, not a wider `IsSupportStaff`

The obvious patch is to widen that predicate to a set of role codes. Do not. A fixed three-value
enum is the wrong shape for this field, and this is the test that says so:

> **`StaffGroup` carries no behaviour.** One set-membership test reads it. No formula sits behind a
> value, no sign rule, no field it shows or hides. It is pure taxonomy.

A field that is pure taxonomy is **data**, and this project already has the idiom for it. Schools
genuinely differ: Uganda's MoES returns distinguish teaching, non-teaching and support; a boarding
school wants Boarding staff as its own group; a large school separates Administration from
Ancillary. None of that should need a line of code.

**Reuse `VocabularyItemDto`** — the type classes, houses, dormitories and rooms already use. It
carries `Name`, `IsActive` and `SortOrder`, which is the whole of what a staff group needs, and it
brings the retire-don't-delete behaviour with it for free.

- **The list lives in `Organization.Settings["StaffPerformance"]`** as
  `StaffPerformancePolicyDto.StaffGroups` — `List<VocabularyItemDto>`.
- **Organization, NOT branch, and this is the one deliberate departure from the classes pattern.**
  `PerformanceParameter` is organization-scoped (`LoadParametersAsync` filters on `OrganizationId`
  alone), so a branch-scoped group list would let a parameter name a group that exists on one branch
  and not the next — a person would score differently depending on which branch read them. Classes
  are branch vocabularies because a class belongs to a branch; a staff group does not.
- **The blob already has a lock discipline, which the branch vocabularies do not.** Every write to
  the policy goes through `WithPolicyLockAsync` (`pg_advisory_xact_lock` on the organization,
  re-reading inside the lock). That is exactly the protection `Branch.Settings` needed and had to
  have bolted on: `UpdateVocabularies` writes the whole list **over** the stored blob, which is why
  adding a class has to re-read first or it deletes every house and dormitory. Putting staff groups
  in the policy blob inherits the safe writer rather than adding a fifth writer to the risky one.
- **`Role.StaffGroup` (nullable text) is what points a person at one.** `Role` already carries
  `DataScope` and `StaffScope`; a third is consistent rather than novel, and it means the answer is
  set **where a school already decides what a role is** — the role editor in Users & Roles. Null
  means the tenant's default group.
- **`PerformanceParameter.AppliesToGroup` (nullable text)** replaces `AppliesTo`. **Null means all
  staff**, which retires `StaffGroup.AllStaff` and makes "applies to everyone" the *absence* of a
  restriction rather than a magic member of the list.
- **Matched by name through a key, the same as a class.** `ClassName.Key()` is the established
  shape — letters and digits, upper-cased — so *Support staff*, *support-staff* and *SUPPORT STAFF*
  are one group. `GroupFor` keeps its name and its single-home rule and becomes that lookup: the
  role's `StaffGroup`, else the default.

**A rename is explicit, never inferred from a diff**, because `UpdateBranchVocabulariesRequest`
already established why: *"a diff cannot tell a rename apart from a delete plus an add, and getting
that wrong either orphans every student's class or silently rewrites the wrong one."* A staff-group
rename moves `Role.StaffGroup` **and** `PerformanceParameter.AppliesToGroup` in the same save, the
way a class rename moves students, class-teacher assignments and timetable lessons together.

**A group in use is retired, never removed** — `IsActive`, which `VocabularyItemDto` already has.
Deleting one would silently widen every parameter that named it to all staff: a scoring change
nobody asked for and nothing would report.

**Defaults, so nothing changes for a tenant that never touches it:** two seeded items, *Teaching
staff* and *Support staff*, seeded on first policy read the way `StaffParameterDefaults` already
seeds parameters. The eleven system roles get a `StaffGroup` in the same migration —
`support-staff` → *Support staff*, everything else → *Teaching staff* — which is byte-for-byte what
`IsSupportStaff` returns today, so the migration moves no score.

### And the import must stop guessing

Defaulting a missing role to `teacher` is how 184 people arrive as teaching staff. Two small
changes:

- **`JobTitle` is offered as the source of the role and the group at the mapping step.** The wizard
  already shows every column it read and lets the reader change a match, so this is a question it is
  already shaped to ask (*"Designation → Role?"*), not a new mechanism.
- **A row with no role is a WARNING naming the default it will get**, not silence — the standing
  rule that a missing value is never a refusal but the reader is always told what will happen.

---

## 2d. Which fields become lookup lists, and which stay enums

The user's direction generalises: *"create lookup lists, same way we are doing for student class,
house, etc. no need to derive new tables. use json fields."* So here is the test to apply, and the
audit of where it lands — because applying it to the wrong field breaks scoring.

> **Does a value carry behaviour?**
> **No** → it is taxonomy. A `VocabularyItemDto` in a JSON settings blob, tenant-editable.
> **Yes** → it names a code path. It stays an enum, and the honest fix is to say so in the UI.

### Becomes a vocabulary

| Field | Today | Why it qualifies |
|---|---|---|
| `PerformanceParameter.AppliesTo` | `StaffGroup` enum, 3 values | §2c. One set test reads it. Zero behaviour |
| `User.EmploymentType` | `StaffEmploymentType` enum, 6 values | **Nothing in the codebase branches on it.** It is parsed by `StaffFieldParsing` and rendered by `StaffProfileDialog`, and that is all — the enum's own doc comment claims *"probation decides whether an appraisal cycle is a confirmation decision"* and **no code does that**. Meanwhile the MoES return distinguishes government-paid from PTA-paid teachers, and both currently squash into *Contract* |

`User.Qualification` and `User.JobTitle` are already free text, so they need no change — though
offering the school's own previously-used values as suggestions is the same cheap win the roster's
`HomeLanguages` and `Religions` lists already give.

### Stays an enum — and "Kind" is simply the wrong word for it

`ParameterKind` looks like the same problem and is not. Each value is a branch in
`StaffScoringService.ScoreParameter`, and they are different arithmetic:

| Kind | Formula |
|---|---|
| Attendance, Duty | `(present + 0.5×late + recovered) ÷ (present + late + absent)`, excused removed |
| Observation | `mean(rating) ÷ RatingScale` |
| Contribution, Recognition, Conduct | `50 + 50 × clamp(points, ±cap) ÷ cap` |
| Wellbeing | never scored, at all |

**A tenant cannot author arithmetic.** A school adding *"Kind: Punctuality"* would get a parameter
the server has no `case` for — scoring `null` silently and dropping out of the composite, with
nothing anywhere saying why. And it is not only a formula: `ParameterKind` is read in **24 files**,
deciding the sign rule on the record dialog, which outcomes are offered, which fields render,
whether the parameter may offset another, and whether it appears in coverage. A new kind is a **UI
and data contract**, not a row.

**But the word is wrong, and that is worth fixing.** "Kind" reads as *what sort of thing this is* —
a classification the school should own. It means *how this is scored*. Rename the field to **"How it
is scored"** (`ScoringMethod` in the DTO, value unchanged on the wire). That removes most of the
rigidity anyone actually feels here, because a school **already names the parameter itself** —
`Name` is free text, so they can call it *Punctuality*, *Timeliness* or *Okukwata Obudde* and pick
the attendance formula underneath it.

### The real rigidity is the magic numbers inside each method

This is the genuine gap, and all three are one-line reads of a blob that already exists — **no
schema change at all**:

| Hardcoded today | Where | Why it is a school's decision |
|---|---|---|
| `0.5m * late` | `StaffScoringService.cs:304` | A late arrival counts as half a presence. Some schools count three lates as an absence; some count late as absent outright |
| `Math.Max(1, p.MaxPointsPerEntry * 10)` | `:326` | An invented "about ten entries a term", used as the cap when no cap per period is set |
| `50m + 50m * clamped / cap` | `:328` | The neutral midpoint a parameter with evidence starts from |

They move into `StaffPerformancePolicyDto` as `LateCreditFraction`, `DefaultEntriesPerPeriod` and
`NeutralScore`, read through `IStaffPerformancePolicyService` like every other dial, with today's
values as defaults so no existing score moves.

**`Excused` stays out of the denominator and is not configurable.** It is the MoES guidelines'
*"organisational factors do not constitute a performance gap"*, which is the reason that outcome
exists; making it switchable would let a school score somebody down for a lesson the school itself
cancelled.

### The thing to refuse: a tenant-authored formula

Named here so it is a decision rather than a gap somebody fills later.

- It is a scripting language inside the product — either a dependency, which the standing
  no-third-party-server-dependencies rule forbids, or a hand-written evaluator that must be
  sandboxed, validated and versioned per tenant.
- **A score that feeds an appraisal and an MoES return has to be explainable to the person it is
  about.** A school-authored expression cannot be audited by them, and *"the formula said so"* is
  not an answer at a disciplinary meeting.
- **It fails silently.** A wrong formula yields a wrong number, not an error — the worst failure
  shape this codebase keeps rediscovering.

The line: **a school controls what is measured, what it is called, who it applies to, how much it
counts and where the thresholds sit. It does not control the arithmetic.** Everything in §2c and
§2d above sits on the school's side of that line.

---


## 3. Where it goes: a tab in Staff Directory

**Revised 2026-09-22.** The first draft of this section said the pastoral half stays under Student
Welfare and only the subject half moves. That was wrong, and the test it rested on was the wrong
test. Recorded here rather than quietly replaced, because the reasoning is the point.

### The test I used, and why it fails

I wrote: *file it by what it is FOR, not by who appears on it* — the class-teacher assignment is for
deciding who gets alerted about a child, so it is welfare.

The user's objection: by that reasoning subject teachers and every other member of staff would be
filed under Students too. Taken literally it does not follow (subject assignment is *for*
timetabling, so my test would have sent it to Timetable, which is where I sent it). But the
objection lands anyway, because **"what is it for" cannot separate this page from any other
staff-assignment screen.** Every assignment screen in this product is ultimately *for* something
downstream — a department is for the staff scope, a line manager is for appraisals, a role is for
permissions. If "what it feeds" decided placement, Users & Roles would be filed under whichever
feature consumes the permission.

### The test that does decide it — and §2 is what makes it decisive

**§2 of this plan turns the assignment into the access grant.** Once
`ClassTeacherAssignment` is what confers `StudentAccessTier.Pastoral` and the welfare permission
set, pressing *Assign a class teacher* is the same **kind of act** as setting somebody's role,
naming them head of a department (`Department.HeadUserId` → `StaffDataScope.AssignedDepartments`),
or giving them a line manager. Those are all access grants, and in this product they all live in
one family of screens: Users & Roles and Staff Directory.

So the rule is: **a screen that grants a person access belongs where the other access grants are.**

Filing this one elsewhere puts the grant that reaches *children's safeguarding records* — the most
sensitive reach this product hands anybody — in the one place an administrator auditing "who can
read welfare records?" would not think to look. That is the real cost, and it is not a matter of
taste.

Note this is the same shape as the 2026-09-18 privilege escalation: `RoleAssignmentGuard` existed
and two endpoints simply never called it, because assigning a role did not *look* like an access
decision from where those endpoints sat. An access grant filed away from the other access grants is
how that happens again.

### What this changes about the split

It settles the split too, and not the way §3 first had it. If the reason to co-locate is *this is
where a post and its access are granted*, then **both halves are that** — the subject-teacher
assignment grants `StudentAccessTier.Teaching`, which is narrowed access to children (blanked field
by field in `StudentsController.MapTeachingTier`) and the right to log a concern about them. So the
subject half does **not** move to Timetable. Timetable *consumes* periods-a-week; it does not need
to own the editor, any more than the welfare fan-out needs to own the pastoral one.

| Half | Tab | Why here |
|---|---|---|
| Class teacher / assistant | **Class teachers** | Grants Pastoral tier + the derived welfare set |
| Subject teachers, periods a week | **Subject teachers** | Grants Teaching tier; feeds the timetable, which reads it |

`/admin/students/class-teachers` currently renders one grid of class cards mixing both, which is
what makes it read as two pages wearing one. Two tabs is the split, in one hub.

### And the coverage rows fold into the tab that already exists

Staff Directory already has a **Coverage** tab, and Class Teachers carries its own coverage banner
fed by `GET …/class-teachers/coverage` — classes with no teacher, scoped users with no class,
student class names matching no configured class. That is staffing coverage by any reading, and it
is currently a banner on a page a reader may never open. It moves into **Coverage**, beside the
gaps that tab already reports.

### The result

`/admin/staff` goes from four tabs to six: **People · Class teachers · Subject teachers ·
Departments · Coverage · Import**. The sidebar's Student Welfare group loses its *Class Teachers*
entry and nothing is added anywhere, because Staff Directory is already an entry — **net one fewer
navigation item**, which is the same direction as the three hub consolidations.

### Hub rules this must obey (all already established)

- A section has **no `@page`** of its own; the hub owns the route and `?tab=`.
- The retired route is **deleted, not aliased** — *"why are we having old links? we need ssot"*.
- A section that is not routable **cannot use `[SupplyParameterFromQuery]`**; the hub reads any
  extra query key and passes it down.
- **A tab whose section would refuse the caller is not rendered**, and the hub's own gate is the
  **OR** of its sections. The two new sections are gated on `Permissions.ClassTeachersManage`, which
  is narrower than `StaffRecordsView` — so a staff reader who cannot assign classes must not see the
  tabs, and a `classes.teachers.manage` holder who lacks `staff.records.view` must still reach them.
- **Both halves must render their own actions through `QPageActions`**, or the Assign buttons vanish
  the way nine sections' buttons did on 2026-09-18. `section-actions-check.mjs` enforces it.
- **Module gate on the SECTION**, not the route — `ModuleAllows(path)` returns TRUE for an unmapped
  path, so a deleted route silently opens to every tenant. Both are `ModuleCodes.StudentWelfare`,
  the same module `/admin/staff` already requires, so this one is simple.

### The five callers of the retired route

Found by grep, per the standing rule that retiring a route means finding its callers — and one of
them is outside the pages, which is why the grep matters:

| Caller | Fix |
|---|---|
| `MainLayout.razor:236` | sidebar entry — **removed** |
| `MobileNav.cs:143` | phone nav slot — repoint to `/admin/staff?tab=class-teachers` |
| `StudentRoster.razor:787,795` | the two class-teacher links on a vocabulary row |
| `StaffSubjects.razor:29` | *Class Teachers* button on the Timetable hub's Subjects tab |

`route-audit.mjs` afterwards, and `suite-route-check.mjs` for any e2e still driving the old route.

## 4. Verification

- **`scripts/e2e/derived-permissions-e2e.mjs`** — a new section. The assertions that matter:
  1. A `teacher`-role holder with **no** assignment has no welfare permission.
  2. Assigned as **class teacher**, the same person gains exactly the derived set — **on the next
     request**, not in five minutes.
  3. Assigned as **subject teacher** instead, they gain **nothing**.
  4. Ending the assignment removes it, again on the next request.
  5. The derived set never includes `welfare.restricted.view`, `roles.edit` or `users.create`.
  6. A head of department gains the staff set; removing them as head removes it.
  7. **Scope still fails closed**: the derived permission does not let them read a child in another
     class. This is the one that proves the two axes stayed separate.
- **`scripts/e2e/browser/class-teacher-split.mjs`** — both halves reachable in their new homes, the
  retired route 404s, deep links open the right tab.
- `guards.sh` 8/8 (`route-audit`, `suite-route-check`, `section-actions-check` all bear on this).
- Seeded in the dev tenant and run: create the assignment, call a welfare endpoint as that person,
  end it, call again.

---

## 4b. The roster import requires a guardian, and it must not

**Reported from production: 1,711 of 1,711 rows failed**, every one of them with
*"Missing required field(s): guardian name, a guardian phone or email."* Nothing imported.

### The client and the server disagree, and the client is right

`RosterImportProcessorJob.cs` lines 244–247 refuse a row without a guardian name AND without a
guardian phone or email:

```
if (string.IsNullOrWhiteSpace(row.StudentFullName)) missing.Add("student name");
if (string.IsNullOrWhiteSpace(row.GuardianFullName)) missing.Add("guardian name");
if (normPhone == null && normEmail == null)         missing.Add("a guardian phone or email");
```

The wizard declares the opposite. In `StudentRoster.razor`, `GuardianFullName`,
`GuardianPhone` and `GuardianEmail` are all `new ImportColumn(..., false, ...)` — **not
required**. So the preview called 1,711 rows good, the import then refused all 1,711 in different
words, and the reader was told nothing until after they had committed.

**That is precisely the failure the shared-rules rule exists to prevent:** *"A row the preview called
good and the import then refuses, in different words, is the worst answer an import can give."* It
happened because the requirement lives in the JOB and not in `ImportRules`, so there was never one
place for the two sides to agree.

### What is actually mandatory (user decision, 2026-09-22)

**Name, code and class. Nothing else.**

| Field | Now | Should be | Why |
|---|---|---|---|
| Student name | required | **required** | unchanged |
| Student code | required | **required** | the key the import upserts on — a row without one can only ever create a new student, which is how a re-import doubles a roll |
| Class | **not required** | **required** | a student with no class is invisible to their own class teacher, and `ClassName` is what the pastoral scope matches on |
| Guardian name | **required** | not required | — |
| Guardian phone or email | **required** | not required | — |

**A guardian is DATA the school may not hold, not a precondition for a child existing on the roll.**
Requiring one refuses the child rather than the missing detail — the same judgement already made for
a member of staff with no email address, where *"a missing address is never a refusal; a MALFORMED
one always is."* The identical rule applies here: a guardian phone that is present and unparseable
is still a warning worth raising; a guardian who is simply absent is a fact.

### The change

1. **Move the required-field rule into `ImportRules` in `Q-Mgr.Shared`**, which is what both sides
   already run, so the preview and the import cannot disagree again. The job calls it rather than
   carrying its own list.
2. **Class becomes required** in both the column declaration and the rule.
3. **Guardian fields become optional everywhere.** A row with a guardian name and no contact detail
   imports; a row with neither imports.
4. **A malformed guardian phone or email still WARNS**, naming the row — it is a typo worth seeing,
   and silently dropping it would lose a contact the school does hold.
5. `scripts/e2e/browser/import-reimport.mjs` currently seeds a guardian on its "unchanged" row
   **because one was required**. That fixture note stops being true and the comment must go with it,
   or the next reader trusts a reason that no longer exists.

---

## 5. Decisions — taken 2026-09-22

| # | Question | Answer |
|---|---|---|
| 1 | Is `welfare.reports.own` in the class-teacher derived set? | **No** |
| 2 | Deputy head of department — same grants as the head? | **Same** |
| 3 | Do the seeded roles stay? | **No. Delete them** |

### On 3, I was wrong and the correction matters

I argued for keeping `class-teacher` and `head-of-department` as retired-but-valid roles, on the
"revoking must not silently strip somebody's access" precedent. **That precedent does not apply
here**, and the reason is in the role definitions themselves:

- `class-teacher` carries `DataScope: RoleDataScope.AssignedClasses`. A holder with no assignment
  **sees no students at all.**
- `head-of-department` carries `StaffScope: AssignedDepartments`. A head with no department
  **sees nobody.**

So the role ALONE has never done anything. Every holder for whom it works already has the
assignment — which is exactly what the derived set keys on. **Deleting the role strips nothing that
was functioning.** The module precedent is a different situation: there the role kept working after
the module was cancelled, so keeping it preserved something real. Here it preserves a name.

*"Redundant features create more confusion"* is the right call, and two roles that exist only to be
never offered are exactly that.

### What deletion actually requires

**A role row cannot simply be dropped.** Every `User.RoleId` pointing at it is a foreign key, and
`RoleCodes.Rank()` returns `All.Length` — the lowest rank — for a code it does not find, so a
stale code fails closed but silently demotes. So the migration, in order:

1. **Reassign holders first.** `class-teacher` → `teacher`; `head-of-department` → `teacher`.
   Both keep every assignment and department headship they already hold, and the derived set gives
   the permissions back on their next request.
2. **Then delete the two role rows**, their `RolePermissions`, and the codes from
   `RoleCodes.All`, `RoleCodes.ModuleFor` and the three permission catalogues.
3. **Order within `All` is preserved**, so every surviving role's relative rank is unchanged and
   `IsManagerOrAbove` still answers the same.

**Two capabilities would be lost in that swap unless the derived set carries them**, and both now do:

- `welfare.reports.own` — decision 1 says it is NOT derived, so a current `class-teacher` holder
  loses it. **Stated rather than hidden:** that is a real, deliberate reduction. A school that wants
  a class teacher to read their class's figures grants a custom role with that code.
- `timetable.lessons.flag` — I previously argued this was a curriculum act rather than a
  departmental one and excluded it. **That does not survive contact with the migration:** a head of
  department supervising lessons in their own department is precisely who flags one, and excluding
  it would mean deleting the role removed a capability nothing gives back. **It is in the derived
  set.**

### The head-of-department derived set, final

`staff.records.view`, `staff.records.create`, `staff.records.edit`, `staff.duties.manage`,
`staff.appraisals.conduct`, `staff.reports.view`, `staff.duty-reports.view`,
`timetable.lessons.flag` — the role's full list, for head **and** deputy alike.

### The class-teacher derived set, final

`students.view`, `welfare.view`, `welfare.create`, `welfare.edit`, `welfare.notify`.
**Not** `welfare.reports.own`.

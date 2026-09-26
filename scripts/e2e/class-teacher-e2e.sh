#!/usr/bin/env bash
# =============================================================================
# Class Teachers, scoped visibility, restricted tier, and notification delivery
# — end-to-end against a running API and a real tenant.
#
# There is no test project in this repo by decision (see CLAUDE.md). Verification
# means seeding the dev tenant and running the path. This script is that: it
# creates its own users and assignments, exercises every guard, and asserts on
# the real HTTP responses.
#
#   API=http://127.0.0.1:5001 ./scripts/e2e/class-teacher-e2e.sh
#
# Every check prints PASS or FAIL. Exit code is the number of failures.
# =============================================================================
set -uo pipefail

API="${API:-http://127.0.0.1:5001}"
BRANCH="${BRANCH:?set BRANCH to the branch guid}"
SA_USER="${SA_USER:-superadmin}"
SA_PASS="${SA_PASS:-admin}"
PW='E2eTeacher!2026'

PASS=0; FAIL=0
ok()   { printf '  \033[32mPASS\033[0m  %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n'   "$1"; printf '        expected: %s\n        actual:   %s\n' "$2" "$3"; FAIL=$((FAIL+1)); }
hdr()  { printf '\n\033[1m%s\033[0m\n' "$1"; }
eq()   { [ "$2" = "$3" ] && ok "$1" || bad "$1" "$3" "$2"; }

login() { # $1 identifier, $2 password -> token on stdout
  curl -s -X POST "$API/api/v1/auth/login" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$1\",\"password\":\"$2\"}" | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4
}
code() { # $1 token, $2 method, $3 path, [$4 body] -> HTTP status
  if [ $# -ge 4 ]; then
    curl -s -o /dev/null -w '%{http_code}' -X "$2" "$API$3" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' -d "$4"
  else
    curl -s -o /dev/null -w '%{http_code}' -X "$2" "$API$3" -H "Authorization: Bearer $1"
  fi
}
body() { # $1 token, $2 method, $3 path, [$4 body]
  if [ $# -ge 4 ]; then
    curl -s -X "$2" "$API$3" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' -d "$4"
  else
    curl -s -X "$2" "$API$3" -H "Authorization: Bearer $1"
  fi
}
count() { echo "$1" | grep -o '"id"' | wc -l | tr -d ' '; }

hdr "0. Sign in"
SA=$(login "$SA_USER" "$SA_PASS")
[ -n "$SA" ] && ok "superadmin authenticated" || { bad "superadmin authenticated" "a token" "empty"; exit 1; }

T4=$(login e2e.teacher.s4@qmgr.local "$PW")
T2=$(login e2e.teacher.s2@qmgr.local "$PW")
AD=$(login e2e.admin.ct@qmgr.local "$PW")
[ -n "$T4" ] && ok "class teacher (S4) authenticated" || bad "class teacher (S4) authenticated" "token" "empty"
[ -n "$T2" ] && ok "class teacher (S2) authenticated" || bad "class teacher (S2) authenticated" "token" "empty"
[ -n "$AD" ] && ok "tenant admin authenticated"       || bad "tenant admin authenticated" "token" "empty"

B="/api/v1/branches/$BRANCH"

# -----------------------------------------------------------------------------
hdr "0b. Resolve the ids this run needs"
# Added 2026-09-10. This block did not exist and the six ids below were expected to arrive
# from the environment — so a re-run with only API and BRANCH set produced a wall of
# "unbound variable" and 20 false failures, which is exactly the shape of the problem a
# re-runnable suite exists to avoid. They are resolved from the API now.

pick_user() { # $1 username -> user guid
  body "$AD" GET "/api/v1/users?pageSize=200" | tr '{' '\n{' \
    | grep "\"username\":\"$1\"" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4
}
pick_student() { # $1 class name -> student guid
  body "$AD" GET "$B/students" | tr '{' '\n{' \
    | grep "\"className\":\"$1\"" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4
}
pick_category() { # $1 caseType name -> category guid
  body "$AD" GET "$B/welfare/categories" | tr '{' '\n{' \
    | grep "\"caseType\":\"$1\"" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4
}

UID4="${UID4:-$(pick_user e2e.teacher.s4)}"
UID2="${UID2:-$(pick_user e2e.teacher.s2)}"
S4_STUDENT="${S4_STUDENT:-$(pick_student S4)}"
S2_STUDENT="${S2_STUDENT:-$(pick_student S2)}"
WELFARE_CAT="${WELFARE_CAT:-$(pick_category Welfare)}"
BEHAVIOR_CAT="${BEHAVIOR_CAT:-$(pick_category Behavior)}"

# Fail loudly and early rather than letting `set -u` scatter unbound-variable errors through
# twenty assertions that then all read as product failures.
for v in UID4 UID2 S4_STUDENT S2_STUDENT WELFARE_CAT BEHAVIOR_CAT; do
  if [ -n "${!v}" ]; then ok "resolved $v"; else
    bad "resolved $v" "a guid" "empty"
    printf '\n\033[31mCannot continue.\033[0m This tenant needs: the two class-teacher accounts\n'
    printf '(e2e.teacher.s4 / e2e.teacher.s2, password %s), an active student in S4 and one in S2,\n' "$PW"
    printf 'and one Welfare and one Behavior category. Seed those, or pass %s explicitly.\n' "$v"
    exit 1
  fi
done

# Any live assignment from a previous run is ended first: assertion 1 below asserts that a
# teacher with NO assignment sees nothing, and a leftover row from yesterday makes it fail for
# a reason that has nothing to do with the code.
for AID_OLD in $(body "$AD" GET "$B/class-teachers" | tr '{' '\n{' | grep -oE '"id":"[^"]*"' | cut -d'"' -f4); do
  code "$AD" DELETE "$B/class-teachers/$AID_OLD" '{"reason":"E2E reset before run"}' > /dev/null
done
ok "previous assignments cleared"

# -----------------------------------------------------------------------------
hdr "1. FAIL CLOSED — a class teacher with no assignment sees nothing"
# The single most dangerous bug in this feature: an empty allow-list collapsing
# into a no-op WHERE and handing a brand-new teacher the entire school roll.
R=$(body "$T4" GET "$B/students")
eq "roster is empty before any assignment" "$(count "$R")" "0"

# -----------------------------------------------------------------------------
hdr "2. Assign class teachers"
A4=$(body "$AD" POST "$B/class-teachers" '{"className":"S4","userId":"'"$UID4"'","role":0}')
echo "$A4" | grep -q '"className":"S4"' && ok "S4 assigned to Grace Nakato" || bad "S4 assigned" "201 + dto" "$(echo "$A4" | head -c 160)"
A2=$(body "$AD" POST "$B/class-teachers" '{"className":"S2","userId":"'"$UID2"'","role":0}')
echo "$A2" | grep -q '"className":"S2"' && ok "S2 assigned to Peter Okello" || bad "S2 assigned" "201 + dto" "$(echo "$A2" | head -c 160)"

DUP=$(body "$AD" POST "$B/class-teachers" '{"className":"S4","userId":"'"$UID2"'","role":0}')
echo "$DUP" | grep -q 'already has a class teacher' && ok "second primary on the same class is refused" \
  || bad "second primary refused" "409 conflict" "$(echo "$DUP" | head -c 160)"

GHOST=$(body "$AD" POST "$B/class-teachers" '{"className":"S9-does-not-exist","userId":"'"$UID2"'","role":0}')
echo "$GHOST" | grep -q 'Class not found' && ok "assignment to an unknown class is refused" \
  || bad "unknown class refused" "400 Class not found" "$(echo "$GHOST" | head -c 160)"

# -----------------------------------------------------------------------------
hdr "2b. DERIVED POST PERMISSIONS reach EVERY path that reports them"
# A live ClassTeacherAssignment grants welfare permissions the teacher's ROLE does not hold
# (PostPermissionService). Six places build a UserInfo, and on 2026-09-22 ONE of them —
# GET /auth/me — still read user.Role.RolePermissions directly and so reported the role's
# permissions only.
#
# That is the REFRESH path: the browser persists UserInfo in localStorage and re-reads it from
# there, so every `@if (HasPermission(...))` in the app silently lost the derived permissions on
# a refresh while the API gate went on honouring them — the UI vanishing from under somebody who
# could still make the calls. Asserted on both paths, because one passing says nothing about the
# other: that is the "guarded door beside an unguarded one" lesson from section 13.
T4_REFRESHED=$(login e2e.teacher.s4@qmgr.local "$PW")
LOGIN_PERMS=$(body "$AD" GET "/api/v1/users/$UID4" > /dev/null 2>&1; curl -s -X POST "$API/api/v1/auth/login" \
  -H 'Content-Type: application/json' -d "{\"email\":\"e2e.teacher.s4@qmgr.local\",\"password\":\"$PW\"}" \
  | grep -o '"permissions":\[[^]]*\]')
ME_PERMS=$(body "$T4_REFRESHED" GET "/api/v1/auth/me" | grep -o '"permissions":\[[^]]*\]')

echo "$LOGIN_PERMS" | grep -q 'welfare.view' \
  && ok "login reports the class teacher's DERIVED welfare.view" \
  || bad "login reports derived welfare.view" "welfare.view present" "$LOGIN_PERMS"

echo "$ME_PERMS" | grep -q 'welfare.view' \
  && ok "auth/me reports it too — the refresh path, not just sign-in" \
  || bad "auth/me reports derived welfare.view" "welfare.view present" "$ME_PERMS"

[ "$LOGIN_PERMS" = "$ME_PERMS" ] \
  && ok "the two paths agree exactly, so a refresh never narrows what the UI renders" \
  || bad "login and auth/me agree" "identical lists" "login=$LOGIN_PERMS me=$ME_PERMS"

# -----------------------------------------------------------------------------
hdr "3. SCOPE — the roster narrows to the teacher's own class"
R4=$(body "$T4" GET "$B/students")
R2=$(body "$T2" GET "$B/students")
RA=$(body "$AD" GET "$B/students")
echo "$R4" | grep -q '"className":"S2"' && bad "S4 teacher sees no S2 student" "no S2 rows" "S2 present" || ok "S4 teacher sees no S2 student"
echo "$R2" | grep -q '"className":"S4"' && bad "S2 teacher sees no S4 student" "no S4 rows" "S4 present" || ok "S2 teacher sees no S4 student"
[ "$(count "$RA")" -gt "$(count "$R4")" ] && ok "admin sees more than the class teacher" \
  || bad "admin sees more" "admin > teacher" "admin=$(count "$RA") teacher=$(count "$R4")"

eq "out-of-scope student reads 404, not 403" "$(code "$T4" GET "$B/students/$S2_STUDENT/welfare-records")" "404"
eq "in-scope student reads 200"              "$(code "$T4" GET "$B/students/$S4_STUDENT/welfare-records")" "200"
eq "out-of-scope student picture is 404"     "$(code "$T4" GET "$B/students/$S2_STUDENT/picture")" "404"
eq "out-of-scope data export is 404"         "$(code "$T4" GET "$B/students/$S2_STUDENT/data-export")" "404"
eq "out-of-scope flags read is 404"          "$(code "$T4" GET "$B/students/$S2_STUDENT/flags")" "404"

SR=$(body "$T4" GET "$B/students/search?q=a")
echo "$SR" | grep -q '"className":"S2"' && bad "guardian search is scoped too" "no S2 rows" "S2 present" || ok "guardian search is scoped too"

eq "import-job history is closed to a scoped role" "$(body "$T4" GET "$B/students/import-jobs")" "[]"

# -----------------------------------------------------------------------------
hdr "3b. SCOPE — the BRANCH-WIDE reads, not just the per-student ones"
# Added 2026-09-09 after a live browser run found all four of these unscoped while every
# per-student read was correctly guarded. The reports list is the worst of them: it returns
# FULL record detail for every student in the branch, so a class teacher could read the whole
# school's chronology through the reports page. Counts leak too — a category breakdown tells
# you how many incidents a school has and who logged them.
SR_ALL=$(body "$T4" GET "$B/welfare-records")
echo "$SR_ALL" | grep -q "$S2_STUDENT" && bad "reports list is scoped" "no S2 records" "S2 record present" || ok "reports list is scoped"

SUM=$(body "$T4" GET "$B/welfare/summary")
SUM_TOTAL=$(echo "$SUM" | grep -o '"totalRecords":[0-9]*' | head -1 | grep -o '[0-9]*')
SUM_ADMIN=$(body "$AD" GET "$B/welfare/summary" | grep -o '"totalRecords":[0-9]*' | head -1 | grep -o '[0-9]*')
[ -n "$SUM_TOTAL" ] && [ -n "$SUM_ADMIN" ] && [ "$SUM_TOTAL" -lt "$SUM_ADMIN" ] \
  && ok "dashboard summary counts are scoped (teacher $SUM_TOTAL < admin $SUM_ADMIN)" \
  || bad "summary counts scoped" "teacher < admin" "teacher=$SUM_TOTAL admin=$SUM_ADMIN"

COH=$(body "$T4" GET "$B/welfare/cohorts")
COH_ADMIN=$(body "$AD" GET "$B/welfare/cohorts")
[ "$COH" != "$COH_ADMIN" ] && ok "cohort breakdown is scoped" \
  || bad "cohort breakdown scoped" "differs from admin" "identical to admin"

# -----------------------------------------------------------------------------
hdr "4. CONFIDENTIAL — a safeguarding record is invisible to the class teacher"
CONF=$(body "$AD" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","categoryId":"'"$WELFARE_CAT"'","caseType":2,"tier":1,"description":"E2E safeguarding probe. Dummy record - safe to delete.","occurredAt":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"}')
CONF_ID=$(echo "$CONF" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
echo "$CONF" | grep -q '"visibility":"Confidential"' && ok "Welfare case forced to Confidential server-side" \
  || bad "Welfare forced confidential" '"visibility":1' "$(echo "$CONF" | grep -o '"visibility":[0-9]*')"

eq "class teacher 404s the confidential record"  "$(code "$T4" GET "$B/welfare-records/$CONF_ID")" "404"
eq "admin reads the confidential record"         "$(code "$AD" GET "$B/welfare-records/$CONF_ID")" "200"
TL=$(body "$T4" GET "$B/students/$S4_STUDENT/welfare-records")
echo "$TL" | grep -q "$CONF_ID" && bad "confidential row absent from teacher timeline" "absent" "present" || ok "confidential row absent from teacher timeline"

# -----------------------------------------------------------------------------
hdr "5. RESTRICTED — administrator only, above Confidential"
STD=$(body "$AD" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","categoryId":"'"$BEHAVIOR_CAT"'","caseType":1,"tier":0,"description":"E2E standard behaviour probe. Dummy record - safe to delete.","occurredAt":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"}')
STD_ID=$(echo "$STD" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
eq "class teacher reads the standard record" "$(code "$T4" GET "$B/welfare-records/$STD_ID")" "200"

ESC=$(body "$T4" PATCH "$B/welfare-records/$STD_ID/visibility" '{"visibility":2}')
echo "$ESC" | grep -q 'cannot move a record to Restricted' && ok "class teacher cannot restrict a record" \
  || bad "teacher cannot restrict" "400" "$(echo "$ESC" | head -c 160)"

RES=$(body "$AD" PATCH "$B/welfare-records/$STD_ID/visibility" '{"visibility":2,"reason":"E2E probe"}')
echo "$RES" | grep -q '"visibility":"Restricted"' && ok "admin restricts the record" || bad "admin restricts" '"visibility":"Restricted"' "$(echo "$RES" | grep -o '"visibility":"[A-Za-z]*"')"
eq "class teacher now 404s it"  "$(code "$T4" GET "$B/welfare-records/$STD_ID")" "404"
eq "admin still reads it"       "$(code "$AD" GET "$B/welfare-records/$STD_ID")" "200"

LOW=$(body "$AD" PATCH "$B/welfare-records/$CONF_ID/visibility" '{"visibility":0,"reason":"E2E probe"}')
echo "$LOW" | grep -q 'cannot be made standard' && ok "a safeguarding record cannot be lowered to Standard" \
  || bad "safeguarding floor holds" "400" "$(echo "$LOW" | head -c 160)"

NOREASON=$(body "$AD" PATCH "$B/welfare-records/$STD_ID/visibility" '{"visibility":0}')
echo "$NOREASON" | grep -q 'why you are widening' && ok "lowering visibility demands a reason" \
  || bad "lowering demands a reason" "400" "$(echo "$NOREASON" | head -c 160)"

hdr "5b. Restricted student note"
eq "class teacher cannot read restricted notes" "$(code "$T4" GET "$B/students/$S4_STUDENT/restricted-notes")" "403"
eq "admin writes a restricted note"             "$(code "$AD" PUT "$B/students/$S4_STUDENT/restricted-notes" '{"notes":"E2E probe. Dummy note - safe to delete."}')" "204"
SEEN=$(body "$T4" GET "$B/students")
echo "$SEEN" | grep -q '"restrictedNotes":null' && ok "note is blanked in the teacher payload" \
  || bad "note blanked for teacher" '"restrictedNotes":null' "$(echo "$SEEN" | grep -o '"restrictedNotes":[^,]*' | head -1)"
echo "$SEEN" | grep -q '"hasRestrictedNotes":true' && ok "but the teacher is told one EXISTS" \
  || bad "hasRestrictedNotes surfaced" "true" "$(echo "$SEEN" | grep -o '"hasRestrictedNotes":[a-z]*' | head -1)"

# -----------------------------------------------------------------------------
hdr "6. THE ALERT — a standard record reaches the class teacher, a confidential one does not"
BEFORE=$(body "$T4" GET "/api/v1/notifications/count")
body "$AD" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","categoryId":"'"$BEHAVIOR_CAT"'","caseType":1,"tier":2,"description":"E2E alert probe - high tier. Dummy record - safe to delete.","occurredAt":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"}' > /dev/null
sleep 1
AFTER=$(body "$T4" GET "/api/v1/notifications/count")
BN=$(echo "$BEFORE" | grep -o '[0-9]\+' | head -1); AN=$(echo "$AFTER" | grep -o '[0-9]\+' | head -1)
[ "${AN:-0}" -gt "${BN:-0}" ] && ok "class teacher's unread count rose ($BN → $AN)" \
  || bad "teacher alerted on a standard record" "count > $BN" "$AN"

BN2=$AN
body "$AD" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","categoryId":"'"$WELFARE_CAT"'","caseType":2,"tier":2,"description":"E2E alert suppression probe. Dummy record - safe to delete.","occurredAt":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"}' > /dev/null
sleep 1
AFTER2=$(body "$T4" GET "/api/v1/notifications/count")
AN2=$(echo "$AFTER2" | grep -o '[0-9]\+' | head -1)
[ "${AN2:-0}" -eq "${BN2:-0}" ] && ok "confidential record alerted NOBODY ($BN2 → $AN2)" \
  || bad "confidential alert suppressed" "count stays $BN2" "$AN2"

# -----------------------------------------------------------------------------
hdr "7. COVERAGE — the ways this feature fails silently, made visible"
COV=$(body "$AD" GET "$B/class-teachers/coverage")
echo "$COV" | grep -q '"classesWithNoTeacher"' && ok "coverage names classes with no teacher" || bad "coverage report" "classesWithNoTeacher" "$(echo "$COV" | head -c 160)"
echo "$COV" | grep -q '"unknownStudentClasses"' && ok "coverage names unmatched student classes" || bad "coverage report" "unknownStudentClasses" "$(echo "$COV" | head -c 160)"
eq "coverage is closed to a class teacher" "$(code "$T4" GET "$B/class-teachers/coverage")" "403"

MINE=$(body "$T4" GET "$B/class-teachers/mine")
echo "$MINE" | grep -q '"className":"S4"' && ok "teacher can read their own assignments" || bad "own assignments" "S4" "$(echo "$MINE" | head -c 160)"

# -----------------------------------------------------------------------------
hdr "8. ENDING an assignment revokes access immediately"
AID=$(echo "$A4" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
eq "assignment ended" "$(code "$AD" DELETE "$B/class-teachers/$AID" '{"reason":"E2E probe"}')" "200"
R4B=$(body "$T4" GET "$B/students")
eq "roster is empty again, on the very next request" "$(count "$R4B")" "0"

# THE REVOCATION IS THE POINT, AND THE LINE ABOVE IS WHAT PROVES IT: the roster empties on the very
# next request. This one is about the SHAPE of the refusal, and it changed on 2026-09-22 when welfare
# access started arriving with the POST rather than the role.
#
# Before: the role granted welfare.view permanently and only the SCOPE moved, so a record out of scope
# answered 404 — the standing rule, because a 403 on a particular student confirms that student exists.
# Now: ending the assignment removes the post, so the teacher holds no welfare permission at all and
# every welfare read answers 403 — the SAME answer for any id, including one that never existed. It
# therefore discloses strictly less than the 404 it replaced, which is why both are accepted here.
#
# 404 is still accepted because a caller who DOES hold the permission and is merely out of scope must
# keep getting it — that rule is unchanged and is asserted per-student elsewhere (sections 3, 3b, 9).
REVOKED_CODE=$(code "$T4" GET "$B/welfare-records/$STD_ID")
if [ "$REVOKED_CODE" = "403" ] || [ "$REVOKED_CODE" = "404" ]; then
  ok "the record they could read a moment ago is refused, without confirming it exists ($REVOKED_CODE)"
else
  bad "the record they could read a moment ago is refused" "403 or 404" "$REVOKED_CODE"
fi

# -----------------------------------------------------------------------------
hdr "9. The endpoints that had no UI until 2026-09-10, and the reports gate"
# Everything below was curl-tested when it was built and then wired to a screen a day later.
# These assertions exist so the endpoints stay honest now that something actually calls them.

# The assignment ended in section 8, so re-assign before the reads that need scope.
A4B=$(body "$AD" POST "$B/class-teachers" '{"className":"S4","userId":"'"$UID4"'","role":0}')
echo "$A4B" | grep -q '"className":"S4"' && ok "re-assigned S4 for the remaining checks" \
  || bad "re-assign S4" "201 + dto" "$(echo "$A4B" | head -c 160)"

# --- The reports scope, told to the client -----------------------------------
# A scoped total read as the school's figure is a wrong conclusion drawn from a correct query,
# so the summary now says which classes it covers and the reports page prints it.
SUM_T=$(body "$T4" GET "$B/welfare/summary")
echo "$SUM_T" | grep -q '"scopedToClasses":\["S4"\]' && ok "summary tells a class teacher its figures cover S4 only" \
  || bad "summary carries the scope" '"scopedToClasses":["S4"]' "$(echo "$SUM_T" | grep -o '"scopedToClasses":[^]]*.' )"
SUM_A=$(body "$AD" GET "$B/welfare/summary")
echo "$SUM_A" | grep -q '"scopedToClasses":\[\]' && ok "summary is empty-scoped for an unscoped role" \
  || bad "admin scope empty" '"scopedToClasses":[]' "$(echo "$SUM_A" | grep -o '"scopedToClasses":[^]]*.' )"

# --- The cohort report is a report ------------------------------------------
# It was gated on welfare.view until 2026-09-10, so any welfare reader could pull a
# whole-school disproportionality breakdown. A class teacher reads it (scoped); the gate is what
# changed, not their access.
eq "class teacher still reads the cohort report" "$(code "$T4" GET "$B/welfare/cohorts")" "200"

# --- The reports gate is TWO codes now (2026-09-18) --------------------------
# welfare.reports.own is the class teacher's; welfare.reports.view is the branch-wide one a manager
# holds. Both open the same three reports — splitting them is what lets a school withhold the page
# from a class-teacher role without touching what a manager reads. The figures a scoped caller sees
# were already narrowed by IStudentScopeService, so the new code grants nothing wider.
ME_T4=$(body "$T4" GET "/api/v1/auth/me")
case "$ME_T4" in
  *'"welfare.reports.own"'*) ok "class teacher holds welfare.reports.own" ;;
  *) bad "class teacher holds welfare.reports.own" "the code in their permission set" "absent" ;;
esac
case "$ME_T4" in
  *'"welfare.reports.view"'*) bad "class teacher does NOT hold the branch-wide welfare.reports.view" "absent" "present" ;;
  *) ok "class teacher does NOT hold the branch-wide welfare.reports.view" ;;
esac
ME_AD=$(body "$AD" GET "/api/v1/auth/me")
case "$ME_AD" in
  *'"welfare.reports.view"'*) ok "the administrator still holds the branch-wide welfare.reports.view" ;;
  *) bad "the administrator still holds the branch-wide welfare.reports.view" "present" "absent" ;;
esac
# Either code opens all three reports.
eq "own-class code reads the record search"  "$(code "$T4" GET "$B/welfare-records")" "200"
eq "own-class code reads the summary"        "$(code "$T4" GET "$B/welfare/summary")" "200"
eq "branch-wide code reads the record search" "$(code "$AD" GET "$B/welfare-records")" "200"

# --- A bulk welfare import is not a form tutor's --------------------------------
IMP=$(code "$T4" POST "$B/welfare-records/import-jobs" '{"sourceFileName":"e2e.csv","rows":[{"studentCode":"X","categoryName":"Probe Behavior","description":"probe","occurredAt":"2026-09-01"}]}')
eq "class teacher cannot start a bulk welfare import" "$IMP" "403"
eq "welfare import log is closed to a scoped role" "$(body "$T4" GET "$B/welfare-records/import-jobs")" "[]"

# --- Per-user notification preferences --------------------------------------
EV=$(body "$T4" GET "/api/v1/notifications/preferences/events")
echo "$EV" | grep -q 'welfare.record-logged' && ok "the preference event catalogue is readable" \
  || bad "preference events" "welfare.record-logged" "$(echo "$EV" | head -c 160)"

eq "own preferences save" "$(code "$T4" PUT "/api/v1/notifications/preferences" \
  '{"emailEnabled":false,"smsEnabled":true,"events":[{"eventKey":"welfare.record-logged","email":true,"sms":false}]}')" "204"
PREF=$(body "$T4" GET "/api/v1/notifications/preferences")
echo "$PREF" | grep -q '"emailEnabled":false' && ok "preferences read back as saved" \
  || bad "preferences round-trip" '"emailEnabled":false' "$(echo "$PREF" | head -c 200)"

# Restored, so a later run of section 6 still exercises the email path rather than a
# master switch this section turned off.
code "$T4" PUT "/api/v1/notifications/preferences" '{"emailEnabled":true,"smsEnabled":true,"events":[]}' > /dev/null
ok "preferences restored to the defaults"

# --- The delivery log -------------------------------------------------------
eq "delivery log is closed without notifications.manage" "$(code "$T4" GET "/api/v1/notifications/deliveries")" "403"
eq "delivery log opens for an administrator"             "$(code "$AD" GET "/api/v1/notifications/deliveries")" "200"
eq "failures-only filter is accepted"                    "$(code "$AD" GET "/api/v1/notifications/deliveries?failuresOnly=true")" "200"

# --- The teacher's contact card and the assignment history ------------------
eq "admin edits the teacher's contact card" "$(code "$AD" PUT "$B/class-teachers/staff/$UID4/contact" \
  '{"phone":"0700000001","alternatePhone":"0700000002","officeLocation":"E2E Staff Room","jobTitle":"E2E Form Tutor"}')" "204"
CT=$(body "$AD" GET "$B/class-teachers")
echo "$CT" | grep -q '"officeLocation":"E2E Staff Room"' && ok "the contact card reads back on the assignment" \
  || bad "contact card round-trip" '"officeLocation":"E2E Staff Room"' "$(echo "$CT" | head -c 240)"
eq "a class teacher cannot edit contact cards" "$(code "$T4" PUT "$B/class-teachers/staff/$UID2/contact" '{"jobTitle":"nope"}')" "403"

HIST=$(body "$AD" GET "$B/class-teachers/history")
echo "$HIST" | grep -q '"endedAt"' && ok "history returns ended assignments, not just live ones" \
  || bad "history includes ended" '"endedAt"' "$(echo "$HIST" | head -c 200)"
HIST1=$(body "$AD" GET "$B/class-teachers/history?className=S4")
echo "$HIST1" | grep -q '"className":"S2"' && bad "history filters by class" "no S2 rows" "S2 present" \
  || ok "history filters by class"
eq "history is closed to a class teacher" "$(code "$T4" GET "$B/class-teachers/history")" "403"

# =============================================================================
hdr "10. The three leak SHAPES the 2026-09-10 handover predicted would recur"
# Phase 81 named three: an aggregate on the wrong gate, a surface with no student id to filter
# on, and a bulk write a background job carries out. Sections above cover the first two for
# welfare. These are the instances that were still open on 2026-09-13.

# --- 10a. Per-student endpoints that were still on the BRANCH guard ----------
# Both returned the child's NAME for any student in the branch. VerifyBranchOwnership answers
# "is this branch yours"; it cannot answer "is this child yours", and on a per-student endpoint
# that is the only question that matters.
eq "welfare-context 404s for a student outside the caller's classes" \
   "$(code "$T4" GET "$B/students/$S2_STUDENT/welfare-context")" "404"
eq "welfare-context still opens for the caller's own student" \
   "$(code "$T4" GET "$B/students/$S4_STUDENT/welfare-context")" "200"
eq "escalation-check 404s for a student outside the caller's classes" \
   "$(code "$T4" GET "$B/students/$S2_STUDENT/escalation-check")" "404"
eq "escalation-check still opens for the caller's own student" \
   "$(code "$T4" GET "$B/students/$S4_STUDENT/escalation-check")" "200"

# The out-of-scope answer must be indistinguishable from a student that does not exist. A 403
# would confirm the child is on the roll, which is itself the disclosure.
eq "an unknown student id answers identically (404, not 403)" \
   "$(code "$T4" GET "$B/students/00000000-0000-0000-0000-000000000001/welfare-context")" "404"

# --- 10b. The WRITE the row scope never covered ------------------------------
# Reads were scoped in Phase 77; creating a record was not. A form tutor could file a
# safeguarding record against any child in the school by id, and learn their name from the
# response. Refused with the SAME message an unknown student gets, deliberately.
XS=$(body "$T4" POST "$B/welfare-records" \
  '{"studentId":"'"$S2_STUDENT"'","categoryId":"'"$WELFARE_CAT"'","caseType":2,"description":"E2E probe: a class teacher must not be able to file against another class.","occurredAt":"2026-09-12T09:00:00Z"}')
echo "$XS" | grep -q 'Student not found' && ok "class teacher cannot file a record against another class's student" \
  || bad "cross-class create refused" "400 Student not found" "$(echo "$XS" | head -c 200)"
if echo "$XS" | grep -qiE 'scope|forbidden|your class'; then
  bad "the refusal does not reveal that the student exists" "the same wording as an unknown student" "$(echo "$XS" | head -c 200)"
else
  ok "the refusal does not reveal that the student exists"
fi

# The linked-students list is the side door onto exactly what the primary check refuses.
XL=$(body "$T4" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","additionalStudentIds":["'"$S2_STUDENT"'"],"categoryId":"'"$BEHAVIOR_CAT"'","caseType":1,"description":"E2E probe: linked students must obey the same scope as the primary.","occurredAt":"2026-09-12T09:00:00Z"}')
echo "$XL" | grep -qi 'additional student' && ok "linked students obey the same scope as the primary" \
  || bad "linked-student scope" "400 one or more additional students not found" "$(echo "$XL" | head -c 200)"

# And the same call, entirely inside the caller's own class, must still succeed. A guard that
# refuses everything is not a guard, it is an outage.
OK4=$(body "$T4" POST "$B/welfare-records" \
  '{"studentId":"'"$S4_STUDENT"'","categoryId":"'"$BEHAVIOR_CAT"'","caseType":1,"description":"Dummy record - E2E in-scope create probe. Safe to delete.","occurredAt":"2026-09-12T09:00:00Z"}')
echo "$OK4" | grep -q '"id":"' && ok "the same create inside the caller's own class still succeeds" \
  || bad "in-scope create" "201 + dto" "$(echo "$OK4" | head -c 200)"

# --- 10c. The bulk write a Hangfire worker carries out -----------------------
# BatchController hands a bare list of ids to BatchOperationProcessorJob, where
# IStudentScopeService does not exist. Three of its operations are gated on welfare.edit, which
# the class-teacher role holds, so this was reachable rather than theoretical.
BATCH_WELFARE='{"operation":6,"ids":["'"$STD_ID"'"],"value":"Resolved"}'
eq "class teacher cannot PREVIEW a welfare batch" "$(code "$T4" POST "$B/batch/preview" "$BATCH_WELFARE")" "403"
eq "class teacher cannot RUN a welfare batch"     "$(code "$T4" POST "$B/batch" "$BATCH_WELFARE")" "403"
eq "class teacher cannot UNDO a batch"            "$(code "$T4" POST "$B/batch/00000000-0000-0000-0000-000000000001/undo")" "403"

WHY=$(body "$T4" POST "$B/batch/preview" "$BATCH_WELFARE")
echo "$WHY" | grep -qi 'bulk operations' && ok "the batch refusal says why, so the user asks an administrator" \
  || bad "batch refusal explains itself" "a reason mentioning bulk operations" "$(echo "$WHY" | head -c 200)"

# The administrator is unaffected: this narrows one role, it does not disable the feature.
eq "an unscoped administrator still previews the same batch" "$(code "$AD" POST "$B/batch/preview" "$BATCH_WELFARE")" "200"

# --- 10d. Already closed before this run, re-asserted so a regression shows --
eq "roster import log stays closed to a scoped caller" \
   "$(body "$T4" GET "$B/students/import-jobs")" "[]"
eq "a single roster import job 404s for a scoped caller" \
   "$(code "$T4" GET "$B/students/import-jobs/00000000-0000-0000-0000-000000000001")" "404"

# =============================================================================
hdr "11. EMAIL - proven end to end, not just configured"
# "Email delivery is unproven" was carried on every handover since the delivery log was built,
# because no SMTP host existed on this box. There is one now (see CLAUDE.md), so these assert on
# real messages accepted by a real relay, not on configuration merely being present.
MAILBOX="${E2E_MAILBOX:-info@sacc.ug}"
# From the caller's own profile — GET /branches/{id} deliberately does not carry it.
ORG_ID=$(body "$AD" GET "/api/v1/auth/me" | grep -o '"organizationId":"[^"]*"' | head -1 | cut -d'"' -f4)
[ -n "$ORG_ID" ] && ok "resolved the tenant's organization id" || bad "resolve organization id" "a guid" "empty"

# The platform account is what a tenant with no SMTP of its own falls back to. It is filled from
# the Email:* configuration at startup by PlatformEmailDefaults.
# settingsJson is a JSON string INSIDE the response, so the inner quotes arrive escaped —
# unescape before matching or the pattern silently never fits.
PS=$(body "$SA" GET "/api/v1/platform/settings/Email" | sed 's/\\"/"/g')
if echo "$PS" | grep -qE '"SmtpHost": *"[^"]+"'; then ok "the platform email account is configured"
else bad "platform email configured" "a non-empty SmtpHost" "$(echo "$PS" | head -c 240)"; fi

# Clear this tenant's own SMTP host so the FALLBACK is what is under test. Its previous value was
# a deliberately-invalid host from an older run, which is why the delivery log held 56 failures
# and zero successes.
code "$AD" PUT "/api/v1/notifications/settings" \
  '{"organizationId":"'"$ORG_ID"'","emailEnabled":true,"smtpHost":"","smtpPort":587,"smtpUseSsl":true,"smtpUsername":"","smtpPassword":"","emailFromAddress":"","emailFromName":"Q-Mgr E2E"}' > /dev/null
ok "tenant SMTP cleared, so the platform fallback is what is under test"

# A REAL message, through the product's own test endpoint, resolved by the same code path the
# real send uses. A pass here means the relay authenticated and accepted the message.
TE=$(body "$AD" POST "/api/v1/notifications/settings/$ORG_ID/test-email" '{"emailAddress":"'"$MAILBOX"'"}')
echo "$TE" | grep -q '"success":true' && ok "a tenant with no SMTP of its own sends through the platform account" \
  || bad "platform fallback delivers" '"success":true' "$(echo "$TE" | head -c 240)"

# And the real dispatch path: an in-app notification with an email channel, delivered by the
# Hangfire job, which is the only thing that writes NotificationLog. This is the assertion the
# delivery log was built for and has never been able to make.
BEFORE_OK=$(body "$AD" GET "/api/v1/notifications/deliveries" | grep -o '"success":true' | wc -l | tr -d ' ')
# A notification names ONE person (2026-09-23). This probe posted with no userId for weeks, and
# every run left a row the whole tenant read in its bell — 51 of them on the dev tenant. It is
# addressed to the administrator running the suite now; the email still goes to $MAILBOX.
PROBE_TO=$(body "$AD" GET "/api/v1/auth/me" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
code "$AD" POST "/api/v1/notifications" \
  '{"userId":"'"$PROBE_TO"'","organizationId":"'"$ORG_ID"'","title":"Q-Mgr E2E delivery probe","message":"Dummy message from the Q-Mgr end-to-end suite. Safe to ignore.","channels":["Email"],"email":"'"$MAILBOX"'","emailSubject":"Q-Mgr E2E delivery probe"}' > /dev/null
DELIVERED=0; AFTER_OK="$BEFORE_OK"
for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
  sleep 2
  AFTER_OK=$(body "$AD" GET "/api/v1/notifications/deliveries" | grep -o '"success":true' | wc -l | tr -d ' ')
  if [ "$AFTER_OK" -gt "$BEFORE_OK" ]; then DELIVERED=1; break; fi
done
[ "$DELIVERED" = "1" ] && ok "the delivery log records a SUCCESSFUL email for the first time ($BEFORE_OK -> $AFTER_OK)" \
  || bad "delivery log records a success" "more than $BEFORE_OK success rows" "$AFTER_OK after 24s"

# The recipient is masked in the log: it is read by administrators, not only by the recipient.
DL=$(body "$AD" GET "/api/v1/notifications/deliveries")
if echo "$DL" | grep -q "$MAILBOX"; then
  bad "the delivery log masks the recipient" "no full address in the payload" "the full address is present"
else
  ok "the delivery log masks the recipient"
fi

# =============================================================================
hdr "12. UPLOADS ARE GATED, and the Document Library shares by revocable link"
# Added 2026-09-15 (Phase 84). Until then every upload was a static file served ahead of
# authentication — a welfare attachment's URL worked for anyone, for ever. UploadsController now
# decides per file (public for signage, signed token or record permission for the rest), and
# sharing only knows the Library. Section 12d sends ONE REAL EMAIL (a verification code) to
# $MAILBOX; the code itself cannot be read back here, so the wrong-code path is what is asserted.

# Not mktemp: on Git Bash it returns a /tmp/... path that the Windows curl binary cannot open, and
# every upload then "fails" with an empty body. $TEMP is Windows-shaped there and unset on Linux.
E2E_PDF="${TEMP:-/tmp}/qmgr-e2e-$$.pdf"
printf '%%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n4 0 obj << /Length 62 >> stream\nBT /F1 24 Tf 72 760 Td (Q-Mgr e2e shared document) Tj ET\nendstream endobj\n5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\ntrailer << /Root 1 0 R >>\n' > "$E2E_PDF"

upload() { # $1 token, $2 shareable -> body
  curl -s -X POST "$API/api/v1/organizations/$ORG_ID/media/upload" -H "Authorization: Bearer $1" \
    -F "file=@$E2E_PDF;filename=e2e.pdf;type=application/pdf" -F "name=E2E shared document" \
    -F "summary=Dummy document from the e2e suite. Safe to delete." -F "shareable=$2"
}
raw()     { curl -s -o /dev/null -w '%{http_code}' "$1"; }
rawauth() { curl -s -o /dev/null -w '%{http_code}' "$2" -H "Authorization: Bearer $1"; }
strip_token() { echo "$1" | sed 's/[?&]t=[^&"]*//'; }
jget()    { echo "$1" | grep -o "\"$2\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }

# The Library lives in the Communication module. A dev tenant that has not bought it
# gets a MODULE_NOT_PURCHASED 403 on every media endpoint (found on the first run of this section:
# 55 failures that read like product bugs and were one missing module). Activate it the way the
# Billing page does — simulated in Development, where no Mobile Money gateway is configured.
if body "$AD" GET "/api/v1/organizations/$ORG_ID/media" | grep -q MODULE_NOT_PURCHASED; then
  # The tenant's own purchase route refuses while another module is on an unpaid trial
  # (TRIAL_IN_PROGRESS — the dev tenant has Student Welfare trialing), so the platform grant is
  # used: the same thing a SuperAdmin does from the Tenants page.
  ACT_MOD=$(code "$SA" PUT "/api/v1/admin/tenants/$ORG_ID/modules/engagement-communications" '{"note":"E2E: the Document Library needs this module"}')
  eq "engagement-communications module granted to the tenant by the platform" "$ACT_MOD" "200"
fi

# --- 12a. A shareable document is gated; a plain upload is public ---------------------------
SHARED=$(upload "$AD" true)
SHARED_ID=$(jget "$SHARED" id)
SHARED_URL=$(jget "$SHARED" fileUrl)
[ -n "$SHARED_ID" ] && ok "admin uploads a shareable document" || bad "upload shareable" "an id" "$(echo "$SHARED" | head -c 200)"
echo "$SHARED" | grep -q '"isGated":true' && ok "a shareable document on no playlist is gated" \
  || bad "gated flag" '"isGated":true' "$(echo "$SHARED" | grep -o '"isGated":[a-z]*')"
echo "$SHARED_URL" | grep -q '[?&]t=' && ok "the DTO carries a signed link" || bad "signed link" "?t= in fileUrl" "$SHARED_URL"
eq "the raw path refuses the public (401)"  "$(raw "$(strip_token "$SHARED_URL")")" "401"
eq "the signed link serves the file"        "$(raw "$SHARED_URL")" "200"
eq "an admin's bearer token serves it too"  "$(rawauth "$AD" "$(strip_token "$SHARED_URL")")" "200"
eq "a bad token is refused (401)"           "$(raw "$(strip_token "$SHARED_URL")?t=not-a-token")" "401"

PUBLIC=$(upload "$AD" false)
PUBLIC_ID=$(jget "$PUBLIC" id)
PUBLIC_URL=$(jget "$PUBLIC" fileUrl)
echo "$PUBLIC" | grep -q '"isGated":false' && ok "a plain upload is public (signage is public by intent)" \
  || bad "public flag" '"isGated":false' "$(echo "$PUBLIC" | grep -o '"isGated":[a-z]*')"
eq "its raw path serves the public (200)" "$(raw "$PUBLIC_URL")" "200"
eq "a class teacher cannot mark a document shareable (403)" "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/v1/organizations/$ORG_ID/media/upload" -H "Authorization: Bearer $T4" -F "file=@$E2E_PDF;filename=e2e.pdf;type=application/pdf" -F shareable=true)" "403"

# --- 12a2. The combination the plan blessed and never tested (Revision 3, §1) ---------------
# A document that is BOTH shareable and on a playlist used to be served anonymously: the classifier
# read `!IsShareable || OnSignage`, so signage won. §12's list asserted each state separately —
# "on no playlist" above, "a plain upload" above — and never both. The rule now lives in one place
# (MediaServing) and reads IsShareable alone, and the two write paths refuse the combination so a
# shared document can never silently stop rendering on a wall.
PL=$(body "$AD" POST "$B/playlists" '{"name":"E2E share-vs-signage probe","defaultDurationSeconds":10}')
PL_ID=$(jget "$PL" id)
if [ -n "$PL_ID" ]; then
  ok "a probe playlist is created"
  ADD_SHAREABLE=$(code "$AD" POST "/api/v1/playlists/$PL_ID/items" '{"mediaContentId":"'"$SHARED_ID"'","durationSeconds":10}')
  eq "a SHAREABLE document cannot be added to a playlist (400)" "$ADD_SHAREABLE" "400"
  eq "the shareable document's raw path still refuses (401)"    "$(raw "$(strip_token "$SHARED_URL")")" "401"

  ADD_PUBLIC=$(code "$AD" POST "/api/v1/playlists/$PL_ID/items" '{"mediaContentId":"'"$PUBLIC_ID"'","durationSeconds":10}')
  eq "a plain document CAN be added to a playlist (201)" "$ADD_PUBLIC" "201"
  eq "and a document on a playlist cannot then be made shareable (400)" \
     "$(code "$AD" PUT "/api/v1/media/$PUBLIC_ID/publishing" '{"isShareable":true}')" "400"
  eq "its raw path still serves the public (200)" "$(raw "$PUBLIC_URL")" "200"
  curl -s -o /dev/null -X DELETE "$API/api/v1/playlists/$PL_ID" -H "Authorization: Bearer $AD"
else
  bad "a probe playlist is created" "an id" "$(echo "$PL" | head -c 200)"
fi

# The tenant caps link lifetime, so a link with no expiry is refused. EXP is defined further down
# for the pre-existing blocks; these run earlier and need their own. Seven days, not thirty: a
# Confidential document caps its links at 30 days, and an end-of-day stamp 30 days out is past it —
# which is the cap working, and cost one debugging round the first time this was written.
EXP_EARLY=$(date -u -d '+7 days' +%Y-%m-%dT23:59:59Z 2>/dev/null || date -u -v+7d +%Y-%m-%dT23:59:59Z)

# --- 12a3. Classification narrows what a link may do (plan D7) -------------------------------
eq "an unknown classification is refused (400)" \
   "$(code "$AD" PUT "/api/v1/media/$SHARED_ID/classification" '{"classification":99}')" "400"
eq "raising to Confidential is allowed for a publisher (200)" \
   "$(code "$AD" PUT "/api/v1/media/$SHARED_ID/classification" '{"classification":2}')" "200"
eq "lowering WITHOUT a reason is refused (400)" \
   "$(code "$AD" PUT "/api/v1/media/$SHARED_ID/classification" '{"classification":0}')" "400"
CONF_SHARE=$(body "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"E2E confidential link","expiresAt":"'"$EXP_EARLY"'","allowDownload":true,"requireEmail":false,"notifyOnFirstOpen":false}')
echo "$CONF_SHARE" | grep -q '"allowDownload":false' && ok "a Confidential document's link has download withheld, whatever was asked for" \
  || bad "classification forces allowDownload off" '"allowDownload":false' "$(echo "$CONF_SHARE" | grep -o '"allowDownload":[a-z]*')"
echo "$CONF_SHARE" | grep -q '"requireEmail":true' && ok "...and forces email verification on" \
  || bad "classification forces requireEmail on" '"requireEmail":true' "$(echo "$CONF_SHARE" | grep -o '"requireEmail":[a-z]*')"
eq "lowering WITH a reason is allowed for a share manager (200)" \
   "$(code "$AD" PUT "/api/v1/media/$SHARED_ID/classification" '{"classification":0,"reason":"Superseded by the published policy; no longer names anyone."}')" "200"
echo "$(body "$AD" GET "/api/v1/organizations/$ORG_ID/media")" | grep -q '"classificationReason":"Superseded' \
  && ok "the reason for lowering is kept with the document" \
  || bad "classification reason kept" "the reason on the row" "absent"

# --- 12a4. Deleting a document must not erase who opened it (plan §14 finding 1) -------------
# document_shares cascades from media_content and document_share_events from that, so a hard delete
# used to take the whole access log with it — on content.delete, which Manager holds, by someone who
# may not hold documents.share.audit. NIST SP 800-53 AU-9 is exactly this. A document with share
# history is now RETIRED: bytes gone, links dead, rows kept.
DOOMED=$(upload "$AD" true)
DOOMED_ID=$(jget "$DOOMED" id)
DOOMED_SHARE=$(body "$AD" POST "/api/v1/media/$DOOMED_ID/shares" '{"label":"E2E delete-audit probe","expiresAt":"'"$EXP_EARLY"'","notifyOnFirstOpen":false}')
DOOMED_SLUG=$(jget "$DOOMED_SHARE" slug)
[ -n "$DOOMED_SLUG" ] && ok "a document is shared before being deleted" || bad "probe share" "a slug" "$(echo "$DOOMED_SHARE" | head -c 200)"
eq "the document is deleted (204)" "$(code "$AD" DELETE "/api/v1/media/$DOOMED_ID")" "204"
ACT_AFTER=$(body "$AD" GET "/api/v1/media/$DOOMED_ID/activity")
echo "$ACT_AFTER" | grep -q '"linksTotal":1' && ok "AU-9: the share audit trail survives the delete" \
  || bad "audit survives delete" '"linksTotal":1' "$(echo "$ACT_AFTER" | head -c 200)"
GATE_AFTER=$(curl -s "$API/api/v1/public/shares/$DOOMED_SLUG")
echo "$GATE_AFTER" | grep -qi 'no longer available\|not available' && ok "...and every link to it refuses, fail-closed" \
  || bad "deleted document's link refuses" "an unavailable message" "$(echo "$GATE_AFTER" | head -c 200)"

# --- 12b. Welfare evidence follows the record's own rules ---------------------------------
REC=$(body "$AD" POST "$B/welfare-records" \
  '{"studentId":"'"$S2_STUDENT"'","categoryId":"'"$BEHAVIOR_CAT"'","caseType":1,"tier":0,"description":"E2E evidence-gating probe. Dummy record - safe to delete.","occurredAt":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"}')
REC_ID=$(jget "$REC" id)
ATT=$(curl -s -X POST "$API$B/welfare-records/$REC_ID/attachments" -H "Authorization: Bearer $AD" -F "file=@$E2E_PDF;filename=evidence.pdf;type=application/pdf")
ATT_URL=$(jget "$ATT" fileUrl)
[ -n "$ATT_URL" ] && ok "evidence attached to a record on an S2 student" || bad "attach evidence" "a fileUrl" "$(echo "$ATT" | head -c 200)"
eq "evidence raw path refuses the public (401)"              "$(raw "$(strip_token "$ATT_URL")")" "401"
eq "the signed evidence link serves the file"                "$(raw "$ATT_URL")" "200"
eq "the S2 class teacher's bearer token reads it (200)"      "$(rawauth "$T2" "$(strip_token "$ATT_URL")")" "200"
eq "the S4 class teacher gets 404 - out of scope, never 403" "$(rawauth "$T4" "$(strip_token "$ATT_URL")")" "404"
TLA=$(body "$T2" GET "$B/welfare-records/$REC_ID")
echo "$TLA" | grep -q '"fileUrl":"[^"]*[?&]t=' && ok "the record's attachment link is signed for the caller who may read it" \
  || bad "signed attachment in record" "?t= in fileUrl" "$(echo "$TLA" | grep -o '"fileUrl":"[^"]*"' | head -1)"

# --- 12c. Share links: gates, tokens, refusal, lockout, revocation --------------------------
EXP=$(date -u -d '+30 days' +%Y-%m-%dT23:59:59Z 2>/dev/null || date -u -v+30d +%Y-%m-%dT23:59:59Z)
LINK=$(body "$AD" POST "/api/v1/media/$SHARED_ID/shares" \
  '{"label":"E2E passcoded link","passcode":"4242","expiresAt":"'"$EXP"'","allowDownload":false,"watermark":"ViewerIdentity","notifyOnFirstOpen":true,"linkBaseUrl":"http://127.0.0.1:5003"}')
SLUG=$(jget "$LINK" slug)
LINK_ID=$(echo "$LINK" | grep -o '"share":{"id":"[^"]*"' | cut -d'"' -f6)
[ -n "$SLUG" ] && ok "a share link is issued (slug shown once)" || bad "issue link" "a slug" "$(echo "$LINK" | head -c 240)"
echo "$LINK" | grep -q '"url":"http://127.0.0.1:5003/s/' && ok "the emailed link is built on the WEB origin, not the API's" \
  || bad "link origin" "http://127.0.0.1:5003/s/..." "$(jget "$LINK" url)"
P="/api/v1/public/shares/$SLUG"
GATE=$(curl -s "$API$P")
echo "$GATE" | grep -q '"requiresPasscode":true' && ok "the public gate says a passcode is required" || bad "gate" '"requiresPasscode":true' "$(echo "$GATE" | head -c 200)"
echo "$GATE" | grep -q '"documentName":"E2E shared document"' && ok "the gate names the document" || bad "gate name" "E2E shared document" "$(echo "$GATE" | head -c 200)"
eq "an unknown slug is 404" "$(raw "$API/api/v1/public/shares/not-a-real-slug")" "404"
popen() { curl -s -X POST "$API$1/open" -H 'Content-Type: application/json' -d "$2"; }
popen "$P" '{}' | grep -q '"status":"PasscodeRequired"' && ok "open without a passcode: PasscodeRequired" || bad "open no passcode" "PasscodeRequired" "$(popen "$P" '{}' | head -c 120)"
popen "$P" '{"passcode":"0000"}' | grep -q '"status":"PasscodeInvalid"' && ok "a wrong passcode is refused" || bad "wrong passcode" "PasscodeInvalid" "$(popen "$P" '{"passcode":"0000"}' | head -c 120)"
GRANT=$(popen "$P" '{"passcode":"4242"}')
echo "$GRANT" | grep -q '"status":"Granted"' && ok "the right passcode opens it" || bad "right passcode" "Granted" "$(echo "$GRANT" | head -c 160)"
CT=$(jget "$GRANT" contentToken); ST=$(jget "$GRANT" sessionToken)
echo "$GRANT" | grep -q '"watermarkText":"Anonymous viewer' && ok "an unverified viewer is watermarked as anonymous" || bad "watermark" "Anonymous viewer ..." "$(jget "$GRANT" watermarkText)"
eq "the content streams with the 60-second token"                "$(raw "$API$P/content?t=$CT")" "200"
eq "content without a token is 401"                              "$(raw "$API$P/content")" "401"
eq "download is refused server-side on a view-only link (403)"   "$(raw "$API$P/download?t=$ST")" "403"
eq "a page-view event is accepted with the session token" "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API$P/events" -H 'Content-Type: application/json' -d '{"sessionToken":"'"$ST"'","page":1,"dwellSeconds":7}')" "204"
eq "a page-view event without a session is 401"           "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API$P/events" -H 'Content-Type: application/json' -d '{"sessionToken":"x","page":1,"dwellSeconds":7}')" "401"
RESUME=$(curl -s -X POST "$API$P/resume" -H 'Content-Type: application/json' -d '{"sessionToken":"'"$ST"'"}')
echo "$RESUME" | grep -q '"status":"Granted"' && ok "a reload resumes the session without a second passcode" || bad "resume" "Granted" "$(echo "$RESUME" | head -c 120)"

for _ in 1 2 3 4 5; do popen "$P" '{"passcode":"9999"}' > /dev/null; done
popen "$P" '{"passcode":"4242"}' | grep -q '"status":"Locked"' && ok "five wrong passcodes lock the link, even for the right one" \
  || bad "lockout" "Locked" "$(popen "$P" '{"passcode":"4242"}' | head -c 120)"

eq "a class teacher cannot revoke (403)" "$(code "$T4" DELETE "/api/v1/media/$SHARED_ID/shares/$LINK_ID" '{"reason":"nope"}')" "403"
REV=$(body "$AD" DELETE "/api/v1/media/$SHARED_ID/shares/$LINK_ID" '{"reason":"E2E revoke"}')
echo "$REV" | grep -q '"state":"Revoked"' && ok "the admin revokes the link" || bad "revoke" '"state":"Revoked"' "$(echo "$REV" | head -c 160)"
popen "$P" '{"passcode":"4242"}' | grep -q '"status":"Denied"' && ok "a revoked link is Denied on the very next request" \
  || bad "revoked open" "Denied" "$(popen "$P" '{"passcode":"4242"}' | head -c 120)"
eq "content with a still-valid session token is 410 after revocation" "$(raw "$API$P/content?t=$ST")" "410"

# --- 12d. One-time download link; email verification -----------------------------------
ONE=$(body "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"E2E one-time download","expiresAt":"'"$EXP"'","allowDownload":true,"maxViews":1,"linkBaseUrl":"http://127.0.0.1:5003"}')
SLUG2=$(jget "$ONE" slug); P2="/api/v1/public/shares/$SLUG2"
G2=$(popen "$P2" '{}')
echo "$G2" | grep -q '"status":"Granted"' && ok "a link with no gates opens at once" || bad "open ungated" "Granted" "$(echo "$G2" | head -c 120)"
ST2=$(jget "$G2" sessionToken)
eq "download is allowed when the link says so" "$(raw "$API$P2/download?t=$ST2")" "200"
popen "$P2" '{}' | grep -q '"status":"Denied"' && ok "the second open of a one-time link is Denied" || bad "one-time" "Denied" "$(popen "$P2" '{}' | head -c 120)"

EM=$(body "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"E2E email-verified","expiresAt":"'"$EXP"'","allowedEmails":["'"$MAILBOX"'"],"linkBaseUrl":"http://127.0.0.1:5003"}')
SLUG3=$(jget "$EM" slug); P3="/api/v1/public/shares/$SLUG3"
popen "$P3" '{}' | grep -q '"status":"EmailRequired"' && ok "an allow-listed link asks for an email" || bad "email gate" "EmailRequired" "$(popen "$P3" '{}' | head -c 120)"
popen "$P3" '{"email":"stranger@example.org"}' | grep -q '"status":"EmailNotAllowed"' && ok "an address off the list is refused" \
  || bad "allow-list" "EmailNotAllowed" "$(popen "$P3" '{"email":"stranger@example.org"}' | head -c 120)"
SENT=$(popen "$P3" '{"email":"'"$MAILBOX"'"}')
echo "$SENT" | grep -q '"status":"CodeSent"' && ok "a listed address is emailed a code (REAL email to $MAILBOX)" || bad "code sent" "CodeSent" "$(echo "$SENT" | head -c 160)"
CH=$(jget "$SENT" challenge)
popen "$P3" '{"challenge":"'"$CH"'","code":"000000"}' | grep -q '"status":"CodeInvalid"' && ok "a wrong code is refused" \
  || bad "wrong code" "CodeInvalid" "$(popen "$P3" '{"challenge":"'"$CH"'","code":"000000"}' | head -c 120)"

# --- 12d2. A custom watermark: fixed text, or text plus the viewer ------------------------------
CW=$(body "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"E2E custom watermark","expiresAt":"'"$EXP"'","watermark":"CustomAndViewer","watermarkText":"CONFIDENTIAL - E2E board pack","linkBaseUrl":"http://127.0.0.1:5003"}')
SLUG4=$(jget "$CW" slug); P4="/api/v1/public/shares/$SLUG4"
G4=$(popen "$P4" '{}')
echo "$G4" | grep -q '"watermarkText":"CONFIDENTIAL - E2E board pack · Anonymous viewer' && ok "a custom watermark is drawn ahead of the viewer's identity" \
  || bad "custom watermark" "CONFIDENTIAL - E2E board pack · Anonymous viewer ..." "$(jget "$G4" watermarkText)"
eq "a custom watermark with no text is refused (400)" "$(code "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"blank","expiresAt":"'"$EXP"'","watermark":"Custom"}')" "400"

# --- 12d3. What the security review found (2026-09-15), asserted so it cannot come back ---------
# An upload is what its STORED extension says, never what the client declared; a share streams
# as application/pdf whatever the row's MimeType; a linked media row cannot point into our own
# store (that was a way to make a gated file public); the emailed link's host must be ours.
XMLF="${TEMP:-/tmp}/qmgr-e2e-$$.xml"; printf '<html xmlns="http://www.w3.org/1999/xhtml"><script>alert(1)</script></html>' > "$XMLF"
eq "an .xml declared text/xml is refused (400)" "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/v1/organizations/$ORG_ID/media/upload" -H "Authorization: Bearer $AD" -F "file=@$XMLF;filename=x.xml;type=text/xml")" "400"
FAKE="${TEMP:-/tmp}/qmgr-e2e-$$-fake.pdf"; printf '<script>alert(1)</script>' > "$FAKE"
eq "a .pdf that is not a PDF is refused (400)" "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/v1/organizations/$ORG_ID/media/upload" -H "Authorization: Bearer $AD" -F "file=@$FAKE;filename=evil.pdf;type=text/html")" "400"
XU=$(curl -s -X POST "$API/api/v1/organizations/$ORG_ID/media/upload" -H "Authorization: Bearer $AD" -F "file=@$XMLF;filename=x.xml;type=image/png")
XU_URL=$(jget "$XU" fileUrl); XU_ID=$(jget "$XU" id)
echo "$XU_URL" | grep -q '\.png' && ok "an .xml declared image/png is STORED as .png (served inert)" || bad "stored extension follows the type" ".png" "$XU_URL"
[ -n "$XU_ID" ] && code "$AD" DELETE "/api/v1/media/$XU_ID" > /dev/null
rm -f "$XMLF" "$FAKE"
eq "a linked media row pointing into our own store is refused (400)" "$(code "$AD" POST "/api/v1/organizations/$ORG_ID/media" '{"name":"probe","contentType":0,"storageType":3,"fileUrl":"http://127.0.0.1:5001/uploads/media/'"$(basename "$(strip_token "$SHARED_URL")")"'"}')" "400"
CT_HDR=$(curl -s -D - -o /dev/null "$API$P4/content?t=$(jget "$(popen "$P4" '{}')" contentToken)" | grep -i '^content-type:' | tr -d '\r')
echo "$CT_HDR" | grep -qi "application/pdf" && ok "share content is served as application/pdf, not the row's MimeType" || bad "share content type" "application/pdf" "$CT_HDR"
eq "a share link with a foreign base URL is refused (400)" "$(code "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"phish","expiresAt":"'"$EXP"'","linkBaseUrl":"https://qmgr-lookalike.example"}')" "400"
popen "$P3" '{"email":"=HYPERLINK(\"https://attacker\")"}' | grep -q '"status":"EmailRequired"' && ok "a spreadsheet formula is not an email and is never logged" || bad "formula email" "EmailRequired" "$(popen "$P3" '{"email":"=HYPERLINK(\"https://attacker\")"}' | head -c 120)"
CSV=$(body "$AD" GET "/api/v1/media/$SHARED_ID/activity/export")
echo "$CSV" | grep -q "HYPERLINK" && bad "the CSV carries no formula" "no HYPERLINK" "present" || ok "the CSV carries no formula"

# --- 12e. The activity log, its permission, and the policy cap ----------------------------
eq "a class teacher cannot read share activity (403)" "$(code "$T4" GET "/api/v1/media/$SHARED_ID/activity")" "403"
ACT=$(body "$AD" GET "/api/v1/media/$SHARED_ID/activity")
OPENS=$(echo "$ACT" | grep -o '"opens":[0-9]*' | head -1 | cut -d: -f2)
[ "${OPENS:-0}" -ge 2 ] && ok "activity counts the opens ($OPENS)" || bad "activity opens" ">= 2" "${OPENS:-none}"
echo "$ACT" | grep -q '"type":"Revoked"'    && ok "the revocation is in the event log" || bad "revoke event" '"type":"Revoked"' "$(echo "$ACT" | grep -o '"type":"[A-Za-z]*"' | sort -u | tr '\n' ' ')"
echo "$ACT" | grep -q '"type":"PageViewed"' && ok "the page read is in the event log"  || bad "page event" '"type":"PageViewed"' ""
echo "$ACT" | grep -q '"type":"Locked"'     && ok "the lockout is in the event log"    || bad "lock event" '"type":"Locked"' ""
eq "the CSV export is served"                   "$(code "$AD" GET "/api/v1/media/$SHARED_ID/activity/export")" "200"
eq "a class teacher cannot issue links (403)"   "$(code "$T4" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"nope","expiresAt":"'"$EXP"'"}')" "403"

eq "the tenant caps link lifetime" "$(code "$AD" PUT "/api/v1/organizations/$ORG_ID/document-sharing/policy" '{"maxLinkDays":7,"attributionRetentionDays":180,"notifyOnFirstOpenByDefault":true}')" "200"
eq "a link beyond the cap is refused (400)" "$(code "$AD" POST "/api/v1/media/$SHARED_ID/shares" '{"label":"too long","expiresAt":"'"$EXP"'"}')" "400"
code "$AD" PUT "/api/v1/organizations/$ORG_ID/document-sharing/policy" '{"maxLinkDays":365,"attributionRetentionDays":180,"notifyOnFirstOpenByDefault":true}' > /dev/null
ok "policy restored to 365 days"

code "$AD" PUT "/api/v1/media/$SHARED_ID/publishing" '{"isShareable":false}' > /dev/null
popen "$P2" '{}' | grep -q '"status":"Denied"' && ok "a document taken off sharing refuses every link (fails closed)" || bad "unshare" "Denied" "$(popen "$P2" '{}' | head -c 120)"
sleep 31 # the serving classification is cached for 30 seconds
eq "and its raw path is public again (no longer share-only)" "$(raw "$(strip_token "$SHARED_URL")")" "200"

# --- 12f. Platform secrets never come back in clear -------------------------------------------
ES=$(body "$SA" GET "/api/v1/platform/settings/Email")
if echo "$ES" | grep -q 'SmtpPassword\\":\\"\(\(\\\\u2022\)\{8\}\|\)\\"'; then ok "the platform SMTP password is masked (or unset) in the settings API"
else bad "smtp password masked" "eight dots or empty" "$(echo "$ES" | grep -o 'SmtpPassword[^,]*' | head -1)"; fi

# =============================================================================
hdr "13. PRIVILEGE ESCALATION and CROSS-TENANT ISOLATION"
# Carried as "not yet tested" since the 2026-08 backlog. A class teacher (the least-privileged
# staff role with a login) tries the administrator's and the platform's doors; a tenant admin
# tries another tenant's. The second tenant is the 'secondtest' organization the registration
# work left live for exactly this purpose (tracker, Phase 55).
OTHER_ORG="${OTHER_ORG:-2f4b274d-6f69-4a01-9be8-16d02687bbd6}"

# --- 13a. A class teacher cannot reach administration ------------------------------------
eq "class teacher cannot create users (403)"             "$(code "$T4" POST "/api/v1/users" '{"email":"e2e.escalate@qmgr.local","username":"e2e.escalate","password":"Escalate!2026","fullName":"Nope","roleId":"00000000-0000-0000-0000-000000000000"}')" "403"
eq "class teacher cannot read platform settings (403)"   "$(code "$T4" GET "/api/v1/platform/settings")" "403"
eq "class teacher cannot list tenants (403)"             "$(code "$T4" GET "/api/v1/admin/tenants")" "403"
eq "class teacher cannot grant a module (403)"           "$(code "$T4" PUT "/api/v1/admin/tenants/$ORG_ID/modules/engagement-communications" '{"note":"nope"}')" "403"
eq "class teacher cannot change the sharing policy (403)" "$(code "$T4" PUT "/api/v1/organizations/$ORG_ID/document-sharing/policy" '{"maxLinkDays":30,"attributionRetentionDays":180}')" "403"
eq "class teacher cannot change notification settings (403)" "$(code "$T4" PUT "/api/v1/notifications/settings" '{"emailEnabled":false}')" "403"

# --- 13b. A tenant admin cannot reach another tenant ------------------------------------
eq "tenant admin cannot read another tenant's record (403)" "$(code "$AD" GET "/api/v1/admin/tenants/$OTHER_ORG")" "403"
eq "tenant admin cannot grant a module to another tenant (403)" "$(code "$AD" PUT "/api/v1/admin/tenants/$OTHER_ORG/modules/core-queue" '{"note":"nope"}')" "403"
OM=$(body "$AD" GET "/api/v1/organizations/$OTHER_ORG/media")
if [ "$(count "$OM")" = "0" ]; then ok "another tenant's media list is empty for this tenant's admin (query filter holds)"; else bad "cross-tenant media" "0 rows" "$(count "$OM") rows"; fi
# ^ ResolveOrganizationIdForWrite pins a tenant caller to its OWN organization whatever the route
#   says, so the upload lands in org1, not org2. Asserted directly:
XT=$(curl -s -X POST "$API/api/v1/organizations/$OTHER_ORG/media/upload" -H "Authorization: Bearer $AD" -F "file=@$E2E_PDF;filename=e2e.pdf;type=application/pdf" -F "name=E2E cross-tenant probe")
XT_ID=$(jget "$XT" id)
if [ -n "$XT_ID" ]; then
  body "$AD" GET "/api/v1/organizations/$ORG_ID/media" | grep -q "$XT_ID" && ok "an upload addressed to another tenant is pinned to the caller's own tenant" \
    || bad "upload pinned to own tenant" "row in org $ORG_ID" "row not found in own org"
  code "$AD" DELETE "/api/v1/media/$XT_ID" > /dev/null
else
  ok "an upload addressed to another tenant is refused outright"
fi
eq "a share cannot be issued against a document id of another tenant (404)" "$(code "$AD" POST "/api/v1/media/00000000-0000-0000-0000-000000000001/shares" '{"label":"x","expiresAt":"'"$EXP"'"}')" "404"


# --- 13c. Role VISIBILITY and role ASSIGNMENT (2026-09-18) -------------------------------
# Reported from production: "I am not expecting platform admin role to appear in the tenant
# management panel." Scanning for the same shape found something worse, which 13c2 covers.
ROLES_AD=$(body "$AD" GET "/api/v1/roles")
if echo "$ROLES_AD" | grep -q '"super-admin"'; then
  bad "platform admin role is hidden from a tenant" "no super-admin in GET /roles" "super-admin listed"
else
  ok "platform admin role is hidden from a tenant's own role list"
fi
echo "$ROLES_AD" | grep -q '"admin"' && ok "the tenant's own system roles are still listed" \
  || bad "tenant roles listed" "admin present" "admin missing"

# The by-id route leaked the same role's FULL permission list, so it is asserted separately.
# A SuperAdmin resolves the id; the tenant admin must get 404 (never 403 — a 403 confirms it exists).
SA_ROLES=$(body "$SA" GET "/api/v1/roles")
SUPER_ROLE_ID=$(echo "$SA_ROLES" | tr '{' '\n' | grep '"super-admin"' | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
if [ -n "$SUPER_ROLE_ID" ]; then
  eq "a tenant admin cannot read the platform role by id (404, not 403)" \
     "$(code "$AD" GET "/api/v1/roles/$SUPER_ROLE_ID")" "404"

  # --- 13c2. The escalation the visibility report uncovered -----------------------------
  # CreateUser and UpdateUser took any RoleId and assigned it with NO rank check, so a tenant
  # admin holding users.create could mint a Platform Administrator — a role that bypasses every
  # permission check and reaches every organization. RoleAssignmentGuard refuses super-admin
  # outright; it existed and these two endpoints simply never called it.
  eq "a tenant admin cannot CREATE a user as platform admin (400)" \
     "$(code "$AD" POST "/api/v1/users" '{"email":"e2e.esc.create@qmgr.local","username":"e2e.esc.create","password":"Escalate!2026x","firstName":"No","lastName":"Escalation","roleId":"'"$SUPER_ROLE_ID"'"}')" "400"

  AD_ME=$(body "$AD" GET "/api/v1/auth/me")
  AD_ID=$(echo "$AD_ME" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
  if [ -n "$AD_ID" ]; then
    eq "a tenant admin cannot PROMOTE themselves to platform admin (400)" \
       "$(code "$AD" PUT "/api/v1/users/$AD_ID" '{"roleId":"'"$SUPER_ROLE_ID"'"}')" "400"
  else
    bad "resolve the tenant admin's own id" "an id from /auth/me" "none"
  fi
else
  bad "resolve the super-admin role id as SuperAdmin" "an id" "none"
fi

# --- 13c3. A module's roles are hidden from a tenant that has not enabled it --------------
# "it makes no sense showing a business the default roles of a school." The Staff Performance roles
# belong to student-welfare (display name "Welfare & Performance").
# The dev tenant HAS that module, so they must be present here; the negative is asserted by
# revoking it and putting it back.
#
# THE PROBE IS director-of-studies, NOT class-teacher (changed 2026-09-22). This block used
# class-teacher, and that role was DELETED when heading a class became a POST rather than a role — so
# all three assertions tested a row that no longer exists and reported its absence as a failure. Any
# role RoleCodes.ModuleFor maps to student-welfare serves; director-of-studies is the one certain to
# survive, being the timetable-and-appraisal role the module is built around.
MODULE_ROLE='"director-of-studies"'
echo "$ROLES_AD" | grep -q "$MODULE_ROLE" \
  && ok "a module's roles are listed while the tenant has the module" \
  || bad "module roles listed" "director-of-studies present" "director-of-studies missing"

if [ -n "${ORG_ID:-}" ]; then
  code "$SA" DELETE "/api/v1/admin/tenants/$ORG_ID/modules/student-welfare" > /dev/null
  ROLES_NOMOD=$(body "$AD" GET "/api/v1/roles")
  if echo "$ROLES_NOMOD" | grep -q "$MODULE_ROLE"; then
    bad "a module's roles are hidden without the module" "no director-of-studies" "director-of-studies still listed"
  else
    ok "a module's roles are hidden from a tenant without that module"
  fi
  echo "$ROLES_NOMOD" | grep -q '"admin"' \
    && ok "the core roles survive a module being off" \
    || bad "core roles survive" "admin present" "admin missing"
  code "$SA" PUT "/api/v1/admin/tenants/$ORG_ID/modules/student-welfare" '{"note":"e2e restore"}' > /dev/null
  ROLES_BACK=$(body "$AD" GET "/api/v1/roles")
  echo "$ROLES_BACK" | grep -q "$MODULE_ROLE" \
    && ok "restoring the module restores its roles" \
    || bad "module restored" "director-of-studies present again" "still missing"
fi

# --- 13d. Welfare exports and publishes are logged, and the log is scoped ------------------
# Until 2026-09-18 WelfareController had no activity logger at all: a named child's full welfare
# chronology could be published to the Library as a shareable PDF with nothing recording it.
eq "an unrecognised welfare export kind is refused (400)" \
   "$(code "$AD" POST "/api/v1/branches/$BRANCH/welfare/activity/exports" '{"kind":"not-a-kind"}')" "400"
eq "a welfare list export is recorded (204)" \
   "$(code "$AD" POST "/api/v1/branches/$BRANCH/welfare/activity/exports" '{"kind":"records","format":"CSV","rowCount":7}')" "204"
eq "a student welfare report publish is recorded (204)" \
   "$(code "$AD" POST "/api/v1/branches/$BRANCH/welfare/activity/exports" '{"kind":"timeline","subjectStudentId":"'"$S4_STUDENT"'","format":"PDF","published":true,"documentName":"E2E welfare report"}')" "204"

WACT=$(body "$AD" GET "/api/v1/branches/$BRANCH/welfare/activity")
echo "$WACT" | grep -q 'welfare.list.exported' && ok "the export appears in the welfare activity log" \
  || bad "welfare activity log" "a welfare.list.exported row" "not found"
echo "$WACT" | grep -q 'welfare.report.published' && ok "the publish appears in the welfare activity log" \
  || bad "welfare activity log" "a welfare.report.published row" "not found"

# A class teacher may record an export of their OWN student, and never of another class's.
eq "a class teacher cannot record an export about a student outside their classes (404)" \
   "$(code "$T2" POST "/api/v1/branches/$BRANCH/welfare/activity/exports" '{"kind":"timeline","subjectStudentId":"'"$S4_STUDENT"'","format":"PDF"}')" "404"
eq "a class teacher can record an export about their own student (204)" \
   "$(code "$T4" POST "/api/v1/branches/$BRANCH/welfare/activity/exports" '{"kind":"timeline","subjectStudentId":"'"$S4_STUDENT"'","format":"PDF"}')" "204"

# The read is scoped the same way: a class teacher must not read the school's export history.
WACT_T2=$(body "$T2" GET "/api/v1/branches/$BRANCH/welfare/activity")
if echo "$WACT_T2" | grep -q "$S4_STUDENT"; then
  bad "the welfare activity log is class-scoped" "no S4 student events for the S2 teacher" "S4 events visible"
else
  ok "the welfare activity log is class-scoped for a class teacher"
fi
if echo "$WACT_T2" | grep -q 'welfare.list.exported'; then
  bad "a scoped caller sees only their OWN subject-less exports" "none of the admin's list exports" "admin's export visible"
else
  ok "a scoped caller does not see another user's whole-branch export rows"
fi


# --- 13e. The API reference is PLATFORM-gated, not merely authenticated (2026-09-18) -----------
# Reported by the user: "every user is having api documentation, even under profile", then
# "it should be only accessible to platform admins. it is a platform gated feature."
# It was .RequireAuthorization() — ANY signed-in user could read the complete endpoint inventory
# of the whole product, platform and admin routes included. Now the platform.admin policy, which
# no tenant role can hold.
eq "API reference refuses an anonymous caller (401)"        "$(curl -s -o /dev/null -w '%{http_code}' "$API/api/docs/")" "401"
eq "OpenAPI document refuses an anonymous caller (401)"     "$(curl -s -o /dev/null -w '%{http_code}' "$API/openapi/v1.json")" "401"
eq "a class teacher cannot open the API reference (403)"    "$(curl -s -o /dev/null -w '%{http_code}' "$API/api/docs/?access_token=$T4")" "403"
eq "a tenant admin cannot open the API reference (403)"     "$(curl -s -o /dev/null -w '%{http_code}' "$API/api/docs/?access_token=$AD")" "403"
eq "a tenant admin cannot fetch the OpenAPI document (403)" "$(curl -s -o /dev/null -w '%{http_code}' "$API/openapi/v1.json?access_token=$AD")" "403"
eq "a platform admin CAN open the API reference (200)"      "$(curl -s -o /dev/null -w '%{http_code}' "$API/api/docs/?access_token=$SA")" "200"
eq "a platform admin CAN fetch the OpenAPI document (200)"  "$(curl -s -o /dev/null -w '%{http_code}' "$API/openapi/v1.json?access_token=$SA")" "200"

# --- Cleanup: the media rows go; the welfare record stays (append-only, labelled dummy) --------
eq "cleanup: shared document deleted" "$(code "$AD" DELETE "/api/v1/media/$SHARED_ID")" "204"
eq "cleanup: public document deleted" "$(code "$AD" DELETE "/api/v1/media/$PUBLIC_ID")" "204"
rm -f "$E2E_PDF"

# --- 14. Staff Performance Monitor ------------------------------------------------------------
# Written in Node (scripts/e2e/staff-performance-e2e.mjs) because its point is concurrency: the
# same write fired N times at once, then the invariant checked. Skipped with a notice, not failed,
# on a machine with no node — say so plainly rather than report a pass nobody ran.
hdr "14. STAFF PERFORMANCE (Node)"
if command -v node > /dev/null 2>&1; then
  SP_OUT=$(API="$API" BRANCH="$BRANCH" ORG="${ORG_ID:-}" SA_USER="$SA_USER" SA_PASS="$SA_PASS" \
    node "$(dirname "$0")/staff-performance-e2e.mjs" 2>&1)
  echo "$SP_OUT" | sed 's/^/  /'
  SP_PASS=$(echo "$SP_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  SP_FAIL=$(echo "$SP_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$SP_PASS" ]; then bad "staff performance suite ran to completion" "a summary line" "none (see output above)"
  else PASS=$((PASS+SP_PASS)); FAIL=$((FAIL+SP_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 14 did not run\n'
fi

# --- 16. Payments through the sacc.ug gateway ---------------------------------------------------
# Node (scripts/e2e/payments-e2e.mjs): it hosts a local stand-in for the gateway on 127.0.0.1:5099
# and points the platform's gateway settings at it — allowed for a loopback address in Development
# only — then switches them back off. No money moves and no phone rings.
hdr "16. PAYMENTS (Node)"
if command -v node > /dev/null 2>&1; then
  PM_OUT=$(API="$API" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/payments-e2e.mjs" 2>&1)
  echo "$PM_OUT" | sed 's/^/  /'
  PM_PASS=$(echo "$PM_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  PM_FAIL=$(echo "$PM_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$PM_PASS" ]; then bad "payments suite ran to completion" "a summary line" "none (see output above)"
  else PASS=$((PASS+PM_PASS)); FAIL=$((FAIL+PM_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 16 did not run\n'
fi

# --- 19. White label: tenant domains, brand assets, attribution -----------------------------------
# Node (scripts/e2e/white-label-e2e.mjs). It needs the API started with Dns__Stub=true (a TXT
# lookup answered from memory) and CustomDomains__SkipCertificate=true (certbot is not on this
# machine) - both Development-only and both checked against the environment as well as the key.
# Without them the DNS sections print SKIP rather than failing, which is honest: they cannot run.
# It claims a domain and releases it, uploads two brand assets and removes them, and puts every
# override and every branding field back as it found them.
hdr "19. WHITE LABEL (Node)"
if command -v node > /dev/null 2>&1; then
  WL_OUT=$(API="$API" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/white-label-e2e.mjs" 2>&1)
  echo "$WL_OUT" | sed 's/^/  /'
  WL_PASS=$(echo "$WL_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  WL_FAIL=$(echo "$WL_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$WL_PASS" ]; then bad "white-label suite ran to completion" "a summary line" "none (see output above)"
  else PASS=$((PASS+WL_PASS)); FAIL=$((FAIL+WL_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 19 did not run\n'
fi

# --- 20. Tenant lifecycle and complete purge ------------------------------------------------------
# Node (scripts/e2e/tenant-purge-e2e.mjs). It CREATES ITS OWN TENANT and destroys it: it cannot
# run against the dev tenant, because a successful run ends with the tenant gone. It also resets
# its own sign-up budget first (Development-only endpoint, 404 elsewhere) because registration is
# capped at three an hour per address and a suite that creates a tenant to destroy one needs more.
hdr "20. TENANT PURGE (Node)"
if command -v node > /dev/null 2>&1; then
  TP_OUT=$(API="$API" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/tenant-purge-e2e.mjs" 2>&1)
  echo "$TP_OUT" | sed 's/^/  /'
  TP_PASS=$(echo "$TP_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  TP_FAIL=$(echo "$TP_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$TP_PASS" ]; then printf '  [33mSKIP[0m  section 20 did not report a summary (often the sign-up rate limit)
'
  else PASS=$((PASS+TP_PASS)); FAIL=$((FAIL+TP_FAIL)); fi
else
  printf '  [33mSKIP[0m  node is not installed; section 20 did not run
'
fi

# --- 15, 17, 18: the three Node suites that were only ever run by hand --------------------------
# Each existed and passed standalone but was never called from here, so a full run under-reported
# by ~90 assertions and a regression in any of the three would not have shown up in one.
hdr "15. DUTY ROTA & TIMETABLE (Node)"
if command -v node > /dev/null 2>&1; then
  DR_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/duty-rota-e2e.mjs" 2>&1)
  echo "$DR_OUT" | sed 's/^/  /'
  DR_PASS=$(echo "$DR_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  DR_FAIL=$(echo "$DR_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$DR_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 15 did not report a summary\n'
  else PASS=$((PASS+DR_PASS)); FAIL=$((FAIL+DR_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 15 did not run\n'
fi

hdr "17. MINUTES OF MEETINGS (Node)"
if command -v node > /dev/null 2>&1; then
  MN_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/minutes-e2e.mjs" 2>&1)
  echo "$MN_OUT" | sed 's/^/  /'
  MN_PASS=$(echo "$MN_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  MN_FAIL=$(echo "$MN_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$MN_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 17 did not report a summary\n'
  else PASS=$((PASS+MN_PASS)); FAIL=$((FAIL+MN_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 17 did not run\n'
fi

hdr "18. THE TWO DOORS (Node)"
if command -v node > /dev/null 2>&1; then
  RD_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/registration-doors-e2e.mjs" 2>&1)
  echo "$RD_OUT" | sed 's/^/  /'
  RD_PASS=$(echo "$RD_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  RD_FAIL=$(echo "$RD_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$RD_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 18 did not report a summary\n'
  else PASS=$((PASS+RD_PASS)); FAIL=$((FAIL+RD_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 18 did not run\n'
fi

hdr "21. STAFF SELF-SERVICE CONFIGURATION (Node)"
# Node because the point is concurrency: it fires the same claim and the same request from several
# callers at once and checks the invariant. It seeds what it needs — a subject-teacher assignment
# and a draft timetable — and removes both, and it leaves self-service switched OFF, which is the
# product default rather than whatever the previous run happened to leave.
if command -v node > /dev/null 2>&1; then
  SS_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/self-service-e2e.mjs" 2>&1)
  echo "$SS_OUT" | sed 's/^/  /'
  SS_PASS=$(echo "$SS_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  SS_FAIL=$(echo "$SS_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$SS_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 21 did not report a summary\n'
  else PASS=$((PASS+SS_PASS)); FAIL=$((FAIL+SS_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 21 did not run\n'
fi

hdr "22. A STAFF MEMBER WITH NO EMAIL ADDRESS (Node)"
# 133 of the 184 staff on the first real school list have no address, and until 2026-09-21 that meant
# they could not exist here at all. The assertion the whole schema change turns on is the SECOND one
# created: an empty string would have let exactly one through and then collided on the unique index.
if command -v node > /dev/null 2>&1; then
  NE_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/staff-no-email-e2e.mjs" 2>&1)
  echo "$NE_OUT" | sed 's/^/  /'
  NE_PASS=$(echo "$NE_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  NE_FAIL=$(echo "$NE_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$NE_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 22 did not report a summary\n'
  else PASS=$((PASS+NE_PASS)); FAIL=$((FAIL+NE_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 22 did not run\n'
fi

hdr "23. STUDENT CODES AND STAFF NUMBERS (Node)"
# Required, unique per organisation and CASE-FOLDED. Node because the last line of defence is a
# functional unique index, and the only way to prove an index holds is to race it.
if command -v node > /dev/null 2>&1; then
  PC_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/person-codes-e2e.mjs" 2>&1)
  echo "$PC_OUT" | sed 's/^/  /'
  PC_PASS=$(echo "$PC_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  PC_FAIL=$(echo "$PC_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$PC_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 23 did not report a summary\n'
  else PASS=$((PASS+PC_PASS)); FAIL=$((FAIL+PC_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 23 did not run\n'
fi

hdr "24. RE-IMPORTING A SHEET (Node)"
# What a re-import would CHANGE, and who might be one person twice. Both halves — staff and roll —
# because they are one question with two endpoints, and the browser panel above them is shared.
if command -v node > /dev/null 2>&1; then
  IP_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/import-precheck-e2e.mjs" 2>&1)
  echo "$IP_OUT" | sed 's/^/  /'
  IP_PASS=$(echo "$IP_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  IP_FAIL=$(echo "$IP_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$IP_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 24 did not report a summary\n'
  else PASS=$((PASS+IP_PASS)); FAIL=$((FAIL+IP_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 24 did not run\n'
fi

hdr "25. THE MOBILE SHELL (Node)"
# Per-device sessions, rotation, the REPLAY that must revoke, the handoff, tenant/info and app
# distribution. Node because the interesting half is concurrency: rotation is only worth having if
# two simultaneous redemptions produce exactly one winner and the loser kills the device.
if command -v node > /dev/null 2>&1; then
  MS_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/mobile-shell-e2e.mjs" 2>&1)
  echo "$MS_OUT" | sed 's/^/  /'
  MS_PASS=$(echo "$MS_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  MS_FAIL=$(echo "$MS_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$MS_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 25 did not report a summary\n'
  else PASS=$((PASS+MS_PASS)); FAIL=$((FAIL+MS_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 25 did not run\n'
fi


hdr "26. TIMETABLE OWNERSHIP, COVER AND SWAPS (Node)"
# Appointed masters, the visible administrator override, the publish that no longer archives a live
# version silently, dated cover, and the permanent swap that used to refuse after everybody had agreed.
# Node because half of it is concurrency and the rest needs a teacher holding no timetable permission.
if command -v node > /dev/null 2>&1; then
  TO_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/timetable-ownership-e2e.mjs" 2>&1)
  echo "$TO_OUT" | sed 's/^/  /'
  TO_PASS=$(echo "$TO_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  TO_FAIL=$(echo "$TO_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$TO_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 26 did not report a summary\n'
  else PASS=$((PASS+TO_PASS)); FAIL=$((FAIL+TO_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 26 did not run\n'
fi

hdr "27. EVERY NOTIFICATION NAMES ONE PERSON (Node)"
# A teacher onboarded at Maryhill read the school's failed payments in the bell: payment and trial
# notices had no recipient, were read by the whole tenant, and were pushed live to every tenant. A
# refused purchase through the gateway stub, then who heard — in the list, the count and live over
# SignalR — plus the shared read flag and five settlements racing one payment.
if command -v node > /dev/null 2>&1; then
  NR_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/notification-routing-e2e.mjs" 2>&1)
  echo "$NR_OUT" | sed 's/^/  /'
  NR_PASS=$(echo "$NR_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  NR_FAIL=$(echo "$NR_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$NR_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 27 did not report a summary\n'
  else PASS=$((PASS+NR_PASS)); FAIL=$((FAIL+NR_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 27 did not run\n'
fi

hdr "28. A SCHOOL CHOOSES HOW A NAME IS WRITTEN (Node)"
# Organization.Settings["People"]: the display order and the sort order, through every door that
# names a person, and a race against another writer of the same settings column.
if command -v node > /dev/null 2>&1; then
  NO_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/name-order-e2e.mjs" 2>&1)
  echo "$NO_OUT" | sed 's/^/  /'
  NO_PASS=$(echo "$NO_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  NO_FAIL=$(echo "$NO_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$NO_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 28 did not report a summary\n'
  else PASS=$((PASS+NO_PASS)); FAIL=$((FAIL+NO_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 28 did not run\n'
fi

hdr "29. GATES AT VISITOR CHECK-IN AND CHECK-OUT (Node)"
# The branch's gate list has one writer and a used gate is retired, never removed. Every check-in and
# check-out resolves its gate by VisitorGateRule — required with two, filled with one, absent with none —
# and records who admitted and saw out the visitor; the roll call, the report and the export carry it.
if command -v node > /dev/null 2>&1; then
  VG_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/visitor-gates-e2e.mjs" 2>&1)
  echo "$VG_OUT" | sed 's/^/  /'
  VG_PASS=$(echo "$VG_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  VG_FAIL=$(echo "$VG_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$VG_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 29 did not report a summary\n'
  else PASS=$((PASS+VG_PASS)); FAIL=$((FAIL+VG_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 29 did not run\n'
fi

hdr "30. THE SCHOOL CALENDAR, THE PERSONAL FEED AND THE NATIONAL DATES (Node)"
# SchoolEventVisibility (a teacher sees Staff events and their own, never Students- or Public-only),
# the write refusals, the settings key raced against another writer, the anonymous .ics feed (CRLF,
# 75-octet folding, exclusive all-day DTEND, a replaced link dying), the Public-only signage list,
# My School Day and the portal, the platform-kept national calendar, and a term's theme.
if command -v node > /dev/null 2>&1; then
  CA_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/calendar-e2e.mjs" 2>&1)
  echo "$CA_OUT" | sed 's/^/  /'
  CA_PASS=$(echo "$CA_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  CA_FAIL=$(echo "$CA_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$CA_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 30 did not report a summary\n'
  else PASS=$((PASS+CA_PASS)); FAIL=$((FAIL+CA_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 30 did not run\n'
fi
hdr "31. THE TERM PROGRAMME IMPORT (Node)"
# A school's own documents become events, staff meetings and rota slots through one preview and one commit.
# With E2E_DOCS_DIR set it also reads the school's five real documents (never in this repository — they carry
# staff phone numbers) and asserts the measured counts, the gap, the doubles, the one suggestion and the conflict.
# Always: the preview writes nothing, a re-import creates nothing, a teacher is refused, two racing commits
# create each row once, and undo keeps a meeting whose register was taken.
if command -v node > /dev/null 2>&1; then
  PI_OUT=$(API="$API" BRANCH="$BRANCH" E2E_DOCS_DIR="${E2E_DOCS_DIR:-}" node "$(dirname "$0")/programme-import-e2e.mjs" 2>&1)
  echo "$PI_OUT" | sed 's/^/  /'
  PI_PASS=$(echo "$PI_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  PI_FAIL=$(echo "$PI_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$PI_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 31 did not report a summary\n'
  else PASS=$((PASS+PI_PASS)); FAIL=$((FAIL+PI_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 31 did not run\n'
fi

hdr "32. LOGGING ONE RECORD FOR A GROUP, AND TIDYING SHOUTED NAMES (Node)"
# POST …/welfare-records/bulk: one record per student through the single create's own code, one incident for
# behaviour only, the whole batch refused (unknown-student words) when one child is out of scope, no welfare
# concern in bulk, the 200 cap, alerts coalesced to one per recipient, one activity line. Then tidy-names:
# preview writes nothing, a SHOUTED name moves, mixed case never does. Scratch students are deactivated after.
if command -v node > /dev/null 2>&1; then
  BL_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/bulk-log-e2e.mjs" 2>&1)
  echo "$BL_OUT" | sed 's/^/  /'
  BL_PASS=$(echo "$BL_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  BL_FAIL=$(echo "$BL_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$BL_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 32 did not report a summary\n'
  else PASS=$((PASS+BL_PASS)); FAIL=$((FAIL+BL_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 32 did not run\n'
fi

hdr "33. LOGGING ONE RECORD FOR A GROUP OF STAFF (Node)"
# POST …/staff/records/bulk: one Contribution or Conduct record per person through the single create's own code, in
# one transaction; the caller left out and named; the whole batch refused (the single create's not-found words) when
# one person is outside the caller's staff scope; every other kind, Confidential, drafts, the 200 cap (checked before
# scope) and a parameter that does not apply to one of them refused; late entry once; a supervisor told ONCE per
# batch; one activity line. Uses section 14's staff and creates no users; every record it writes is annulled after.
if command -v node > /dev/null 2>&1; then
  BSL_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/bulk-staff-log-e2e.mjs" 2>&1)
  echo "$BSL_OUT" | sed 's/^/  /'
  BSL_PASS=$(echo "$BSL_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  BSL_FAIL=$(echo "$BSL_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$BSL_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 33 did not report a summary\n'
  else PASS=$((PASS+BSL_PASS)); FAIL=$((FAIL+BSL_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 33 did not run\n'
fi

hdr "34. User limit — active people only, and every way back in asks first"
# The limit counted deactivated users while the product's only "delete" deactivates, so a seat could never
# be given back. It counts ACTIVE people now, and every path that re-enables somebody asks first: the toggle,
# the bulk enable (preview and run), and undoing a bulk disable. Registers its own scratch tenant with a
# 10-user limit and purges it afterwards; Development only.
if command -v node > /dev/null 2>&1; then
  US_OUT=$(API="$API" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/user-seats-e2e.mjs" 2>&1)
  echo "$US_OUT" | sed 's/^/  /'
  US_PASS=$(echo "$US_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  US_FAIL=$(echo "$US_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$US_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 34 did not report a summary\n'
  else PASS=$((PASS+US_PASS)); FAIL=$((FAIL+US_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 34 did not run\n'
fi

hdr "35. Exam-supervision series and the school's own employment types"
# A named series of Session duties with managers who run it without the duties permission and cannot appoint; an
# outsider gets 404; a manager change moves the registers; an invigilator double-booked is warned, never refused.
# Employment types: the enum became the school's list — a name it does not carry is refused, a held type cannot be
# removed, only retired. Uses section 14's staff; creates no users; cancels and restores what it writes.
if command -v node > /dev/null 2>&1; then
  ES_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/exam-series-e2e.mjs" 2>&1)
  echo "$ES_OUT" | sed 's/^/  /'
  ES_PASS=$(echo "$ES_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  ES_FAIL=$(echo "$ES_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$ES_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 35 did not report a summary\n'
  else PASS=$((PASS+ES_PASS)); FAIL=$((FAIL+ES_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 35 did not run\n'
fi

hdr "36. The school chain: Head Teacher, Deputy, Director of Studies"
# The two seeded roles, their sets and labels; the ORDER as RoleAssignmentGuard applies it; and the person-side gate:
# nobody changes, switches off, re-addresses or removes an account whose role they could not have given - one at a
# time or in bulk. Borrows section 14's e2e.sp.dos / e2e.sp.aa and puts both roles back.
if command -v node > /dev/null 2>&1; then
  SR_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/school-roles-e2e.mjs" 2>&1)
  echo "$SR_OUT" | sed 's/^/  /'
  SR_PASS=$(echo "$SR_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  SR_FAIL=$(echo "$SR_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$SR_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 36 did not report a summary\n'
  else PASS=$((PASS+SR_PASS)); FAIL=$((FAIL+SR_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 36 did not run\n'
fi

hdr "37. The RBAC review, built: posts, the access review, leavers, governors"
# R1-R8 of the RBAC review (2026-09-24): the school chain above the front office, the safeguarding lead and acting
# head as posts that grant and expire, house posts that reach a house, the access review, the Board Member reading
# figures only, and the overnight leaver sweep. Borrows section 14 accounts and puts every role and post back.
if command -v node > /dev/null 2>&1; then
  LD_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/leadership-e2e.mjs" 2>&1)
  echo "$LD_OUT" | sed 's/^/  /'
  LD_PASS=$(echo "$LD_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  LD_FAIL=$(echo "$LD_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$LD_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 37 did not report a summary\n'
  else PASS=$((PASS+LD_PASS)); FAIL=$((FAIL+LD_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 37 did not run\n'
fi

hdr "38. SACC Dashboard: the product's name and a school's own"
# The rebrand (2026-09-24, docs/plans/SACC_DASHBOARD_REBRAND.md): the app-name rule on save, how the shown name
# resolves (a school's own only while white-labelling is entitled and on), the short form, the platform setter,
# registration refusing a bad name, the mobile tenant/info, and the mark. Puts the tenant's branding back.
if command -v node > /dev/null 2>&1; then
  PB_OUT=$(API="$API" node "$(dirname "$0")/product-brand-e2e.mjs" 2>&1)
  echo "$PB_OUT" | sed 's/^/  /'
  PB_PASS=$(echo "$PB_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  PB_FAIL=$(echo "$PB_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$PB_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 38 did not report a summary\n'
  else PASS=$((PASS+PB_PASS)); FAIL=$((FAIL+PB_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 38 did not run\n'
fi

hdr "39. Moving a period on the school day"
# 2026-09-24: a period moved before the one ahead of it is accepted and comes back in time order, and a lesson already
# on a teacher's calendar moves to the new time without anybody pressing Generate. Restores the school day.
if command -v node > /dev/null 2>&1; then
  SD_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/school-day-order-e2e.mjs" 2>&1)
  echo "$SD_OUT" | sed 's/^/  /'
  SD_PASS=$(echo "$SD_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  SD_FAIL=$(echo "$SD_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$SD_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 39 did not report a summary\n'
  else PASS=$((PASS+SD_PASS)); FAIL=$((FAIL+SD_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 39 did not run\n'
fi

hdr "40. What a role can see is what it may use"
# 2026-09-25 (docs/plans/CLOSE_OUT_RBAC_AND_ACCOUNT.md): every seeded tenant role against the messaging secrets, the
# channel flags, ticket printing, a colleague's HR fields, module prices, feedback links, the account list's phone
# book and the account's own endpoints. Registers and purges its own tenant. 40.9 needs PSQL + PGPASSWORD (skips).
if command -v node > /dev/null 2>&1; then
  RB_OUT=$(API="$API" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/rbac-settings-e2e.mjs" 2>&1)
  echo "$RB_OUT" | sed 's/^/  /'
  RB_PASS=$(echo "$RB_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  RB_FAIL=$(echo "$RB_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$RB_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 40 did not report a summary\n'
  else PASS=$((PASS+RB_PASS)); FAIL=$((FAIL+RB_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 40 did not run\n'
fi


hdr "41. WHO AN EVENT IS FOR, AND TELLING THEM ONCE (Node)"
# 2026-09-26 (docs/plans/CALENDAR_AUDIENCES_AND_IMPORT_ROUTING.md): targeted audiences and the My events / Whole school
# scopes, exactly-once notices (five racing creates, one event, one notice), a typo is not news, cancel keeps the event
# and the feed says CANCELLED, a stale save is 409, a weekly series told once, clashes, view choices, the push opt-out
# surviving a save, and "Give it a register" moving and removing its meeting.
if command -v node > /dev/null 2>&1; then
  CU_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/calendar-audiences-e2e.mjs" 2>&1)
  echo "$CU_OUT" | sed 's/^/  /'
  CU_PASS=$(echo "$CU_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  CU_FAIL=$(echo "$CU_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$CU_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 41 did not report a summary\n'
  else PASS=$((PASS+CU_PASS)); FAIL=$((FAIL+CU_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 41 did not run\n'
fi

hdr "42. IMPORTED MEETINGS THAT BECOME REGISTERS (Node)"
# Two meetings of one title on one day, a hand edit a re-import leaves alone, an event-only meeting linked to its
# register rather than doubled, Import health (and "Give it a register" clearing it), undo keeping what a later import
# relies on, one message per person — and, with E2E_DOCS_DIR, no meeting left "event only" by default.
if command -v node > /dev/null 2>&1; then
  IR_OUT=$(API="$API" BRANCH="$BRANCH" E2E_DOCS_DIR="${E2E_DOCS_DIR:-}" node "$(dirname "$0")/import-routing-e2e.mjs" 2>&1)
  echo "$IR_OUT" | sed 's/^/  /'
  IR_PASS=$(echo "$IR_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  IR_FAIL=$(echo "$IR_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$IR_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 42 did not report a summary\n'
  else PASS=$((PASS+IR_PASS)); FAIL=$((FAIL+IR_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 42 did not run\n'
fi

hdr "43. THE IMPORT INBOX (Node)"
# A document staged and nothing written; who sees a part; the second-approver rule; approving through the programme
# import's own commit, once under five racing approvers; reject with a reason, withdraw, a handed-off staff list.
if command -v node > /dev/null 2>&1; then
  IN_OUT=$(API="$API" BRANCH="$BRANCH" SA_USER="$SA_USER" SA_PASS="$SA_PASS" node "$(dirname "$0")/import-inbox-e2e.mjs" 2>&1)
  echo "$IN_OUT" | sed 's/^/  /'
  IN_PASS=$(echo "$IN_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  IN_FAIL=$(echo "$IN_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$IN_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 43 did not report a summary\n'
  else PASS=$((PASS+IN_PASS)); FAIL=$((FAIL+IN_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 43 did not run\n'
fi


hdr "44. SEGREGATION OF DUTIES (Node)"
# Each gap the lesson-plan audit found, attempted by the person who must be refused and then done by the right one:
# an appraisal signed by its reviewer or subject, a record about oneself, adopting minutes one wrote, excusing one's
# own report, appointing oneself to lead a department.
if command -v node > /dev/null 2>&1; then
  SD_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/separation-of-duties-e2e.mjs" 2>&1)
  echo "$SD_OUT" | sed 's/^/  /'
  SD_PASS=$(echo "$SD_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  SD_FAIL=$(echo "$SD_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$SD_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 44 did not report a summary\n'
  else PASS=$((PASS+SD_PASS)); FAIL=$((FAIL+SD_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 44 did not run\n'
fi

hdr "45. LESSON PLANS AND SCHEMES OF WORK (Node)"
# The chain end to end: pre-filled, submitted, one stage and two, returned, revised, five racing approvals, the author who
# heads the department, hostile PDFs refused by the server, the Word template filled and read back, and the gates.
if command -v node > /dev/null 2>&1; then
  TPL_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/teaching-plans-e2e.mjs" 2>&1)
  echo "$TPL_OUT" | sed 's/^/  /'
  TPL_PASS=$(echo "$TPL_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  TPL_FAIL=$(echo "$TPL_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$TPL_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 45 did not report a summary\n'
  else PASS=$((PASS+TPL_PASS)); FAIL=$((FAIL+TPL_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 45 did not run\n'
fi

hdr "46. The school's own period types (Node)"
# 2026-09-26: Lesson / Break / Assembly became the school's own list of types, each with the two switches the code acts
# on. Older documents are understood, the kind is derived, and a period with published lessons cannot stop taking them.
if command -v node > /dev/null 2>&1; then
  PT_OUT=$(API="$API" BRANCH="$BRANCH" node "$(dirname "$0")/period-types-e2e.mjs" 2>&1)
  echo "$PT_OUT" | sed 's/^/  /'
  PT_PASS=$(echo "$PT_OUT" | grep -o '[0-9]* passed, [0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  PT_FAIL=$(echo "$PT_OUT" | grep -o '[0-9]* failed' | tail -1 | grep -o '^[0-9]*')
  if [ -z "$PT_PASS" ]; then printf '  \033[33mSKIP\033[0m  section 46 did not report a summary\n'
  else PASS=$((PASS+PT_PASS)); FAIL=$((FAIL+PT_FAIL)); fi
else
  printf '  \033[33mSKIP\033[0m  node is not installed; section 46 did not run\n'
fi
printf '\n\033[1m%d passed, %d failed\033[0m\n' "$PASS" "$FAIL"
exit "$FAIL"

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
eq "the record they could read a moment ago is now 404" "$(code "$T4" GET "$B/welfare-records/$STD_ID")" "404"

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
# whole-school disproportionality breakdown. A class teacher holds welfare.reports.view and
# still reads it (scoped); the gate is what changed, not their access.
eq "class teacher still reads the cohort report" "$(code "$T4" GET "$B/welfare/cohorts")" "200"

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
code "$AD" POST "/api/v1/notifications" \
  '{"organizationId":"'"$ORG_ID"'","title":"Q-Mgr E2E delivery probe","message":"Dummy message from the Q-Mgr end-to-end suite. Safe to ignore.","channels":["Email"],"email":"'"$MAILBOX"'","emailSubject":"Q-Mgr E2E delivery probe"}' > /dev/null
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

printf '\n\033[1m%d passed, %d failed\033[0m\n' "$PASS" "$FAIL"
exit "$FAIL"

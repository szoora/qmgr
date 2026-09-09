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

printf '\n\033[1m%d passed, %d failed\033[0m\n' "$PASS" "$FAIL"
exit "$FAIL"

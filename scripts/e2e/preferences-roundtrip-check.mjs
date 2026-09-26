// A PREFERENCE SAVE COPIES THE WHOLE RECORD (2026-09-26, plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING, bug B4).
//
// NotificationPreferenceResolver.SaveAsync built its "clean" copy by listing properties by hand — EmailEnabled,
// SmsEnabled, LastStaffDigestSentAt, Events — and PushEnabled, added later, was not on the list. Every save therefore
// reset a person's push opt-out to ON, and the weekly digest saves every recipient's preferences, so an opt-out lasted
// a week. Nothing in a build or a type check could see it: the initializer compiled either way.
//
// The rule: a save of a per-person preference blob COPIES the record with `with` and narrows only what it must. This
// guard fails when a SaveAsync in either preference store builds a `new UserNotificationPreferencesDto` or
// `new UserUiPreferencesDto` — the hand-listed copy coming back.
//
//   node scripts/e2e/preferences-roundtrip-check.mjs
import fs from 'node:fs';

const FILES = [
  'src/Q-Mgr.API/Infrastructure/Services/NotificationPreferenceResolver.cs',
  'src/Q-Mgr.API/Infrastructure/Services/UserPreferencesService.cs',
];
const findings = [];
for (const f of FILES) {
  if (!fs.existsSync(f)) { findings.push(`${f}: missing — the preference store moved; point this guard at it`); continue; }
  const text = fs.readFileSync(f, 'utf8');
  // The body of every SaveAsync, to its first closing brace at the method's indentation.
  const re = /public async Task(?:<[^>]+>)? SaveAsync\([^)]*\)\s*\{([\s\S]*?)\n    \}/g;
  let m, saves = 0;
  while ((m = re.exec(text))) {
    saves++;
    const body = m[1];
    if (/new\s+User(?:Notification|Ui)PreferencesDto\s*\{/.test(body))
      findings.push(`${f}: SaveAsync builds a new preferences record by hand — copy it with \`with\`, or a property added later is dropped on every save (B4)`);
    if (!/\bwith\s*\{/.test(body) && !/Normalize\(/.test(body))
      findings.push(`${f}: SaveAsync does not copy the record with \`with\` (or through Normalize, which does)`);
  }
  if (saves === 0) findings.push(`${f}: no SaveAsync found — the guard no longer reads this file`);
}
if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} finding(s). A preference save must carry every property it was given.`);
  process.exit(1);
}
console.log(`${FILES.length} preference store(s) — every save copies the whole record.`);

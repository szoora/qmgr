// "IS THIS PERSON IN THIS AUDIENCE" HAS ONE HOME (2026-09-26, plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING §5.1).
//
// StaffAudienceRule (Shared) is the test; StaffAudience (API) supplies its facts — the staff group through GroupFor
// with its fallback, leavers and the platform account excluded. Before it, three places compared a staff group three
// ways: the notice fan-out read the raw Role.StaffGroup with no fallback, the portal compared with `==`, and the
// notices page passed no group at all — so a teacher could see a group notice they were never told of, or never see
// one they were told of.
//
// This guard fails when code outside those homes compares a staff group by hand: `StaffGroups.Applies(` or
// `StaffGroups.Key(` used against an audience field (AudienceStaffGroup(s)) anywhere but the allowed files.
//
//   node scripts/e2e/audience-home-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOTS = ['src/Q-Mgr.API', 'src/Q-Mgr.Web', 'src/Q-Mgr.Shared'];
const ALLOWED = new Set([
  'src/Q-Mgr.Shared/Application/DTOs/StaffAudienceDto.cs',          // the rule
  'src/Q-Mgr.API/Application/Services/StaffAudience.cs',            // its facts
  'src/Q-Mgr.API/Application/Services/StaffNoticeFanOut.cs',        // a notice's single clause, fed by StaffAudience
  'src/Q-Mgr.API/Controllers/v1/StaffPortalController.cs',          // the portal's notice list, through StaffGroups.Applies
  'src/Q-Mgr.Shared/Domain/Enums/StaffPerformanceEnums.cs',         // StaffGroups itself
  'src/Q-Mgr.Shared/Application/Import/Programme/AudienceTextResolver.cs', // reads a document's words INTO an audience
]);
const walk = (dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
  const p = path.join(dir, e.name);
  if (e.isDirectory()) return /[\\/](bin|obj|Migrations|wwwroot)$/.test(p) ? [] : walk(p);
  return /\.(cs|razor)$/.test(e.name) ? [p] : [];
});
// A DIRECT comparison of an audience field, or StaffGroups.Applies/Key called on one. An assignment is not a test.
const HAND_MADE = /(AudienceStaffGroups?\s*(==|!=)|(==|!=)\s*[\w.?]*AudienceStaffGroups?\b|StaffGroups\.(Applies|Key)\([^;\n]*AudienceStaffGroups?\b)/;
const findings = [];
let scanned = 0;
for (const root of ROOTS) for (const f of walk(root)) {
  scanned++;
  const rel = f.replaceAll('\\', '/');
  if (ALLOWED.has(rel)) continue;
  fs.readFileSync(f, 'utf8').split('\n').forEach((line, i) => {
    if (!line.trim().startsWith('//') && HAND_MADE.test(line)) findings.push(`${rel}:${i + 1}  ${line.trim().slice(0, 150)}`);
  });
}
if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} hand-made audience test(s). Use StaffAudienceRule / StaffAudience — one test, one set of facts.`);
  process.exit(1);
}
console.log(`${scanned} file(s) scanned — every audience test goes through StaffAudienceRule.`);

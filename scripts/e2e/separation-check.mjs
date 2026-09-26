// EVERY DECISION ON SOMEBODY'S WORK ASKS DutySeparation (2026-09-26, plan LESSON_PLANS_AND_SCHEMES_OF_WORK §3).
//
// The audit found "the author cannot decide" written by hand in seven places and missing in seven more. It is one rule
// now — DutySeparation — and this guard fails when an endpoint that DECIDES stops calling it:
//   * the lesson-plan chain: forward, approve, return;
//   * an appraisal's review, moderation and sign-off;
//   * adopting minutes; excusing a duty report ("no duty");
//   * a record's annulment, re-scoring and change of rung.
// And it fails when a NEW decision endpoint appears on those controllers without it: any action whose route ends in
// /approve, /forward, /return, /moderate, /sign or /decide is checked, listed here or not.
//
//   node scripts/e2e/separation-check.mjs
import fs from "node:fs";

const C = "src/Q-Mgr.API/Controllers/v1/";
const REQUIRED = [
  [C + "TeachingPlansController.cs", ["Forward", "Approve", "Return"]],
  [C + "StaffAppraisalsController.cs", ["SubmitReview", "Moderate", "Sign"]],
  [C + "StaffMinutesController.cs", ["Approve"]],
  [C + "StaffDutyReportsController.cs", ["NoDuty", "Return", "Review"]],
  [C + "StaffRecordsController.cs", ["Annul", "CorrectPoints", "UpdateVisibility"]],
];
const DECISION_ROUTE = /\[Http(?:Post|Put|Patch)\("[^"]*\/(approve|forward|return|moderate|sign|decide)"\)\]/;

function methods(text) {
  // Each public action: its attributes, its name and its body (to the next "    [Http" or "    // ----" or end).
  const out = [];
  const re = /((?:\s*\[[^\]]+\]\s*)+)\s*public async Task<IActionResult> (\w+)\(/g;
  let m;
  while ((m = re.exec(text))) {
    const start = m.index + m[0].length;
    const next = text.slice(start).search(/\n    \[Http|\n    \/\/ ----|\n    private /);
    out.push({ attrs: m[1], name: m[2], body: text.slice(start, next < 0 ? undefined : start + next) });
  }
  return out;
}

const findings = [];
let checked = 0;
for (const [file, names] of REQUIRED) {
  if (!fs.existsSync(file)) { findings.push(`${file}: missing — point this guard at where it moved`); continue; }
  const all = methods(fs.readFileSync(file, "utf8"));
  const wanted = new Set(names);
  for (const m of all) if (DECISION_ROUTE.test(m.attrs)) wanted.add(m.name);
  for (const name of wanted) {
    const m = all.find((x) => x.name === name);
    if (!m) { findings.push(`${file}: ${name} not found — the guard lists it as a decision`); continue; }
    checked++;
    if (!/DutySeparation\./.test(m.body)) findings.push(`${file}: ${name} decides on somebody's work without asking DutySeparation`);
  }
}
// The self-service decision lives in a service, not the controller: StaffSelfService.RefuseDecision.
const ss = "src/Q-Mgr.API/Application/Services/StaffSelfService.cs";
if (fs.existsSync(ss)) {
  checked++;
  const body = fs.readFileSync(ss, "utf8").split("public static string? RefuseDecision")[1]?.split("\n    }\n")[0] ?? "";
  if (!/DutySeparation\./.test(body)) findings.push(`${ss}: RefuseDecision no longer asks DutySeparation (G6: the colleague in a swap decides it)`);
}

if (findings.length) {
  console.log(findings.join("\n"));
  console.log(`\n${findings.length} decision(s) without the separation-of-duties rule.`);
  process.exit(1);
}
console.log(`${checked} decision(s) checked — every one asks DutySeparation.`);

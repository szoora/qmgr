// A LINK INTO ADMINISTRATION, BILLING OR THE PLATFORM IS SHOWN ONLY BEHIND A CONDITION (2026-09-25).
//
// The user menu's "Settings" and the account page's "System Settings" linked every signed-in person —
// every teacher — to /admin/settings with no condition of any kind, and the phone sheet offered
// Billing the same way. Each place that draws such a link must decide whether to draw it, and the
// decision belongs to NavGates (one rule per destination).
//
// Two checks:
//   1. In the layout, the account page and Home, every href / NavigateTo into admin/, billing or
//      platform/ in MARKUP must sit inside an @if. (Heuristic: an enclosing @if by indentation. It
//      proves a condition exists, not that it is the right one — the browser suite rbac-links.mjs
//      proves that.)
//   2. In MobileNav.cs, every slot whose Href is under /admin, /billing or /platform carries a
//      Permission or a Gate.
//
//   node scripts/e2e/nav-gate-check.mjs
import fs from 'node:fs';

const MARKUP_FILES = [
  'src/Q-Mgr.Web/Components/Layout/MainLayout.razor',
  'src/Q-Mgr.Web/Components/Pages/Profile.razor',
  'src/Q-Mgr.Web/Components/Pages/Dashboard.razor',
];
const TARGET = /(href="\/?(admin|billing|platform)\b|NavigateTo\("\/(admin|billing|platform)\b)/;
const indent = l => l.match(/^\s*/)[0].replace(/\t/g, '    ').length;

const findings = [];
let links = 0;
for (const file of MARKUP_FILES) {
  if (!fs.existsSync(file)) continue;
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  const codeAt = lines.findIndex(l => /^@code\s*\{/.test(l));
  const markupEnd = codeAt < 0 ? lines.length : codeAt;
  for (let i = 0; i < markupEnd; i++) {
    const line = lines[i];
    if (!TARGET.test(line) || /^\s*(@\*|\/\/)/.test(line)) continue;
    links++;
    let min = indent(line), gated = false;
    for (let j = i - 1; j >= 0 && min > 0; j--) {
      const t = lines[j].trim();
      if (!t || t === '{' || t === '}' || t.startsWith('@*') || t.startsWith('*@')) continue;
      const d = indent(lines[j]);
      if (d >= min) continue;
      min = d;
      if (/^(@if\b|@if\(|else if\b|\}\s*else if\b|if \()/.test(t)) { gated = true; break; }
    }
    if (!gated) findings.push(`${file}:${i + 1}  ${line.trim().slice(0, 130)}`);
  }
}

const mobile = fs.readFileSync('src/Q-Mgr.Web/Components/Shared/MobileNav.cs', 'utf8').split('\n');
mobile.forEach((line, i) => {
  const m = line.match(/new\("([^"]+)",\s*"[^"]*",\s*"[^"]*",\s*"(\/(admin|billing|platform)[^"]*)"\s*(,?[^;]*)\)/);
  if (!m) return;
  links++;
  if (!/Permissions\.|Gate:/.test(m[4])) findings.push(`MobileNav.cs:${i + 1}  slot "${m[1]}" → ${m[2]} has no Permission or Gate`);
});

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} link(s) into administration, billing or the platform with no condition. Gate each with NavGates.`);
  process.exit(1);
}
console.log(`${links} administration/billing/platform link(s) scanned — every one is behind a condition.`);

// EVERY TICK BOX IS QCheckbox AND EVERY ON/OFF SETTING IS QSwitch (2026-09-23).
//
// Sixty-five raw `<input type="checkbox">` sat across twenty-one files: forty Bootstrap switches on the
// settings pages and twenty-five tick boxes drawn by the browser. They ignored the control size scale,
// had no visible keyboard focus of the app's own, and followed a tenant's brand colour only where one
// !important override happened to reach them — "the checkboxes look like plain bootstrap".
//
// The shared components live in Components/Shared/UI and are the only files allowed a raw one:
// QCheckbox and QSwitch wrap exactly one each, and QMultiSelect draws its own list rows.
//
//   node scripts/e2e/raw-checkbox-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';
const ALLOWED = new Set(['QCheckbox.razor', 'QSwitch.razor', 'QMultiSelect.razor']);
const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

const findings = [];
for (const f of files) {
  if (ALLOWED.has(path.basename(f))) continue;
  const src = fs.readFileSync(f, 'utf8').replace(/@\*[\s\S]*?\*@/g, m => m.replace(/[^\n]/g, ' '));
  src.split('\n').forEach((line, i) => {
    if (/<input\b[^>]*type\s*=\s*"checkbox"/i.test(line) || /class="form-check form-switch"/.test(line))
      findings.push(`${f.replaceAll('\\', '/')}:${i + 1}  ${line.trim().slice(0, 140)}`);
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} raw checkbox(es). Use QCheckbox for choosing, QSwitch for turning a setting on or off.`);
  process.exit(1);
}
console.log(`${files.length} components scanned — every checkbox is QCheckbox, every setting switch is QSwitch.`);

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
// RADIOS TOO (2026-09-23): every one-of-a-few choice is QRadioGroup. Five pages had five hand-made styles,
// three of them Bootstrap's form-check. The one allowed exception is named below with its reason.
//
//   node scripts/e2e/raw-checkbox-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';
const ALLOWED = new Set(['QCheckbox.razor', 'QSwitch.razor', 'QMultiSelect.razor']);
const ALLOWED_RADIO = new Map([
  ['QRadioGroup.razor', 'the component itself'],
  // The sign-up module picker is a grid of selectable CARDS: the radio is the keyboard and screen-reader half
  // of a card, not an option in a list, and a QRadioGroup row would not draw a card.
  ['Register.razor', 'the module picker is a card picker'],
]);
const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

const CHECKBOX = [/<input\b[^>]*type\s*=\s*"checkbox"/i, /class="form-check form-switch"/];
const RADIO = [/<input\b[^>]*type\s*=\s*"radio"/i, /class="form-check"/];

const findings = [];
for (const f of files) {
  const base = path.basename(f);
  const src = fs.readFileSync(f, 'utf8').replace(/@\*[\s\S]*?\*@/g, m => m.replace(/[^\n]/g, ' '));
  src.split('\n').forEach((line, i) => {
    const checkbox = CHECKBOX.some(r => r.test(line));
    const radio = RADIO.some(r => r.test(line));
    if ((checkbox && !ALLOWED.has(base)) || (radio && !ALLOWED_RADIO.has(base)))
      findings.push(`${f.replaceAll('\\', '/')}:${i + 1}  ${line.trim().slice(0, 140)}`);
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} raw checkbox(es) or radio(s). Use QCheckbox for choosing, QSwitch for turning a setting on or off, QRadioGroup for one of a few.`);
  process.exit(1);
}
console.log(`${files.length} components scanned — every checkbox is QCheckbox, every setting switch is QSwitch, every radio group is QRadioGroup.`);

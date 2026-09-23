// A PERSON'S NAME IS WRITTEN IN ONE PLACE (2026-09-23).
//
// A school asked why names started with the first name and where to change it. There was nowhere:
// User.FullName and about seventy hand-written `$"{FirstName} {LastName}"` fixed the order in code — in
// lists, notification text, emails, exports and error messages. They now all go through PersonNames
// (API, which reads the organisation's chosen order) or PersonName.Join (Shared, the one home for
// putting two halves together). A new hand-written concatenation is the drift coming back: it compiles,
// it looks right in a school that shows names first-name-first, and it is wrong in one that does not.
//
// What it looks for, in .cs and .razor under src/ (comments blanked first):
//   FirstName + " " + ...           $"{x.FirstName} {x.LastName}"          @x.FirstName @x.LastName
//
// A SEARCH that must match either order says so on the line with `name-format: search` — that is
// matching what somebody typed, not writing a name.
//
//   node scripts/e2e/name-format-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOTS = ['src/Q-Mgr.API', 'src/Q-Mgr.Web', 'src/Q-Mgr.Shared'];
const HOMES = new Set(['PersonName.cs', 'PersonNames.cs']);
const files = [];
for (const root of ROOTS) (function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'Migrations', 'node_modules', 'wwwroot'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p);
    else if ((e.name.endsWith('.cs') || e.name.endsWith('.razor')) && !HOMES.has(e.name)) files.push(p);
  }
})(root);

const blank = s => s
  .replace(/@\*[\s\S]*?\*@/g, m => m.replace(/[^\n]/g, ' '))
  .replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '))
  .replace(/\/\/\/[^\n]*/g, m => ' '.repeat(m.length));

const SHAPES = [
  /FirstName\s*\+\s*" "\s*\+/,
  /\{[\w.?!\[\]()]*FirstName\}\s+\{[\w.?!\[\]()]*LastName\}/,
  /\{[\w.?!\[\]()]*LastName\}\s*,?\s+\{[\w.?!\[\]()]*FirstName\}/,
  /@[\w.?]*FirstName\s+@[\w.?]*LastName/,
];

const findings = [];
for (const f of files) {
  const raw = fs.readFileSync(f, 'utf8').split('\n');
  const lines = blank(raw.join('\n')).split('\n');
  lines.forEach((line, i) => {
    if (/name-format: search/.test(raw[i])) return;
    const code = line.replace(/(^|[^:])\/\/[^\n]*/, '$1');
    if (SHAPES.some(r => r.test(code))) findings.push(`${f.replaceAll('\\', '/')}:${i + 1}  ${raw[i].trim().slice(0, 140)}`);
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} hand-written name(s). Use PersonNames.Display (API) or PersonName.Join (Shared/Web) so the school's chosen order applies.`);
  process.exit(1);
}
console.log(`${files.length} files scanned — every person's name goes through PersonNames or PersonName.Join.`);

// A LETTER OR DIGIT DIRECTLY BEFORE `@if` MAKES RAZOR PRINT THE CODE (2026-09-24).
//
// Razor reads `word@identifier` as an EMAIL ADDRESS, not a code transition, so it is emitted as text.
// Staff Directory's heading was `<h1>Staff@if (TabHelp != null) { … }</h1>`, and production showed the
// literal "Staff@if (TabHelp != null) {" as the page title. The build was clean and nothing warned.
//
// `word@(expr)` is safe: a parenthesis cannot continue an email address, so that pluralising idiom
// ("student@(n == 1 ? "" : "s")") is left alone. Only a letter-led identifier after the @ is flagged,
// and real email addresses (a dot and a domain after the @) are skipped.
//
//   node scripts/e2e/razor-email-transition-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';
const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

// Markup text only: from a `>` to the next `<` or quote, so attribute values and C# blocks are not read.
const PATTERN = /[A-Za-z0-9]@([A-Za-z_][A-Za-z0-9_]*)(\.[A-Za-z]{2,})?/g;

const findings = [];
for (const f of files) {
  const lines = fs.readFileSync(f, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const seg of line.matchAll(/>([^<"]*)/g)) {
      for (const m of seg[1].matchAll(PATTERN)) {
        if (m[2]) continue; // name@domain.tld — a real address
        findings.push(`${f}:${i + 1}: "${m[0]}" is read as an email address; put a space before the @`);
      }
    }
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} Razor transition(s) that render as text.`);
  process.exitCode = 1;
} else {
  console.log(`No word@code transitions in ${files.length} components.`);
}

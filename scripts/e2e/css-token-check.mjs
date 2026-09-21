// A `var()` fallback hides a wrong token name, and nothing anywhere says so.
//
// `var(--qm-bg-subtle, #f4f4f5)` looks like a themed panel and is not one: `--qm-bg-subtle` has
// never existed, so that rule ALWAYS took the literal. It rendered a light grey panel behind
// theme-coloured text — invisible in dark mode — on the tenant dialog, and nothing failed. The
// same shape with no fallback at all is worse: `var(--surface-color)` on the /unauthorized card
// made the whole declaration invalid, so that page's card had no background, no radius and no
// shadow for as long as it existed.
//
// Neither is visible in a build, a type check or a code read. Only the resolved value shows it,
// and by then somebody has to already suspect the token. So: every token a rule reads must be
// defined somewhere, whatever fallback sits beside it.
//
// Static, instant, no server. Run it after touching any CSS or component <style>.
//
//   node scripts/e2e/css-token-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOTS = ['src/Q-Mgr.Web/wwwroot/css', 'src/Q-Mgr.Web/Components', 'src/Q-Mgr.Web/Services'];
const EXTS = ['.css', '.razor', '.cs', '.js'];

const files = [];
for (const root of ROOTS) {
  if (!fs.existsSync(root)) continue;
  (function walk(d) {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (['bin', 'obj', 'node_modules', 'lib'].includes(e.name)) continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (EXTS.includes(path.extname(e.name))) files.push(p);
    }
  })(root);
}

// A definition is `--name:` anywhere — a stylesheet's :root, a component's own block, an inline
// style attribute, or a C# string that writes one (BrandPalette derives the whole --qm-primary-*
// family at runtime, so those tokens exist without appearing in any .css file).
const defined = new Set();
const sources = new Map();
for (const f of files) {
  const s = fs.readFileSync(f, 'utf8');
  for (const m of s.matchAll(/(--[a-zA-Z0-9-]+)\s*:/g)) {
    defined.add(m[1]);
    if (!sources.has(m[1])) sources.set(m[1], f);
  }
}

// Tokens a browser defines, or that a third-party stylesheet we load owns. Reading one of these is
// not a typo.
const EXTERNAL = /^--(rz|bs|fa|swiper|stf)-/;

const findings = [];
for (const f of files) {
  if (!['.css', '.razor'].includes(path.extname(f))) continue;
  // Blank out comment bodies (keeping newlines, so line numbers still point at the real line).
  // Several of these notes quote the broken rule they replaced, which would otherwise report
  // itself for ever.
  const lines = fs.readFileSync(f, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, c => c.replace(/[^\n]/g, ' '))
    .split('\n');
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/var\(\s*(--[a-zA-Z0-9-]+)\s*(,([^)]*(\([^)]*\))?[^)]*))?\)/g)) {
      const token = m[1];
      if (defined.has(token) || EXTERNAL.test(token)) continue;
      const fallback = (m[3] ?? '').trim();
      const kind = !m[2] ? 'no fallback — the whole declaration is invalid'
        : /^var\(/.test(fallback) ? `falls through to ${fallback}`
        : `silently takes the literal ${fallback}`;
      findings.push({
        file: f.replace(/\\/g, '/').replace('src/Q-Mgr.Web/', ''),
        line: i + 1, token, kind,
      });
    }
  });
}

if (findings.length === 0) {
  console.log(`${files.length} file(s) scanned, ${defined.size} token(s) defined — every var() names one of them.`);
  process.exit(0);
}

console.log(`${findings.length} var() usage(s) name a token that is defined nowhere:\n`);
for (const x of findings) {
  console.log(`  ${`${x.file}:${x.line}`.padEnd(64)} ${x.token.padEnd(24)} ${x.kind}`);
}
console.log(`
Each of these is a rule that does not do what it reads as. Fix it by naming the token that really
exists — grep qm-theme.css for the family — or, where the value genuinely must not follow the
theme (a print sheet's paper, a QR quiet zone), write the literal with a comment saying why.
A fallback literal is not a safety net: it is what hides the mistake.`);
process.exit(1);

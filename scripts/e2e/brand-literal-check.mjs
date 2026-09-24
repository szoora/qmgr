// THE PRODUCT'S NAME HAS ONE HOME, AND THIS KEEPS IT THERE (rebrand 2026-09-24).
//
// Before the rebrand "Q-Mgr" was typed about 170 times across seventy files, and three of the
// places that looked like a home for it (BrandContext, TenantHostContext, EmailTemplates.AppName)
// each held their own copy. Renaming the product meant finding every one of them, and the one
// nobody found would have gone on saying the old name in somebody's inbox.
//
// Now the name lives in Q-Mgr.Shared/Application/Branding/ProductBrand.cs, and this fails the build
// when a product name is typed anywhere else under src/ — so the next rename is one file.
//
// Read: .razor .cs .js .json .html, comments stripped (a comment may say what used to be there).
// Not read: bin/, obj/, Migrations/ (a migration matches the value a row shipped with, by design).
//
//   node scripts/e2e/brand-literal-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src';
const EXT = new Set(['.razor', '.cs', '.js', '.json', '.html']);

// Where a name MAY be typed, and why.
const ALLOWED = new Map([
  ['ProductBrand.cs', 'the one home'],
  ['ProductMark.cs', 'the mark carries the product name as its accessible label, from ProductBrand'],
  // The catalogue wording a migration matches existing rows on — the value a row SHIPPED with.
  ['ModuleCatalogDefaults.cs', 'WhiteLabelPlusDescriptionShipped is the old wording a migration rewrites'],
]);

const PATTERNS = [
  { re: /Q-Mgr(?![.\w-])/g, what: 'the old product name' },
  { re: /SACC Dashboard/g, what: 'our product name — use ProductBrand.Name' },
  { re: /SACC Software/g, what: 'the legal entity — use ProductBrand.LegalEntity' },
  { re: /Powered by/gi, what: 'a "Powered by" credit — the only credit is the copyright line (QCopyright)' },
];

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'Migrations', 'node_modules'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p);
    else if (EXT.has(path.extname(e.name))) files.push(p);
  }
})(ROOT);

/** Blanks comments while keeping line numbers, so a finding points at the right line. */
function stripComments(text, ext) {
  const blank = m => m.replace(/[^\n]/g, ' ');
  let t = text;
  if (ext === '.razor') t = t.replace(/@\*[\s\S]*?\*@/g, blank);
  if (ext === '.razor' || ext === '.html') t = t.replace(/<!--[\s\S]*?-->/g, blank);
  if (ext !== '.json') {
    t = t.replace(/\/\*[\s\S]*?\*\//g, blank);
    // A line comment: // not preceded by ':' (so "https://" in a string survives).
    t = t.replace(/(^|[^:"'\\])\/\/.*$/gm, (m, pre) => pre + blank(m.slice(pre.length)));
  }
  return t;
}

const findings = [];
for (const f of files) {
  const base = path.basename(f);
  if (ALLOWED.has(base)) continue;
  const ext = path.extname(f);
  const lines = stripComments(fs.readFileSync(f, 'utf8'), ext).split('\n');
  lines.forEach((line, i) => {
    for (const { re, what } of PATTERNS) {
      re.lastIndex = 0;
      if (re.test(line)) findings.push(`${f.replace(/\\/g, '/')}:${i + 1}: ${what}\n      ${line.trim().slice(0, 150)}`);
    }
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} product name(s) typed outside ProductBrand. Read it from ProductBrand instead.`);
  process.exitCode = 1;
} else {
  console.log(`No product name typed outside ProductBrand in ${files.length} files.`);
}

// A hub SECTION may hide its title band when embedded. It must never hide its ACTIONS: the hub
// has no way to render them, so a wrapped header-actions block means the section loses its
// buttons the moment it is folded into a hub — and the reader has no way to do the thing the
// section exists for.
//
// Found on 2026-09-19: StaffNotices wrapped its whole page-header in @if (!Embedded), so the
// Setup hub's Notices tab offered no "New notice" at all.
import fs from 'node:fs';
import path from 'node:path';

const files = [];
(function w(d) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { if (['bin', 'obj'].includes(e.name)) continue; const p = path.join(d, e.name); e.isDirectory() ? w(p) : e.name.endsWith('.razor') && files.push(p); } })('src/Q-Mgr.Web/Components');

const bad = [];
for (const f of files) {
  const s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
  if (!/\[Parameter\] public bool Embedded/.test(s)) continue;
  const rel = f.replace(/\\/g, '/').replace('src/Q-Mgr.Web/Components/', '');

  // Find each `@if (!Embedded) { ... }` block and see whether an actions row is inside it.
  for (const m of s.matchAll(/@if \(!Embedded\)\s*\n\s*\{\n/g)) {
    let i = m.index + m[0].length, depth = 1;
    while (i < s.length && depth > 0) {
      if (s[i] === '{') depth++;
      else if (s[i] === '}') depth--;
      i++;
    }
    const block = s.slice(m.index, i);
    if (/class="header-actions"|<QButton|<QDataExport/.test(block)) {
      const buttons = [...block.matchAll(/<QButton[^>]*Text="([^"]*)"/g)].map((x) => x[1]);
      bad.push({ rel, buttons: buttons.length ? buttons : ['(an actions row)'] });
    }
  }
}

for (const b of bad) console.log(`  ${b.rel.padEnd(44)} hides: ${b.buttons.join(', ')}`);
console.log(`\n${bad.length} section(s) hide their own actions when embedded`);
process.exit(bad.length ? 1 : 0);

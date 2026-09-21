// Every font size and family the app actually renders, counted by how much text carries it.
//
// "the font still looks big. we need to use uniform font and size across the project."
// Measure before changing anything: a type scale is a small set of sizes used deliberately, and
// the way to tell one from a mess is to count what is on screen, not to read the stylesheets.
//
//   node scripts/e2e/browser/type-audit.mjs [route ...]
//
// IT IS A GUARD NOW (2026-09-19). The scale is six sizes — 11, 12, 13, 15, 17, 20 — the --qm-text-*
// tokens in layout.css. Before the sweep there were EIGHTEEN on screen. Any size off the scale fails
// the run and names the elements carrying it, because a new raw px font-size is the drift coming back.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const ROUTES = process.argv.slice(2).length ? process.argv.slice(2) : [
  '/', '/admin/branches', '/admin/users', '/admin/settings', '/admin/appearance',
  '/content/library', '/content/signage', '/admin/staff', '/admin/staff/records',
  '/admin/students/roster', '/admin/welfare-reports', '/portal', '/notifications',
  '/billing', '/admin/feedback',
];

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

// Only elements that actually paint text, and only their OWN text — a wrapper inherits its
// children's size and would otherwise be counted twice.
const COLLECT = `(() => {
  const out = {};
  const fams = {};
  const walk = document.createTreeWalker(document.querySelector('.qm-main') || document.body, NodeFilter.SHOW_TEXT);
  let n;
  while ((n = walk.nextNode())) {
    const text = n.nodeValue.trim();
    if (!text) continue;
    const el = n.parentElement;
    if (!el || el.offsetParent === null) continue;
    const cs = getComputedStyle(el);
    const size = Math.round(parseFloat(cs.fontSize) * 10) / 10;
    const key = size + 'px';
    if (!out[key]) out[key] = { chars: 0, nodes: 0, sample: '', tags: {} };
    out[key].chars += text.length;
    out[key].nodes += 1;
    if (!out[key].sample) out[key].sample = text.slice(0, 34);
    const tag = el.tagName.toLowerCase() + (el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\\s+/)[0] : '');
    out[key].tags[tag] = (out[key].tags[tag] || 0) + 1;
    const fam = cs.fontFamily.split(',')[0].replace(/["']/g, '');
    fams[fam] = (fams[fam] || 0) + text.length;
  }
  return JSON.stringify({ out, fams });
})()`;

const total = {};
const families = {};

for (const route of ROUTES) {
  await t.goto(BASE + route);
  const ok = await t.waitFor(`!!document.querySelector('.qm-main')`, 20000).then(() => true).catch(() => false);
  if (!ok) { console.log(`${route}  did not render`); continue; }
  await t.sleep(2200);
  const { out, fams } = JSON.parse(await t.eval(COLLECT));
  for (const [size, v] of Object.entries(out)) {
    if (!total[size]) total[size] = { chars: 0, nodes: 0, sample: v.sample, tags: {} };
    total[size].chars += v.chars;
    total[size].nodes += v.nodes;
    for (const [tag, c] of Object.entries(v.tags)) total[size].tags[tag] = (total[size].tags[tag] || 0) + c;
  }
  for (const [f, c] of Object.entries(fams)) families[f] = (families[f] || 0) + c;
}

const rows = Object.entries(total).sort((a, b) => parseFloat(b[0]) - parseFloat(a[0]));
const chars = rows.reduce((a, [, v]) => a + v.chars, 0);

console.log(`\nsize      nodes     chars    share  commonest elements`);
for (const [size, v] of rows) {
  const top = Object.entries(v.tags).sort((a, b) => b[1] - a[1]).slice(0, 3).map(([k]) => k).join(', ');
  console.log(
    size.padEnd(9) + String(v.nodes).padStart(6) + String(v.chars).padStart(10) +
    (((v.chars / chars) * 100).toFixed(1) + '%').padStart(8) + '  ' + top.slice(0, 62));
}
console.log(`\n${rows.length} distinct sizes across ${ROUTES.length} pages`);
console.log('\nfamilies by characters:');
for (const [f, c] of Object.entries(families).sort((a, b) => b[1] - a[1])) {
  console.log(`  ${f.padEnd(28)} ${c}`);
}

const SCALE = new Set(["11px", "12px", "13px", "15px", "17px", "20px"]);
const off = rows.filter(([size]) => !SCALE.has(size));
for (const [size, v] of off) {
  const top = Object.entries(v.tags).sort((a, b) => b[1] - a[1]).slice(0, 4).map(([k]) => k).join(", ");
  console.log(`  OFF SCALE  ${size.padEnd(7)} ${v.nodes} nodes — ${top}`);
}
console.log(off.length ? `\nFAIL: ${off.length} size(s) off the scale` : '\nPASS: every size is on the scale');
await t.close();
process.exit(off.length ? 1 : 0);

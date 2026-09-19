// Every route the e2e scripts navigate to, checked against the routes the app actually declares.
//
// The hubs retired 29 routes across 2026-09-18 and 2026-09-19, and three suites went on driving
// the old ones: uniform-check measured a 404 page and reported a null title three times over, and
// select-verify opened /admin/staff/notices, found no modal and died on a null element. A suite
// that fails this way reads like a product bug, which is the worst kind of false alarm.
//
// Static, instant, no server needed. Run it with route-audit.mjs after any route change.
//
//   node scripts/e2e/suite-route-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const routes = new Set();
(function walkApp(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    if (e.isDirectory()) walkApp(p);
    else if (e.name.endsWith('.razor')) {
      for (const m of fs.readFileSync(p, 'utf8').matchAll(/^@page "([^"]+)"/gm)) routes.add(m[1]);
    }
  }
})('src/Q-Mgr.Web/Components');

const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const patterns = [...routes].map((r) =>
  new RegExp('^' + r.split('/').map((s) => (s.startsWith('{') ? '[^/]+' : escape(s))).join('/') + '$', 'i'));
const resolves = (p) => patterns.some((x) => x.test(p));

const scripts = [];
(function walkScripts(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (e.name === 'node_modules') continue;
    const p = path.join(d, e.name);
    if (e.isDirectory()) walkScripts(p);
    // route-audit.mjs carries route PREFIXES of its own ("/admin", "/content"), not navigations.
    else if (/\.(mjs|js|sh)$/.test(e.name) && !/^(suite-route-check|route-audit)\.mjs$/.test(e.name)) scripts.push(p);
  }
})('scripts/e2e');

const PREFIX = /^\/(admin|content|reports|portal|billing|queue|display|platform|notifications|my-day|s)\b/;
const bad = [];
const seen = new Set();

for (const f of scripts) {
  const s = fs.readFileSync(f, 'utf8');

  // A suite that ASSERTS a retired route 404s has to name it. Those lists are called `retired`,
  // and a reference inside one is the suite doing its job, not a stale navigation.
  const retired = new Set(
    [...s.matchAll(/const retired\s*=\s*\[[\s\S]*?\];/g)]
      .flatMap((b) => [...b[0].matchAll(/['"`](\/[^'"`]+)/g)].map((x) => x[1])));

  for (const m of s.matchAll(/['"`](\/[A-Za-z0-9/_?=&{}$.-]*)/g)) {
    let link = m[1].split('?')[0].split('#')[0];
    if (!PREFIX.test(link) || link.includes('${') || link.includes('{')) continue;
    link = link.replace(/\/$/, '');
    if (!link || resolves(link) || retired.has(link)) continue;
    const key = f + link;
    if (seen.has(key)) continue;
    seen.add(key);
    bad.push([f.replace(/\\/g, '/'), link]);
  }
}

for (const [f, l] of bad) console.log(`  ${l.padEnd(40)} ${f}`);
console.log(`\n${routes.size} routes declared · ${scripts.length} scripts scanned · ${bad.length} stale reference(s)`);
process.exit(bad.length ? 1 : 0);

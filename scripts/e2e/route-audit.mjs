// Every route a page declares, against every route anything links to.
// The Phase 94 lesson: retiring a route means finding its callers, and they are not all in the
// Web project — notification ActionUrls are built in the API.
import fs from 'node:fs';
import path from 'node:path';

const roots = ['src/Q-Mgr.Web', 'src/Q-Mgr.API', 'src/Q-Mgr.Shared'];
const files = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules') continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.(razor|cs)$/.test(e.name)) files.push(p);
  }
})('src');

// ---- what routes exist
const routes = new Set();
for (const f of files) {
  if (!f.endsWith('.razor')) continue;
  for (const m of fs.readFileSync(f, 'utf8').matchAll(/^@page "([^"]+)"/gm)) routes.add(m[1]);
}

// A declared route may carry parameters: /admin/students/{id}/picture. Match a link against it
// segment by segment, treating {…} as a wildcard.
const patterns = [...routes].map(r => ({
  route: r,
  re: new RegExp('^' + r.split('/').map(s => (s.startsWith('{') ? '[^/]+' : s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))).join('/') + '$', 'i'),
}));

const resolves = (p) => patterns.some(x => x.re.test(p));

// ---- what is linked to
const RETIRED_PREFIXES = ['/admin/', '/content/', '/reports/', '/portal', '/my-day', '/billing', '/queue/', '/display/', '/platform/'];
const found = new Map();   // link -> [files]
const linkRes = [
  /ActionUrl\s*=\s*\$?"([^"{]*(?:\{[^}]*\}[^"{]*)*)"/g,
  /NavigateTo\(\s*\$?"([^"{]*(?:\{[^}]*\}[^"{]*)*)"/g,
  /Href\s*=\s*"(\/[^"?#]*)"/g,
  /ConfigureUrl\s*=\s*"(\/[^"?#]*)"/g,
  /<a href="(\/[^"?#]*)"/g,
  // The blind spots that hid seven dead billing links until 2026-09-19: an email's link, and the
  // upgrade / purchase / action / return URL an API refusal or a payment gateway hands a browser.
  /EmailTemplates\.Link\(\s*\w+\s*,\s*\$?"(\/[^"]*)"/g,
  /(?:upgradeUrl|purchaseUrl|actionUrl|returnUrl|successUrl|cancelUrl)\s*[=:]\s*\$?"(\/[^"{]*)"/gi,
  /WriteForbiddenResponse\([^;]*?"(\/[^"]*)"\)/g,
];
for (const f of files) {
  const s = fs.readFileSync(f, 'utf8');
  for (const re of linkRes) {
    for (const m of s.matchAll(re)) {
      let link = m[1].split('?')[0].split('#')[0];
      if (!link.startsWith('/')) continue;
      if (!RETIRED_PREFIXES.some(p => link.startsWith(p))) continue;
      // A trailing interpolation NOT preceded by "/" is an appended query string, not a segment:
      // $"/admin/students/{id}/welfare/report{query}". Strip it before matching.
      link = link.replace(/(?<!\/)\{[^}]*\}$/, '').replace(/\{[^}]*\}/g, '{x}');
      if (!link || link === '/') continue;
      if (!found.has(link)) found.set(link, new Set());
      found.get(link).add(f);
    }
  }
}

// BillingLinks (Q-Mgr.Shared) is the one home for billing addresses; its hub must be a real route,
// or every link built from it is broken at once.
{
  const bl = fs.readFileSync('src/Q-Mgr.Shared/Domain/Constants/BillingLinks.cs', 'utf8');
  const hub = bl.match(/const string Hub = "([^"]+)"/)?.[1];
  if (hub) {
    if (!found.has(hub)) found.set(hub, new Set());
    found.get(hub).add('src/Q-Mgr.Shared/Domain/Constants/BillingLinks.cs (Hub)');
  }
}

let broken = 0;
for (const [link, where] of [...found].sort()) {
  const probe = link.replace(/\{x\}/g, '00000000-0000-0000-0000-000000000000');
  if (resolves(probe)) continue;
  broken++;
  console.log(`BROKEN  ${link}`);
  for (const f of where) console.log(`          ${f}`);
}
console.log(`\n${routes.size} routes declared, ${found.size} distinct internal links checked, ${broken} broken.`);
process.exit(broken ? 1 : 0);

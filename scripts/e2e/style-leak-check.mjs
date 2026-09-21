// A Razor <style> block is NOT scoped. Blazor injects it into the document as written, so a page
// that redefines a SHARED class name overrides the global scale for every element of that name
// while the page is mounted — a hub's own frame included.
//
// On 2026-09-19 there were 170 such overrides of 14 names. Three of them were doing real damage:
//   * `.page-header { margin-bottom: 24px }` beat the --qm-block-gap token on whichever page
//     happened to be open, so the density work of 2026-09-18 landed and was overridden;
//   * `.admin-page { max-width: 900px; margin: 0 auto }` in BrandingSettings and KioskSettings
//     capped the ENTIRE Appearance hub hosting them — 312px of a 1212px page unused;
//   * `.subtitle { font-size: 14px }` in 24 components beat the 12.5px the band was set to.
//
// This is static, instant and needs no server. Run it after touching any component <style>.
//
//   node scripts/e2e/style-leak-check.mjs
import fs from 'node:fs';
import path from 'node:path';

// Names the GLOBAL stylesheets own. A page may use them; it may not redefine them.
const SHARED = [
  'page-header', 'page-subtitle', 'admin-page', 'page-container', 'subtitle',
  'form-row', 'form-actions', 'filter-row', 'modal-actions', 'q-tabs', 'q-filter-bar',
  'data-table', 'q-card', 'breadcrumb', 'header-content', 'header-actions',
  // 2026-09-19: 50 components sized their own empty state, so no two matched.
  'empty-state', 'empty-state-icon', 'q-empty',
];

// The excluded set every size rule in CLAUDE.md skips: the kiosk, the public display and signage,
// public feedback, booking, ticket status, shared documents, the print sheets and the sign-in
// pages. They keep their own large-format or paper sizing.
//
// Both patterns here are anchored deliberately. A bare `Report\.razor` also caught
// VisitorReport.razor and a bare `Register\.razor` also caught StaffRegister.razor — two ordinary
// admin pages that silently escaped the first sweep because of it.
const EXCLUDED = /Kiosk\/|Display\/|Feedback\/Feedback|JoinQueue|TicketStatus|BookAppointment|SharedDocument|Print\.razor|TimelineReport\.razor|JoinStaff|Unsubscribe|VerifyEmail|AccountStatus|Pages\/Login\.razor|Pages\/Register\.razor|ForgotPassword|ResetPassword|Unauthorized/;

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})('src/Q-Mgr.Web/Components');

const leaks = [];
let scanned = 0;

for (const f of files) {
  const unix = f.replace(/\\/g, '/');
  if (EXCLUDED.test(unix)) continue;
  const s = fs.readFileSync(f, 'utf8');
  const styles = [...s.matchAll(/<style>([\s\S]*?)<\/style>/g)].map(m => m[1]).join('\n');
  if (!styles.trim()) continue;
  scanned++;
  const rel = unix.replace('src/Q-Mgr.Web/Components/', '');

  for (const r of styles.matchAll(/([^{}]+)\{([^}]*)\}/g)) {
    const sel = r[1].trim().replace(/\s+/g, ' ');
    if (!sel || sel.startsWith('@') || sel.startsWith('/*')) continue;
    for (const part of sel.split(',').map(x => x.trim())) {
      // Only a selector whose LAST simple part is the bare shared class redefines it. A page-owned
      // context in front of it (`.doclib .q-card`) is the page's own object and is fine.
      const last = part.split(/\s+|>/).filter(Boolean).pop() || '';
      const cls = last.startsWith('.') ? last.slice(1) : '';
      if (!SHARED.includes(cls)) continue;
      if (part.trim() !== `.${cls}`) continue;
      leaks.push({ rel, sel: part, props: r[2].split(';').map(x => x.split(':')[0].trim()).filter(Boolean) });
    }
  }
}

if (leaks.length === 0) {
  console.log(`${scanned} components with a <style> block scanned — no shared class name redefined.`);
  process.exit(0);
}

console.log(`${leaks.length} shared class name(s) redefined in a component <style>:\n`);
for (const l of leaks) console.log(`  ${l.rel.padEnd(46)} ${l.sel} { ${l.props.join('; ')} }`);
console.log(`
Each of these overrides the global rule for EVERY element of that name while the page is mounted.
Fix one of three ways:
  * delete it, when the global rule already says the same thing;
  * rename it to something the page owns, when the page genuinely needs it different;
  * promote it to the global stylesheet, when every user of the name should have it —
    and check the other users first.`);
process.exit(1);

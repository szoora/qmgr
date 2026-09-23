// A ⓘ THAT REVEALS WHAT THE READER IS ALREADY READING IS A CONTROL THAT DOES NOTHING.
//
// The 2026-09-19 prose sweep moved 52 blocks of standing explanation into QInfo popovers, which was
// right. What it left behind, on 35 pages, was the other half of the move: the page's own
// `<p class="subtitle">` stayed where it was, so the band and the popover carried the SAME SENTENCE.
// Pressing the ⓘ on Settings, Staff Onboarding, every hub and most admin pages showed you the line
// you were looking at.
//
// User decision, 2026-09-22: the ⓘ goes, the subtitle stays. If the words are already on screen the
// popover is a button that does nothing, and the subtitle is orientation that costs no click. This
// is the same family as QAvatar.PhotoUrl and the kiosk's two fake language buttons — a control that
// looks like a feature and isn't — except that here it is worse, because it appears to offer MORE
// and delivers exactly what is already there.
//
// This is not an opinion about which prose belongs in a popover. It fails only on EXACT duplication,
// after normalising whitespace, punctuation and entities: the two strings are the same sentence.
// Deciding whether a given hint *should* fold into a ⓘ needs a human read — a scan cannot tell
// standing explanation from an error message, an interpolated value or live state, and a
// measurement that flags the wrong thing is worse than no measurement.
//
//   node scripts/e2e/duplicate-prose-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

// Entities, tags, runs of whitespace and the punctuation that differs between a sentence written as
// a title attribute and the same sentence written as markup. What is left is the words.
const strip = t => t
  .replace(/&mdash;/g, ' ').replace(/&amp;/g, ' ')
  .replace(/<[^>]+>/g, ' ')
  .replace(/\s+/g, ' ')
  .replace(/[.,;:@()—-]/g, '')
  .trim().toLowerCase();

const rel = f => f.split(/Components[\\/]/)[1].replace(/\\/g, '/');
const found = [];
let withInfo = 0;

for (const f of files) {
  const s = fs.readFileSync(f, 'utf8');
  const infos = [...s.matchAll(/<QInfo\s[^>]*?\/>/g)];
  if (!infos.length) continue;
  withInfo++;

  const subs = [...s.matchAll(/<p class="subtitle">([\s\S]{25,400}?)<\/p>/g)].map(m => strip(m[1]));
  if (!subs.length) continue;

  for (const m of infos) {
    const text = /\sText="([^"]*)"/.exec(m[0])?.[1];
    if (!text) continue;
    const a = strip(text);
    if (a && subs.includes(a)) found.push([rel(f), a]);
  }
}

if (found.length) {
  console.log(`\n  ${found.length} page(s) render the same sentence in a QInfo AND in the subtitle beside it:\n`);
  for (const [f, t] of found) console.log(`    ${f.padEnd(46)} "${t.slice(0, 66)}…"`);
  console.log('\n  Remove the QInfo. The words are already on the page, so the button reveals nothing —');
  console.log('  and a control that appears to offer more and delivers what is already there is worse');
  console.log('  than no control at all. Keep the ⓘ only where it carries something the band does not.\n');
  process.exitCode = 1;
} else {
  console.log(`${withInfo} component(s) carry a QInfo — none repeats its own subtitle.`);
}

// EVERY NOTIFICATION NAMES ONE PERSON. This guard is what keeps that true (2026-09-23).
//
// A newly onboarded teacher at Maryhill opened the bell and read the school's failed payments. Three
// senders — payment outcomes and the two module-trial notices — built a CreateNotificationRequest
// with no UserId. The readers treated a recipient-less row as everybody's (`|| n.UserId == null`),
// and the live push sent it through SignalR's Clients.All — every signed-in browser on the PLATFORM,
// every tenant. Nothing failed, nothing logged, and the build was clean.
//
// The service now throws on a missing recipient and the column is NOT NULL, so a regression would
// fail at run time. This catches it at build time, in the three shapes it took:
//
//   1. A CreateNotificationRequest built with no `UserId =` in its initializer — whether it goes
//      straight to CreateInAppNotificationAsync or through a page's own SendAsync wrapper. A template
//      passed to NotifyManyAsync is exempt: that method sets the recipient, one row per person, from
//      an audience NotificationAudience built.
//   2. A notification reader that admits a row with no recipient — `UserId == null` anywhere near
//      the Notifications set.
//   3. A notification pushed to anything wider than one person: Clients.All, or a branch group,
//      carrying "ReceiveNotification".
//
//   node scripts/e2e/notification-recipient-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.env.GUARD_ROOT ?? 'src/Q-Mgr.API';
const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'Migrations'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.cs') && files.push(p);
  }
})(ROOT);

// Comments are blanked (keeping line breaks) so a note that QUOTES the old bug does not report itself.
const blank = s => s
  .replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '))
  .replace(/\/\/[^\n]*/g, m => ' '.repeat(m.length));

// The balanced { ... } that starts at `open`.
function block(src, open) {
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) return src.slice(open, i + 1);
  }
  return src.slice(open);
}

const lineOf = (src, i) => src.slice(0, i).split('\n').length;
const findings = [];
let sends = 0;

for (const f of files) {
  const src = blank(fs.readFileSync(f, 'utf8'));
  const rel = f.replaceAll('\\', '/');

  // 1. Every request names its recipient, unless it is a NotifyManyAsync template.
  for (const m of src.matchAll(/new\s+(?:[\w.]+\.)?CreateNotificationRequest\s*(?:\(\s*\))?\s*\{/g)) {
    sends++;
    const before = src.slice(Math.max(0, m.index - 250), m.index);
    if (/NotifyManyAsync\([^;]*$/.test(before)) continue;
    const init = block(src, m.index + m[0].length - 1);
    if (!/\bUserId\s*=/.test(init)) findings.push(`${rel}:${lineOf(src, m.index)}  a notification is built with no UserId — name the recipient, or use NotifyManyAsync with NotificationAudience`);
  }

  // 2. No reader admits a recipient-less row.
  for (const m of src.matchAll(/UserId\s*==\s*null/g)) {
    const around = src.slice(Math.max(0, m.index - 400), m.index + 50);
    if (/Notifications\b/.test(around)) findings.push(`${rel}:${lineOf(src, m.index)}  a notification query admits UserId == null — a row with no recipient is nobody's, never everybody's`);
  }

  // 3. No notification is pushed wider than its one recipient.
  for (const m of src.matchAll(/Clients\.(All|Group\(\s*\$?"branch-[^)]*\))\s*\.SendAsync\(\s*"ReceiveNotification"/g)) {
    findings.push(`${rel}:${lineOf(src, m.index)}  "ReceiveNotification" pushed to Clients.${m[1].startsWith('All') ? 'All' : 'a branch group'} — a notification goes to its one recipient's group`);
  }
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} notification routing finding(s).`);
  process.exit(1);
}
console.log(`${sends} notification send(s) scanned — every one names its recipient; no reader or push reaches wider.`);

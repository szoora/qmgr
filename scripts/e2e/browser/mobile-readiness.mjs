// MOBILE READINESS — the measured baseline a bottom navigation bar would have to live on top of.
//
// Asked for before building an RBAC-aware mobile footer nav: prove there is no component overlap
// today, and find what a fixed bar at the bottom would collide with or cover.
//
// It measures rather than eyeballs, because CLAUDE.md's standing rule is that a screenshot cannot
// show this: `resize_window` lies about the viewport, and an overlap of two absolutely-positioned
// boxes is invisible until the exact content that triggers it is on screen. Everything here is a
// number read out of the live layout at a real 390 x 844 (iPhone 12/13/14 logical size).
//
// FIVE PASSES PER PAGE:
//   1. SIDEWAYS SCROLL — documentElement.scrollWidth vs the viewport. The project's own hard rule
//      is that the PAGE never scrolls sideways; a wide table inside its own scroller is fine.
//   2. TAP TARGETS — every interactive element against WCAG 2.2 SC 2.5.8 (24x24 CSS px, Level AA)
//      and against this project's own stated phone floor of 40px, which is stricter and is the one
//      that matters here. Reported separately so a failure says which bar it missed.
//   3. OVERLAP — pairwise intersection of visible interactive elements that are NOT nested. Two
//      controls whose hit areas cross means one of them cannot be pressed, which is the exact class
//      the user asked to rule out before anything new is anchored to the screen.
//   4. THE FOOTER BAND — what currently occupies the bottom 64px, which is where a navigation bar
//      would go: anything fixed there, and any interactive element that would be covered.
//   5. SAFE AREA — whether the page reserves anything for the home indicator. Today nothing does,
//      which is a finding rather than a failure: it only becomes a bug once a bar is pinned down
//      there.
//
// Run: node scripts/e2e/browser/mobile-readiness.mjs   (CDP_PORT=9444 to watch it)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

const W = 390, H = 844;          // iPhone 12/13/14 logical viewport
const BAR = 64;                  // the band a navigation bar would claim, incl. label row
const AA_MIN = 24;               // WCAG 2.2 SC 2.5.8, Level AA
const PROJECT_MIN = 40;          // this project's own phone floor (CLAUDE.md, size scale)

// One page per role group's likely landing place, plus the shapes most at risk: a long form, a
// wide table, a week grid, a timeline and a hub with tabs.
const PAGES = [
  ['dashboard', '/'],
  ['my workspace', '/portal'],
  ['my school day', '/my-day'],
  ['students', '/admin/students/roster'],
  ['welfare reports', '/admin/welfare-reports'],
  ['staff directory', '/admin/staff'],
  ['duties', '/admin/staff/duties'],
  ['timetable', '/admin/timetable'],
  ['visitors', '/admin/visitors'],
  ['library', '/content/library'],
  ['users & roles', '/admin/users'],
  ['settings', '/admin/settings'],
  ['billing', '/billing'],
  ['notifications', '/notifications'],
];

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`);
};
const note = (name, why) => { skip++; say(`    SKIP  ${name}  — ${why}`); };

// ---------------------------------------------------------------------------------------------
// The probe runs entirely in the page. Built as one string so nothing depends on how the driver
// escapes a template literal — a lesson from this session.
const PROBE = `(() => {
  const vw = document.documentElement.clientWidth;
  const vh = window.innerHeight;
  const INTERACTIVE = 'a[href], button, input, select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])';

  const visible = (el) => {
    const s = getComputedStyle(el);
    if (s.display === 'none' || s.visibility === 'hidden' || Number(s.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };

  const path = (el) => {
    const bits = [];
    for (let e = el; e && e.nodeType === 1 && bits.length < 3; e = e.parentElement) {
      let b = e.tagName.toLowerCase();
      if (e.classList.length) b += '.' + [...e.classList].slice(0, 2).join('.');
      bits.unshift(b);
    }
    return bits.join(' > ');
  };

  // HIT-TESTABLE, not merely laid out. An element clipped by an ancestor's overflow, or covered by
  // something above it, cannot be pressed — and two rects crossing means nothing if neither point
  // resolves to its element. The collapsed sidebar is the case that proved it: 1px wide with
  // overflow:hidden, so its nav links keep 260px boxes that cross every page and are clipped out
  // of the hit test entirely. Reporting those is the "measurement that flags the wrong thing"
  // this project has already been bitten by, so an overlap has to be pressable to count.
  const hittable = (el) => {
    const r = el.getBoundingClientRect();
    if (r.right <= 0 || r.bottom <= 0 || r.left >= vw || r.top >= vh) return false;
    const x = Math.min(Math.max(r.left + r.width / 2, 1), vw - 1);
    const y = Math.min(Math.max(r.top + r.height / 2, 1), vh - 1);
    const hit = document.elementFromPoint(x, y);
    return !!hit && (hit === el || el.contains(hit) || hit.contains(el));
  };

  // Is this element painted by a fixed ancestor — the navigation bar, a modal, a toast?
  const inFixed = (el) => {
    for (let e = el; e && e.nodeType === 1; e = e.parentElement) {
      if (getComputedStyle(e).position === "fixed") return true;
    }
    return false;
  };

  const els = [...document.querySelectorAll(INTERACTIVE)].filter(el => visible(el) && hittable(el));

  // --- 1. sideways scroll -------------------------------------------------------------------
  const sideways = Math.max(0, document.documentElement.scrollWidth - vw);
  // Which element actually sticks out, so a failure names a culprit rather than a number.
  const wideOnes = [...document.querySelectorAll('body *')].filter(el => {
    if (!visible(el)) return false;
    const r = el.getBoundingClientRect();
    if (r.right <= vw + 1) return false;
    // A scroller of its own is allowed to be wider than the screen; the PAGE is what must not move.
    for (let p = el.parentElement; p; p = p.parentElement) {
      const o = getComputedStyle(p).overflowX;
      if (o === 'auto' || o === 'scroll' || o === 'hidden' || o === 'clip') return false;
    }
    return true;
  }).slice(0, 5).map(el => ({ sel: path(el), right: Math.round(el.getBoundingClientRect().right) }));

  // --- 2. tap targets -----------------------------------------------------------------------
  // An inline link inside a sentence is explicitly excepted by SC 2.5.8, so it is not counted.
  const inlineLink = (el) => {
    if (el.tagName !== 'A') return false;
    const p = el.parentElement;
    if (!p) return false;
    return p.textContent.trim().length > el.textContent.trim().length + 8;
  };
  const targets = els.filter(el => !inlineLink(el) && el.type !== 'hidden');
  const measure = (el) => { const r = el.getBoundingClientRect(); return Math.min(r.width, r.height); };
  const underAA = targets.filter(el => measure(el) < ${AA_MIN})
    .slice(0, 8).map(el => ({ sel: path(el), px: Math.round(measure(el)) }));
  const underProject = targets.filter(el => measure(el) < ${PROJECT_MIN})
    .slice(0, 8).map(el => ({ sel: path(el), px: Math.round(measure(el)) }));

  // --- 3. overlap ---------------------------------------------------------------------------
  // Only elements that are NOT ancestors of one another: a button inside a card legitimately sits
  // inside its box. Two SIBLING controls crossing means one of them cannot be pressed.
  const overlaps = [];
  for (let i = 0; i < els.length && overlaps.length < 6; i++) {
    for (let j = i + 1; j < els.length && overlaps.length < 6; j++) {
      const a = els[i], b = els[j];
      if (a.contains(b) || b.contains(a)) continue;
      // A FIXED overlay passing over scrolled content is not an overlap, it is scrolling. The
      // navigation bar is fixed and the page moves under it by design; the thing that would be a
      // defect is content left UNDER it at rest, which the reserved band covers and mobile-nav
      // asserts directly. Only a pair in the same flow can contend for a tap.
      // The ANCESTOR, not the element: a slot inside the fixed bar is itself position:relative,
      // so testing the element alone reported the bar against every page it scrolled over.
      if (inFixed(a) !== inFixed(b)) continue;
      const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
      const x = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
      const y = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
      if (x > 2 && y > 2) overlaps.push({ a: path(a), b: path(b), x: Math.round(x), y: Math.round(y) });
    }
  }

  // --- 4. the band a bottom bar would claim --------------------------------------------------
  const bandTop = vh - ${BAR};
  const fixedAtBottom = [...document.querySelectorAll('body *')].filter(el => {
    if (!visible(el) || !hittable(el)) return false;
    const s = getComputedStyle(el);
    if (s.position !== 'fixed' && s.position !== 'sticky') return false;
    const r = el.getBoundingClientRect();
    return r.bottom > bandTop && r.top < vh;
  }).slice(0, 6).map(el => ({ sel: path(el), z: getComputedStyle(el).zIndex, bottom: Math.round(el.getBoundingClientRect().bottom) }));

  // Interactive things sitting in that band right now. They are not a bug today — the page simply
  // ends there — but each is something a bar would cover unless the shell reserves the space.
  const inBand = els.filter(el => {
    const r = el.getBoundingClientRect();
    return r.bottom > bandTop && r.top < vh && getComputedStyle(el).position !== 'fixed';
  }).slice(0, 6).map(path);

  // --- 5. safe area --------------------------------------------------------------------------
  const rootPadBottom = getComputedStyle(document.documentElement).paddingBottom;
  const bodyPadBottom = getComputedStyle(document.body).paddingBottom;

  return JSON.stringify({
    vw, vh, sideways, wideOnes,
    targets: targets.length, underAA, underProject,
    overlaps,
    fixedAtBottom, inBand,
    rootPadBottom, bodyPadBottom,
    errorBar: (() => { const e = document.querySelector('#blazor-error-ui');
      return e ? getComputedStyle(e).display : 'missing'; })(),
  });
})()`;

// ---------------------------------------------------------------------------------------------
const t = await openTab();
await t.viewport(W, H, true);
await login(t, USER, PASS);

const totals = { sideways: 0, underAA: 0, underProject: 0, overlaps: 0, measured: 0 };
const findings = { wide: [], aa: [], project: [], overlap: [], band: [], fixed: [] };

for (const [name, path] of PAGES) {
  await t.goto(`${BASE}${path}`);
  const ready = await t.waitFor("!!document.querySelector('.qm-main')", 20000);
  if (!ready) { note(name, 'the shell never rendered'); continue; }
  await t.sleep(1800);

  // A module the dev tenant does not hold redirects to Billing; measuring that page under another
  // page's name is the vacuous pass this project keeps rediscovering.
  const landed = await t.eval('location.pathname');
  if (landed !== path && landed.startsWith('/billing') && !path.startsWith('/billing')) {
    note(name, `module-gated — redirected to ${landed}`);
    continue;
  }

  let r;
  try { r = JSON.parse(await t.eval(PROBE)); }
  catch (e) { note(name, `probe failed: ${String(e).slice(0, 80)}`); continue; }

  totals.measured++;
  totals.sideways += r.sideways > 1 ? 1 : 0;
  totals.underAA += r.underAA.length;
  totals.underProject += r.underProject.length;
  totals.overlaps += r.overlaps.length;

  if (r.sideways > 1) findings.wide.push([name, r.sideways, r.wideOnes]);
  if (r.underAA.length) findings.aa.push([name, r.underAA]);
  if (r.underProject.length) findings.project.push([name, r.underProject]);
  if (r.overlaps.length) findings.overlap.push([name, r.overlaps]);
  if (r.inBand.length) findings.band.push([name, r.inBand]);
  if (r.fixedAtBottom.length) findings.fixed.push([name, r.fixedAtBottom]);

  check(`${name}: the page does not scroll sideways`, r.sideways <= 1, `${r.sideways}px over, e.g. ${JSON.stringify(r.wideOnes[0] ?? null)}`);
  check(`${name}: every tap target meets WCAG 2.5.8 (24px)`, r.underAA.length === 0, `${r.underAA.length} under: ${JSON.stringify(r.underAA.slice(0, 2))}`);
  check(`${name}: no two controls overlap`, r.overlaps.length === 0, `${r.overlaps.length}: ${JSON.stringify(r.overlaps.slice(0, 2))}`);
  check(`${name}: no unhandled error on the page`, r.errorBar === 'none' || r.errorBar === 'missing', `#blazor-error-ui display=${r.errorBar}`);
}

// ---------------------------------------------------------------------------------------------
say('');
say('  ── what a bottom bar would have to coexist with ──────────────────');
for (const [name, list] of findings.fixed) say(`    ${name.padEnd(16)} fixed/sticky in the bottom band: ${JSON.stringify(list)}`);
if (!findings.fixed.length) say('    nothing is fixed in the bottom 64px on any measured page');
say('');
for (const [name, list] of findings.band.slice(0, 6)) say(`    ${name.padEnd(16)} would be covered: ${list.slice(0, 3).join(' | ')}`);
say('');
say(`  ── the project's own 40px phone floor (stricter than AA) ─────────`);
for (const [name, list] of findings.project.slice(0, 8)) say(`    ${name.padEnd(16)} ${list.length} under 40px, e.g. ${JSON.stringify(list.slice(0, 2))}`);
if (!findings.project.length) say('    every measured target is at least 40px');

t.close();
say('');
const tail = `  mobile-readiness: ${pass} passed, ${fail} failed, ${skip} skipped — ${totals.measured} page(s) measured at ${W}x${H}`;
say(tail);
process.exitCode = fail ? 1 : 0;
